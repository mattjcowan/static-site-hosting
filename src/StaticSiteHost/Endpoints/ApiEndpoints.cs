using System.Security.Claims;
using StaticSiteHost.Models;
using StaticSiteHost.Security;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;

namespace StaticSiteHost.Endpoints;

public static class Policies
{
    /// <summary>Any active user presenting a valid API key.</summary>
    public const string ApiUser = "api-user";

    /// <summary>An API key owned by an administrator.</summary>
    public const string ApiAdministrator = "api-administrator";

    /// <summary>A signed-in administrator in the management UI.</summary>
    public const string Administrator = "administrator";
}

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1")
            .RequireAuthorization(Policies.ApiUser)
            .DisableAntiforgery();

        api.MapGet("/me", (ClaimsPrincipal principal) => Results.Ok(new
        {
            username = principal.Identity?.Name,
            displayName = principal.FindFirstValue("display_name"),
            role = principal.FindFirstValue(ClaimTypes.Role),
            apiKeyId = principal.FindFirstValue(ApiKeyAuthenticationHandler.ApiKeyIdClaim)
        }));

        api.MapGet("/sites", (SiteStore sites) =>
            Results.Ok(sites.List().Select(Describe)));

        api.MapGet("/sites/{domain}", (string domain, SiteStore sites) =>
        {
            var (normalized, error) = SiteStore.NormalizeDomain(domain);
            if (normalized is null) return Results.BadRequest(new { error });

            var site = sites.TryGet(normalized);
            return site is null
                ? Results.NotFound(new { error = $"No site is published at '{normalized}'." })
                : Results.Ok(Describe(site));
        });

        api.MapPost("/sites/{domain}/deploy", DeployAsync);

        api.MapPost("/sites/{domain}/rollback/{releaseId}", async (
            string domain,
            string releaseId,
            ClaimsPrincipal principal,
            ZipDeploymentService deployer) =>
        {
            var (normalized, error) = SiteStore.NormalizeDomain(domain);
            if (normalized is null) return Results.BadRequest(new { error });

            var (ok, failure) = await deployer.RollbackAsync(normalized, releaseId, Actor(principal));
            return ok
                ? Results.Ok(new { ok = true, domain = normalized, release = releaseId })
                : Results.BadRequest(new { error = failure });
        });

        api.MapDelete("/sites/{domain}", async (
            string domain,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteContentServer content,
            AuditLog audit) =>
        {
            var (normalized, error) = SiteStore.NormalizeDomain(domain);
            if (normalized is null) return Results.BadRequest(new { error });

            if (!await sites.DeleteAsync(normalized))
                return Results.NotFound(new { error = $"No site is published at '{normalized}'." });

            content.Evict(normalized);
            await audit.WriteAsync("site.delete", Actor(principal), new { domain = normalized });
            return Results.Ok(new { ok = true, domain = normalized });
        }).RequireAuthorization(Policies.ApiAdministrator);

        return app;
    }

    private static async Task<IResult> DeployAsync(
        string domain,
        HttpContext http,
        ClaimsPrincipal principal,
        ZipDeploymentService deployer,
        CancellationToken ct)
    {
        Stream? archive = null;
        string? archiveName = null;

        if (http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null)
                return Results.BadRequest(new { error = "Attach the zip archive as a form field named 'file'." });

            archive = file.OpenReadStream();
            archiveName = file.FileName;
        }
        else
        {
            archive = http.Request.Body;
            archiveName = http.Request.Headers["X-Archive-Name"].FirstOrDefault();
        }

        var result = await deployer.DeployAsync(domain, archive, archiveName, Actor(principal), "api", ct);
        if (!result.Ok) return Results.BadRequest(new { error = result.Error });

        return Results.Ok(new
        {
            ok = true,
            domain = result.Domain,
            url = $"{http.Request.Scheme}://{result.Domain}/",
            release = result.Release,
            warnings = result.Warnings ?? []
        });
    }

    private static object Describe(SiteRecord site) => new
    {
        domain = site.Domain,
        currentRelease = site.CurrentRelease,
        createdUtc = site.CreatedUtc,
        updatedUtc = site.UpdatedUtc,
        createdBy = site.CreatedBy,
        lastDeployedBy = site.LastDeployedBy,
        fileCount = site.Current?.FileCount ?? 0,
        totalBytes = site.Current?.TotalBytes ?? 0,
        releases = site.Releases
    };

    private static string Actor(ClaimsPrincipal principal)
    {
        var name = principal.Identity?.Name ?? "unknown";
        var keyId = principal.FindFirstValue(ApiKeyAuthenticationHandler.ApiKeyIdClaim);
        return keyId is null ? name : $"{name} (key {keyId})";
    }
}
