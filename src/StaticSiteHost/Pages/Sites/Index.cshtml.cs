using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages.Sites;

public class IndexModel : PageModel
{
    private readonly SiteStore _sites;
    private readonly ZipDeploymentService _deployer;
    private readonly IStringLocalizer<Strings> _strings;

    public IndexModel(SiteStore sites, ZipDeploymentService deployer, IStringLocalizer<Strings> strings)
    {
        _sites = sites;
        _deployer = deployer;
        _strings = strings;
    }

    public IReadOnlyList<SiteRecord> Sites { get; private set; } = [];

    public bool IsAdministrator => User.IsInRole(Roles.Administrator);

    public void OnGet() => Sites = _sites.List();

    /// <summary>
    /// The sites and the site page's tabs, for the Go to… palette (Ctrl K). Fetched once, when the
    /// palette first opens, so a server with many sites does not put them all in every page.
    /// </summary>
    public IActionResult OnGetPalette() => new JsonResult(new
    {
        sites = _sites.List().Select(site => new { domain = site.Domain, url = Url.Page("/Sites/Details", new { domain = site.Domain }) }),
        tabs = SiteTabs.For(IsAdministrator).Where(tab => tab.Id != SiteTabs.Default).Select(tab => new { id = tab.Id, label = _strings.Text(tab.LabelKey) })
    });

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

        TempData["StatusMessage"] = _strings.Text(IsAdministrator ? "sites.new.createdAdmin" : "sites.new.createdMember", new { domain = created });
        return RedirectToPage("Details", null, new { domain = created, tab = IsAdministrator ? "routing" : null }, IsAdministrator ? "proxy" : null);
    }
}
