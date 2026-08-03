using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StaticSiteHost.Models;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages.Sites;

public class IndexModel : PageModel
{
    private readonly SiteStore _sites;

    public IndexModel(SiteStore sites) => _sites = sites;

    public IReadOnlyList<SiteRecord> Sites { get; private set; } = [];

    public void OnGet() => Sites = _sites.List();
}
