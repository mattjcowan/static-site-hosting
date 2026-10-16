using System.Net;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Features;
using StaticSiteHost.Models;
using StaticSiteHost.Services;
using Yarp.ReverseProxy.Forwarder;

namespace StaticSiteHost.Serving;

/// <summary>
/// Forwards a site's requests along its proxy routes (<see cref="SiteRecord.Proxy"/>). It runs where
/// the site's files would otherwise answer: after the passcode gate, the middleware and the
/// functions, and before redirects and files (see <see cref="SiteHostingMiddleware"/>). So a site's
/// sign-in protects what it proxies, and a function can still answer one path inside a proxied one.
///
/// YARP's <see cref="IHttpForwarder"/> moves the bytes: streaming both ways, WebSockets over HTTP/1.1
/// and HTTP/2, gRPC, cancellation. Matching, settings and errors are this class's.
/// </summary>
public sealed class SiteProxy
{
    /// <summary>Set on a streaming route's request; <see cref="SiteResponseCompressionProvider"/> then leaves it alone.</summary>
    internal const string NoCompressionKey = "ssh.no-compression";

    /// <summary>
    /// How many times a request has been through a proxy route of this server, or of another
    /// copy of it. A route whose target leads back here through a public name or a published port
    /// is not something the address check can see; this is what stops it going round for ever.
    /// </summary>
    internal const string HopsHeader = "X-Site-Host-Hops";

    /// <summary>The most proxy hops a request may already have made when it arrives.</summary>
    internal const int MaxHops = 5;

    /// <summary>
    /// What each site's routes compile to: its own and the server-wide ones that apply to it
    /// (<see cref="ProxySites.Effective"/>), with the three lists it was compiled from. Each list is
    /// replaced, never edited, when it changes (see <see cref="SiteRecord.Proxy"/>), so a table is
    /// still current exactly while all three are the same objects; otherwise it is compiled again.
    /// </summary>
    private readonly ConcurrentDictionary<string, CachedTable> _tables = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedTable(object SiteRoutes, object ServerRoutes, object Disabled, ProxyRouteTable Table);

    private readonly IHttpForwarder _forwarder;
    private readonly ProxyInvokers _invokers;
    private readonly SiteContentServer _content;
    private readonly SiteProxyService _routes;
    private readonly ILogger<SiteProxy> _logger;

    public SiteProxy(
        IHttpForwarder forwarder, ProxyInvokers invokers, SiteContentServer content, SiteProxyService routes, ILogger<SiteProxy> logger)
    {
        _forwarder = forwarder;
        _invokers = invokers;
        _content = content;
        _routes = routes;
        _logger = logger;
    }

    /// <summary>
    /// Forwards the request when one of the site's enabled routes matches it, and answers for it
    /// when that route cannot. False when no route applies, and the request is the files' to answer.
    /// </summary>
    public async Task<bool> TryForwardAsync(HttpContext context, SiteRecord site)
    {
        var serverRoutes = await _routes.ServerRoutesAsync();
        if (site.Proxy.Count == 0 && serverRoutes.Count == 0) return false;

        var table = TableFor(site, serverRoutes);
        if (!table.TryMatch(context.Request.Path, out var route, out var remainder)) return false;

        var requestPath = context.Request.Path.Value ?? "/";
        if (route.Route.FilesFirst && _content.HasFile(site, requestPath)) return false;

        await ForwardAsync(context, site, route, remainder, requestPath);
        return true;
    }

    private ProxyRouteTable TableFor(SiteRecord site, IReadOnlyList<ServerProxyRoute> serverRoutes)
    {
        if (_tables.TryGetValue(site.Domain, out var cached) &&
            ReferenceEquals(cached.SiteRoutes, site.Proxy) &&
            ReferenceEquals(cached.ServerRoutes, serverRoutes) &&
            ReferenceEquals(cached.Disabled, site.DisabledServerRoutes))
            return cached.Table;

        var table = ProxyRouteTable.Compile(
            ProxySites.Effective(site.Domain, site.Proxy, serverRoutes, site.DisabledServerRoutes), site.Domain, _logger);
        _tables[site.Domain] = new CachedTable(site.Proxy, serverRoutes, site.DisabledServerRoutes, table);
        return table;
    }

