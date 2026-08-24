using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages.Sites;

/// <summary>
/// A rule a person can drop straight into an editor on the site page. The point is that
/// nobody has to work the syntax out from a description before their first rule.
/// </summary>
public sealed record RuleExample(string Label, string Hint, string Snippet);

/// <summary>A ready-to-run command shown on a site's detail page.</summary>
public sealed record DeploySnippet(string Id, string Title, string Hint, string Command)
{
    public int Lines => Command.Count(c => c == '\n') + 1;
}

public class DetailsModel : PageModel
{
    private readonly SiteStore _sites;
    private readonly ZipDeploymentService _deployer;
    private readonly SiteContentServer _content;
    private readonly SitePasscodeGate _passcodes;
    private readonly ApiKeyStore _apiKeys;
    private readonly SiteRuleService _rules;
    private readonly AuditLog _audit;
    private readonly SiteHostingOptions _options;

    public DetailsModel(
        SiteStore sites,
        ZipDeploymentService deployer,
        SiteContentServer content,
        SitePasscodeGate passcodes,
        ApiKeyStore apiKeys,
        SiteRuleService rules,
        AuditLog audit,
        IOptions<SiteHostingOptions> options)
    {
        _sites = sites;
        _deployer = deployer;
        _content = content;
        _passcodes = passcodes;
        _apiKeys = apiKeys;
        _rules = rules;
        _audit = audit;
        _options = options.Value;
    }

    public SiteRecord Site { get; private set; } = null!;

    public bool IsAdministrator => User.IsInRole(Roles.Administrator);

    public int MinPasscodeLength => _passcodes.MinLength;

    /// <summary>The default asset max-age, quoted in the header rule editor.</summary>
    public int AssetCacheSeconds => _options.AssetCacheSeconds;

    /// <summary>How long an unlocked visitor stays unlocked, in words.</summary>
    public string PasscodeSession
    {
        get
        {
            var lifetime = _passcodes.SessionLifetime;
            return lifetime.TotalHours < 48
                ? $"{Math.Round(lifetime.TotalHours):0} hour(s)"
                : $"{Math.Round(lifetime.TotalDays):0} day(s)";
        }
    }

    /// <summary>Commands for this domain, already filled in. See <see cref="BuildDeploySnippets"/>.</summary>
    public IReadOnlyList<DeploySnippet> DeploySnippets { get; private set; } = [];

    /// <summary>False when the snippets would fail for want of a key the user has not made yet.</summary>
    public bool HasUsableApiKey { get; private set; }

    /// <summary>The site's own rules, in the text form each editor shows.</summary>
    public string HeaderRules { get; private set; } = "";

    public string RedirectRules { get; private set; } = "";

    /// <summary>Rules that came in with the current release, shown but not editable here.</summary>
    public IReadOnlyList<HeaderRule> ReleaseHeaderRules => Site.Current?.Headers ?? [];

    public IReadOnlyList<RedirectRule> ReleaseRedirectRules => Site.Current?.Redirects ?? [];

    public IReadOnlyList<string> HeaderRuleErrors { get; private set; } = [];

    public IReadOnlyList<string> RedirectRuleErrors { get; private set; } = [];

    /// <summary>Starting points offered under the header rule editor.</summary>
    public static IReadOnlyList<RuleExample> HeaderExamples { get; } =
    [
        new("Stop caching JSON",
            "For data files rewritten in place on every deploy — the browser revalidates instead of reusing a stale copy.",
            "/*.json\n  Cache-Control: no-cache"),

        new("Cache fingerprinted assets forever",
            "Safe only when the filename changes with the content, as app.a1b2c3.js does.",
            "/assets/**\n  Cache-Control: public, max-age=31536000, immutable"),

        new("Never store a folder",
            "Stronger than no-cache: nothing is written to disk by the browser or any proxy.",
            "/private/**\n  Cache-Control: no-store"),

        new("Common security headers",
            "Sensible defaults for a site that embeds nothing and is embedded nowhere.",
            "/**\n  X-Frame-Options: DENY\n  Referrer-Policy: strict-origin-when-cross-origin\n  Permissions-Policy: geolocation=(), camera=(), microphone=()"),

        new("Allow cross-origin reads",
            "Lets another origin fetch these files — fonts and data a second site loads.",
            "/data/**\n  Access-Control-Allow-Origin: *"),

        new("Short cache on a feed",
            "A middle ground for something regenerated often but read constantly.",
            "/feed.xml\n  Cache-Control: public, max-age=300")
    ];

