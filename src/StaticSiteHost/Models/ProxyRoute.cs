namespace StaticSiteHost.Models;

/// <summary>
/// One proxy route: requests under <see cref="Path"/> are forwarded to <see cref="Target"/>.
/// Persisted in <see cref="SiteRecord.Proxy"/>. Only administrators may change routes, and a
/// deploy never brings any: a route makes the server send requests to wherever it points.
/// See <c>Serving/SiteProxy.cs</c> for how a request is matched and forwarded.
/// </summary>
public sealed class ProxyRoute
{
    /// <summary>
    /// The path prefix, matched by whole segments and case-insensitively: <c>/api</c> matches
    /// <c>/api</c> and <c>/api/users</c> but not <c>/apis</c>. <c>/</c> matches everything. The
    /// longest matching path wins.
    /// </summary>
    public string Path { get; set; } = "/";

    /// <summary>
    /// Where requests go: an <c>http://</c> or <c>https://</c> URL, optionally with a base path.
    /// <c>${env:NAME}</c> anywhere in it is replaced by that environment variable.
    /// </summary>
    public string Target { get; set; } = "";

    /// <summary>
    /// Remove <see cref="Path"/> before forwarding: <c>/api/users</c> goes to <c>{target}/users</c>.
    /// Off, it goes to <c>{target}/api/users</c>.
    /// </summary>
    public bool StripPath { get; set; } = true;

    /// <summary>Off keeps the route but passes its requests on as if it were not there.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Forward WebSocket upgrades. Off, an upgrade is refused with 403.</summary>
    public bool WebSockets { get; set; }

    /// <summary>
    /// Forward a WebSocket upgrade whose Origin is another site. Off by default: the visitor's
    /// cookies go with the upgrade, so any page on the web could otherwise open a socket as them.
    /// </summary>
    public bool CrossSiteWebSockets { get; set; }

    /// <summary>
    /// For server-sent events, long polls and large downloads: the response is never compressed or
    /// buffered here, and carries <c>X-Accel-Buffering: no</c> for an nginx in front.
    /// </summary>
    public bool Streaming { get; set; }

    /// <summary>One of <see cref="ProxyHttpVersions"/>.</summary>
    public string HttpVersion { get; set; } = ProxyHttpVersions.Auto;

    /// <summary>
    /// The longest the backend may go without sending anything: waiting for the headers, between
    /// pieces of a streamed body, and on an idle WebSocket.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 100;

    /// <summary>Largest request body forwarded, in bytes. Null keeps the server's <c>MaxUploadBytes</c>.</summary>
    public long? MaxRequestBodyBytes { get; set; }

    /// <summary>Send the visitor's Host header rather than the target's.</summary>
    public bool KeepHost { get; set; }

    /// <summary>
    /// Send <c>X-Forwarded-For</c>, <c>-Proto</c>, <c>-Host</c> and, when the path is removed,
    /// <c>-Prefix</c>. Whatever the visitor sent under those names is dropped either way.
    /// </summary>
    public bool ForwardedHeaders { get; set; } = true;

    /// <summary>Request headers to set, or to remove when the value is empty.</summary>
    public List<ProxyHeader> RequestHeaders { get; set; } = [];

    /// <summary>One of <see cref="ProxyCookieModes"/>: where cookies the backend sets are scoped.</summary>
    public string CookieMode { get; set; } = ProxyCookieModes.Host;

    /// <summary>The domain cookies are moved to when <see cref="CookieMode"/> is <c>parent</c>.</summary>
    public string? CookieParentDomain { get; set; }

    /// <summary>Turn a redirect to the target's own address back into a path on this site.</summary>
    public bool RewriteRedirects { get; set; } = true;

    /// <summary>Serve the site's file when one exists at the path, and forward only when none does.</summary>
    public bool FilesFirst { get; set; }

    /// <summary>Accept any certificate from an <c>https://</c> target, a self-signed one included.</summary>
    public bool AcceptAnyCertificate { get; set; }
}

public sealed class ProxyHeader
{
    public string Name { get; set; } = "";

    /// <summary>Empty removes the header. <c>${env:NAME}</c> is expanded.</summary>
    public string Value { get; set; } = "";
}

public static class ProxyHttpVersions
{
    /// <summary>HTTP/2 when an <c>https://</c> target offers it, otherwise HTTP/1.1.</summary>
    public const string Auto = "auto";

    public const string Http11 = "1.1";

    /// <summary>HTTP/2 only, over plain <c>http://</c> too (h2c), as gRPC to a container needs.</summary>
    public const string Http2 = "2";

    public static readonly string[] All = [Auto, Http11, Http2];
}

public static class ProxyCookieModes
{
    /// <summary>The cookie's Domain attribute is removed, so it belongs to the site's own host.</summary>
    public const string Host = "host";

    /// <summary>Left as the backend sent it.</summary>
    public const string Keep = "keep";

    /// <summary>Moved to <see cref="ProxyRoute.CookieParentDomain"/>, shared by its subdomains.</summary>
    public const string Parent = "parent";

    public static readonly string[] All = [Host, Keep, Parent];
}
