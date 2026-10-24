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
    private readonly StaticSiteHost.Tls.CertificateManager _certificates;
    private readonly IStringLocalizer<Strings> _strings;

    public DnsModel(DnsService dns, SiteStore sites, StaticSiteHost.Tls.CertificateManager certificates, IStringLocalizer<Strings> strings)
    {
        _dns = dns;
        _sites = sites;
        _certificates = certificates;
        _strings = strings;
    }

    public DnsSettings Settings { get; private set; } = new();

    public bool Connected { get; private set; }

    /// <summary>Whether this server ends HTTPS itself, which changes what Cloudflare's SSL mode should be.</summary>
    public bool HttpsInServer => _certificates.Enabled;

    /// <summary>Cloudflare's "Create token" form with the permissions and a name for this server filled in.</summary>
    public string CreateTokenUrl => CloudflareApi.CreateTokenUrl($"Site server at {Request.Host.Host}");

    /// <summary>The zones the token can see, or why they could not be listed.</summary>
    public IReadOnlyList<CloudflareZone> Zones { get; private set; } = [];

    public string? ZonesError { get; private set; }

    /// <summary>Each site's record, in domain order.</summary>
    public IReadOnlyList<SiteDnsStatus> Sites { get; private set; } = [];

    /// <summary>For each row that is one of a site's other domains, the site's domain. Their records follow the site's.</summary>
    public IReadOnlyDictionary<string, string> AliasOwners { get; private set; } = new Dictionary<string, string>();

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

        // Each site, then its other domains, whose records follow the site's.
        var status = _dns.Status;
        SiteDnsStatus StatusOf(string name) => status.TryGetValue(name, out var known) ? known : new SiteDnsStatus(name, "pending", null, null, null);
        var rows = new List<SiteDnsStatus>();
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in _sites.List())
        {
            rows.Add(StatusOf(site.Domain));
            foreach (var alias in site.Aliases)
            {
                if (!_sites.Exists(alias) && owners.TryAdd(alias, site.Domain)) rows.Add(StatusOf(alias));
            }
        }

        Sites = rows;
        AliasOwners = owners;
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
