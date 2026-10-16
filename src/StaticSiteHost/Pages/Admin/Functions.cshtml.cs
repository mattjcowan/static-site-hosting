using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Pages.Shared;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages.Admin;

/// <summary>Functions every hosted site answers with, after its own. Administrators only, via the /Admin folder policy.</summary>
public class FunctionsModel : PageModel
{
    private readonly FunctionDeploymentService _functions;
    private readonly FunctionHost _host;
    private readonly IStringLocalizer<Strings> _strings;

    public FunctionsModel(FunctionDeploymentService functions, FunctionHost host, IStringLocalizer<Strings> strings)
    {
        _functions = functions;
        _host = host;
        _strings = strings;
    }

    public FunctionBundle? Current { get; private set; }

    /// <summary>Whether the live global functions are loaded, and how their background work is doing.</summary>
    public FunctionScopeStatus? Status { get; private set; }

    public string? FunctionError { get; private set; }

    public IReadOnlyList<FunctionDiagnostic> FunctionDiagnostics { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Current = (await _functions.GlobalAsync()).Current;
        Status = Current is null ? null : await _host.StatusAsync(null);
    }

    public async Task<IActionResult> OnPostAsync(List<IFormFile> files, string? mode, CancellationToken ct)
    {
        var (uploaded, readError) = await FunctionUploads.ReadAsync(files, _strings, ct);
        var result = uploaded is null
            ? FunctionDeployResult.Failed(readError!)
            : await _functions.DeployAsync(null, uploaded, User.Identity?.Name ?? "unknown", "web", replace: mode == "replace", ct);

        if (result.Ok)
        {
            TempData["StatusMessage"] = FunctionUploads.Describe(result, null, _strings);
            return RedirectToPage();
        }

        await OnGetAsync();
        FunctionError = result.Error;
        FunctionDiagnostics = result.Diagnostics ?? [];
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveFileAsync(string? fileName, CancellationToken ct)
    {
        var result = await _functions.RemoveFileAsync(null, fileName ?? "", User.Identity?.Name ?? "unknown", "web", ct);
        if (result.Ok)
        {
            TempData["StatusMessage"] = FunctionUploads.DescribeRemoved(result, fileName ?? "", null, _strings);
            return RedirectToPage();
        }

        await OnGetAsync();
        FunctionError = result.Error;
        FunctionDiagnostics = result.Diagnostics ?? [];
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveAsync()
    {
        var (ok, _) = await _functions.RemoveAsync(null, User.Identity?.Name ?? "unknown");
        if (ok) TempData["StatusMessage"] = _strings.Text("functions.remove.done");

        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetSourceAsync() =>
        await _functions.DownloadAsync(null) is { } download
            ? File(download.Content, download.ContentType, download.FileName)
            : NotFound();
}
