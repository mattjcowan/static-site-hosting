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
            string? functions,
            ClaimsPrincipal principal,
            ZipDeploymentService deployer) =>
        {
            var (normalized, error) = SiteStore.NormalizeDomain(domain);
            if (normalized is null) return Results.BadRequest(new { error });

            // ?functions=keep runs the current functions on the older release; the default brings
            // back the functions that release had.
            var keep = string.Equals(functions, "keep", StringComparison.OrdinalIgnoreCase);
            var (ok, failure) = await deployer.RollbackAsync(normalized, releaseId, Actor(principal), keep);
            return ok
                ? Results.Ok(new { ok = true, domain = normalized, release = releaseId })
                : Results.BadRequest(new { error = failure });
        });

        api.MapPut("/sites/{domain}/passcode", async (
            string domain,
            PasscodeRequest? body,
            ClaimsPrincipal principal,
            SiteStore sites,
            SitePasscodeGate passcodes) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            var (ok, error) = await passcodes.SetAsync(site, body?.Passcode, Actor(principal));
            return ok
                ? Results.Ok(new { ok = true, domain = site.Domain, passcodeProtected = true })
                : Results.BadRequest(new { error });
        });

        api.MapDelete("/sites/{domain}/passcode", async (
            string domain,
            ClaimsPrincipal principal,
            SiteStore sites,
            SitePasscodeGate passcodes) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            await passcodes.ClearAsync(site, Actor(principal));
            return Results.Ok(new { ok = true, domain = site.Domain, passcodeProtected = false });
        });

        api.MapGet("/sites/{domain}/headers", (string domain, SiteStore sites) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            return Results.Ok(new
            {
                domain = site.Domain,
                siteRules = site.Headers ?? [],
                releaseRules = site.Current?.Headers ?? []
            });
        });

        api.MapPut("/sites/{domain}/headers", async (
            string domain,
            HeaderRulesRequest? body,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteRuleService rules) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            var (ok, errors) = await rules.SetHeadersAsync(site, body?.Headers ?? [], Actor(principal));
            return ok
                ? Results.Ok(new { ok = true, domain = site.Domain, rules = site.Headers.Count })
                : Results.BadRequest(new { error = errors[0], errors });
        });

        api.MapDelete("/sites/{domain}/headers", async (
            string domain,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteRuleService rules) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            await rules.SetHeadersAsync(site, [], Actor(principal));
            return Results.Ok(new { ok = true, domain = site.Domain, rules = 0 });
        });

        api.MapGet("/sites/{domain}/redirects", (string domain, SiteStore sites) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            return Results.Ok(new
            {
                domain = site.Domain,
                siteRules = site.Redirects ?? [],
                releaseRules = site.Current?.Redirects ?? []
            });
        });

        api.MapPut("/sites/{domain}/redirects", async (
            string domain,
            RedirectRulesRequest? body,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteRuleService rules) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            var (ok, errors) = await rules.SetRedirectsAsync(site, body?.Redirects ?? [], Actor(principal));
            return ok
                ? Results.Ok(new { ok = true, domain = site.Domain, rules = site.Redirects.Count })
                : Results.BadRequest(new { error = errors[0], errors });
        });

        api.MapDelete("/sites/{domain}/redirects", async (
            string domain,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteRuleService rules) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            await rules.SetRedirectsAsync(site, [], Actor(principal));
            return Results.Ok(new { ok = true, domain = site.Domain, rules = 0 });
        });

        api.MapPost("/sites/{domain}/rename", async (
            string domain,
            RenameRequest? body,
            ClaimsPrincipal principal,
            ZipDeploymentService deployer,
            HttpContext http) =>
        {
            var (normalized, error) = SiteStore.NormalizeDomain(domain);
            if (normalized is null) return Results.BadRequest(new { error });

            var (ok, failure, to) = await deployer.RenameAsync(normalized, body?.Domain, Actor(principal));
            return ok
                ? Results.Ok(new { ok = true, from = normalized, domain = to, url = SiteLinks.For(http.Request, to!) })
                : Results.BadRequest(new { error = failure });
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapDelete("/sites/{domain}", async (
            string domain,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteContentServer content,
            FunctionHost functions,
            AuditLog audit) =>
        {
            var (normalized, error) = SiteStore.NormalizeDomain(domain);
            if (normalized is null) return Results.BadRequest(new { error });

            functions.Evict(normalized);
            if (!await sites.DeleteAsync(normalized))
                return Results.NotFound(new { error = $"No site is published at '{normalized}'." });

            content.Evict(normalized);
            await audit.WriteAsync("site.delete", Actor(principal), new { domain = normalized });
            return Results.Ok(new { ok = true, domain = normalized });
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapFunctionEndpoints();

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

        var result = await deployer.DeployAsync(domain, archive, archiveName, Actor(principal), "api",
            canDeployFunctions: principal.IsInRole(Roles.Administrator), ct);
        if (!result.Ok) return Results.BadRequest(new { error = result.Error, diagnostics = result.Diagnostics });

        return Results.Ok(new
        {
            ok = true,
            domain = result.Domain,
            url = SiteLinks.For(http.Request, result.Domain!),
            release = result.Release,
            functions = result.Functions,
            warnings = result.Warnings ?? []
        });
    }

    /// <summary>Resolves a domain to a site, or the response to return instead.</summary>
    private static (SiteRecord? Site, IResult? Failure) Find(string domain, SiteStore sites)
    {
        var (normalized, error) = SiteStore.NormalizeDomain(domain);
        if (normalized is null) return (null, Results.BadRequest(new { error }));

        var site = sites.TryGet(normalized);
        return site is null
            ? (null, Results.NotFound(new { error = $"No site is published at '{normalized}'." }))
            : (site, null);
    }

    private static object Describe(SiteRecord site) => new
    {
        domain = site.Domain,
        currentRelease = site.CurrentRelease,
        createdUtc = site.CreatedUtc,
        updatedUtc = site.UpdatedUtc,
        createdBy = site.CreatedBy,
        lastDeployedBy = site.LastDeployedBy,
        passcodeProtected = site.IsPasscodeProtected,
        passcodeSetUtc = site.PasscodeSetUtc,
        headerRules = (site.Headers?.Count ?? 0) + (site.Current?.Headers?.Count ?? 0),
        redirectRules = (site.Redirects?.Count ?? 0) + (site.Current?.Redirects?.Count ?? 0),
        fileCount = site.Current?.FileCount ?? 0,
        totalBytes = site.Current?.TotalBytes ?? 0,
        functions = site.CurrentFunctions,
        releases = site.Releases
    };

    internal static string Actor(ClaimsPrincipal principal)
    {
        var name = principal.Identity?.Name ?? "unknown";
        var keyId = principal.FindFirstValue(ApiKeyAuthenticationHandler.ApiKeyIdClaim);
        return keyId is null ? name : $"{name} (key {keyId})";
    }
}

/// <summary>Body of POST /api/v1/sites/{domain}/rename.</summary>
public sealed record RenameRequest(string? Domain);

/// <summary>Body of PUT /api/v1/sites/{domain}/passcode.</summary>
public sealed record PasscodeRequest(string? Passcode);

/// <summary>Body of PUT /api/v1/sites/{domain}/headers. Replaces the site's whole rule list.</summary>
public sealed record HeaderRulesRequest(List<HeaderRule>? Headers);

/// <summary>Body of PUT /api/v1/sites/{domain}/redirects. Replaces the site's whole rule list.</summary>
public sealed record RedirectRulesRequest(List<RedirectRule>? Redirects);
