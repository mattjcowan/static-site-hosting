using System.Net;

namespace StaticSiteHost.Services.Ai;

/// <summary>
/// A sliding one-minute window of chat requests per site and client address, in memory, for
/// <c>/_host/ai/chat</c> (<see cref="SlidingWindowLimiter"/>). Every request it lets through is
/// billed to someone's key, so it counts every caller, loopback included: behind a proxy whose
/// forwarded headers are not trusted, all visitors share one address and so one allowance, which
/// errs on the side of the bill. Set SiteHosting:TrustForwardedHeaders there to count each visitor
/// apart.
///
/// An IPv6 client is counted by its /64 network (<see cref="ClientAddress"/>), since one household
/// is handed a whole /64 and could otherwise take a fresh address, and a fresh allowance, for every
/// request.
///
/// Like the passcode throttle, it forgets everything on restart and suits the single instance this
/// app runs as.
/// </summary>
public sealed class AiVisitorLimiter
{
    private readonly SlidingWindowLimiter _window = new();

    /// <summary>
    /// Counts a request, or refuses it when the address has made <paramref name="perMinute"/> in the
    /// last minute. A limit of zero or less lets everything through.
    /// </summary>
    /// <param name="retryAfter">When refused, how long until the oldest request leaves the window.</param>
    public bool TryAcquire(string domain, IPAddress? address, int perMinute, out TimeSpan retryAfter) =>
        _window.TryAcquire(domain, address, perMinute, out retryAfter);
}
