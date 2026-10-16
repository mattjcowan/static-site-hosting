using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Localization;
using StaticSiteHost.Services.Dns;
using StaticSiteHost.Tls;

namespace StaticSiteHost.Pages.Admin;

/// <summary>
/// HTTPS for the whole server: whether it is ended here, every host's certificate, and the shared
/// certificates that cover many hosts. Administrators only, via the /Admin folder policy. Each
/// site's own choices are on its page.
/// </summary>
public class HttpsModel : PageModel
{
    private readonly CertificateManager _certificates;
    private readonly DnsService _dns;
    private readonly IStringLocalizer<Strings> _strings;

    public HttpsModel(CertificateManager certificates, DnsService dns, IOptions<SiteHostingOptions> options, IStringLocalizer<Strings> strings)
    {
        _certificates = certificates;
        _dns = dns;
        _strings = strings;
        Tls = options.Value.Tls;
    }

    public TlsOptions Tls { get; }

    public bool Enabled => _certificates.Enabled;

    public IReadOnlyList<HostCertificateStatus> Hosts { get; private set; } = [];

    /// <summary>Every shared certificate, including ones asked for and not got yet.</summary>
    public IReadOnlyList<(SharedCertificateInfo Info, ServedCertificate? Certificate, bool Busy)> Shared { get; private set; } = [];

    /// <summary>Whether Cloudflare is connected (DNS page), so certificates can be got through it.</summary>
    public bool CloudflareConnected { get; private set; }

    /// <summary>Why the last shared upload was refused.</summary>
    public string? UploadError { get; private set; }

    /// <summary>Why the last request for a shared certificate was refused, and what was typed.</summary>
    public string? RequestError { get; private set; }

    public string? RequestNames { get; private set; }
    public string? RequestSource { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct)
    {
        Hosts = _certificates.Enabled ? _certificates.Status() : [];
        Shared = _certificates.SharedAll();
        CloudflareConnected = await _dns.ConnectedAsync(ct);
    }

    public IActionResult OnPostRenew(string? host)
    {
        var status = Enabled ? _certificates.Status().FirstOrDefault(s => s.Host == host) : null;
        if (status is null)
        {
            TempData["ErrorMessage"] = _strings.Text("https.hosts.unknown");
            return RedirectToPage();
        }

        _certificates.RenewNow(status.Host);
        TempData["StatusMessage"] = _strings.Text("https.hosts.renewAsked", new { host = status.Host });
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddSharedAsync(string? certificate, string? privateKey, CancellationToken ct)
    {
        var (info, error) = await _certificates.AddSharedAsync(certificate, privateKey, User.Identity?.Name ?? "unknown");
        if (info is null)
        {
            await LoadAsync(ct);
            UploadError = error;
            return Page();
        }

        TempData["StatusMessage"] = _strings.Text("https.shared.saved", new { names = string.Join(", ", info.Names) });
        return RedirectToPage();
    }

    /// <summary>Asks for a shared certificate got and renewed here, such as a wildcard.</summary>
    public async Task<IActionResult> OnPostRequestSharedAsync(string? names, string? source, CancellationToken ct)
    {
        var (info, error) = await _certificates.RequestSharedAsync(names, source, User.Identity?.Name ?? "unknown", ct);
        if (info is null)
        {
            await LoadAsync(ct);
            RequestError = error!.Text(_strings);
            (RequestNames, RequestSource) = (names, source);
            return Page();
        }

        TempData["StatusMessage"] = _strings.Text("https.request.asked", new { names = string.Join(", ", info.Requested) });
        return RedirectToPage();
    }

    public IActionResult OnPostRenewShared(string? id)
    {
        if (_certificates.RenewSharedNow(id ?? "")) TempData["StatusMessage"] = _strings.Text("https.shared.renewAsked");
        else TempData["ErrorMessage"] = _strings.Text("https.shared.notFound");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveSharedAsync(string? id)
    {
        if (await _certificates.RemoveSharedAsync(id ?? "", User.Identity?.Name ?? "unknown"))
            TempData["StatusMessage"] = _strings.Text("https.shared.removed");
        else
            TempData["ErrorMessage"] = _strings.Text("https.shared.notFound");
        return RedirectToPage();
    }

    /// <summary>Whole days until a certificate expires; below 0 once it has.</summary>
    public static int DaysLeft(DateTimeOffset notAfter) => (int)Math.Floor((notAfter - DateTimeOffset.UtcNow).TotalDays);
}
