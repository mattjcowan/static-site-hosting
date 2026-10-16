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
        if (string.IsNullOrWhiteSpace(target)) return (null, "a target URL is required");

        var unset = new List<string>();
        var expanded = SiteVariableService.Expand(target.Trim(), unset).Trim();
        if (unset.Count > 0)
            return (null, $"the server has no environment variable {string.Join(", ", unset)}");

        if (!Uri.TryCreate(expanded, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return (null, "the target must be an http:// or https:// URL");

        if (string.IsNullOrEmpty(uri.Host)) return (null, "the target URL has no host");
        if (uri.UserInfo.Length > 0) return (null, "the target URL may not carry a user name or password; set an Authorization request header instead");
        if (uri.Query.Length > 0) return (null, "the target URL may not have a query string");
        if (uri.Fragment.Length > 0) return (null, "the target URL may not have a fragment");

        return (uri, null);
    }

    /// <summary>Human-readable reasons a site's routes cannot be saved. Empty means they can.</summary>
    /// <param name="domain">The site the routes are for, or null for server-wide routes (<see cref="ServerProxyRoute"/>).</param>
    public static IReadOnlyList<string> Validate(string? domain, IReadOnlyList<ProxyRoute> routes)
    {
        var errors = new List<string>();

        if (routes.Count > MaxRoutes)
        {
            errors.Add(domain is null
                ? $"There can be at most {MaxRoutes} server-wide proxy routes; this is {routes.Count}."
                : $"A site can have at most {MaxRoutes} proxy routes; this is {routes.Count}.");
            return errors;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            var label = $"Route {i + 1} ({route.Path})";

            var server = route as ServerProxyRoute;
            if (server is not null && ProxySites.Check(server.Sites) is { } sitesError) errors.Add($"{label}: {sitesError}.");

            // Two server-wide routes may share a path when they are for different sites.
            if (CheckPath(route.Path) is { } pathError) errors.Add($"{label}: {pathError}.");
            else if (!seen.Add(server is null ? route.Path : $"{ProxySites.Normalize(server.Sites)}|{route.Path}"))
                errors.Add($"{label}: another route already has this path{(server is null ? "" : " for the same sites")}.");

            if (string.IsNullOrWhiteSpace(route.Target))
                errors.Add($"{label}: a target URL is required.");
            else if (route.Target.Length > MaxTargetLength)
                errors.Add($"{label}: the target must be {MaxTargetLength} characters or fewer.");
            else if (!SiteVariableService.ReferencesEnvironment(route.Target) && ResolveTarget(route.Target).Error is { } targetError)
                errors.Add($"{label}: {targetError}.");

            if (!ProxyHttpVersions.All.Contains(route.HttpVersion))
                errors.Add($"{label}: the HTTP version must be one of {string.Join(", ", ProxyHttpVersions.All)}.");

            if (route.TimeoutSeconds < 1 || route.TimeoutSeconds > MaxTimeoutSeconds)
                errors.Add($"{label}: the timeout must be between 1 and {MaxTimeoutSeconds} seconds.");

            if (route.MaxRequestBodyBytes is < 0)
                errors.Add($"{label}: the largest request body cannot be negative.");

            if (!ProxyCookieModes.All.Contains(route.CookieMode))
                errors.Add($"{label}: the cookie mode must be one of {string.Join(", ", ProxyCookieModes.All)}.");
            else if (route.CookieMode == ProxyCookieModes.Parent &&
                     (server is not null ? ProxySites.CheckParentDomain(server.Sites, route.CookieParentDomain)
                         : CheckParentDomain(domain ?? "", route.CookieParentDomain)) is { } parentError)
                errors.Add($"{label}: {parentError}.");

            var headers = route.RequestHeaders ?? [];
            if (headers.Count > MaxRequestHeaders)
                errors.Add($"{label}: sets {headers.Count} request headers, more than the {MaxRequestHeaders} allowed.");

            var headerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
            {
                if (CheckRequestHeader(header?.Name, header?.Value) is { } headerError) errors.Add($"{label}: {headerError}.");
                else if (!headerNames.Add(header!.Name)) errors.Add($"{label}: the request header {header.Name} is listed twice.");
            }
        }

        return errors;
    }

    private static string? CheckPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/')) return "the path must start with /";
        if (path.Length > MaxPathLength) return $"the path must be {MaxPathLength} characters or fewer";
        if (path.Any(c => char.IsControl(c) || c is '?' or '#' or '\\' or '%' or ' '))
            return "the path may not contain spaces, ?, #, %, \\ or control characters";
        if (path.Contains("//", StringComparison.Ordinal)) return "the path may not contain an empty segment (//)";
        if (path.Split('/').Any(segment => segment is "." or "..")) return "the path may not contain . or .. segments";

        if (path.Equals("/_host", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/_host/", StringComparison.OrdinalIgnoreCase))
            return "/_host/ belongs to the server's own endpoints";
        if (path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/healthz/", StringComparison.OrdinalIgnoreCase))
            return "/healthz belongs to the server";

        return null;
    }

    private static string? CheckParentDomain(string domain, string? parent)
    {
        var (normalized, _) = SiteStore.NormalizeDomain(parent);
        if (normalized is null) return "name the parent domain cookies are moved to";
        if (!normalized.Contains('.')) return $"'{normalized}' is too broad to share cookies across";
        if (!domain.Equals(normalized, StringComparison.OrdinalIgnoreCase) &&
            !domain.EndsWith("." + normalized, StringComparison.OrdinalIgnoreCase))
            return $"'{normalized}' is not {domain} or a domain it belongs to";
        return null;
    }

    private static string? CheckRequestHeader(string? name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name)) return "a request header needs a name";
        if (name.Length > MaxHeaderNameLength) return $"request header names must be {MaxHeaderNameLength} characters or fewer";
        if (!name.All(IsTokenChar)) return $"'{name}' is not a valid header name";
        if (ReservedRequestHeaders.Contains(name)) return $"the request header {name} is managed by the server and cannot be set";
        if ((value ?? "").Length > MaxHeaderValueLength) return $"the value of {name} must be {MaxHeaderValueLength} characters or fewer";
        if ((value ?? "").Any(c => c is '\r' or '\n' or '\0')) return $"the value of {name} may not contain line breaks";
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
                error = $"Request headers, line {lineNumber}: write Name: value, or Name: alone to remove a header.";
                return false;
            }

            headers.Add(new ProxyHeader { Name = line[..colon].Trim(), Value = line[(colon + 1)..].Trim() });
        }

        return true;
    }
}
