using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StaticSiteHost.Models;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages.Sites;

public class IndexModel : PageModel
{
    private readonly SiteStore _sites;
    private readonly ZipDeploymentService _deployer;

    public IndexModel(SiteStore sites, ZipDeploymentService deployer)
    {
        _sites = sites;
        _deployer = deployer;
    }

    public IReadOnlyList<SiteRecord> Sites { get; private set; } = [];

    public bool IsAdministrator => User.IsInRole(Roles.Administrator);

    public void OnGet() => Sites = _sites.List();

    /// <summary>
    /// Creates a site with no files, for one made of proxy routes or to set up before the first
    /// deploy. Administrators land on the Proxy card, where such a site is usually set up.
    /// </summary>
    public async Task<IActionResult> OnPostCreateAsync(string? domain)
    {
        var (ok, error, created, _) = await _deployer.CreateAsync(domain, User.Identity?.Name ?? "unknown");
        if (!ok)
        {
            TempData["ErrorMessage"] = error;
            return RedirectToPage();
        }

        TempData["StatusMessage"] = IsAdministrator
            ? $"{created} was created with no files. Add proxy routes below, or deploy a zip to it."
            : $"{created} was created with no files. Deploy a zip to it, or ask an administrator to add proxy routes.";
        return RedirectToPage("Details", null, new { domain = created }, IsAdministrator ? "proxy" : null);
    }
}
