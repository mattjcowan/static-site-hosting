using System.Net;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Services;

namespace StaticSiteHost.Serving;

/// <summary>
/// The fork in the pipeline: a request either belongs to the management app or to a
/// hosted site, decided purely by the Host header.
///
///   * a host listed in SiteHosting:ManagementHosts  → management UI and API
///   * a host with a deployed site                   → static content for that site
///   * anything else                                 → management app when no management
///                                                     hosts are configured, otherwise 404
/// </summary>
public sealed class SiteHostingMiddleware
{
    private const string HealthPath = "/healthz";

    private readonly RequestDelegate _next;
    private readonly SiteStore _sites;
    private readonly SiteContentServer _content;
    private readonly SiteHostingOptions _options;

    public SiteHostingMiddleware(
        RequestDelegate next,
        SiteStore sites,
        SiteContentServer content,
        IOptions<SiteHostingOptions> options)
    {
        _next = next;
        _sites = sites;
        _content = content;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;

        if (string.IsNullOrEmpty(host) || context.Request.Path.StartsWithSegments(HealthPath))
        {
            await _next(context);
            return;
        }

        host = host.TrimEnd('.').ToLowerInvariant();

        if (_options.IsManagementHost(host))
        {
            await _next(context);
            return;
        }

        if (_sites.Exists(host))
        {
            await _content.ServeAsync(context, host);
            return;
        }

        if (!_options.HasManagementHosts)
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync($"""
            <!doctype html><meta charset="utf-8"><title>404 — No site here</title>
            <p>No site is published at <strong>{WebUtility.HtmlEncode(host)}</strong>.</p>
            """);
    }
}

public static class SiteHostingMiddlewareExtensions
{
    public static IApplicationBuilder UseHostedSites(this IApplicationBuilder app) =>
        app.UseMiddleware<SiteHostingMiddleware>();
}
