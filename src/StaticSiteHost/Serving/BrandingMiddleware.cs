using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using StaticSiteHost.Configuration;
using StaticSiteHost.Services;

namespace StaticSiteHost.Serving;

/// <summary>
/// The branding's files, on every domain, ahead of the passcode gate: a private site's passcode
/// screen needs its logo and colours before anyone is let in, and they are no secret.
///
///   GET /_host/brand/brand.css           the branding's stylesheet: the server's on the management
///                                        domain and on a domain with no site, merged with the site's
///                                        own on a site's domain
///   GET /_host/brand/a/{id}              an uploaded logo or icon
///   GET /_host/brand/f/{font}/{file}     a font file from the library
///
/// The stylesheet's address carries its version (<c>?v=</c>), and an asset's id is its content's
/// hash, so both are cached for a year and change address when they change. An SVG is served with
/// <c>Content-Security-Policy: sandbox</c> and no scripts, so opening one directly runs nothing in it.
/// </summary>
public sealed class BrandingMiddleware
{
    private const int Year = 365 * 24 * 60 * 60;

    private readonly RequestDelegate _next;
    private readonly BrandingService _branding;
    private readonly SiteStore _sites;
    private readonly SiteHostingOptions _options;

    public BrandingMiddleware(RequestDelegate next, BrandingService branding, SiteStore sites, IOptions<SiteHostingOptions> options)
    {
        _next = next;
        _branding = branding;
        _sites = sites;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(BrandingService.Prefix, StringComparison.OrdinalIgnoreCase, out var rest) ||
            !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        {
            await _next(context);
            return;
        }

        var parts = (rest.Value ?? "").Trim('/').Split('/');
        var response = context.Response;
        response.Headers.XContentTypeOptions = "nosniff";

        switch (parts)
        {
            case ["brand.css"]:
                await StylesheetAsync(context);
                return;

            case ["a", var id] when _branding.Store.AssetPath(id) is { } asset:
                response.ContentType = ContentType(id);
                response.Headers.CacheControl = $"public, max-age={Year}, immutable";
                if (id.EndsWith(".svg", StringComparison.Ordinal))
                    response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; sandbox";
                await response.SendFileAsync(asset, context.RequestAborted);
                return;

            case ["f", var font, var file] when await _branding.Store.FontFilePathAsync(font, file, context.RequestAborted) is { } path:
                response.ContentType = Path.GetExtension(file) switch
                {
                    ".woff2" => "font/woff2",
                    ".woff" => "font/woff",
                    ".otf" => "font/otf",
                    _ => "font/ttf"
                };
                response.Headers.CacheControl = $"public, max-age={Year}, immutable";
                await response.SendFileAsync(path, context.RequestAborted);
                return;

            default:
                response.StatusCode = StatusCodes.Status404NotFound;
                response.Headers.CacheControl = "no-store";
                return;
        }
    }

    private async Task StylesheetAsync(HttpContext context)
    {
        var host = context.Request.Host.Host.TrimEnd('.').ToLowerInvariant();
        var site = _options.IsManagementHost(host) ? null : _sites.TryGet(host);
        var (version, css) = await _branding.StylesheetAsync(site, context.RequestAborted);

        var response = context.Response;
        response.ContentType = "text/css; charset=utf-8";
        var tag = new EntityTagHeaderValue($"\"{version}\"");
        response.Headers.ETag = tag.ToString();

        // Asked for by its current version: cache it for good. Otherwise check each time.
        response.Headers.CacheControl = context.Request.Query["v"] == version ? $"public, max-age={Year}, immutable" : "no-cache";

        if (context.Request.Headers.IfNoneMatch.ToString() == tag.ToString())
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        await response.WriteAsync(css, context.RequestAborted);
    }

    private static string ContentType(string id) => Path.GetExtension(id) switch
    {
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream"
    };
}
