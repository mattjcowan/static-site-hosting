using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Pages.Sites;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages.Admin;

/// <summary>
/// Server-wide proxy routes: one route for many sites, chosen by a Sites pattern. Administrators
/// only, via the /Admin folder policy. Each form is the one a site's Proxy card uses
/// (Pages/Sites/_ProxyRoute.cshtml), with the Sites field added.
/// </summary>
public class ProxyModel : PageModel
{
    private readonly SiteProxyService _proxy;
    private readonly SiteStore _sites;
    private readonly IStringLocalizer<Strings> _strings;

    public ProxyModel(SiteProxyService proxy, SiteStore sites, IStringLocalizer<Strings> strings)
    {
        _proxy = proxy;
        _sites = sites;
        _strings = strings;
    }

    /// <summary>One form per route, in path order, then the one for adding.</summary>
    public IReadOnlyList<ProxyRouteEditor> Editors { get; private set; } = [];

    /// <summary>For each route, by id, the sites it applies to now, as the page lists them under the route.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(string Domain, string State)>> Reach { get; private set; } =
        new Dictionary<string, IReadOnlyList<(string, string)>>();

    public async Task OnGetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        var routes = await _proxy.ServerRoutesAsync();
        var testedId = TempData["ProxyTestId"] as string;
        (string, bool)? test = TempData["ProxyTestResult"] is string message
            ? (message, TempData["ProxyTestAnswered"] as bool? == true)
            : null;

        Editors =
        [
            .. routes.Select((route, i) => new ProxyRouteEditor(
                $"server-{i}", null, route, route.Id, ProxyRouteRules.FormatHeaders(route.RequestHeaders), [],
                route.Id == testedId ? test : null)),
            new ProxyRouteEditor("server-new", null, new ServerProxyRoute { Sites = "" }, null, "", [])
        ];

        var sites = _sites.List();
        Reach = routes.ToDictionary(
            route => route.Id,
            route => (IReadOnlyList<(string, string)>)sites
                .Select(site => (site.Domain, State: ProxySites.ForSite(site.Domain, site.Proxy, routes, site.DisabledServerRoutes)
                    .FirstOrDefault(r => r.Route.Id == route.Id).State))
                .Where(s => s.State is not null)
                .ToList());
    }

    public async Task<IActionResult> OnPostSaveProxyRouteAsync(string? routeId, ProxyRouteInput input)
    {
        ProxyRouteRules.TryParseHeaders(input.RequestHeaders, out var headers, out var headerError);

        IReadOnlyList<string> errors;
        if (headerError is not null) errors = [headerError];
        else if (input.BodyLimitError is { } bodyLimitError) errors = [bodyLimitError];
        else
        {
            var (ok, failures, saved) = await _proxy.SaveServerRouteAsync(input.ToServerRoute(headers, routeId), User.Identity?.Name ?? "unknown");
            if (ok)
            {
                TempData["StatusMessage"] = routeId is null
                    ? _strings.Text("proxy.route.added", new { path = saved!.Path, sites = saved.Sites, target = saved.Target })
                    : _strings.Text("proxy.route.saved", new { path = saved!.Path, sites = saved.Sites });
                return RedirectToPage();
            }

            errors = failures;
        }

        // Rendered in place, so what was typed comes back with the errors.
        await LoadAsync();
        var draft = input.ToServerRoute(headers, routeId);
        Editors = Editors
            .Select(editor => editor.OriginalPath == routeId
                ? editor with { Route = draft, HeadersText = input.RequestHeaders ?? "", BodyLimitText = input.MaxRequestBodyMegabytes, Errors = errors }
                : editor)
            .ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveProxyRouteAsync(string? id)
    {
        if (await _proxy.RemoveServerRouteAsync(id, User.Identity?.Name ?? "unknown"))
            TempData["StatusMessage"] = _strings.Text("proxy.route.removed");
        else
            TempData["ErrorMessage"] = _strings.Text("proxy.route.notFound");

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestProxyRouteAsync(string? id, CancellationToken ct)
    {
        var route = (await _proxy.ServerRoutesAsync()).FirstOrDefault(r => r.Id == id);
        if (route is null)
        {
            TempData["ErrorMessage"] = _strings.Text("proxy.route.notFound");
            return RedirectToPage();
        }

        var result = await _proxy.TestAsync(route, ct);
        TempData["ProxyTestId"] = route.Id;
        TempData["ProxyTestResult"] = result.Describe();
        TempData["ProxyTestAnswered"] = result.Answered;
        return RedirectToPage();
    }
}
