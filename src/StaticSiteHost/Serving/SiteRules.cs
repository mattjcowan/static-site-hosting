using StaticSiteHost.Models;

namespace StaticSiteHost.Serving;

/// <summary>
/// Everything a site's rules compile down to, cached as one object per domain and dropped
/// by <see cref="SiteContentServer.Evict"/>.
///
/// A site's own rules take precedence over the ones that arrived with the release in both
/// lists, which the two orderings below say in the two different ways the lists are read:
/// header rules all apply and the last one to name a header wins, while the first redirect
/// to match answers the request.
/// </summary>
public sealed class SiteRules
{
    public static readonly SiteRules None = new(SiteHeaderRules.None, SiteRedirectRules.None);

    private SiteRules(SiteHeaderRules headers, SiteRedirectRules redirects)
    {
        Headers = headers;
        Redirects = redirects;
    }

    public SiteHeaderRules Headers { get; }

    public SiteRedirectRules Redirects { get; }

    public static SiteRules Compile(SiteRecord site) => new(
        SiteHeaderRules.Compile(site.Current?.Headers, site.Headers),
        SiteRedirectRules.Compile(site.Redirects, site.Current?.Redirects));
}
