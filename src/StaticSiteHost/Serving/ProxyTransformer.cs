using System.Text.RegularExpressions;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using StaticSiteHost.Models;
using Yarp.ReverseProxy.Forwarder;

namespace StaticSiteHost.Serving;

/// <summary>
/// What one proxy route changes on the way through. YARP's base class copies the method, the body
/// and the headers both ways and drops the ones that describe a single connection; this adds the
/// route's settings and the server's own rules on top. Built once per route when
/// <see cref="SiteProxy"/> compiles a site's routes, and shared by every request on that route; what
/// differs between requests travels in <see cref="ProxyRequestState"/>.
/// </summary>
internal sealed partial class ProxyTransformer : HttpTransformer
{
    private static readonly string[] ForwardedNames =
        ["X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Forwarded-Prefix", "Forwarded"];

    private readonly ProxyRoute _route;
    private readonly string _prefix;
    private readonly Uri _target;
    private readonly KeyValuePair<string, string>[] _requestHeaders;

    /// <param name="route">The route as saved.</param>
    /// <param name="prefix">Its path as matched, with "" for <c>/</c>.</param>
    /// <param name="target">Its target, <c>${env:…}</c> already expanded.</param>
    /// <param name="requestHeaders">Its request headers, values expanded the same way.</param>
    public ProxyTransformer(ProxyRoute route, string prefix, Uri target, KeyValuePair<string, string>[] requestHeaders)
    {
        _route = route;
        _prefix = prefix;
        _target = target;
        _requestHeaders = requestHeaders;
    }

    [GeneratedRegex(@";\s*Domain\s*=[^;]*", RegexOptions.IgnoreCase)]
    private static partial Regex CookieDomain();

    public override async ValueTask TransformRequestAsync(
        HttpContext context, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(context, proxyRequest, destinationPrefix, cancellationToken);

        var request = context.Request;
        var state = ProxyRequestState.Of(context);

        proxyRequest.RequestUri = RequestUtilities.MakeDestinationAddress(
            destinationPrefix, _route.StripPath ? state.Remainder : request.Path, request.QueryString);

        // Null sends the target's own host, from the address above.
        proxyRequest.Headers.Host = _route.KeepHost ? request.Host.Value : null;

        // The server's cookies (the management session, a site's passcode) are no business of the
        // backend's, so they never leave, whatever the route says.
        proxyRequest.Headers.Remove(HeaderNames.Cookie);
        if (WithoutServerCookies(request.Headers.Cookie) is { Length: > 0 } cookies)
            proxyRequest.Headers.TryAddWithoutValidation(HeaderNames.Cookie, cookies);

        // Whatever the visitor sent under these names is dropped either way, so they cannot claim
        // to be someone else. The values set come from this request, which the forwarded headers
        // middleware has already corrected when the server trusts a proxy in front of it.
        foreach (var name in ForwardedNames) proxyRequest.Headers.Remove(name);
        if (_route.ForwardedHeaders)
        {
            if (context.Connection.RemoteIpAddress is { } address)
                proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", address.ToString());
            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", request.Scheme);
            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Host", request.Host.Value);
            if (_route.StripPath && _prefix.Length > 0)
                proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", _prefix);
        }

        proxyRequest.Headers.Remove(SiteProxy.HopsHeader);
        proxyRequest.Headers.TryAddWithoutValidation(SiteProxy.HopsHeader,
            (SiteProxy.HopsOf(context) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));

        foreach (var (name, value) in _requestHeaders)
        {
            // Content-Type and the like live on the content, not the request.
            proxyRequest.Headers.Remove(name);
            proxyRequest.Content?.Headers.Remove(name);
            if (value.Length == 0) continue;

            if (!proxyRequest.Headers.TryAddWithoutValidation(name, value))
                proxyRequest.Content?.Headers.TryAddWithoutValidation(name, value);
        }

        // gRPC-Web goes on as gRPC: its own content type, and the TE header gRPC servers look for.
        if (state.GrpcWeb != GrpcWebMode.None)
        {
            if (proxyRequest.Content is { } content)
            {
                content.Headers.Remove(HeaderNames.ContentType);
                content.Headers.TryAddWithoutValidation(HeaderNames.ContentType, state.GrpcContentType);
                if (state.GrpcWeb == GrpcWebMode.Text) content.Headers.ContentLength = null;
            }

            proxyRequest.Headers.Remove(HeaderNames.TE);
            proxyRequest.Headers.TryAddWithoutValidation(HeaderNames.TE, "trailers");
        }
    }

