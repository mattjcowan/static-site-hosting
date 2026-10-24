using System.Security.Claims;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Dns;

namespace StaticSiteHost.Endpoints;

/// <summary>
/// DNS over the API: the Cloudflare token, whether each site gets a DNS record and what it points
/// at, and each site's record. Administrators only: a record decides where a domain's visitors go.
/// </summary>
public static class DnsEndpoints
{
    public static RouteGroupBuilder MapDnsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/dns", async (DnsService dns, CancellationToken ct) => Results.Ok(await DescribeAsync(dns, ct)))
            .RequireAuthorization(Policies.ApiAdministrator);

        // The token is checked with Cloudflare before it is kept, encrypted. It is never sent back.
        api.MapPut("/dns/cloudflare", async (CloudflareTokenRequest? body, ClaimsPrincipal principal, DnsService dns, StringCatalog catalog, CancellationToken ct) =>
            await dns.SetTokenAsync(body?.Token, ApiEndpoints.Actor(principal), ct) is { } error
                ? Results.BadRequest(new { error = error.English(catalog) })
                : Results.Ok(await DescribeAsync(dns, ct)))
            .RequireAuthorization(Policies.ApiAdministrator);

        api.MapDelete("/dns/cloudflare", async (ClaimsPrincipal principal, DnsService dns, CancellationToken ct) =>
        {
            await dns.RemoveTokenAsync(ApiEndpoints.Actor(principal), ct);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapPut("/dns/records", async (DnsRecordsRequest? body, ClaimsPrincipal principal, DnsService dns, StringCatalog catalog, CancellationToken ct) =>
        {
            var current = await dns.SettingsAsync(ct);
            var error = await dns.SetRecordsAsync(
                body?.RecordsForSites ?? current.RecordsForSites,
                body is { Ipv4: not null } ? body.Ipv4 : current.Ipv4,
                body is { Ipv6: not null } ? body.Ipv6 : current.Ipv6,
                body is { Target: not null } ? body.Target : current.Target,
                body?.Proxied ?? current.Proxied,
                ApiEndpoints.Actor(principal), ct);
            return error is null ? Results.Ok(await DescribeAsync(dns, ct)) : Results.BadRequest(new { error = error.English(catalog) });
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapPost("/dns/refresh", async (DnsService dns, CancellationToken ct) =>
        {
            await dns.ReconcileAsync(ct);
            return Results.Ok(await DescribeAsync(dns, ct));
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapPost("/sites/{domain}/dns", async (
            string domain, SiteDnsRequest? body, ClaimsPrincipal principal, DnsService dns, SiteStore sites, StringCatalog catalog, CancellationToken ct) =>
        {
            if (Find(domain, sites) is not { } site) return Results.NotFound(new { error = ApiEndpoints.NoSite(domain, sites) });
            return await dns.AddSiteAsync(site.Domain, body?.Proxied, ApiEndpoints.Actor(principal), ct) is { } error
                ? Results.BadRequest(new { error = error.English(catalog) })
                : Results.Ok(Describe(dns.Status.GetValueOrDefault(site.Domain)));
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapDelete("/sites/{domain}/dns", async (
            string domain, ClaimsPrincipal principal, DnsService dns, SiteStore sites, StringCatalog catalog, CancellationToken ct) =>
        {
            if (Find(domain, sites) is not { } site) return Results.NotFound(new { error = ApiEndpoints.NoSite(domain, sites) });
            return await dns.RemoveSiteAsync(site.Domain, ApiEndpoints.Actor(principal), ct) is { } error
                ? Results.BadRequest(new { error = error.English(catalog) })
                : Results.Ok(new { ok = true, domain = site.Domain });
        }).RequireAuthorization(Policies.ApiAdministrator);

        return api;
    }

    private static SiteRecord? Find(string domain, SiteStore sites) =>
        SiteStore.NormalizeDomain(domain).Domain is { } normalized ? sites.TryGet(normalized) : null;

    internal static async Task<object> DescribeAsync(DnsService dns, CancellationToken ct)
    {
        var settings = await dns.SettingsAsync(ct);
        var connected = await dns.ConnectedAsync(ct);
        IReadOnlyList<CloudflareZone> zones = [];
        string? zonesError = null;
        if (connected)
        {
            try
            {
                zones = await dns.ZonesAsync(ct);
            }
            catch (Exception ex) when (ex is CloudflareException or HttpRequestException)
            {
                zonesError = ex.Message;
            }
        }

        return new
        {
            cloudflare = new { connected, tokenSetUtc = settings.TokenSetUtc, tokenSetBy = settings.TokenSetBy, zones = zones.Select(z => z.Name), zonesError },
            records = new { forEverySite = settings.RecordsForSites, settings.Ipv4, settings.Ipv6, settings.Target, settings.Proxied },
            sites = dns.Status.Values.OrderBy(s => s.Domain, StringComparer.Ordinal).Select(Describe)
        };
    }

    internal static object? Describe(SiteDnsStatus? status) => status is null ? null : new
    {
        domain = status.Domain,
        aliasOf = status.AliasOf,
        state = status.State,
        detail = status.Detail,
        zone = status.Zone,
        proxied = status.Records?.Proxied,
        records = status.Records?.Records.Select(r => new { r.Type, r.Content })
    };
}

/// <summary>Body of PUT /api/v1/dns/cloudflare.</summary>
public sealed record CloudflareTokenRequest(string? Token);

/// <summary>Body of PUT /api/v1/dns/records. Each field left out is left as it is; an empty string clears an address.</summary>
public sealed record DnsRecordsRequest(bool? RecordsForSites, string? Ipv4, string? Ipv6, string? Target, bool? Proxied);

/// <summary>Body of POST /api/v1/sites/{domain}/dns: whether the record goes through Cloudflare's proxy.</summary>
public sealed record SiteDnsRequest(bool? Proxied);
