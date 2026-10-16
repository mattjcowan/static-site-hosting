using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Dns;

namespace StaticSiteHost.Pages.Admin;

/// <summary>
/// DNS for the whole server: the Cloudflare API token, whether each site gets a DNS record and what
/// it points at, and each site's record. Administrators only, via the /Admin folder policy.
/// </summary>
public class DnsModel : PageModel
{
    private readonly DnsService _dns;
    private readonly SiteStore _sites;
    private readonly IStringLocalizer<Strings> _strings;

    public DnsModel(DnsService dns, SiteStore sites, IStringLocalizer<Strings> strings)
    {
        _dns = dns;
        _sites = sites;
        _strings = strings;
    }

    public DnsSettings Settings { get; private set; } = new();

    public bool Connected { get; private set; }

    /// <summary>The zones the token can see, or why they could not be listed.</summary>
    public IReadOnlyList<CloudflareZone> Zones { get; private set; } = [];

    public string? ZonesError { get; private set; }

    /// <summary>Each site's record, in domain order.</summary>
    public IReadOnlyList<SiteDnsStatus> Sites { get; private set; } = [];

    /// <summary>Why the last change on this page was refused, and which form it came from.</summary>
    public string? Error { get; private set; }

    public string? ErrorFor { get; private set; }

    /// <summary>What was typed into the records form, put back when it is refused.</summary>
    public string? Ipv4 { get; private set; }

    public string? Ipv6 { get; private set; }
    public string? Target { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct)
    {
        Settings = await _dns.SettingsAsync(ct);
        Connected = await _dns.ConnectedAsync(ct);
        Ipv4 ??= Settings.Ipv4;
        Ipv6 ??= Settings.Ipv6;
        Target ??= Settings.Target;

        if (Connected)
        {
            try
            {
                Zones = await _dns.ZonesAsync(ct);
            }
            catch (Exception ex) when (ex is CloudflareException or HttpRequestException)
            {
                ZonesError = ex.Message;
            }
        }

        var status = _dns.Status;
        Sites = _sites.List()
            .Select(s => status.TryGetValue(s.Domain, out var known) ? known : new SiteDnsStatus(s.Domain, "pending", null, null, null))
            .OrderBy(s => s.Domain, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IActionResult> OnPostTokenAsync(string? token, CancellationToken ct)
    {
        if (await _dns.SetTokenAsync(token, Actor, ct) is { } error) return await RefusedAsync("token", error, ct);

        TempData["StatusMessage"] = _strings.Text("dns.token.saved");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveTokenAsync(CancellationToken ct)
    {
        await _dns.RemoveTokenAsync(Actor, ct);
        TempData["StatusMessage"] = _strings.Text("dns.token.removed");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRecordsAsync(bool forEverySite, string? ipv4, string? ipv6, string? target, bool proxied, CancellationToken ct)
    {
        if (await _dns.SetRecordsAsync(forEverySite, ipv4, ipv6, target, proxied, Actor, ct) is { } error)
        {
            (Ipv4, Ipv6, Target) = (ipv4, ipv6, target);
            return await RefusedAsync("records", error, ct);
        }

        TempData["StatusMessage"] = _strings.Text("dns.records.saved");
        return RedirectToPage();
    }

    /// <summary>Fills the address with this server's public one, as Cloudflare sees it.</summary>
    public async Task<IActionResult> OnPostDetectAsync(CancellationToken ct)
    {
        var address = await _dns.DetectAddressAsync(ct);
        await LoadAsync(ct);
        if (address is null)
        {
            Error = _strings.Text("dns.records.detectFailed");
            ErrorFor = "records";
        }
        else if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            (Ipv6, Target) = (address.ToString(), null);
        }
        else
        {
            (Ipv4, Target) = (address.ToString(), null);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAddSiteAsync(string domain, bool? proxied, CancellationToken ct)
    {
        if (await _dns.AddSiteAsync(domain, proxied, Actor, ct) is { } error) return await RefusedAsync(domain, error, ct);

        TempData["StatusMessage"] = _strings.Text("dns.site.added", new { domain });
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveSiteAsync(string domain, CancellationToken ct)
    {
        if (await _dns.RemoveSiteAsync(domain, Actor, ct) is { } error) return await RefusedAsync(domain, error, ct);

        TempData["StatusMessage"] = _strings.Text("dns.site.removed", new { domain });
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRefreshAsync(CancellationToken ct)
    {
        await _dns.ReconcileAsync(ct);
        TempData["StatusMessage"] = _strings.Text("dns.refreshed");
        return RedirectToPage();
    }

    private async Task<IActionResult> RefusedAsync(string form, KeyedText error, CancellationToken ct)
    {
        await LoadAsync(ct);
        Error = error.Text(_strings);
        ErrorFor = form;
        return Page();
    }

    private string Actor => User.Identity?.Name ?? "unknown";
}