    public override async ValueTask<bool> TransformResponseAsync(
        HttpContext context, HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
    {
        var proceed = await base.TransformResponseAsync(context, proxyResponse, cancellationToken);
        if (proxyResponse is null) return proceed;

        var headers = context.Response.Headers;
        var state = ProxyRequestState.Of(context);

        if (headers.SetCookie.Count > 0)
            headers.SetCookie = RewriteCookies(headers.SetCookie, context.Request.Host.Host);

        if (_route.RewriteRedirects && headers.Location.Count == 1)
            headers.Location = RewriteLocation(headers.Location.ToString());

        if (_route.Streaming) headers["X-Accel-Buffering"] = "no";

        // A gRPC answer to a gRPC-Web call goes back as gRPC-Web. Anything else, such as the
        // target's own error page, goes back as it came.
        if (state.GrpcWeb != GrpcWebMode.None &&
            GrpcWeb.WebContentTypeFor(proxyResponse.Content.Headers.ContentType?.ToString(), state.GrpcWeb) is { } webContentType)
        {
            headers.ContentType = webContentType;
            headers.ContentLength = null;
            headers["X-Accel-Buffering"] = "no";
            state.GrpcWebResponse = true;
            if (context.Response.Body is GrpcWebTextResponseStream text) text.Active = true;
        }

        headers.XContentTypeOptions = "nosniff";

        // The site's header rules, after the backend's own headers so a rule wins, as it does over
        // the server's defaults for files.
        state.HeaderRules.Apply(headers, state.RequestPath, null);

        // Last word, as for files: the gate is in this server, so nothing behind it may sit in a
        // shared cache or a search index where the gate no longer applies.
        if (state.IsPrivate)
        {
            headers.CacheControl = SiteContentServer.KeepPrivate(headers.CacheControl.ToString());
            headers["X-Robots-Tag"] = "noindex, nofollow";
        }

        return proceed;
    }

    /// <summary>
    /// The target's trailers. For a gRPC-Web call they go at the end of the body, as the frame the
    /// browser reads <c>grpc-status</c> from; none means the status came with the headers
    /// (a trailers-only answer), and those have been passed on already.
    /// </summary>
    public override async ValueTask TransformResponseTrailersAsync(
        HttpContext context, HttpResponseMessage proxyResponse, CancellationToken cancellationToken)
    {
        try
        {
            if (!ProxyRequestState.Of(context).GrpcWebResponse)
            {
                // Trailers after no body at all, as a gRPC error that follows the headers is: sent now,
                // the headers go without the Content-Length: 0 Kestrel would give a response with an
                // empty body, which nginx's grpc_pass takes for the end and drops the trailers after.
                if (!context.Response.HasStarted && proxyResponse.TrailingHeaders.Any() && context.Response.SupportsTrailers())
                    await context.Response.StartAsync(cancellationToken);

                await base.TransformResponseTrailersAsync(context, proxyResponse, cancellationToken);
                return;
            }

            if (!proxyResponse.TrailingHeaders.Any()) return;

            await context.Response.Body.WriteAsync(GrpcWeb.TrailersFrame(proxyResponse.TrailingHeaders), cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (context.RequestAborted.IsCancellationRequested || ex is IOException or OperationCanceledException)
        {
            // The visitor went away as the target finished, as one closing a server stream does:
            // nobody is left to send the status to. YARP lets this through, so it is ended here.
        }
    }

    /// <summary>The Cookie header without the server's own cookies, whose names start with <c>ssh.</c>.</summary>
    internal static string WithoutServerCookies(StringValues header) =>
        string.Join("; ", header
            .SelectMany(value => (value ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(pair => !IsServerCookie(pair)));

    private static bool IsServerCookie(string nameValue) =>
        nameValue.TrimStart().StartsWith("ssh.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The backend's cookies as the route says to scope them. One named like the server's own is
    /// dropped: it could shadow the management session or a passcode cookie, which is the same
    /// sibling-subdomain risk that keeps header rules from setting cookies at all.
    /// </summary>
    internal string[] RewriteCookies(StringValues cookies, string requestHost)
    {
        var parent = _route.CookieParentDomain?.Trim().TrimEnd('.');
        var parentApplies = _route.CookieMode == ProxyCookieModes.Parent && !string.IsNullOrEmpty(parent) &&
                            (requestHost.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
                             requestHost.EndsWith("." + parent, StringComparison.OrdinalIgnoreCase));

        return cookies
            .OfType<string>()
            .Where(cookie => cookie.Length > 0 && !IsServerCookie(cookie))
            .Select(cookie => _route.CookieMode switch
            {
                ProxyCookieModes.Keep => cookie,
                // A parent that no longer covers this host (the site was renamed) falls back to the host.
                ProxyCookieModes.Parent when parentApplies => CookieDomain().Replace(cookie, "") + "; Domain=" + parent,
                _ => CookieDomain().Replace(cookie, "")
            })
            .ToArray();
    }

    /// <summary>
    /// A redirect to the target's own address, which the browser could not follow, becomes the same
    /// place on this site, with the route's path put back in front when the route removed it. Any
    /// other address, and a path with no host, is left alone: a backend that redirects with a bare
    /// path and sits behind a removed prefix should build it from <c>X-Forwarded-Prefix</c>.
    /// </summary>
    internal string RewriteLocation(string location)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var absolute)) return location;
        if (!string.Equals(absolute.Scheme, _target.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(absolute.Authority, _target.Authority, StringComparison.OrdinalIgnoreCase))
            return location;

        var basePath = _target.AbsolutePath.TrimEnd('/');
        var path = absolute.AbsolutePath;

        string rest;
        if (basePath.Length == 0) rest = path;
        else if (path == basePath) rest = "";
        else if (path.StartsWith(basePath + "/", StringComparison.Ordinal)) rest = path[basePath.Length..];
        else return location;

        var local = (_route.StripPath ? _prefix : "") + rest;
        return (local.Length == 0 ? "/" : local) + absolute.Query + absolute.Fragment;
    }
}

/// <summary>What one request through a proxy route carries for <see cref="ProxyTransformer"/>.</summary>
/// <param name="Remainder">The request path after the route's own, for a route that removes it.</param>
/// <param name="RequestPath">The path the visitor asked for, which header rules match against.</param>
/// <param name="HeaderRules">The site's header rules.</param>
/// <param name="IsPrivate">Whether the site is behind a passcode.</param>
internal sealed record ProxyRequestState(PathString Remainder, string RequestPath, SiteHeaderRules HeaderRules, bool IsPrivate)
{
    private const string Key = "ssh.proxy";

    /// <summary>A gRPC-Web call the route translates, and in which form; None for any other request.</summary>
    public GrpcWebMode GrpcWeb { get; init; }

    /// <summary>The gRPC content type the call goes to the target with.</summary>
    public string GrpcContentType { get; init; } = "";

    /// <summary>Set once the target answered the call with gRPC, so the trailers go in the body.</summary>
    public bool GrpcWebResponse { get; set; }

    public void Attach(HttpContext context) => context.Items[Key] = this;

    public static ProxyRequestState Of(HttpContext context) =>
        context.Items.TryGetValue(Key, out var value) && value is ProxyRequestState state
            ? state
            : new ProxyRequestState(context.Request.Path, context.Request.Path.Value ?? "/", SiteHeaderRules.None, false);
}
