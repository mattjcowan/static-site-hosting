using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;

namespace StaticSiteHost.Endpoints;

/// <summary>
/// What a site's Files tab (wwwroot/js/files.js) calls, under <c>/ui/sites/{domain}/files</c>,
/// signed in with the UI's cookie, each change with its antiforgery token. Every call works on one
/// release: the live one, or the one <c>release</c> names. Anyone who may deploy may use it. The
/// answers' messages are in the viewer's language; scripts use <c>PATCH /api/v1/sites/{domain}/files</c>.
/// </summary>
public static class SiteFileEditorEndpoints
{
    /// <summary>The image types the tab previews, by extension. Anything else is sent as bytes to download.</summary>
    private static readonly Dictionary<string, string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
        [".avif"] = "image/avif", [".svg"] = "image/svg+xml", [".ico"] = "image/x-icon", [".bmp"] = "image/bmp"
    };

    public static IEndpointRouteBuilder MapSiteFileEditor(this IEndpointRouteBuilder app)
    {
        // Each change checks the antiforgery token itself, as ModelSuggestionEndpoints does.
        var files = app.MapGroup("/ui/sites/{domain}/files").RequireAuthorization().DisableAntiforgery();

        files.MapGet("", (string domain, string? release, SiteStore sites, SiteFiles reader, IStringLocalizer<Strings> strings) =>
        {
            if (sites.TryGet(Normalize(domain)) is not { } site) return Refused(new FileEditError("site.files.error.noSite", new { domain }), strings);

            var chosen = SiteFiles.ReleaseOf(site, release);
            if (chosen is null && !string.IsNullOrWhiteSpace(release)) return Refused(new FileEditError("site.files.error.noRelease", new { id = release }), strings);

            return Results.Ok(new
            {
                release = chosen is null ? null : Describe(site, chosen),
                releases = site.Releases.Select(r => Describe(site, r)),
                files = chosen is null ? [] : reader.List(site, chosen)
            });
        });

        // Sent under a sandbox policy, as the function editor's test answers are: a page or an SVG in
        // the site must never run on the management origin, not even opened in a tab of its own. Bytes
        // to edit go as an opaque download; ?preview=true sends an image as one, for the preview.
        files.MapGet("/content", (
            string domain, string? release, string? path, bool? download, bool? preview, SiteStore sites, SiteFiles reader,
            HttpContext http, IStringLocalizer<Strings> strings) =>
        {
            if (sites.TryGet(Normalize(domain)) is not { } site || SiteFiles.ReleaseOf(site, release) is not { } chosen ||
                reader.Open(site, chosen, path ?? "") is not { } stream)
                return Results.NotFound(new { error = strings.Text("site.files.error.notFound", new { path }) });

            http.Response.Headers.ContentSecurityPolicy = "sandbox";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.CacheControl = "no-store";

            var type = preview == true && Images.TryGetValue(Path.GetExtension(path ?? ""), out var image) ? image : "application/octet-stream";
            return Results.Stream(stream, type, download == true ? Path.GetFileName(path) : null);
        });

        // The body is the file's bytes, read into a file of its own first: a large upload must not
        // hold up the site's other changes. ?create=true writes only where no file is yet.
        files.MapPut("/content", async (
            string domain, string? release, string? path, bool? create, HttpContext http, IAntiforgery antiforgery,
            ZipDeploymentService deployer, DataPaths paths, IOptions<SiteHostingOptions> options, IStringLocalizer<Strings> strings,
            CancellationToken ct) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();

            var normalized = ZipDeploymentService.NormalizeSitePath(path);
            var limit = normalized is not null && ReleaseEditor.IsConfiguration(normalized) ? ZipDeploymentService.MaxRuleFileBytes : options.Value.MaxEntryBytes;

            Directory.CreateDirectory(paths.TempDir);
            var upload = Path.Combine(paths.TempDir, $"upload-{Guid.NewGuid():n}");
            try
            {
                await using (var file = new FileStream(upload, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    var buffer = new byte[81920];
                    long size = 0;
                    int read;
                    while ((read = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
                    {
                        size += read;
                        if (size > limit) return Refused(new FileEditError("site.files.error.tooLarge", new { path = normalized ?? path, size = Format.Bytes(limit) }), strings);
                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                }

                var result = await deployer.EditFilesAsync(domain, release, [ZipDeploymentService.FileChange.FromFile(path ?? "", upload)], [],
                    Actor(http.User), "web", http.User.IsInRole(Roles.Administrator), createOnly: create == true, ct: ct);
                return result.Error is { } error
                    ? Refused(error, strings)
                    : Results.Ok(new { ok = true, path = result.Paths?.FirstOrDefault(), release = result.Release!.Id, live = result.Live, warnings = result.Warnings ?? [] });
            }
            finally
            {
                if (File.Exists(upload)) File.Delete(upload);
            }
        });

        files.MapPost("/delete", async (
            string domain, FileEditorRequest? body, HttpContext http, IAntiforgery antiforgery, ZipDeploymentService deployer,
            IStringLocalizer<Strings> strings, CancellationToken ct) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();

            var result = await deployer.EditFilesAsync(domain, body?.Release, [], [body?.Path ?? ""], Actor(http.User), "web",
                http.User.IsInRole(Roles.Administrator), ct: ct);
            return result.Error is { } error ? Refused(error, strings) : Results.Ok(new { ok = true, release = result.Release!.Id, live = result.Live });
        });

        files.MapPost("/rename", async (
            string domain, FileEditorRequest? body, HttpContext http, IAntiforgery antiforgery, ZipDeploymentService deployer,
            IStringLocalizer<Strings> strings, CancellationToken ct) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();

            var result = await deployer.MoveFileAsync(Normalize(domain), body?.Release, body?.Path ?? "", body?.To ?? "", Actor(http.User),
                http.User.IsInRole(Roles.Administrator), ct);
            return result.Error is { } error
                ? Refused(error, strings)
                : Results.Ok(new { ok = true, path = result.Paths?.FirstOrDefault(), release = result.Release!.Id, live = result.Live });
        });

        files.MapPost("/duplicate", async (
            string domain, FileEditorRequest? body, HttpContext http, IAntiforgery antiforgery, ZipDeploymentService deployer, SiteStore sites,
            IStringLocalizer<Strings> strings) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();
            if (sites.TryGet(Normalize(domain)) is not { } site) return Refused(new FileEditError("site.files.error.noSite", new { domain }), strings);
            if (SiteFiles.ReleaseOf(site, body?.Release) is not { } original) return Refused(new FileEditError("site.files.error.noRelease", new { id = body?.Release }), strings);

            var result = await deployer.DuplicateReleaseAsync(site.Domain, original.Id, Actor(http.User));
            return result.Error is { } error
                ? Refused(error, strings)
                : Results.Ok(new { ok = true, release = result.Release!.Id, message = strings.Text("site.files.duplicated", new { id = result.Release.Id, from = original.Id }) });
        });

        // The same as Make live on the Releases tab: the release brings back its own functions.
        files.MapPost("/make-live", async (
            string domain, FileEditorRequest? body, HttpContext http, IAntiforgery antiforgery, ZipDeploymentService deployer, SiteStore sites,
            IStringLocalizer<Strings> strings) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();
            if (sites.TryGet(Normalize(domain)) is not { } site) return Refused(new FileEditError("site.files.error.noSite", new { domain }), strings);
            if (SiteFiles.ReleaseOf(site, body?.Release) is not { } chosen) return Refused(new FileEditError("site.files.error.noRelease", new { id = body?.Release }), strings);

            var (ok, error) = await deployer.RollbackAsync(site.Domain, chosen.Id, Actor(http.User));
            return ok
                ? Results.Ok(new { ok = true, release = chosen.Id, message = strings.Text("site.releases.rolledBack", new { id = chosen.Id, domain = site.Domain }) })
                : Results.BadRequest(new { error });
        });

        return app;
    }

    private static object Describe(SiteRecord site, ReleaseRecord release) => new
    {
        id = release.Id,
        live = release.Id == site.CurrentRelease,
        createdUtc = release.CreatedUtc,
        deployedBy = release.DeployedBy,
        copiedFrom = release.CopiedFrom,
        editedUtc = release.EditedUtc,
        editedBy = release.EditedBy,
        fileCount = release.FileCount,
        totalBytes = release.TotalBytes
    };

    private static string Normalize(string domain) => SiteStore.NormalizeDomain(domain).Domain ?? domain;

    private static string Actor(ClaimsPrincipal user) => user.Identity?.Name ?? "unknown";

    private static IResult Refused(FileEditError error, IStringLocalizer<Strings> strings)
    {
        var body = new { error = strings.Text(error.Key, error.Values), details = error.Details ?? [] };
        return error.Key is "site.files.error.noSite" or "site.files.error.noRelease" or "site.files.error.notFound"
            ? Results.NotFound(body)
            : Results.BadRequest(body);
    }
}

/// <summary>Body of the Files tab's changes: the release (the live one when empty), the file or folder, and where a rename moves it.</summary>
public sealed record FileEditorRequest(string? Release, string? Path, string? To);
