using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages.Admin;

/// <summary>Body of the editor's Check and Deploy calls: every open file, as it stands.</summary>
public sealed record EditorFiles(List<FunctionFile>? Files);

/// <summary>
/// Edit a site's functions (or the global ones) in the browser, compile them without going
/// live, run requests against the result, and deploy. Administrators only, via the /Admin
/// folder policy. Everything it does goes through the same services as an upload, so the
/// editor can accept nothing an upload would refuse.
/// </summary>
public class FunctionEditorModel : PageModel
{
    /// <summary>What a new file starts as, and what a site with no functions opens with.</summary>
    public const string StarterCode = """
        #:sdk Microsoft.NET.Sdk.Web

        using System.Text.Json;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.Mvc;

        // Handlers are public static methods on any public class, marked with the route they
        // answer. Split them across files as you like; give each class its own name.
        public static class Handlers
        {
            // Serialise with options owned by this file, so they are discarded with it on redeploy.
            private static readonly JsonSerializerOptions Json = new();

            [HttpGet("/hello/{name?}")]
            public static IResult Hello(string? name) =>
                Results.Json(new { message = $"Hello {name ?? "world"}", at = DateTimeOffset.UtcNow }, Json);
        }

        """;

    private static readonly JsonSerializerOptions PageJson = new(JsonSerializerDefaults.Web);

    private readonly SiteStore _sites;
    private readonly FunctionDeploymentService _functions;
    private readonly FunctionTestRunner _tests;

    public FunctionEditorModel(SiteStore sites, FunctionDeploymentService functions, FunctionTestRunner tests)
    {
        _sites = sites;
        _functions = functions;
        _tests = tests;
    }

    /// <summary>The site being edited; null for the global functions.</summary>
    public string? Domain { get; private set; }

    public FunctionBundle? Live { get; private set; }

    /// <summary>Everything the script needs to start, serialised once for the page.</summary>
    public string BootstrapJson { get; private set; } = "{}";

    public async Task<IActionResult> OnGetAsync(string? domain)
    {
        if (!TryScope(domain, out var scope)) return RedirectToPage("/Sites/Index");

        Domain = scope;
        Live = await _functions.LiveAsync(scope);

        var files = await _functions.LiveFilesAsync(scope);
        if (files.Count == 0) files = [new FunctionFile("Functions.cs", StarterCode)];

        BootstrapJson = JsonSerializer.Serialize(new
        {
            domain = scope,
            siteUrl = scope is null ? null : SiteLinks.For(Request, scope),
            files,
            live = Live?.Routes ?? [],
            starter = StarterCode,
        }, PageJson);

        return Page();
    }

    public async Task<IActionResult> OnPostCheckAsync(string? domain, [FromBody] EditorFiles body, CancellationToken ct)
    {
        if (!TryScope(domain, out var scope)) return NotFound();

        var result = await _functions.CheckAsync(scope, body.Files ?? [], User.Identity?.Name ?? "unknown", ct);
        return BuildResponse(result, draft: result.Bundle?.Id);
    }

    public async Task<IActionResult> OnPostDeployAsync(string? domain, [FromBody] EditorFiles body, CancellationToken ct)
    {
        if (!TryScope(domain, out var scope)) return NotFound();

        // The editor holds every file, so what it sends is the whole set.
        var result = await _functions.DeployAsync(scope, body.Files ?? [], User.Identity?.Name ?? "unknown", "editor", replace: true, ct);
        return BuildResponse(result, draft: null);
    }

    /// <summary>
    /// Runs one request and returns what the function answered: the body as raw bytes, and the
    /// status, headers and timing in <c>X-Fn-Meta</c>. The body is always sent as an opaque
    /// download under a sandbox policy, so HTML a function returns can never execute on the
    /// management origin, where the administrator's session lives. The page decides how to
    /// show it from the function's own content type.
    /// </summary>
    public async Task<IActionResult> OnPostRunAsync(string? domain, [FromBody] FunctionTestRequest request, CancellationToken ct)
    {
        if (!TryScope(domain, out var scope)) return NotFound();

        var binDir = await _functions.ResolveBuildAsync(scope, request.Target);
        FunctionTestResult result;

        if (binDir is null)
        {
            result = new FunctionTestResult(0, null, [], [], 0, false, false,
                request.Target is null or "live"
                    ? "Nothing is live to test. Check the files, then test the checked build."
                    : "That checked build has expired. Check the files again.");
        }
        else
        {
            var host = scope ?? (string.IsNullOrWhiteSpace(request.Host) ? "localhost" : request.Host.Trim());
            result = await _tests.RunAsync(binDir, host, _functions.DataDirectory(scope), request, HttpContext.RequestServices, ct);
        }

        var meta = JsonSerializer.Serialize(new
        {
            status = result.Status,
            contentType = result.ContentType,
            headers = result.Headers,
            elapsedMs = result.ElapsedMs,
            matched = result.Matched,
            truncated = result.Truncated,
            error = result.Error,
        }, PageJson);

        Response.Headers["X-Fn-Meta"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(meta));
        Response.Headers.ContentSecurityPolicy = "sandbox";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "no-store";

        return File(result.Body, "application/octet-stream", "response.bin");
    }

    private JsonResult BuildResponse(FunctionDeployResult result, string? draft) => new(new
    {
        ok = result.Ok,
        error = result.Error,
        draft,
        label = result.Bundle?.Label,
        routes = result.Bundle?.Routes ?? [],
        diagnostics = result.Diagnostics ?? [],
    }, PageJson);

    /// <summary>A known site's domain, or null for the global functions when no domain is given.</summary>
    private bool TryScope(string? domain, out string? scope)
    {
        scope = null;
        if (string.IsNullOrEmpty(domain)) return true;

        var (normalized, _) = SiteStore.NormalizeDomain(domain);
        if (normalized is null || _sites.TryGet(normalized) is null)
        {
            TempData["ErrorMessage"] = $"No site is published at '{domain}'.";
            return false;
        }

        scope = normalized;
        return true;
    }
}
