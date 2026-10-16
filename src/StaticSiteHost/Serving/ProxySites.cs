using StaticSiteHost.Models;
using StaticSiteHost.Services;

namespace StaticSiteHost.Serving;

/// <summary>
/// The Sites pattern of a server-wide proxy route, and how those routes join a site's own. A
/// pattern is <c>*</c> (every site), <c>*.apps.example.com</c> (every site under that domain, at
/// any depth), or a domain (that site); several are separated by commas.
/// </summary>
public static class ProxySites
{
    /// <summary>The form a pattern is stored in: lowercase, trimmed, one comma and space between entries.</summary>
    public static string Normalize(string? sites) =>
        string.Join(", ", Split(sites).Select(entry => entry.ToLowerInvariant()));

    private static IEnumerable<string> Split(string? sites) =>
        (sites ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Why a pattern cannot be used, or null.</summary>
    public static string? Check(string? sites)
    {
        var entries = Split(sites).ToList();
        if (entries.Count == 0) return "say which sites it applies to: * for every site, or a pattern such as *.apps.example.com";

        foreach (var entry in entries)
        {
            if (entry == "*") continue;

            var domain = entry.StartsWith("*.", StringComparison.Ordinal) ? entry[2..] : entry;
            var (normalized, _) = SiteStore.NormalizeDomain(domain);
            if (normalized is null || normalized != domain.ToLowerInvariant() || domain.Contains('*'))
                return $"'{entry}' is not *, a domain, or *. followed by a domain";
        }

        return null;
    }

    /// <summary>Whether a pattern applies to a site, and how specifically: higher is more specific, null is no.</summary>
    public static int? Match(string sites, string domain)
    {
        int? best = null;
        foreach (var entry in Split(sites))
        {
            int? score = entry == "*" ? 0
                : entry.StartsWith("*.", StringComparison.Ordinal)
                    ? domain.EndsWith(entry[1..], StringComparison.OrdinalIgnoreCase) ? entry.Length : null
                    : domain.Equals(entry, StringComparison.OrdinalIgnoreCase) ? 10_000 + entry.Length : null;

            if (score is { } s && (best is null || s > best)) best = s;
        }

        return best;
    }

    /// <summary>
    /// Whether a cookie domain covers every site a pattern can match: <c>example.com</c> covers
    /// <c>*.apps.example.com</c>, and nothing but the browser's own rules covers <c>*</c>.
    /// </summary>
    public static string? CheckParentDomain(string sites, string? parent)
    {
        var (normalized, _) = SiteStore.NormalizeDomain(parent);
        if (normalized is null) return "name the parent domain cookies are moved to";
        if (!normalized.Contains('.')) return $"'{normalized}' is too broad to share cookies across";

        foreach (var entry in Split(sites))
        {
            if (entry == "*") continue; // sites the parent does not cover get host-only cookies, as after a rename
            var domain = entry.StartsWith("*.", StringComparison.Ordinal) ? entry[2..] : entry;
            if (!domain.Equals(normalized, StringComparison.OrdinalIgnoreCase) &&
                !domain.EndsWith("." + normalized, StringComparison.OrdinalIgnoreCase))
                return $"'{normalized}' does not cover the sites '{entry}' names";
        }

        return null;
    }

    /// <summary>
    /// A site's routes as requests see them: its own, then each server-wide route that is enabled,
    /// applies to it, has a path none of its own enabled routes has, and is not turned off for it.
    /// When server-wide routes share a path, the one whose pattern names the site most specifically
    /// wins. A route switched off (<see cref="ProxyRoute.Enabled"/>) takes no part, so it neither
    /// overrides nor blocks another.
    /// </summary>
    public static IReadOnlyList<ProxyRoute> Effective(
        string domain, IReadOnlyList<ProxyRoute> siteRoutes, IReadOnlyList<ServerProxyRoute> serverRoutes, IReadOnlyList<string> disabled)
    {
        if (serverRoutes.Count == 0) return siteRoutes;

        var taken = siteRoutes.Where(r => r.Enabled).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var off = disabled.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var inherited = serverRoutes
            .Where(route => route.Enabled)
            .Select(route => (Route: route, Score: Match(route.Sites, domain)))
            .Where(r => r.Score is not null && !taken.Contains(r.Route.Path) && !off.Contains(r.Route.Path))
            .GroupBy(r => r.Route.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(r => r.Score).First().Route);

        return [.. siteRoutes, .. inherited];
    }

    // What ForSite says of each route. The API returns these words as they are; the pages show
    // each in the viewer's language (Pages/Shared/ProxyStateNames.cs).
    public const string StateOn = "on";
    public const string StateOffEverywhere = "switched off for every site";
    public const string StateOverridden = "overridden by this site's own route";
    public const string StateOffHere = "off for this site";
    public const string StateCloserMatch = "another server-wide route at this path names this site more closely";

    /// <summary>
    /// The server-wide routes a site's page and the API list for it: each that applies, and
    /// whether it is in effect or why not.
    /// </summary>
    public static IEnumerable<(ServerProxyRoute Route, string State)> ForSite(
        string domain, IReadOnlyList<ProxyRoute> siteRoutes, IReadOnlyList<ServerProxyRoute> serverRoutes, IReadOnlyList<string> disabled)
    {
        var effective = Effective(domain, siteRoutes, serverRoutes, disabled).OfType<ServerProxyRoute>().ToHashSet();
        var own = siteRoutes.Where(r => r.Enabled).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var off = disabled.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var route in serverRoutes.Where(r => Match(r.Sites, domain) is not null).OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            yield return (route,
                effective.Contains(route) ? StateOn
                : !route.Enabled ? StateOffEverywhere
                : own.Contains(route.Path) ? StateOverridden
                : off.Contains(route.Path) ? StateOffHere
                : StateCloserMatch);
        }
    }
}
