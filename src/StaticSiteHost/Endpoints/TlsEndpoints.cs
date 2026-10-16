using System.Security.Claims;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Models;
using StaticSiteHost.Services;
using StaticSiteHost.Tls;

namespace StaticSiteHost.Endpoints;

/// <summary>
/// HTTPS over the API: the server's certificates, each site's HTTPS settings, uploads and renewals.
/// Reading a site's is for anyone who can see the site; everything else is for administrators,
/// since a certificate's source decides what the server asks Let's Encrypt for, and HSTS binds
/// browsers for a year.
/// </summary>
public static class TlsEndpoints
{
    public static RouteGroupBuilder MapTlsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/tls", (CertificateManager certificates, IOptions<SiteHostingOptions> options) =>
            Results.Ok(DescribeServer(certificates, options.Value.Tls)))
            .RequireAuthorization(Policies.ApiAdministrator);

        api.MapPost("/tls/renew/{host}", (string host, CertificateManager certificates) =>
        {
            if (!certificates.Enabled) return Results.BadRequest(new { error = NotOn });

            var status = certificates.Status().FirstOrDefault(s => s.Host.Equals(host.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
            if (status is null) return Results.NotFound(new { error = $"This server does not answer for '{host}'." });

            certificates.RenewNow(status.Host);
            return Results.Accepted(value: new { ok = true, host = status.Host, note = Renewing });
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapPost("/tls/shared", async (CertificateUploadRequest? body, ClaimsPrincipal principal, CertificateManager certificates) =>
        {
            var (info, error) = await certificates.AddSharedAsync(body?.Certificate, body?.PrivateKey, ApiEndpoints.Actor(principal));
            return info is null ? Results.BadRequest(new { error }) : Results.Ok(info);
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapDelete("/tls/shared/{id}", async (string id, ClaimsPrincipal principal, CertificateManager certificates) =>
            await certificates.RemoveSharedAsync(id, ApiEndpoints.Actor(principal))
                ? Results.Ok(new { ok = true, id })
                : Results.NotFound(new { error = "No shared certificate has that id." }))
            .RequireAuthorization(Policies.ApiAdministrator);

        api.MapGet("/sites/{domain}/tls", (string domain, SiteStore sites, CertificateManager certificates) =>
            WithSite(domain, sites, site => Results.Ok(DescribeSite(site, certificates))));

        api.MapPut("/sites/{domain}/tls", (string domain, SiteTlsRequest? body, ClaimsPrincipal principal, SiteStore sites, CertificateManager certificates) =>
            WithSite(domain, sites, async site =>
                await certificates.SetSiteAsync(site, body?.Source, body?.RedirectToHttps, body?.Hsts, ApiEndpoints.Actor(principal)) is { } error
                    ? Results.BadRequest(new { error })
                    : Results.Ok(DescribeSite(site, certificates))))
            .RequireAuthorization(Policies.ApiAdministrator);

        api.MapPost("/sites/{domain}/tls/renew", (string domain, SiteStore sites, CertificateManager certificates) =>
            WithSite(domain, sites, site =>
            {
                if (!certificates.Enabled) return Results.BadRequest(new { error = NotOn });
                certificates.RenewNow(site.Domain);
                return Results.Accepted(value: new { ok = true, host = site.Domain, note = Renewing });
            }))
            .RequireAuthorization(Policies.ApiAdministrator);

        api.MapPut("/sites/{domain}/tls/certificate", (string domain, CertificateUploadRequest? body, ClaimsPrincipal principal, SiteStore sites, CertificateManager certificates) =>
            WithSite(domain, sites, async site =>
                await certificates.UploadForSiteAsync(site, body?.Certificate, body?.PrivateKey, ApiEndpoints.Actor(principal)) is { } error
                    ? Results.BadRequest(new { error })
                    : Results.Ok(DescribeSite(site, certificates))))
            .RequireAuthorization(Policies.ApiAdministrator);

        api.MapDelete("/sites/{domain}/tls/certificate", (string domain, ClaimsPrincipal principal, SiteStore sites, CertificateManager certificates) =>
            WithSite(domain, sites, async site =>
            {
                await certificates.RemoveUploadedAsync(site, ApiEndpoints.Actor(principal));
                return Results.Ok(DescribeSite(site, certificates));
            }))
            .RequireAuthorization(Policies.ApiAdministrator);

        return api;
    }

    public const string NotOn = "HTTPS is not on in this server (SiteHosting:Tls:Enabled). It is ended in front of it, so there is nothing to renew here.";

    public const string Renewing = "Asked for a new certificate. It takes a few seconds to a minute; GET the host's TLS status to see it.";

    // ---------------------------------------------------------------- shapes

    internal static object DescribeServer(CertificateManager certificates, TlsOptions tls) => new
    {
        enabled = tls.Enabled,
        httpPort = tls.HttpPort,
        httpsPort = tls.HttpsPort,
        defaultSource = tls.DefaultSource,
        acmeDirectory = tls.AcmeDirectoryUrl,
        staging = tls.IsStaging,
        acmeEmail = tls.AcmeEmail,
        renewDaysBefore = tls.RenewDaysBefore,
        hosts = tls.Enabled ? certificates.Status().Select(DescribeHost) : [],
        shared = certificates.Shared.Select(s => new
        {
            id = s.Info.Id,
            names = s.Info.Names,
            issuer = s.Info.Issuer,
            notAfter = s.Info.NotAfterUtc,
            uploadedBy = s.Info.UploadedBy,
            uploadedUtc = s.Info.UploadedUtc
        })
    };

    internal static object DescribeSite(SiteRecord site, CertificateManager certificates) => new
    {
        domain = site.Domain,
        httpsInServer = certificates.Enabled,
        source = site.Tls?.Source ?? CertificateSources.Automatic,
        redirectToHttps = site.Tls?.RedirectToHttps ?? true,
        hsts = site.Tls?.Hsts ?? false,
        status = certificates.Enabled ? DescribeHost(certificates.StatusOf(site.Domain)) : null
    };

    internal static object DescribeHost(HostCertificateStatus status) => new
    {
        host = status.Host,
        site = status.IsSite,
        source = status.Wanted,
        effective = status.Effective,
        problem = status.Problem,
        issuing = status.Busy,
        certificate = status.Served is { } served
            ? new
            {
                source = status.ServedSource,
                names = served.Names,
                issuer = served.Issuer,
                notBefore = served.NotBefore,
                notAfter = served.NotAfter,
                selfSigned = served.SelfSigned
            }
            : null,
        renewsAt = status.RenewsAt,
        lastError = status.State.LastError,
        lastAttemptUtc = status.State.LastAttemptUtc,
        nextAttemptUtc = status.State.NextAttemptUtc,
        failures = status.State.Failures
    };

    private static IResult WithSite(string domain, SiteStore sites, Func<SiteRecord, IResult> action)
    {
        var (normalized, error) = SiteStore.NormalizeDomain(domain);
        if (normalized is null) return Results.BadRequest(new { error });
        return sites.TryGet(normalized) is { } site ? action(site) : Results.NotFound(new { error = $"No site is published at '{normalized}'." });
    }

    private static async Task<IResult> WithSite(string domain, SiteStore sites, Func<SiteRecord, Task<IResult>> action)
    {
        var (normalized, error) = SiteStore.NormalizeDomain(domain);
        if (normalized is null) return Results.BadRequest(new { error });
        return sites.TryGet(normalized) is { } site ? await action(site) : Results.NotFound(new { error = $"No site is published at '{normalized}'." });
    }
}

/// <summary>Body of PUT /api/v1/sites/{domain}/tls. Each field left out is left as it is.</summary>
public sealed record SiteTlsRequest(string? Source, bool? RedirectToHttps, bool? Hsts);

/// <summary>Body of the certificate uploads: the certificate (with any intermediates after it) and its private key, both PEM.</summary>
public sealed record CertificateUploadRequest(string? Certificate, string? PrivateKey);
