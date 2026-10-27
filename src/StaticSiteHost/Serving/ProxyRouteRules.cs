using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Services;

namespace StaticSiteHost.Serving;

/// <summary>
/// What a proxy route may say, and how its target is read. Shared by the site page, the API and
/// <see cref="SiteProxy"/>, so all three accept the same routes and read targets the same way.
/// </summary>
public static class ProxyRouteRules
{
    public const int MaxRoutes = 50;
    public const int MaxPathLength = 512;
    public const int MaxTargetLength = 2048;
    public const int MaxRequestHeaders = 25;
    public const int MaxHeaderNameLength = 64;
    public const int MaxHeaderValueLength = 2048;
    public const int MaxTimeoutSeconds = 24 * 60 * 60;

    /// <summary>
    /// Request headers a route may not set or remove. The transport's own, and the ones the proxy
    /// manages itself: Cookie (the server's cookies are taken out of it), the forwarded headers and
    /// the hop count.
    /// </summary>
    private static readonly HashSet<string> ReservedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Proxy-Connection", "Transfer-Encoding", "TE", "Trailer",
        "Upgrade", "Expect", "Content-Length", "Cookie",
        "X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Forwarded-Prefix", "Forwarded",
        SiteProxy.HopsHeader
    };

    /// <summary>
    /// The form a path is stored in: trimmed, with a leading slash and no trailing one, except
    /// for <c>/</c> itself. Text that is no path at all comes back as it was, for
    /// <see cref="Validate"/> to refuse.
    /// </summary>
    public static string NormalizePath(string? path)
    {
        var value = (path ?? "").Trim();
        if (value.Length == 0) return "/";
        if (!value.StartsWith('/')) value = "/" + value;

        value = value.TrimEnd('/');
        return value.Length == 0 ? "/" : value;
    }

    /// <summary>
    /// Reads a target: <c>${env:…}</c> expanded, then an absolute <c>http</c> or <c>https</c> URL
    /// with a host and no user name, password, query or fragment. Returns the URL, or why not.
    /// </summary>
    public static (Uri? Target, string? Error) ResolveTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return (null, ServerText.Get("proxy.error.targetRequired"));

        var unset = new List<string>();
        var expanded = SiteVariableService.Expand(target.Trim(), unset).Trim();
        if (unset.Count > 0)
            return (null, ServerText.Get("proxy.error.targetUnset", new { names = string.Join(", ", unset) }));

        if (!Uri.TryCreate(expanded, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return (null, ServerText.Get("proxy.error.targetScheme"));

        if (string.IsNullOrEmpty(uri.Host)) return (null, ServerText.Get("proxy.error.targetNoHost"));
        if (uri.UserInfo.Length > 0) return (null, ServerText.Get("proxy.error.targetUserInfo"));
        if (uri.Query.Length > 0) return (null, ServerText.Get("proxy.error.targetQuery"));
        if (uri.Fragment.Length > 0) return (null, ServerText.Get("proxy.error.targetFragment"));

        return (uri, null);
    }

    /// <summary>Human-readable reasons a site's routes cannot be saved. Empty means they can.</summary>
    /// <param name="domain">The site the routes are for, or null for server-wide routes (<see cref="ServerProxyRoute"/>).</param>
    /// <param name="aliases">The site's other domains, which a parent cookie domain may cover instead of its domain.</param>
    public static IReadOnlyList<string> Validate(string? domain, IReadOnlyList<ProxyRoute> routes, IReadOnlyList<string>? aliases = null)
    {
        var errors = new List<string>();

        if (routes.Count > MaxRoutes)
        {
            errors.Add(ServerText.Get(domain is null ? "proxy.error.tooManyServer" : "proxy.error.tooManySite", new { max = MaxRoutes, count = routes.Count }));
            return errors;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            var label = ServerText.Get("proxy.error.routeLabel", new { number = i + 1, path = route.Path });

            var server = route as ServerProxyRoute;
            if (server is not null && ProxySites.Check(server.Sites) is { } sitesError) errors.Add(Problem(label, sitesError));

            // Two server-wide routes may share a path when they are for different sites.
            if (CheckPath(route.Path) is { } pathError) errors.Add(Problem(label, pathError));
            else if (!seen.Add(server is null ? route.Path : $"{ProxySites.Normalize(server.Sites)}|{route.Path}"))
                errors.Add(Problem(label, ServerText.Get(server is null ? "proxy.error.samePath" : "proxy.error.samePathSameSites")));

            if (string.IsNullOrWhiteSpace(route.Target))
                errors.Add(Problem(label, ServerText.Get("proxy.error.targetRequired")));
            else if (route.Target.Length > MaxTargetLength)
                errors.Add(Problem(label, ServerText.Get("proxy.error.targetLong", new { max = MaxTargetLength })));
            else if (!SiteVariableService.ReferencesEnvironment(route.Target) && ResolveTarget(route.Target).Error is { } targetError)
                errors.Add(Problem(label, targetError));

            if (!ProxyHttpVersions.All.Contains(route.HttpVersion))
                errors.Add(Problem(label, ServerText.Get("proxy.error.httpVersion", new { versions = string.Join(", ", ProxyHttpVersions.All) })));
            else if (route.GrpcWeb && route.HttpVersion == ProxyHttpVersions.Http11)
                errors.Add(Problem(label, ServerText.Get("proxy.error.grpcWebHttp11")));

            if (route.TimeoutSeconds < 1 || route.TimeoutSeconds > MaxTimeoutSeconds)
                errors.Add(Problem(label, ServerText.Get("proxy.error.timeout", new { max = MaxTimeoutSeconds })));

            if (route.MaxRequestBodyBytes is < 0)
                errors.Add(Problem(label, ServerText.Get("proxy.error.bodyNegative")));

            if (!ProxyCookieModes.All.Contains(route.CookieMode))
                errors.Add(Problem(label, ServerText.Get("proxy.error.cookieMode", new { modes = string.Join(", ", ProxyCookieModes.All) })));
            else if (route.CookieMode == ProxyCookieModes.Parent &&
                     (server is not null ? ProxySites.CheckParentDomain(server.Sites, route.CookieParentDomain)
                         : CheckParentDomain(domain ?? "", aliases ?? [], route.CookieParentDomain)) is { } parentError)
                errors.Add(Problem(label, parentError));

            var headers = route.RequestHeaders ?? [];
            if (headers.Count > MaxRequestHeaders)
                errors.Add(Problem(label, ServerText.Get("proxy.error.tooManyHeaders", new { count = headers.Count, max = MaxRequestHeaders })));

            var headerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
            {
                if (CheckRequestHeader(header?.Name, header?.Value) is { } headerError) errors.Add(Problem(label, headerError));
                else if (!headerNames.Add(header!.Name)) errors.Add(Problem(label, ServerText.Get("proxy.error.headerTwice", new { name = header.Name })));
            }
        }

        return errors;
    }

    /// <summary>One reason a route cannot be saved, after the route it is about.</summary>
    private static string Problem(string label, string problem) => ServerText.Get("proxy.error.route", new { label, problem });

    private static string? CheckPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/')) return ServerText.Get("proxy.error.pathSlash");
        if (path.Length > MaxPathLength) return ServerText.Get("proxy.error.pathLong", new { max = MaxPathLength });
        if (path.Any(c => char.IsControl(c) || c is '?' or '#' or '\\' or '%' or ' '))
            return ServerText.Get("proxy.error.pathCharacters");
        if (path.Contains("//", StringComparison.Ordinal)) return ServerText.Get("proxy.error.pathEmptySegment");
        if (path.Split('/').Any(segment => segment is "." or "..")) return ServerText.Get("proxy.error.pathDots");

        if (path.Equals("/_host", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/_host/", StringComparison.OrdinalIgnoreCase))
            return ServerText.Get("proxy.error.pathHost");
        if (path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/healthz/", StringComparison.OrdinalIgnoreCase))
            return ServerText.Get("proxy.error.pathHealthz");

        return null;
    }

    /// <summary>
    /// Whether a cookie domain covers the site's domain or one of its other domains: a wildcard's names
    /// are covered by the domain it sits on and the domains that one belongs to. At a name the parent does
    /// not cover, the route's cookies stay host-only (see <see cref="ProxyTransformer.RewriteCookies"/>).
    /// </summary>
    private static string? CheckParentDomain(string domain, IReadOnlyList<string> aliases, string? parent)
    {
        var (normalized, _) = SiteStore.NormalizeDomain(parent);
        if (normalized is null) return ServerText.Get("proxy.error.parentRequired");
        if (!normalized.Contains('.')) return ServerText.Get("proxy.error.parentTooBroad", new { parent = normalized });

        var names = aliases.Select(alias => SiteAliases.IsWildcard(alias) ? alias[2..] : alias).Prepend(domain);
        if (!names.Any(name => name.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                               name.EndsWith("." + normalized, StringComparison.OrdinalIgnoreCase)))
            return aliases.Count == 0
                ? ServerText.Get("proxy.error.parentNotCovering", new { parent = normalized, domain })
                : ServerText.Get("proxy.error.parentNotCoveringAliases", new { parent = normalized, domain });
        return null;
    }

    private static string? CheckRequestHeader(string? name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name)) return ServerText.Get("proxy.error.headerNoName");
        if (name.Length > MaxHeaderNameLength) return ServerText.Get("proxy.error.headerNameLong", new { max = MaxHeaderNameLength });
        if (!name.All(IsTokenChar)) return ServerText.Get("proxy.error.headerNameInvalid", new { name });
        if (ReservedRequestHeaders.Contains(name)) return ServerText.Get("proxy.error.headerReserved", new { name });
        if ((value ?? "").Length > MaxHeaderValueLength) return ServerText.Get("proxy.error.headerValueLong", new { name, max = MaxHeaderValueLength });
        if ((value ?? "").Any(c => c is '\r' or '\n' or '\0')) return ServerText.Get("proxy.error.headerValueLineBreak", new { name });
        return null;
    }

    private static bool IsTokenChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' ||
        "!#$%&'*+-.^_`|~".Contains(c);

    // ---- request headers as text, for the site page -------------------------------------

    /// <summary>One header per line, <c>Name: value</c>; <c>Name:</c> alone removes it.</summary>
    public static string FormatHeaders(IEnumerable<ProxyHeader>? headers) =>
        string.Join("\n", (headers ?? []).Select(h => h.Value.Length == 0 ? $"{h.Name}:" : $"{h.Name}: {h.Value}"));

    public static bool TryParseHeaders(string? text, out List<ProxyHeader> headers, out string? error)
    {
        headers = [];
        error = null;

        var lineNumber = 0;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                error = ServerText.Get("proxy.error.headersLine", new { line = lineNumber });
                return false;
            }

            headers.Add(new ProxyHeader { Name = line[..colon].Trim(), Value = line[(colon + 1)..].Trim() });
        }

        return true;
    }
}
