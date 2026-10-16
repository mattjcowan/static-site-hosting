using System.Security.Claims;
using StaticSiteHost.Models;
using StaticSiteHost.Services;

namespace StaticSiteHost.Endpoints;

/// <summary>
/// Branding over the API: the server's, a site's own for its visitor screens, images and the font
/// library. Administrators only, like the Branding page. A branding is the JSON of <see cref="Branding"/>;
/// a field left out keeps the built-in look (or, on a site, the server's).
/// </summary>
public static class BrandingEndpoints
{
    public static RouteGroupBuilder MapBrandingEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/branding", async (BrandingService branding, CancellationToken ct) =>
            Results.Ok(await DescribeAsync(branding, null, ct)))
            .RequireAuthorization(Policies.ApiAdministrator);

        api.MapPut("/branding", async (Branding? body, ClaimsPrincipal principal, BrandingService branding, CancellationToken ct) =>
        {
            var errors = await branding.SetServerAsync(body ?? new Branding(), ApiEndpoints.Actor(principal), ct);
            return errors.Count > 0 ? Results.BadRequest(new { error = errors[0], errors }) : Results.Ok(await DescribeAsync(branding, null, ct));
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapDelete("/branding", async (ClaimsPrincipal principal, BrandingService branding, CancellationToken ct) =>
        {
            await branding.SetServerAsync(new Branding(), ApiEndpoints.Actor(principal), ct);
            return Results.Ok(await DescribeAsync(branding, null, ct));
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapPost("/branding/images", async (HttpRequest request, BrandingService branding, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Send the image as a form field named 'file'." });
            var form = await request.ReadFormAsync(ct);
            if ((form.Files.GetFile("file") ?? form.Files.FirstOrDefault()) is not { } file)
                return Results.BadRequest(new { error = "Send the image as a form field named 'file'." });

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            var (id, error) = await branding.Store.SaveAssetAsync(buffer.ToArray(), ct);
            return id is null
                ? Results.BadRequest(new { error })
                : Results.Ok(new { id, url = BrandingService.AssetUrl(id), note = "Use the id as logoLight, logoDark or favicon in PUT /branding. An image no branding uses is removed after an hour." });
        }).RequireAuthorization(Policies.ApiAdministrator).DisableAntiforgery();

        api.MapPut("/branding/google-fonts", async (GoogleFontsRequest? body, ClaimsPrincipal principal, BrandingService branding, CancellationToken ct) =>
        {
            if (body?.Allow is not { } allow) return Results.BadRequest(new { error = "Send {\"allow\": true} or {\"allow\": false}." });
            await branding.SetAllowGoogleFontsAsync(allow, ApiEndpoints.Actor(principal), ct);
            return Results.Ok(await DescribeAsync(branding, null, ct));
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapPost("/branding/fonts/google", async (GoogleFontRequest? body, ClaimsPrincipal principal, BrandingService branding, CancellationToken ct) =>
        {
            var (font, error) = await branding.AddGoogleFontAsync(body?.Family ?? "", body?.Download ?? true, ApiEndpoints.Actor(principal), ct);
            return font is null ? Results.BadRequest(new { error }) : Results.Ok(DescribeFont(font));
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapPost("/branding/fonts", async (HttpRequest request, ClaimsPrincipal principal, BrandingService branding, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Send a form with 'family' and one or more 'files'." });
            var form = await request.ReadFormAsync(ct);
            var uploads = new List<(string FileName, byte[] Data)>();
            foreach (var file in form.Files.Where(f => f.Length > 0))
            {
                if (file.Length > BrandingStore.MaxFontFileBytes)
                    return Results.BadRequest(new { error = $"{file.FileName} is larger than {BrandingStore.MaxFontFileBytes / (1024 * 1024)} MB." });
                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, ct);
                uploads.Add((file.FileName, buffer.ToArray()));
            }

            var (font, error) = await branding.AddUploadedFontAsync(form["family"].ToString(), uploads, ApiEndpoints.Actor(principal), ct);
            return font is null ? Results.BadRequest(new { error }) : Results.Ok(DescribeFont(font));
        }).RequireAuthorization(Policies.ApiAdministrator).DisableAntiforgery();

        api.MapDelete("/branding/fonts/{id}", async (string id, ClaimsPrincipal principal, BrandingService branding, CancellationToken ct) =>
            await branding.RemoveFontAsync(id, ApiEndpoints.Actor(principal), ct)
                ? Results.Ok(new { ok = true, id })
                : Results.NotFound(new { error = "No font in the library has that id." }))
            .RequireAuthorization(Policies.ApiAdministrator);

        api.MapGet("/sites/{domain}/branding", async (string domain, SiteStore sites, BrandingService branding, CancellationToken ct) =>
            Find(domain, sites) is { } site ? Results.Ok(await DescribeAsync(branding, site, ct)) : NotFound(domain))
            .RequireAuthorization(Policies.ApiAdministrator);

        api.MapPut("/sites/{domain}/branding", async (string domain, Branding? body, ClaimsPrincipal principal, SiteStore sites, BrandingService branding, CancellationToken ct) =>
        {
            if (Find(domain, sites) is not { } site) return NotFound(domain);
            var errors = await branding.SetSiteAsync(site, body, ApiEndpoints.Actor(principal), ct);
            return errors.Count > 0 ? Results.BadRequest(new { error = errors[0], errors }) : Results.Ok(await DescribeAsync(branding, site, ct));
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapDelete("/sites/{domain}/branding", async (string domain, ClaimsPrincipal principal, SiteStore sites, BrandingService branding, CancellationToken ct) =>
        {
            if (Find(domain, sites) is not { } site) return NotFound(domain);
            await branding.SetSiteAsync(site, null, ApiEndpoints.Actor(principal), ct);
            return Results.Ok(await DescribeAsync(branding, site, ct));
        }).RequireAuthorization(Policies.ApiAdministrator);

        return api;
    }

    /// <summary>
    /// The server's branding (or a site's own and what its visitor screens end up with), the font
    /// library, and any colour pairs that are hard to read.
    /// </summary>
    internal static async Task<object> DescribeAsync(BrandingService branding, SiteRecord? site, CancellationToken ct)
    {
        var settings = await branding.Store.ReadAsync(ct);
        var effective = await branding.Store.ForAsync(site, ct);
        return new
        {
            domain = site?.Domain,
            server = settings.Server,
            site = site is null ? null : site.Branding,
            allowGoogleFonts = settings.AllowGoogleFonts,
            fonts = settings.Fonts.Select(DescribeFont),
            systemFonts = BrandFonts.System,
            colorTokens = BrandColors.Tokens.Select(BrandColors.JsonName),
            stylesheet = $"{BrandingService.Prefix}/brand.css",
            contrastWarnings = BrandingTheme.ContrastWarnings(effective)
        };
    }

    internal static object DescribeFont(BrandFont font) => new
    {
        id = font.Id,
        family = font.Family,
        kind = font.Kind,
        files = font.Files.Count,
        addedBy = font.AddedBy,
        addedUtc = font.AddedUtc
    };

    private static SiteRecord? Find(string domain, SiteStore sites) =>
        SiteStore.NormalizeDomain(domain).Domain is { } normalized ? sites.TryGet(normalized) : null;

    private static IResult NotFound(string domain) => Results.NotFound(new { error = $"No site is published at '{domain}'." });
}

/// <summary>Body of PUT /api/v1/branding/google-fonts.</summary>
public sealed record GoogleFontsRequest(bool? Allow);

/// <summary>Body of POST /api/v1/branding/fonts/google. Download (the default) serves the files from this server.</summary>
public sealed record GoogleFontRequest(string? Family, bool? Download);
