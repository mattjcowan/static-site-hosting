using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
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

    public HttpsModel(CertificateManager certificates, IOptions<SiteHostingOptions> options)
    {
        _certificates = certificates;
        Tls = options.Value.Tls;
    }

    public TlsOptions Tls { get; }

    public bool Enabled => _certificates.Enabled;

    public IReadOnlyList<HostCertificateStatus> Hosts { get; private set; } = [];

    public IReadOnlyList<(SharedCertificateInfo Info, ServedCertificate Certificate)> Shared { get; private set; } = [];

    /// <summary>Why the last shared upload was refused.</summary>
    public string? UploadError { get; private set; }

    public void OnGet() => Load();

    private void Load()
    {
        Hosts = _certificates.Enabled ? _certificates.Status() : [];
        Shared = _certificates.Shared;
    }

    public IActionResult OnPostRenew(string? host)
    {
        var status = Enabled ? _certificates.Status().FirstOrDefault(s => s.Host == host) : null;
        if (status is null)
        {
            TempData["ErrorMessage"] = "That host is not one this server answers for.";
            return RedirectToPage();
        }

        _certificates.RenewNow(status.Host);
        TempData["StatusMessage"] = $"Asked for a new certificate for {status.Host}. Reload in a few seconds to see it.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddSharedAsync(string? certificate, string? privateKey)
    {
        var (info, error) = await _certificates.AddSharedAsync(certificate, privateKey, User.Identity?.Name ?? "unknown");
        if (info is null)
        {
            Load();
            UploadError = error;
            return Page();
        }

        TempData["StatusMessage"] = $"Saved the certificate for {string.Join(", ", info.Names)}. Sites it covers that are on automatic use it now.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveSharedAsync(string? id)
    {
        if (await _certificates.RemoveSharedAsync(id ?? "", User.Identity?.Name ?? "unknown"))
            TempData["StatusMessage"] = "The shared certificate was removed. The sites it covered get their own.";
        else
            TempData["ErrorMessage"] = "That shared certificate no longer exists.";
        return RedirectToPage();
    }

    public static string Days(DateTimeOffset notAfter)
    {
        var days = (int)Math.Floor((notAfter - DateTimeOffset.UtcNow).TotalDays);
        return days < 0 ? "expired" : days == 1 ? "1 day" : $"{days} days";
    }
}
