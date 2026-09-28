using System.Security.Claims;
using System.Text;
using StaticSiteHost.Models;
using StaticSiteHost.Services;

namespace StaticSiteHost.Endpoints;

/// <summary>
/// /api/v1/sites/{domain}/functions and /api/v1/functions. Reading is open to any API user;
/// uploading and removing is for administrators only, because a function is code that runs
/// inside this server.
/// </summary>
public static class FunctionEndpoints
{
    public static RouteGroupBuilder MapFunctionEndpoints(this RouteGroupBuilder api)
    {
        // ---- per site

        // The bundle as deployed, and where it stands now: loaded and running its jobs, failed and
        // why, or waiting for its first request.
        api.MapGet("/sites/{domain}/functions", (string domain, SiteStore sites, FunctionHost host) =>
            WithSite(domain, sites, async site =>
                Results.Ok(new { domain = site.Domain, functions = site.CurrentFunctions, status = await host.StatusAsync(site.Domain) })));

        api.MapGet("/sites/{domain}/functions/source", (string domain, SiteStore sites, FunctionDeploymentService functions) =>
            WithSite(domain, sites, async site => SourceResult(await functions.DownloadAsync(site.Domain))));

        api.MapPut("/sites/{domain}/functions", (
            string domain, string? mode, HttpContext http, ClaimsPrincipal principal, SiteStore sites,
            FunctionDeploymentService functions, CancellationToken ct) =>
            WithSite(domain, sites, site => DeployAsync(site.Domain, mode, http, principal, functions, ct)))
            .RequireAuthorization(Policies.ApiAdministrator);

        api.MapDelete("/sites/{domain}/functions", (
            string domain, ClaimsPrincipal principal, SiteStore sites, FunctionDeploymentService functions) =>
            WithSite(domain, sites, site => RemoveAsync(site.Domain, principal, functions)))
            .RequireAuthorization(Policies.ApiAdministrator);

        api.MapDelete("/sites/{domain}/functions/files/{fileName}", (
            string domain, string fileName, ClaimsPrincipal principal, SiteStore sites,
            FunctionDeploymentService functions, CancellationToken ct) =>
            WithSite(domain, sites, site => RemoveFileAsync(site.Domain, fileName, principal, functions, ct)))
            .RequireAuthorization(Policies.ApiAdministrator);

        // ---- global

        api.MapGet("/functions", async (FunctionDeploymentService functions, FunctionHost host) =>
            Results.Ok(new { functions = (await functions.GlobalAsync()).Current, status = await host.StatusAsync(null) }));

        api.MapGet("/functions/source", async (FunctionDeploymentService functions) =>
            SourceResult(await functions.DownloadAsync(null)));

        api.MapPut("/functions", (string? mode, HttpContext http, ClaimsPrincipal principal, FunctionDeploymentService functions, CancellationToken ct) =>
            DeployAsync(null, mode, http, principal, functions, ct))
            .RequireAuthorization(Policies.ApiAdministrator);

        api.MapDelete("/functions", (ClaimsPrincipal principal, FunctionDeploymentService functions) =>
            RemoveAsync(null, principal, functions))
            .RequireAuthorization(Policies.ApiAdministrator);

        api.MapDelete("/functions/files/{fileName}", (
            string fileName, ClaimsPrincipal principal, FunctionDeploymentService functions, CancellationToken ct) =>
            RemoveFileAsync(null, fileName, principal, functions, ct))
            .RequireAuthorization(Policies.ApiAdministrator);

        return api;
    }

    private static async Task<IResult> RemoveFileAsync(
        string? domain, string fileName, ClaimsPrincipal principal, FunctionDeploymentService functions, CancellationToken ct)
    {
        var result = await functions.RemoveFileAsync(domain, fileName, ApiEndpoints.Actor(principal), "api", ct);
        return result.Ok
            ? Results.Ok(new { ok = true, functions = result.Bundle })
            : Results.BadRequest(new { error = result.Error, diagnostics = result.Diagnostics });
    }

