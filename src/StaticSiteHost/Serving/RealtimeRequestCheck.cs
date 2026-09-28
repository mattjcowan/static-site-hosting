using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace StaticSiteHost.Serving;

/// <summary>
/// Whether a request for a site's realtime hub comes from one of the site's own pages, decided
/// from its headers alone, for every request under the hub's path: the negotiation, the WebSocket,
/// the server-sent events stream, and each long poll and send.
///
/// CORS does not stand in the way of a WebSocket, nor of a plain GET such as an image's, and either
/// carries the visitor's cookies for the site, a passcode's among them. A sibling site under the
/// same parent domain is the case that matters: the browser counts it as the same site, so even
/// SameSite=Strict cookies go along. Long polling makes it worse: SignalR keeps the request that
/// opened the connection for as long as the connection lasts, and the site's
/// <c>[RealtimeConnect]</c> and <c>[RealtimeJoin]</c> hooks judge it. A page on a sibling site whose
/// own server had negotiated a connection could have the visitor's browser make that first poll,
/// with an <c>&lt;img&gt;</c>, and then drive the connection as the signed-in visitor. So, in order:
///
///   1. A request with <c>Sec-Fetch-Site</c>, which every current browser sends, must say
///      <c>same-origin</c>, or <c>none</c> for one the visitor made themselves. <c>same-site</c>, the
///      sibling, and <c>cross-site</c> are refused.
///   2. Without it, a request with an <c>Origin</c> must name the site. Sites are told apart by host
///      alone, so the scheme and port do not matter.
///   3. With neither, a WebSocket is let through: a browser always sends <c>Origin</c> with one, so
///      one without is not a browser, and could have sent any headers it liked. Anything else must
///      carry <c>X-Requested-With: XMLHttpRequest</c>, which SignalR's client sends with every fetch
///      and XHR it makes, and which a page on another site cannot add to a request without a CORS
///      preflight, which the hub never answers.
///
/// The server-sent events stream is the one request a browser's SignalR client makes without that
/// header, since EventSource takes none; every browser that has EventSource and fetch metadata sends
/// <c>Sec-Fetch-Site</c> with it, and one that does not falls back to long polling.
/// </summary>
public static class RealtimeRequestCheck
{
    /// <summary>What SignalR's JavaScript client sends with every request it makes through fetch or XHR.</summary>
    public const string RequestedWithHeader = "X-Requested-With";

    public const string RequestedWithValue = "XMLHttpRequest";

    /// <summary>Why the request is refused, as a sentence for its JSON body; null when it may go on to the hub.</summary>
    /// <param name="headers">The request's headers.</param>
    /// <param name="isWebSocket">Whether it asks to become a WebSocket.</param>
    /// <param name="domain">The site the request is for.</param>
    public static string? Refusal(IHeaderDictionary headers, bool isWebSocket, string domain)
    {
        var site = headers["Sec-Fetch-Site"];
        if (!StringValues.IsNullOrEmpty(site))
        {
            return site.Count == 1 && (IsValue(site[0], "same-origin") || IsValue(site[0], "none"))
                ? null
                : $"Only pages on {domain} can connect to its realtime hub, and this request came from a page somewhere else.";
        }

        var origin = headers.Origin;
        if (!StringValues.IsNullOrEmpty(origin))
        {
            return IsSite(origin, domain) ? null : $"Only pages on {domain} can connect to its realtime hub.";
        }

        if (isWebSocket) return null;

        return headers.TryGetValue(RequestedWithHeader, out var requestedWith) &&
               requestedWith.Count == 1 && IsValue(requestedWith[0], RequestedWithValue)
            ? null
            : $"Only pages on {domain} can connect to its realtime hub. A client that is not a browser must send " +
              $"{RequestedWithHeader}: {RequestedWithValue} with each request, as SignalR's own clients do.";
    }

    /// <summary>
    /// Whether the request asks to become a WebSocket: an HTTP/1.1 GET with <c>Upgrade: websocket</c>,
    /// or HTTP/2's extended CONNECT for the websocket protocol. Read from the request itself, because
    /// the feature behind <c>HttpContext.WebSockets</c> is only added inside the hub's own pipeline,
    /// after this check. A page cannot set either on a request of its own; only a WebSocket does.
    /// </summary>
    public static bool IsWebSocketUpgrade(HttpContext context) =>
        context.WebSockets.IsWebSocketRequest ||
        (HttpMethods.IsGet(context.Request.Method) &&
         string.Equals(context.Request.Headers.Upgrade.ToString(), "websocket", StringComparison.OrdinalIgnoreCase)) ||
        (context.Features.Get<IHttpExtendedConnectFeature>() is { IsExtendedConnect: true } connect &&
         string.Equals(connect.Protocol, "websocket", StringComparison.OrdinalIgnoreCase));

    private static bool IsSite(StringValues origin, string domain) =>
        origin.Count == 1 &&
        Uri.TryCreate(origin[0], UriKind.Absolute, out var uri) &&
        string.Equals(uri.IdnHost.TrimEnd('.'), domain, StringComparison.OrdinalIgnoreCase);

    private static bool IsValue(string? value, string expected) =>
        string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}