    private async Task ForwardAsync(HttpContext context, SiteRecord site, CompiledProxyRoute route, PathString remainder, string requestPath)
    {
        // Fail closed: a route whose target cannot be read still owns its path, so a missing
        // environment variable never lets the request fall through to files behind it.
        if (route.Target is null || route.Transformer is null)
        {
            await WriteFailureAsync(context, StatusCodes.Status502BadGateway, "This path's backend is not set up correctly.");
            return;
        }

        if (HopsOf(context) >= MaxHops)
        {
            _logger.LogWarning("Proxy route {Path} on {Domain} refused a request that has been proxied {Hops} times: its target leads back here",
                route.Route.Path, site.Domain, HopsOf(context));
            await WriteFailureAsync(context, StatusCodes.Status508LoopDetected, "This path's backend leads back to this server.");
            return;
        }

        if (IsUpgrade(context))
        {
            if (!route.Route.WebSockets)
            {
                await WriteFailureAsync(context, StatusCodes.Status403Forbidden, "WebSockets are not turned on for this path.");
                return;
            }

            if (!route.Route.CrossSiteWebSockets && IsCrossSite(context))
            {
                await WriteFailureAsync(context, StatusCodes.Status403Forbidden, "WebSockets to this path may only be opened by the site's own pages.");
                return;
            }
        }

        if (route.Route.MaxRequestBodyBytes is { } max &&
            context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = max;
        }

        if (route.Route.Streaming) context.Items[NoCompressionKey] = true;

        new ProxyRequestState(remainder, requestPath, _content.HeaderRulesFor(site), site.IsPasscodeProtected).Attach(context);

        var error = await _forwarder.SendAsync(
            context, route.TargetPrefix!, _invokers.For(route.Route.AcceptAnyCertificate), route.Config, route.Transformer);

        if (error != ForwarderError.None) await FailedAsync(context, site, route, error);
    }

    private async Task FailedAsync(HttpContext context, SiteRecord site, CompiledProxyRoute route, ForwarderError error)
    {
        var exception = context.Features.Get<IForwarderErrorFeature>()?.Exception;

        // The visitor went away, or sent a body that broke off: nothing to tell them.
        if (context.RequestAborted.IsCancellationRequested || IsClientSide(error))
        {
            _logger.LogDebug(exception, "Proxy route {Path} on {Domain}: the visitor's side ended the request ({Error})",
                route.Route.Path, site.Domain, error);
            return;
        }

        _logger.LogWarning(exception, "Proxy route {Path} on {Domain} could not complete a request to {Target}: {Error}",
            route.Route.Path, site.Domain, route.TargetPrefix, error);

        var timedOut = error is ForwarderError.RequestTimedOut or ForwarderError.UpgradeActivityTimeout;
        await WriteFailureAsync(context,
            timedOut ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status502BadGateway,
            timedOut ? "The backend took too long to answer." : "The backend could not be reached.");
    }

    /// <summary>The hop count the request arrived with: 0 when it has none, or none that can be read.</summary>
    internal static int HopsOf(HttpContext context) =>
        int.TryParse(context.Request.Headers[HopsHeader].ToString(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var hops) ? hops : 0;

    private static bool IsClientSide(ForwarderError error) => error is
        ForwarderError.RequestCanceled or ForwarderError.RequestBodyCanceled or ForwarderError.RequestBodyClient or
        ForwarderError.ResponseBodyCanceled or ForwarderError.ResponseBodyClient or
        ForwarderError.UpgradeRequestCanceled or ForwarderError.UpgradeRequestClient or
        ForwarderError.UpgradeResponseCanceled or ForwarderError.UpgradeResponseClient;

    /// <summary>
    /// A short answer that says nothing about the target: internal names and ports are not the
    /// visitor's business. JSON for a caller that asked for it, the server's error page otherwise.
    /// </summary>
    internal static async Task WriteFailureAsync(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted) return;

        context.Response.Clear();

        if (context.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = status;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { error = message });
            return;
        }