    /// <summary>
    /// ?mode=replace makes the upload the whole set. Otherwise it is merged into what is live:
    /// same-named files are replaced and the rest carried over.
    /// </summary>
    private static async Task<IResult> DeployAsync(
        string? domain, string? mode, HttpContext http, ClaimsPrincipal principal, FunctionDeploymentService functions, CancellationToken ct)
    {
        if (mode is not (null or "merge" or "replace"))
            return Results.BadRequest(new { error = "mode is 'merge' (the default) or 'replace'." });

        var (files, readError) = await ReadUploadAsync(http, ct);
        if (files is null) return Results.BadRequest(new { error = readError });

        var result = await functions.DeployAsync(domain, files, ApiEndpoints.Actor(principal), "api", replace: mode == "replace", ct);
        return result.Ok
            ? Results.Ok(new { ok = true, functions = result.Bundle, kept = result.Kept, diagnostics = result.Diagnostics })
            : Results.BadRequest(new { error = result.Error, diagnostics = result.Diagnostics });
    }

    private static async Task<IResult> RemoveAsync(string? domain, ClaimsPrincipal principal, FunctionDeploymentService functions)
    {
        var (ok, error) = await functions.RemoveAsync(domain, ApiEndpoints.Actor(principal));
        return ok ? Results.Ok(new { ok = true, domain }) : Results.BadRequest(new { error });
    }

    private static async Task<IResult> WithSite(string domain, SiteStore sites, Func<SiteRecord, Task<IResult>> action)
    {
        var (normalized, error) = SiteStore.NormalizeDomain(domain);
        if (normalized is null) return Results.BadRequest(new { error });

        return sites.TryGet(normalized) is { } site
            ? await action(site)
            : Results.NotFound(new { error = $"No site is published at '{normalized}'." });
    }

    private static IResult SourceResult(FunctionSourceDownload? download) =>
        download is null
            ? Results.NotFound(new { error = "No functions are deployed here." })
            : Results.File(download.Content, download.ContentType, download.FileName);

    /// <summary>
    /// Takes the files as multipart (<c>-F file=@Orders.cs -F file=@Reports.linq</c>, any field
    /// names), or one file as the raw body with its name in X-File-Name
    /// (<c>--data-binary @Orders.cs</c>). Any of them may be a .zip of such files. A name matters
    /// for its extension, which picks the reader, and to tell files apart in compiler messages.
    /// </summary>
    private static async Task<(List<FunctionFile>? Files, string? Error)> ReadUploadAsync(HttpContext http, CancellationToken ct)
    {
        // A zip of several files may be larger than one file; the builder still holds each
        // file inside it, and the set as a whole, to their own limits.
        var limit = FunctionBundleBuilder.MaxTotalBytes;
        var tooLarge = $"An upload is limited to {Format.Bytes(limit)}.";

        if (http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync(ct);
            if (form.Files.Count == 0) return (null, "Attach the function files as form fields, e.g. -F file=@Orders.cs.");

            var files = new List<FunctionFile>();
            foreach (var file in form.Files)
            {
                if (file.Length > limit) return (null, $"{file.FileName}: {tooLarge}");

                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, ct);
                if (FunctionUploadReader.Read(file.FileName, buffer.ToArray(), files) is { } error) return (null, error);
            }

            return (files, null);
        }

        if (http.Request.ContentLength > limit) return (null, tooLarge);

        using var body = new MemoryStream();
        await http.Request.Body.CopyToAsync(body, ct);
        if (body.Length > limit) return (null, tooLarge);
        if (body.Length == 0) return (null, "The request body is empty. Send a function file as the body, or files as form fields.");

        var name = http.Request.Headers["X-File-Name"].FirstOrDefault() ?? "Functions.cs";
        var read = new List<FunctionFile>();
        return FunctionUploadReader.Read(name, body.ToArray(), read) is { } bodyError ? (null, bodyError) : (read, null);
    }
}
