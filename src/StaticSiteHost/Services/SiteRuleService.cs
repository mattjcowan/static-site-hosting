using StaticSiteHost.Models;
using StaticSiteHost.Serving;

namespace StaticSiteHost.Services;

/// <summary>
/// Validates and stores the rules a site owns, as opposed to the ones that arrive inside a
/// release. Shared by the site page and the API so both reject the same things and leave the
/// same trail.
/// </summary>
public sealed class SiteRuleService
{
    private readonly SiteStore _sites;
    private readonly SiteContentServer _content;
    private readonly AuditLog _audit;

    public SiteRuleService(SiteStore sites, SiteContentServer content, AuditLog audit)
    {
        _sites = sites;
        _content = content;
        _audit = audit;
    }

    public Task<(bool Ok, IReadOnlyList<string> Errors)> SetHeadersAsync(
        SiteRecord site, IReadOnlyList<HeaderRule> rules, string actor) =>
        SaveAsync(site, SiteHeaderRules.Validate(rules), () => site.Headers = [.. rules], "site.headers", rules.Count, actor);

    public Task<(bool Ok, IReadOnlyList<string> Errors)> SetRedirectsAsync(
        SiteRecord site, IReadOnlyList<RedirectRule> rules, string actor) =>
        SaveAsync(site, SiteRedirectRules.Validate(rules), () => site.Redirects = [.. rules], "site.redirects", rules.Count, actor);

    private async Task<(bool Ok, IReadOnlyList<string> Errors)> SaveAsync(
        SiteRecord site, IReadOnlyList<string> errors, Action apply, string action, int count, string actor)
    {
        if (errors.Count > 0) return (false, errors);

        apply();
        await _sites.SaveAsync(site);

        // The compiled rules are cached per domain; drop them so the next request recompiles.
        _content.Evict(site.Domain);

        await _audit.WriteAsync(action, actor, new { domain = site.Domain, rules = count });
        return (true, []);
    }
}
