using System.Net;

namespace StaticSiteHost.Services.Realtime;

/// <summary>
/// A sliding one-minute window of new realtime connections per site and client address, in memory
/// (<see cref="SlidingWindowLimiter"/>), counted as a page negotiates, which is before the site's
/// <c>[RealtimeConnect]</c> hook runs. The hook is the site's own code, often a database lookup, so
/// without this anyone with curl could make the site run it as fast as they could send requests.
/// SiteHosting:RealtimeNegotiationsPerMinute sets the allowance; a page that loses its connection
/// and tries again a few times stays well inside it.
/// </summary>
public sealed class RealtimeNegotiationLimiter
{
    private readonly SlidingWindowLimiter _window = new();

    /// <summary>
    /// Counts a new connection, or refuses it when the address has started <paramref name="perMinute"/>
    /// in the last minute. A limit of zero or less lets everything through.
    /// </summary>
    /// <param name="retryAfter">When refused, how long until the oldest start leaves the window.</param>
    public bool TryAcquire(string domain, IPAddress? address, int perMinute, out TimeSpan retryAfter) =>
        _window.TryAcquire(domain, address, perMinute, out retryAfter);
}
