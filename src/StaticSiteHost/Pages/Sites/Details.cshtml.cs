using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages.Sites;

public class DetailsModel : PageModel
{
    private readonly SiteStore _sites;
    private readonly ZipDeploymentService _deployer;
    private readonly SiteContentServer _content;
    private readonly SitePasscodeGate _passcodes;
    private readonly AuditLog _audit;

    public DetailsModel(
        SiteStore sites,
        ZipDeploymentService deployer,
        SiteContentServer content,
        SitePasscodeGate passcodes,
        AuditLog audit)
    {
        _sites = sites;
        _deployer = deployer;
        _content = content;
        _passcodes = passcodes;
        _audit = audit;
    }

    public SiteRecord Site { get; private set; } = null!;

    public bool IsAdministrator => User.IsInRole(Roles.Administrator);

    public int MinPasscodeLength => _passcodes.MinLength;

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

    public IActionResult OnGet(string domain)
    {
        var site = Resolve(domain);
        if (site is null) return RedirectToPage("Index");

        Site = site;
        return Page();
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
