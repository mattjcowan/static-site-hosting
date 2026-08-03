using System.Collections.Concurrent;
using System.Net;

namespace StaticSiteHost.Security;

/// <summary>
/// In-memory failed-login counters. Two independent limits, because they catch different
/// attacks:
///
///   * <b>per username</b> — someone working a password list against one known account.
///   * <b>per client address</b> — spraying, where a few common passwords are tried against
///     many usernames. No single account ever reaches its own limit, so a username-only
///     counter never notices.
///
/// Deliberately simple: this app runs as a single instance, and the goal is to make online
/// guessing impractical rather than to survive a distributed attack. Put a rate limit in
/// front of it for raw volume — see deploy/nginx/static-site-host.conf.
/// </summary>
public sealed class LoginThrottle
{
    private sealed record Policy(int MaxFailures, TimeSpan Window, TimeSpan Lockout);

    private static readonly Policy PerUsername = new(8, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
    private static readonly Policy PerAddress = new(25, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30));

    private const int MaxTrackedKeys = 10_000;

    /// <summary>Ceiling on the escalating delay, so a failed attempt never holds a connection long.</summary>
    private const int MaxBackoffSeconds = 8;

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset WindowStart;
        public DateTimeOffset? LockedUntil;
    }

    private readonly ConcurrentDictionary<string, Entry> _byUsername = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Entry> _byAddress = new(StringComparer.Ordinal);

    public bool IsLocked(string username, IPAddress? address, out TimeSpan retryAfter)
    {
        var byUsername = RemainingLockout(_byUsername, username);
        var byAddress = Trackable(address)
            ? RemainingLockout(_byAddress, address!.ToString())
            : TimeSpan.Zero;

        retryAfter = byUsername > byAddress ? byUsername : byAddress;
        return retryAfter > TimeSpan.Zero;
    }

    public void RecordFailure(string username, IPAddress? address)
    {
        Record(_byUsername, username, PerUsername);
        if (Trackable(address)) Record(_byAddress, address!.ToString(), PerAddress);
    }

    /// <summary>
    /// How long to wait before answering a failed attempt. The first two mistakes cost
    /// nothing; after that each failure roughly doubles the wait, up to a ceiling. Serial
    /// guessing becomes arithmetically hopeless well before the hard lockout, while a
    /// person who fat-fingers their password twice never notices.
    ///
    /// Keyed on the username only. Applying it per address would punish everyone sharing an
    /// office NAT for one colleague's typos — spraying is what the address limit is for.
    /// </summary>
    public TimeSpan GetBackoff(string username)
    {
        if (!_byUsername.TryGetValue(username, out var entry)) return TimeSpan.Zero;

        int failures;
        lock (entry) failures = entry.Failures;

        if (failures < 2) return TimeSpan.Zero;

        var seconds = Math.Min(1 << Math.Min(failures - 2, 10), MaxBackoffSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Clears both counters after a successful sign-in. That does mean a valid account
    /// behind a shared address resets the address counter, which is the right trade: a
    /// busy office NAT should not lock out everyone who works there.
    /// </summary>
    public void Reset(string username, IPAddress? address)
    {
        _byUsername.TryRemove(username, out _);
        if (Trackable(address)) _byAddress.TryRemove(address!.ToString(), out _);
    }

    /// <summary>
    /// A loopback address means every caller looks identical — either local development, or
    /// a reverse proxy whose forwarded headers we are not configured to trust. Counting
    /// those would let one bad actor lock out every user at once, so they are skipped.
    /// Set SiteHosting:TrustForwardedHeaders behind a proxy to get real client addresses.
    /// </summary>
    private static bool Trackable(IPAddress? address) =>
        address is not null && !IPAddress.IsLoopback(address);

    private static TimeSpan RemainingLockout(ConcurrentDictionary<string, Entry> entries, string key)
    {
        if (!entries.TryGetValue(key, out var entry)) return TimeSpan.Zero;

        lock (entry)
        {
            if (entry.LockedUntil is { } until && until > DateTimeOffset.UtcNow)
                return until - DateTimeOffset.UtcNow;
        }

        return TimeSpan.Zero;
    }

    private static void Record(ConcurrentDictionary<string, Entry> entries, string key, Policy policy)
    {
        // Guessing at random keys would otherwise grow these dictionaries without bound.
        if (entries.Count > MaxTrackedKeys) Prune(entries, policy);

        var entry = entries.GetOrAdd(key, _ => new Entry { WindowStart = DateTimeOffset.UtcNow });
        lock (entry)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - entry.WindowStart > policy.Window)
            {
                entry.WindowStart = now;
                entry.Failures = 0;
                entry.LockedUntil = null;
            }

            entry.Failures++;
            if (entry.Failures >= policy.MaxFailures) entry.LockedUntil = now + policy.Lockout;
        }
    }

    private static void Prune(ConcurrentDictionary<string, Entry> entries, Policy policy)
    {
        var cutoff = DateTimeOffset.UtcNow - (policy.Window + policy.Lockout);
        foreach (var (key, entry) in entries)
        {
            lock (entry)
            {
                if (entry.WindowStart < cutoff &&
                    (entry.LockedUntil is null || entry.LockedUntil < DateTimeOffset.UtcNow))
                {
                    entries.TryRemove(key, out _);
                }
            }
        }
    }
}
