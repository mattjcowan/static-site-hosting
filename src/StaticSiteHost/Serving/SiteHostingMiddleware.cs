using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Hubs;
using StaticSiteHost.Models;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Realtime;

namespace StaticSiteHost.Serving;

/// <summary>
/// The fork in the pipeline: a request either belongs to the management app or to a
/// hosted site, decided purely by the Host header.
///
///   * a host listed in SiteHosting:ManagementHosts  → management UI and API
///   * a host with a deployed site                   → the site, behind its passcode if it
///                                                     has one (see below)
///   * anything else                                 → management app when no management
///                                                     hosts are configured, otherwise 404
///
/// A site's request goes: the passcode gate, the host's own /_host/ endpoints, then
/// <see cref="FunctionHost.HandleAsync"/>, which runs the global middleware, the site's
/// middleware, the site's functions and the global functions, and hands whatever they pass on
/// to the static content. So middleware sees every request for the site's files as well as
/// its functions, and no function, middleware included, runs before the passcode gate or
/// answers under /_host/.
///
/// One /_host/ path leaves this class: the realtime hub, <see cref="SiteHub.Path"/>, is a SignalR
/// endpoint that Program.cs maps, so its requests go on down the pipeline to routing, carrying
/// the site they are for. No other request for a site ever reaches the rest of the pipeline.
/// </summary>
public sealed class SiteHostingMiddleware
{
    private const string HealthPath = "/healthz";

    private readonly RequestDelegate _next;
    private readonly SiteStore _sites;
    private readonly SiteContentServer _content;
    private readonly SitePasscodeGate _passcodes;
    private readonly SiteHostEndpoints _hostEndpoints;
    private readonly FunctionHost _functions;
    private readonly RealtimeRegistry _realtime;
    private readonly RealtimeNegotiationLimiter _negotiations;
    private readonly SiteHostingOptions _options;

    public SiteHostingMiddleware(
        RequestDelegate next,
        SiteStore sites,
        SiteContentServer content,
        SitePasscodeGate passcodes,
        SiteHostEndpoints hostEndpoints,
        FunctionHost functions,
        RealtimeRegistry realtime,
        RealtimeNegotiationLimiter negotiations,
        IOptions<SiteHostingOptions> options)
    {
        _next = next;
        _sites = sites;
        _content = content;
        _passcodes = passcodes;
        _hostEndpoints = hostEndpoints;
        _functions = functions;
        _realtime = realtime;
        _negotiations = negotiations;
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

        if (_sites.TryGet(host) is { } site)
        {
            if (await _passcodes.TryHandleAsync(context, site)) return;

            if (SiteHub.Handles(context.Request.Path))
            {
                await PassToHubAsync(context, site);
                return;
            }

            if (await _hostEndpoints.TryHandleAsync(context, site)) return;

            await _functions.HandleAsync(context, site, () => _content.ServeAsync(context, site));
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

    /// <summary>
    /// Hands a request for the realtime hub on to routing, with the site it is for in
    /// <c>HttpContext.Items</c>, where <see cref="SiteHub"/> reads it. Some are refused here instead,
    /// while the answer can still be plain HTTP with a JSON body a script can read, and before the
    /// site's <c>[RealtimeConnect]</c> hook can run:
    ///
    ///   * any request that does not come from one of the site's own pages, which
    ///     <see cref="RealtimeRequestCheck"/> decides in three steps: <c>Sec-Fetch-Site</c> when the
    ///     browser sends it, else <c>Origin</c>, else a WebSocket or <c>X-Requested-With</c> (403);
    ///   * a new connection from an address that has started SiteHosting:RealtimeNegotiationsPerMinute
    ///     in the last minute on this site (429, with Retry-After);
    ///   * a new connection to a site that has as many as it may, or from an address that has as
    ///     many to it as one address may (503).
    ///
    /// A new connection is a negotiation, or a WebSocket that skipped it (see
    /// <see cref="SiteHub.StartsConnection"/>). The hub counts again, exactly, as it registers the
    /// connection, and closes one over either limit; refusing here as well means a page that cannot
    /// get in is told so by the failed start, instead of seeing a connection open and close at once.
    /// </summary>
    private async Task PassToHubAsync(HttpContext context, SiteRecord site)
    {
        // Every answer under /_host/, the hub's own included, is for the site's pages alone.
        SiteHostEndpoints.MarkSameOrigin(context.Response);

        if (RealtimeRequestCheck.Refusal(context.Request.Headers, RealtimeRequestCheck.IsWebSocketUpgrade(context), site.Domain) is { } refusal)
        {
            await SiteHostEndpoints.WriteErrorAsync(context, StatusCodes.Status403Forbidden, refusal);
            return;
        }

        if (SiteHub.StartsConnection(context.Request) && !await AdmitAsync(context, site)) return;

        context.Items[SiteHub.SiteItemKey] = site;
        await _next(context);
    }

    /// <summary>Whether a new connection may start; when not, the refusal has been written.</summary>
    private async Task<bool> AdmitAsync(HttpContext context, SiteRecord site)
    {
        if (_realtime.MaxConnectionsPerSite == 0)
        {
            await SiteHostEndpoints.WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
                "Realtime is turned off on this server (SiteHosting:RealtimeMaxConnectionsPerSite is 0).");
            return false;
        }

        var address = context.Connection.RemoteIpAddress;

        // Counted before the capacity checks, so a client hammering a full site still runs out of tries.
        if (!_negotiations.TryAcquire(site.Domain, address, _options.RealtimeNegotiationsPerMinute, out var retryAfter))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            await SiteHostEndpoints.WriteErrorAsync(context, StatusCodes.Status429TooManyRequests,
                $"This address has started too many realtime connections to {site.Domain} in the last minute. " +
                $"Try again in {seconds} second{(seconds == 1 ? "" : "s")}.");
            return false;
        }

        if (_realtime.IsFull(site.Domain))
        {
            context.Response.Headers.RetryAfter = "30";
            await SiteHostEndpoints.WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
                $"{site.Domain} has as many realtime connections open as it allows. Try again later.");
            return false;
        }

        if (_realtime.IsAddressFull(site.Domain, ClientAddress.Key(address)))
        {
            context.Response.Headers.RetryAfter = "30";
            await SiteHostEndpoints.WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
                $"This address has {_realtime.MaxConnectionsPerAddress} realtime connections open to {site.Domain}, the most " +
                "one address may. Close a page that has the site open, or try again later.");
            return false;
        }

        return true;
    }
}

public static class SiteHostingMiddlewareExtensions
{
    public static IApplicationBuilder UseHostedSites(this IApplicationBuilder app) =>
        app.UseMiddleware<SiteHostingMiddleware>();
}