        await SiteContentServer.WriteMessageAsync(context, status, ReasonPhrase(status), WebUtility.HtmlEncode(message));
    }

    private static string ReasonPhrase(int status) => status switch
    {
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status504GatewayTimeout => "Gateway timeout",
        StatusCodes.Status508LoopDetected => "Loop detected",
        _ => "Bad gateway"
    };

    /// <summary>A WebSocket, or any other upgrade, over HTTP/1.1 or HTTP/2.</summary>
    private static bool IsUpgrade(HttpContext context) =>
        context.Features.Get<IHttpUpgradeFeature>()?.IsUpgradableRequest == true ||
        context.Features.Get<IHttpExtendedConnectFeature>()?.IsExtendedConnect == true;

    /// <summary>
    /// True when a browser says the request comes from a page on another host. A client that sends
    /// no Origin is not a browser page, and cookies alone are not what it would be riding on.
    /// </summary>
    private static bool IsCrossSite(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length == 0) return false;

        return !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
               !string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A site's routes, compiled: longest path first, so the first match is the one that wins.</summary>
internal sealed class ProxyRouteTable
{
    private readonly CompiledProxyRoute[] _routes;

    private ProxyRouteTable(CompiledProxyRoute[] routes) => _routes = routes;

    public static ProxyRouteTable Compile(IReadOnlyList<ProxyRoute> routes, string domain, ILogger logger) => new(
        routes
            .Where(route => route.Enabled)
            .Select(route => CompiledProxyRoute.Compile(route, domain, logger))
            .OrderByDescending(route => route.Prefix.Length)
            .ToArray());

    public bool TryMatch(PathString path, out CompiledProxyRoute route, out PathString remainder)
    {
        foreach (var candidate in _routes)
        {
            if (candidate.Prefix.Length == 0)
            {
                (route, remainder) = (candidate, path);
                return true;
            }

            if (path.StartsWithSegments(candidate.Prefix, StringComparison.OrdinalIgnoreCase, out remainder))
            {
                route = candidate;
                return true;
            }
        }

        (route, remainder) = (null!, default);
        return false;
    }
}

/// <summary>One route, ready to forward through: the target read, the request settings and the transformer built.</summary>
internal sealed class CompiledProxyRoute
{
    private CompiledProxyRoute(ProxyRoute route, string prefix) => (Route, Prefix) = (route, prefix);

    public ProxyRoute Route { get; }

    /// <summary>The path matched, with "" for <c>/</c>.</summary>
    public string Prefix { get; }

    /// <summary>The target, or null when it cannot be read, and then the route answers 502.</summary>
    public Uri? Target { get; private init; }

    public string? TargetPrefix => Target?.AbsoluteUri;

    public ForwarderRequestConfig Config { get; private init; } = ForwarderRequestConfig.Empty;

    public ProxyTransformer? Transformer { get; private init; }

    public static CompiledProxyRoute Compile(ProxyRoute route, string domain, ILogger logger)
    {
        var prefix = route.Path == "/" ? "" : route.Path;

        var (target, problem) = ProxyRouteRules.ResolveTarget(route.Target);
        if (target is null)
        {
            // Once per change to the routes, not once per request.
            logger.LogError("Proxy route {Path} on {Domain} answers 502 until it is fixed: {Problem}", route.Path, domain, problem);
            return new CompiledProxyRoute(route, prefix);
        }

        var unset = new List<string>();
        var headers = (route.RequestHeaders ?? [])
            .Select(h => KeyValuePair.Create(h.Name, SiteVariableService.Expand(h.Value, unset)))
            .ToArray();
        if (unset.Count > 0)
            logger.LogWarning("Proxy route {Path} on {Domain} sends empty request headers: the server has no environment variable {Names}",
                route.Path, domain, string.Join(", ", unset));

        var (version, policy) = route.HttpVersion switch
        {
            ProxyHttpVersions.Http11 => (HttpVersion.Version11, HttpVersionPolicy.RequestVersionOrLower),
            ProxyHttpVersions.Http2 => (HttpVersion.Version20, HttpVersionPolicy.RequestVersionExact),
            _ => (HttpVersion.Version20, HttpVersionPolicy.RequestVersionOrLower)
        };

        return new CompiledProxyRoute(route, prefix)
        {
            Target = target,
            Config = new ForwarderRequestConfig
            {
                ActivityTimeout = TimeSpan.FromSeconds(route.TimeoutSeconds),
                Version = version,
                VersionPolicy = policy,
                AllowResponseBuffering = false
            },
            Transformer = new ProxyTransformer(route, prefix, target, headers)
        };
    }
}