    /// <summary>Starting points offered under the redirect editor.</summary>
    public static IReadOnlyList<RuleExample> RedirectExamples { get; } =
    [
        new("A page moved",
            "The plainest rule there is. 301 tells browsers and search engines the move is permanent.",
            "/old-page  /new-page  301"),

        new("A whole section moved",
            ":1 stands for whatever the * matched, so /blog/hello lands on /articles/hello.",
            "/blog/*  /articles/:1  301"),

        new("Send visitors to another site",
            "** crosses slashes, so the rest of the path comes along. 302 keeps it temporary.",
            "/docs/**  https://docs.example.com/:1  302"),

        new("Serve one page for a whole subtree",
            "A rewrite: the address stays put and the file underneath changes. For a SPA mounted at /app.",
            "/app/**  /app/index.html  200"),

        new("Retire a page",
            "Answers 404 with your own page rather than pretending the URL still works.",
            "/removed/**  /404.html  404"),

        new("Apply even where a file exists",
            "The ! overrides the usual behaviour of standing aside for real content.",
            "/legacy/*  /new/:1  301!")
    ];

    public async Task<IActionResult> OnGetAsync(string domain)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        await LoadAsync(site);
        return Page();
    }

    private async Task LoadAsync(SiteRecord site)
    {
        Site = site;
        DeploySnippets = BuildDeploySnippets(site.Domain);
        HeaderRules = HeaderRuleText.Format(site.Headers);
        RedirectRules = RedirectRuleText.Format(site.Redirects);

        var keys = await _apiKeys.ListForUserAsync(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "");
        HasUsableApiKey = keys.Any(k => k.IsUsable);
    }

    /// <summary>
    /// The two deploy calls from the README, pointed at this server and this domain so they
    /// can go straight into a terminal or a CI job without being edited first. The key stays
    /// a <c>$SSH_KEY</c> reference — it is never rendered into the page.
    /// </summary>
    private IReadOnlyList<DeploySnippet> BuildDeploySnippets(string domain)
    {
        var endpoint = $"{Request.Scheme}://{Request.Host}/api/v1/sites/{domain}/deploy";

        return
        [
            new DeploySnippet(
                "deploy-zip",
                "Upload a zip you already have",
                "Point file=@ at your archive. The zip's contents land at the root of the site; a single wrapping folder is unwrapped for you.",
                $"""
                 curl -sS --fail-with-body -X POST \
                   -H "X-Api-Key: $SSH_KEY" \
                   -F "file=@site.zip" \
                   "{endpoint}"
                 """),

            new DeploySnippet(
                "deploy-folder",
                "Zip a folder and upload it in one command",
                "Streams the archive as it is built, so nothing is written to disk on your side. Change dist to your build output.",
                $"""
                 (cd dist && zip -qr - .) | curl -sS --fail-with-body -X POST \
                   -H "X-Api-Key: $SSH_KEY" \
                   -H "Content-Type: application/zip" \
                   -H "X-Archive-Name: dist.zip" \
                   --data-binary @- \
                   "{endpoint}"
                 """)
        ];
    }

    public async Task<IActionResult> OnPostRollbackAsync(string domain, string releaseId)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        var actor = User.Identity?.Name ?? "unknown";
        var (ok, error) = await _deployer.RollbackAsync(site.Domain, releaseId, actor);

        if (ok) TempData["StatusMessage"] = $"{site.Domain} is now serving release {releaseId}.";
        else TempData["ErrorMessage"] = error;

        return RedirectToPage(new { domain = site.Domain });
    }

    public async Task<IActionResult> OnPostSetPasscodeAsync(string domain, string? passcode)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        var replacing = site.IsPasscodeProtected;
        var (ok, error) = await _passcodes.SetAsync(site, passcode, User.Identity?.Name ?? "unknown");

        if (!ok) TempData["ErrorMessage"] = error;
        else if (replacing) TempData["StatusMessage"] = $"The passcode for {site.Domain} was replaced. Anyone using the old one has to enter the new one.";
        else TempData["StatusMessage"] = $"{site.Domain} is now private — visitors must enter the passcode.";

        return RedirectToPage(new { domain = site.Domain });
    }

    public async Task<IActionResult> OnPostRemovePasscodeAsync(string domain)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        if (await _passcodes.ClearAsync(site, User.Identity?.Name ?? "unknown"))
            TempData["StatusMessage"] = $"{site.Domain} is public again — anyone with the link can read it.";

        return RedirectToPage(new { domain = site.Domain });
    }

    public async Task<IActionResult> OnPostHeadersAsync(string domain, string? rules)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        // On a rejection the page is re-rendered rather than redirected to, so whatever was
        // typed comes back with the errors instead of being thrown away.
        if (!HeaderRuleText.TryParse(rules, out var parsed, out var errors))
            return await RejectAsync(site, headers: rules, redirects: null, errors, []);

        var (ok, failures) = await _rules.SetHeadersAsync(site, parsed, User.Identity?.Name ?? "unknown");
        if (!ok) return await RejectAsync(site, headers: rules, redirects: null, failures, []);

        TempData["StatusMessage"] = Saved(parsed.Count, "header rule", site.Domain);
        return RedirectToPage(new { domain = site.Domain });
    }

    public async Task<IActionResult> OnPostRedirectsAsync(string domain, string? rules)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        if (!RedirectRuleText.TryParse(rules, out var parsed, out var errors))
            return await RejectAsync(site, headers: null, redirects: rules, [], errors);

        var (ok, failures) = await _rules.SetRedirectsAsync(site, parsed, User.Identity?.Name ?? "unknown");
        if (!ok) return await RejectAsync(site, headers: null, redirects: rules, [], failures);

        TempData["StatusMessage"] = Saved(parsed.Count, "redirect", site.Domain);
        return RedirectToPage(new { domain = site.Domain });
    }

    private static string Saved(int count, string noun, string domain) => count == 0
        ? $"The {noun}s for {domain} were removed."
        : $"{count} {noun}{(count == 1 ? "" : "s")} saved for {domain}.";

    /// <summary>Re-renders the page with the text that was rejected still in its box.</summary>
    private async Task<IActionResult> RejectAsync(
        SiteRecord site,
        string? headers,
        string? redirects,
        IReadOnlyList<string> headerErrors,
        IReadOnlyList<string> redirectErrors)
    {
        await LoadAsync(site);

        if (headers is not null) HeaderRules = headers;
        if (redirects is not null) RedirectRules = redirects;

        HeaderRuleErrors = headerErrors;
        RedirectRuleErrors = redirectErrors;
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string domain)
    {
        if (!IsAdministrator) return Forbid();

        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        await _sites.DeleteAsync(site.Domain);
        _content.Evict(site.Domain);
        await _audit.WriteAsync("site.delete", User.Identity?.Name, new { domain = site.Domain });

        TempData["StatusMessage"] = $"{site.Domain} and all of its content were removed.";
        return RedirectToPage("Index");
    }

    private SiteRecord? Resolve(string domain)
    {
        var (normalized, _) = SiteStore.NormalizeDomain(domain);
        if (normalized is null)
        {
            TempData["ErrorMessage"] = "That is not a valid domain name.";
            return null;
        }

        var site = _sites.TryGet(normalized);
        if (site is null) TempData["ErrorMessage"] = $"No site is published at '{normalized}'.";
        return site;
    }
}
