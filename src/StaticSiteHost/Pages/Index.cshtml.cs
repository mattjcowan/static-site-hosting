using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages;

public class IndexModel : PageModel
{
    private readonly SiteStore _sites;
    private readonly ZipDeploymentService _deployer;
    private readonly SiteHostingOptions _options;

    public IndexModel(SiteStore sites, ZipDeploymentService deployer, IOptions<SiteHostingOptions> options)
    {
        _sites = sites;
        _deployer = deployer;
        _options = options.Value;
    }

    public IReadOnlyList<SiteRecord> Sites { get; private set; } = [];

    public string MaxUploadDisplay => Format.Bytes(_options.MaxUploadBytes);

    public void OnGet() => Sites = _sites.List();

    public async Task<IActionResult> OnPostDeployAsync(string? domain, IFormFile? archive, CancellationToken cancellationToken)
    {
        Sites = _sites.List();

        if (archive is null || archive.Length == 0)
            return Respond(false, "Choose a .zip archive to upload.");

        await using var stream = archive.OpenReadStream();

        var actor = User.Identity?.Name ?? "unknown";
        var result = await _deployer.DeployAsync(domain, stream, archive.FileName, actor, "web",
            canDeployFunctions: User.IsInRole(Roles.Administrator), cancellationToken);

        if (!result.Ok) return Respond(false, result.Error, result.Diagnostics);

        var release = result.Release!;
        var url = SiteLinks.For(Request, result.Domain!);

        if (IsXhr)
        {
            return new JsonResult(new
            {
                ok = true,
                domain = result.Domain,
                url,
                release = release.Id,
                fileCount = release.FileCount,
                size = Format.Bytes(release.TotalBytes),
                functions = result.Functions?.Routes,
                warnings = result.Warnings ?? []
            });
        }

        TempData["StatusMessage"] =
            $"Published {release.FileCount} file(s) ({Format.Bytes(release.TotalBytes)}) to {result.Domain}.";
        return RedirectToPage();
    }

    private bool IsXhr => Request.Headers.XRequestedWith == "fetch";

    private IActionResult Respond(bool ok, string? message, IReadOnlyList<FunctionDiagnostic>? diagnostics = null)
    {
        // Only errors: warnings would bury the line that stopped the deploy.
        if (IsXhr)
            return new JsonResult(new { ok, error = message, diagnostics = diagnostics?.Where(d => d.Severity == "error").Select(d => d.ToString()) });

        TempData["ErrorMessage"] = message;
        return RedirectToPage();
    }
}
