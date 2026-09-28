using System.Collections.Concurrent;
using System.Net;

namespace StaticSiteHost.Services;

/// <summary>
/// A sliding one-minute window of requests per site and client address, in memory: the pattern
/// behind the host's per-visitor limits (<see cref="Ai.AiVisitorLimiter"/> for chat,
/// <see cref="Realtime.RealtimeNegotiationLimiter"/> for new realtime connections). Each owner keeps
/// an instance of its own, so one allowance never spends another's.
///
/// It counts every caller, loopback included, and a client by <see cref="ClientAddress.Key"/>, so
/// an IPv6 client is counted by its /64. Like the passcode throttle, it forgets everything on
/// restart and suits the single instance this app runs as.
/// </summary>
public sealed class SlidingWindowLimiter
{
    private const long WindowMilliseconds = 60_000;
    private const int MaxTrackedKeys = 10_000;

    /// <summary>The times of recent requests, oldest first, per site and address.</summary>
    private readonly ConcurrentDictionary<string, Queue<long>> _windows = new(StringComparer.Ordinal);

    /// <summary>
    /// Counts a request, or refuses it when the address has made <paramref name="perMinute"/> in the
    /// last minute on this site. A limit of zero or less lets everything through. A refused request is
    /// not counted, so a client that waits as told gets in.
    /// </summary>
    /// <param name="retryAfter">When refused, how long until the oldest request leaves the window.</param>
    public bool TryAcquire(string domain, IPAddress? address, int perMinute, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (perMinute <= 0) return true;

        var now = Environment.TickCount64;

        // Guessing at addresses would otherwise grow the dictionary without bound.
        if (_windows.Count > MaxTrackedKeys) Prune(now);

        var window = _windows.GetOrAdd($"{domain.ToLowerInvariant()}|{ClientAddress.Key(address)}", _ => new Queue<long>());
        lock (window)
        {
            while (window.Count > 0 && now - window.Peek() >= WindowMilliseconds) window.Dequeue();

            if (window.Count >= perMinute)
            {
                retryAfter = TimeSpan.FromMilliseconds(WindowMilliseconds - (now - window.Peek()));
                return false;
            }

            window.Enqueue(now);
            return true;
        }
    }

    /// <summary>
    /// Drops the windows no request has touched for a minute. A request racing the removal of its
    /// own idle window is counted in the discarded one: one request, once, when the table is full.
    /// </summary>
    private void Prune(long now)
    {
        foreach (var (key, window) in _windows)
        {
            lock (window)
            {
                if (window.Count == 0 || now - window.Last() >= WindowMilliseconds)
                    _windows.TryRemove(new KeyValuePair<string, Queue<long>>(key, window));
            }
        }
    }
}
