using System.Collections.Concurrent;

namespace StaticSiteHost.Security;

/// <summary>
/// In-memory failed-login counter, keyed by username. Deliberately simple: this app
/// runs as a single instance, and the goal is only to make online guessing impractical.
/// </summary>
public sealed class LoginThrottle
{
    private const int MaxFailures = 8;
    private const int MaxTrackedKeys = 10_000;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset WindowStart;
        public DateTimeOffset? LockedUntil;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLocked(string key, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_entries.TryGetValue(key, out var entry)) return false;

        lock (entry)
        {
            if (entry.LockedUntil is { } until && until > DateTimeOffset.UtcNow)
            {
                retryAfter = until - DateTimeOffset.UtcNow;
                return true;
            }
        }

        return false;
    }

    public void RecordFailure(string key)
    {
        // Guessing at random usernames would otherwise grow this dictionary without bound.
        if (_entries.Count > MaxTrackedKeys) PruneExpired();

        var entry = _entries.GetOrAdd(key, _ => new Entry { WindowStart = DateTimeOffset.UtcNow });
        lock (entry)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - entry.WindowStart > FailureWindow)
            {
                entry.WindowStart = now;
                entry.Failures = 0;
                entry.LockedUntil = null;
            }

            entry.Failures++;
            if (entry.Failures >= MaxFailures) entry.LockedUntil = now + LockoutDuration;
        }
    }

    public void Reset(string key) => _entries.TryRemove(key, out _);

    private void PruneExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - (FailureWindow + LockoutDuration);
        foreach (var (key, entry) in _entries)
        {
            lock (entry)
            {
                if (entry.WindowStart < cutoff && (entry.LockedUntil is null || entry.LockedUntil < DateTimeOffset.UtcNow))
                    _entries.TryRemove(key, out _);
            }
        }
    }
}
