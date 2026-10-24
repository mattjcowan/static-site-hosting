using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Services;

namespace StaticSiteHost.Tls;

/// <summary>
/// The part of HTTPS that happens per request, ahead of everything else:
///
///   * Let's Encrypt's check, <c>/.well-known/acme-challenge/{token}</c>, answered for an order in
///     progress, on any host and either port, before any site, function or passcode can;
///   * an HTTP request for a host that has a certificate sent to HTTPS (308, so a POST stays a
///     POST), unless its site turned that off; <c>/healthz</c> is never redirected. A site's other
///     domains go by the site's settings, and a management host by none, even under a site's wildcard;
///   * <c>Strict-Transport-Security</c> on HTTPS answers for a site that turned it on.
///
/// With HTTPS ended in front of the server instead (SiteHosting:TrustForwardedHeaders), the
/// request reads as HTTPS here and nothing is redirected.
/// </summary>
public sealed class TlsMiddleware
{
    private const string HstsValue = "max-age=31536000";

    private readonly RequestDelegate _next;
    private readonly AcmeChallenges _challenges;
    private readonly CertificateManager _certificates;
    private readonly SiteStore _sites;
    private readonly SiteHostingOptions _options;

    public TlsMiddleware(RequestDelegate next, AcmeChallenges challenges, CertificateManager certificates, SiteStore sites, IOptions<SiteHostingOptions> options)
    {
        _next = next;
        _challenges = challenges;
        _certificates = certificates;
        _sites = sites;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;

        if (request.Path.StartsWithSegments("/.well-known/acme-challenge", StringComparison.OrdinalIgnoreCase, out var rest) &&
            rest.Value is { Length: > 1 } tokenPath && _challenges.AnswerFor(tokenPath[1..]) is { } answer)
        {
            context.Response.ContentType = "text/plain";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(answer);
            return;
        }

        var host = request.Host.Host.TrimEnd('.').ToLowerInvariant();
        var site = host.Length > 0 && !_options.IsManagementHost(host) ? _sites.Resolve(host) : null;

        if (!request.IsHttps)
        {
            if (host.Length > 0 && !request.Path.StartsWithSegments("/healthz") &&
                _certificates.Serves(host) && (site?.Tls?.RedirectToHttps ?? true))
            {
                var port = _options.Tls.PublicHttpsPort;
                var location = $"https://{host}{(port == 443 ? "" : ":" + port)}{request.PathBase}{request.Path}{request.QueryString}";
                context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
                context.Response.Headers.Location = location;
                return;
            }
        }
        else if (site?.Tls?.Hsts == true)
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.StrictTransportSecurity = HstsValue;
                return Task.CompletedTask;
            });
        }

        await _next(context);
    }
}
