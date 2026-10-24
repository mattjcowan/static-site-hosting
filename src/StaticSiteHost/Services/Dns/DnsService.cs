using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Tls;

namespace StaticSiteHost.Services.Dns;

/// <summary>Where one site's DNS record stands, for the DNS page and the API.</summary>
/// <param name="State">
/// <c>ok</c>, <c>off</c> (records are not made for every site and this one has none),
/// <c>excluded</c> (removed by hand), <c>noZone</c> (no zone the token can edit holds the domain),
/// <c>noAddress</c> (nothing to point at is set), <c>conflict</c> (a record this server did not make
/// is there), <c>error</c>, or <c>notConnected</c>.
/// </param>
public sealed record SiteDnsStatus(string Domain, string State, string? Detail, SiteDnsRecords? Records, string? Zone)
{
    /// <summary>For one of a site's other domains, the site's domain; null for a site.</summary>
    public string? AliasOf { get; init; }
}

/// <summary>
/// Cloudflare for this server: the API token, DNS records that follow sites, and what certificates
/// need from it (DNS-01 checks, Origin CA certificates).
///
/// Records follow sites in the background: whenever a site is added, renamed or deleted, and every
/// hour, each site that should have a record gets one pointing at the address set here, and so do
/// its other domains; the records of sites and names that are gone are deleted. Only records this server made, whose ids it keeps
/// in config/dns.json, are ever changed or deleted; a site whose name already has a record made
/// elsewhere is left alone and shown as such.
/// </summary>
public sealed class DnsService : BackgroundService
{
    public const string DohClientName = "doh";

    private const string ProtectorPurpose = "StaticSiteHost.Dns";
    private static readonly TimeSpan ZonesLifetime = TimeSpan.FromMinutes(10);

    private readonly JsonFileStore<DnsSettings> _store;
    private readonly IDataProtector _protector;
    private readonly ICloudflareApi _api;
    private readonly SiteStore _sites;
    private readonly AuditLog _audit;
    private readonly IHttpClientFactory _http;
    private readonly SiteHostingOptions _options;
    private readonly ILogger<DnsService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile IReadOnlyDictionary<string, SiteDnsStatus> _status = new Dictionary<string, SiteDnsStatus>();
    private (DateTimeOffset At, IReadOnlyList<CloudflareZone> Zones)? _zones;
    private readonly Dictionary<string, (DateTimeOffset At, bool Allowed)> _originAllowed = new(StringComparer.Ordinal);

    public DnsService(
        DataPaths paths, IDataProtectionProvider protection, ICloudflareApi api, SiteStore sites, AuditLog audit,
        IHttpClientFactory http, IOptions<SiteHostingOptions> options, ILogger<DnsService> logger)
    {
        _store = new JsonFileStore<DnsSettings>(Path.Combine(paths.ConfigDir, "dns.json"));
        _protector = protection.CreateProtector(ProtectorPurpose);
        _api = api;
        _sites = sites;
        _audit = audit;
        _http = http;
        _options = options.Value;
        _logger = logger;
        _sites.Changed += Nudge;
    }

    // ---------------------------------------------------------------- the token

    public async Task<DnsSettings> SettingsAsync(CancellationToken ct = default) => await _store.ReadAsync(ct);

    public async Task<bool> ConnectedAsync(CancellationToken ct = default) => await TokenAsync(ct) is not null;

    /// <summary>The token, decrypted, or null when there is none or it cannot be read.</summary>
    private async Task<string?> TokenAsync(CancellationToken ct)
    {
        var settings = await _store.ReadAsync(ct);
        if (settings.CloudflareToken is not { Length: > 0 } stored) return null;
        try
        {
            return _protector.Unprotect(stored);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            _logger.LogWarning("The Cloudflare token cannot be read: the data-protection keys changed. Save it again under DNS.");
            return null;
        }
    }

    /// <summary>Checks a token with Cloudflare and saves it, encrypted. Returns why not.</summary>
    public async Task<KeyedText?> SetTokenAsync(string? token, string actor, CancellationToken ct)
    {
        token = token?.Trim();
        if (string.IsNullOrEmpty(token)) return new KeyedText("dns.error.noToken");

        if (await _api.VerifyAsync(token, ct) is { } problem) return new KeyedText("dns.error.tokenRefused", new { problem });

        IReadOnlyList<CloudflareZone> zones;
        try
        {
            zones = await _api.ZonesAsync(token, ct);
        }
        catch (Exception ex) when (ex is CloudflareException or HttpRequestException)
        {
            return new KeyedText("dns.error.tokenRefused", new { problem = ex.Message });
        }

        if (zones.Count == 0) return new KeyedText("dns.error.noZones");

        await _store.UpdateAsync(settings =>
        {
            settings.CloudflareToken = _protector.Protect(token);
            settings.TokenSetUtc = DateTimeOffset.UtcNow;
            settings.TokenSetBy = actor;
            return (true, true);
        }, ct);

        lock (_originAllowed) _originAllowed.Clear();
        _zones = (DateTimeOffset.UtcNow, zones);
        await _audit.WriteAsync("dns.token.set", actor, new { zones = zones.Select(z => z.Name) });
        Nudge();
        return null;
    }

    /// <summary>Forgets the token. Records made with it stay in Cloudflare, and are no longer changed from here.</summary>
    public async Task RemoveTokenAsync(string actor, CancellationToken ct)
    {
        await _store.UpdateAsync(settings =>
        {
            settings.CloudflareToken = null;
            settings.TokenSetUtc = null;
            settings.TokenSetBy = null;
            return (true, true);
        }, ct);

        _zones = null;
        lock (_originAllowed) _originAllowed.Clear();
        await _audit.WriteAsync("dns.token.remove", actor);
        Nudge();
    }

    // ---------------------------------------------------------------- zones

    /// <summary>The zones the token can see, read again every 10 minutes. Empty without a token.</summary>
    public async Task<IReadOnlyList<CloudflareZone>> ZonesAsync(CancellationToken ct, bool fresh = false)
    {
        if (await TokenAsync(ct) is not { } token) return [];
        if (!fresh && _zones is { } cached && DateTimeOffset.UtcNow - cached.At < ZonesLifetime) return cached.Zones;

        var zones = await _api.ZonesAsync(token, ct);
        _zones = (DateTimeOffset.UtcNow, zones);
        return zones;
    }

    /// <summary>The zone that holds a name: the longest whose domain the name is or ends with. A wildcard's <c>*.</c> is ignored.</summary>
    public static CloudflareZone? ZoneFor(string name, IReadOnlyList<CloudflareZone> zones)
    {
        var host = name.TrimStart('*', '.').TrimEnd('.').ToLowerInvariant();
        return zones
            .Where(z => host == z.Name || host.EndsWith("." + z.Name, StringComparison.Ordinal))
            .OrderByDescending(z => z.Name.Length)
            .FirstOrDefault();
    }

    /// <summary>Whether certificates for the name can be checked through DNS: a token, and a zone it can edit that holds the name.</summary>
    public async Task<bool> CanCheckThroughDnsAsync(string name, CancellationToken ct)
    {
        try
        {
            return ZoneFor(name, await ZonesAsync(ct)) is not null;
        }
        catch (Exception ex) when (ex is CloudflareException or HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>Whether the token may make Cloudflare Origin certificates for the name's zone. Asked once every 10 minutes per zone.</summary>
    public async Task<bool> CanMakeOriginCertificatesAsync(string name, CancellationToken ct)
    {
        if (await TokenAsync(ct) is not { } token) return false;
        CloudflareZone? zone;
        try
        {
            zone = ZoneFor(name, await ZonesAsync(ct));
        }
        catch (Exception ex) when (ex is CloudflareException or HttpRequestException)
        {
            return false;
        }

        if (zone is null) return false;
        lock (_originAllowed)
        {
            if (_originAllowed.TryGetValue(zone.Id, out var known) && DateTimeOffset.UtcNow - known.At < ZonesLifetime) return known.Allowed;
        }

        var allowed = await _api.CanMakeOriginCertificatesAsync(token, zone.Id, ct);
        lock (_originAllowed) _originAllowed[zone.Id] = (DateTimeOffset.UtcNow, allowed);
        return allowed;
    }

    // ---------------------------------------------------------------- certificates

    /// <summary>A DNS-01 solver for an ACME order: TXT records at <c>_acme-challenge.</c> each name, through Cloudflare.</summary>
    public async Task<IAcmeChallengeSolver> ChallengeSolverAsync(CancellationToken ct)
    {
        var token = await TokenAsync(ct) ?? throw new CloudflareException("No Cloudflare token is saved.");
        return new CloudflareChallengeSolver(_api, token, await ZonesAsync(ct), _http, _logger);
    }

    /// <summary>A Cloudflare Origin CA certificate for the names, from a CSR (PEM) made here.</summary>
    public async Task<CloudflareOriginCertificate> CreateOriginCertificateAsync(IReadOnlyList<string> names, string csrPem, CancellationToken ct)
    {
        var token = await TokenAsync(ct) ?? throw new CloudflareException("No Cloudflare token is saved.");
        return await _api.CreateOriginCertificateAsync(token, names, csrPem, ct);
    }

    /// <summary>Revokes an Origin CA certificate this server made. Failures are only logged: it is also of no use without its key.</summary>
    public async Task RevokeOriginCertificateAsync(string id, CancellationToken ct)
    {
        try
        {
            if (await TokenAsync(ct) is { } token) await _api.RevokeOriginCertificateAsync(token, id, ct);
        }
        catch (Exception ex) when (ex is CloudflareException or HttpRequestException)
        {
            _logger.LogWarning("Could not revoke the Cloudflare Origin certificate {Id}: {Error}", id, ex.Message);
        }
    }

    // ---------------------------------------------------------------- records for sites

    public IReadOnlyDictionary<string, SiteDnsStatus> Status => _status;

    /// <summary>Changes what records point at and whether every site gets one. Returns why not.</summary>
    public async Task<KeyedText?> SetRecordsAsync(bool forEverySite, string? ipv4, string? ipv6, string? target, bool proxied, string actor, CancellationToken ct)
    {
        ipv4 = Blank(ipv4);
        ipv6 = Blank(ipv6);
        target = Blank(target)?.TrimEnd('.').ToLowerInvariant();

        if (ipv4 is not null && (!IPAddress.TryParse(ipv4, out var v4) || v4.AddressFamily != AddressFamily.InterNetwork))
            return new KeyedText("dns.error.badIpv4", new { value = ipv4 });
        if (ipv6 is not null && (!IPAddress.TryParse(ipv6, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6))
            return new KeyedText("dns.error.badIpv6", new { value = ipv6 });
        if (target is not null && (!CertificateStore.IsHostName(target) || !target.Contains('.') || IPAddress.TryParse(target, out _)))
            return new KeyedText("dns.error.badTarget", new { value = target });
        if (target is not null && (ipv4 is not null || ipv6 is not null)) return new KeyedText("dns.error.targetAndAddress");
        if (forEverySite && ipv4 is null && ipv6 is null && target is null) return new KeyedText("dns.error.noAddress");

        await _store.UpdateAsync(settings =>
        {
            settings.RecordsForSites = forEverySite;
            settings.Ipv4 = ipv4 is null ? null : IPAddress.Parse(ipv4).ToString();
            settings.Ipv6 = ipv6 is null ? null : IPAddress.Parse(ipv6).ToString();
            settings.Target = target;
            settings.Proxied = proxied;
            return (true, true);
        }, ct);

        await _audit.WriteAsync("dns.records.settings", actor, new { forEverySite, ipv4, ipv6, target, proxied });
        await ReconcileAsync(ct);
        return null;
    }

    /// <summary>Gives a site a record now, even when records are not made for every site. Returns why not.</summary>
    public async Task<KeyedText?> AddSiteAsync(string domain, bool? proxied, string actor, CancellationToken ct)
    {
        if (_sites.TryGet(domain) is not { } site) return new KeyedText("dns.error.noSite", new { domain });
        if (!await ConnectedAsync(ct)) return new KeyedText("dns.error.notConnected");

        await _store.UpdateAsync(settings =>
        {
            settings.Excluded.RemoveAll(d => d.Equals(site.Domain, StringComparison.OrdinalIgnoreCase));
            var entry = settings.Sites.FirstOrDefault(e => e.Domain == site.Domain);
            if (entry is null)
            {
                entry = new SiteDnsRecords { Domain = site.Domain, Proxied = proxied ?? settings.Proxied };
                settings.Sites.Add(entry);
            }
            else if (proxied is { } value)
            {
                entry.Proxied = value;
            }

            return (true, true);
        }, ct);

        await _audit.WriteAsync("dns.site.add", actor, new { domain = site.Domain, proxied });
        await ReconcileAsync(ct);
        return _status.TryGetValue(site.Domain, out var status) && status.State != "ok" ? Problem(status) : null;
    }

    /// <summary>Deletes the records this server made for a site, and makes none for it again until asked.</summary>
    public async Task<KeyedText?> RemoveSiteAsync(string domain, string actor, CancellationToken ct)
    {
        if (_sites.TryGet(domain) is not { } site) return new KeyedText("dns.error.noSite", new { domain });

        await _gate.WaitAsync(ct);
        try
        {
            var settings = await _store.ReadAsync(ct);
            var entry = settings.Sites.FirstOrDefault(e => e.Domain == site.Domain);
            if (entry is not null && entry.Records.Count > 0)
            {
                if (await TokenAsync(ct) is not { } token) return new KeyedText("dns.error.notConnected");
                try
                {
                    foreach (var record in entry.Records) await _api.DeleteRecordAsync(token, entry.ZoneId, record.Id, ct);
                }
                catch (Exception ex) when (ex is CloudflareException or HttpRequestException)
                {
                    return new KeyedText("dns.error.cloudflare", new { problem = ex.Message });
                }
            }

            await _store.UpdateAsync(s =>
            {
                s.Sites.RemoveAll(e => e.Domain == site.Domain);
                if (!s.Excluded.Contains(site.Domain, StringComparer.OrdinalIgnoreCase)) s.Excluded.Add(site.Domain);
                return (true, true);
            }, ct);
        }
        finally
        {
            _gate.Release();
        }

        await _audit.WriteAsync("dns.site.remove", actor, new { domain = site.Domain });
        await ReconcileAsync(ct);
        return null;
    }

    /// <summary>Carries a site's DNS choices to its new name: the old name's records go, the new name gets them.</summary>
    public async Task SiteRenamedAsync(string from, string to)
    {
        await _store.UpdateAsync(settings =>
        {
            var changed = false;
            if (settings.Sites.FirstOrDefault(e => e.Domain == from) is { } old && settings.Sites.All(e => e.Domain != to))
            {
                settings.Sites.Add(new SiteDnsRecords { Domain = to, Proxied = old.Proxied });
                changed = true;
            }

            if (settings.Excluded.Contains(from, StringComparer.OrdinalIgnoreCase))
            {
                settings.Excluded.Add(to);
                changed = true;
            }

            return (true, changed);
        });
        Nudge();
    }

    /// <summary>This server's public address as Cloudflare sees it, for the form. Null when it cannot be found.</summary>
    public async Task<IPAddress?> DetectAddressAsync(CancellationToken ct)
    {
        try
        {
            var text = await _http.CreateClient(DohClientName).GetStringAsync("https://cloudflare.com/cdn-cgi/trace", ct);
            var line = text.Split('\n').FirstOrDefault(l => l.StartsWith("ip=", StringComparison.Ordinal));
            return line is not null && IPAddress.TryParse(line[3..].Trim(), out var address) ? address : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public void Nudge()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already asked.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Bringing the DNS records up to date failed; trying again later");
            }

            try
            {
                await _wake.WaitAsync(TimeSpan.FromHours(1), stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Brings every site's records in line with the settings: made where wanted, pointed at the
    /// address set, proxied as chosen, and deleted for sites that are gone. Each site on its own,
    /// so one that fails never stops the rest.
    ///
    /// A site's other domains (<see cref="SiteRecord.Aliases"/>) are names of their own here, a
    /// wildcard one included (a <c>*.app</c> record), and follow their site: they have records
    /// while the site does, proxied as the site's are, and lose them when it stops having any.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var settings = await _store.ReadAsync(ct);
            var siteList = _sites.List();
            var sites = siteList.Select(s => s.Domain).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var status = new Dictionary<string, SiteDnsStatus>(StringComparer.OrdinalIgnoreCase);

            // Every name that should be in DNS, to the domain of the site it belongs to.
            var names = sites.ToDictionary(domain => domain, domain => domain, StringComparer.OrdinalIgnoreCase);
            foreach (var site in siteList)
                foreach (var alias in site.Aliases) names.TryAdd(alias, site.Domain);

            // Other domains carry the site they belong to, for the API and the tools.
            void Publish()
            {
                foreach (var (name, owner) in names)
                    if (!name.Equals(owner, StringComparison.OrdinalIgnoreCase) && status.TryGetValue(name, out var entry))
                        status[name] = entry with { AliasOf = owner };
                _status = status;
            }

            if (await TokenAsync(ct) is not { } token)
            {
                foreach (var name in names.Keys) status[name] = new SiteDnsStatus(name, "notConnected", null, null, null);
                Publish();
                return;
            }

            IReadOnlyList<CloudflareZone> zones;
            try
            {
                zones = await ZonesAsync(ct, fresh: true);
            }
            catch (Exception ex) when (ex is CloudflareException or HttpRequestException)
            {
                foreach (var name in names.Keys) status[name] = new SiteDnsStatus(name, "error", ex.Message, null, null);
                Publish();
                return;
            }

            var changed = false;

            // Whether a site has records, or is to get them: the other domains' follow from this.
            bool HasRecords(string domain) =>
                settings.Sites.Any(e => e.Domain == domain) ||
                settings.RecordsForSites && !settings.Excluded.Contains(domain, StringComparer.OrdinalIgnoreCase);

            // The records of names that are gone: deleted sites, other domains removed, and the other
            // domains of a site that has no records.
            foreach (var entry in settings.Sites.Where(e => !names.TryGetValue(e.Domain, out var owner) || owner != e.Domain && !HasRecords(owner)).ToList())
            {
                try
                {
                    foreach (var record in entry.Records) await _api.DeleteRecordAsync(token, entry.ZoneId, record.Id, ct);
                    settings.Sites.Remove(entry);
                    changed = true;
                    await _audit.WriteAsync("dns.site.deleted", "server", new { domain = entry.Domain, records = entry.Records.Select(r => r.Type) });
                }
                catch (Exception ex) when (ex is CloudflareException or HttpRequestException)
                {
                    _logger.LogWarning("Could not delete the DNS records of {Domain}, which is gone: {Error}", entry.Domain, ex.Message);
                }
            }

            changed |= settings.Excluded.RemoveAll(d => !sites.Contains(d)) > 0;

            var wantedRecords = Wanted(settings);
            var comment = Comment();
            foreach (var (domain, owner) in names.OrderBy(n => n.Key, StringComparer.Ordinal))
            {
                var entry = settings.Sites.FirstOrDefault(e => e.Domain == domain);
                var proxied = settings.Proxied;
                if (owner != domain)
                {
                    if (!HasRecords(owner))
                    {
                        var ownerExcluded = settings.Excluded.Contains(owner, StringComparer.OrdinalIgnoreCase);
                        status[domain] = new SiteDnsStatus(domain, ownerExcluded ? "excluded" : "off", null, null, ZoneFor(domain, zones)?.Name);
                        continue;
                    }

                    proxied = settings.Sites.FirstOrDefault(e => e.Domain == owner)?.Proxied ?? settings.Proxied;
                    if (entry is not null) entry.Proxied = proxied;
                }
                else
                {
                    var excluded = settings.Excluded.Contains(domain, StringComparer.OrdinalIgnoreCase);
                    if (entry is null && (!settings.RecordsForSites || excluded))
                    {
                        status[domain] = new SiteDnsStatus(domain, excluded ? "excluded" : "off", null, null, ZoneFor(domain, zones)?.Name);
                        continue;
                    }
                }

                if (ZoneFor(domain, zones) is not { } zone)
                {
                    status[domain] = new SiteDnsStatus(domain, "noZone", null, entry, null);
                    continue;
                }

                if (wantedRecords.Count == 0)
                {
                    status[domain] = new SiteDnsStatus(domain, "noAddress", null, entry, zone.Name);
                    continue;
                }

                var isNew = entry is null;
                entry ??= new SiteDnsRecords { Domain = domain, ZoneId = zone.Id, Proxied = proxied };
                try
                {
                    var (state, detail, entryChanged) = await ApplyAsync(token, zone, entry, wantedRecords, comment, ct);
                    if (entryChanged && isNew) settings.Sites.Add(entry);
                    changed |= entryChanged;
                    status[domain] = new SiteDnsStatus(domain, state, detail, entry.Records.Count > 0 ? entry : null, zone.Name);
                }
                catch (Exception ex) when (ex is CloudflareException or HttpRequestException)
                {
                    status[domain] = new SiteDnsStatus(domain, "error", ex.Message, isNew ? null : entry, zone.Name);
                    _logger.LogWarning("Could not bring the DNS records of {Domain} up to date: {Error}", domain, ex.Message);
                }
            }

            if (changed)
            {
                await _store.UpdateAsync(stored =>
                {
                    stored.Sites = settings.Sites;
                    stored.Excluded = settings.Excluded;
                    return (true, true);
                }, ct);
            }

            Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Makes one site's records what they should be. Records made elsewhere at its name are left as
    /// they are, and the site is reported as in conflict. Returns the state, and whether the entry
    /// (the records this server made) changed.
    /// </summary>
    private async Task<(string State, string? Detail, bool Changed)> ApplyAsync(
        string token, CloudflareZone zone, SiteDnsRecords entry, IReadOnlyList<(string Type, string Content)> wanted, string comment, CancellationToken ct)
    {
        var changed = false;
        if (entry.ZoneId != zone.Id)
        {
            // The zone was made again, or the name moved to another: the ids kept are of no use there.
            entry.ZoneId = zone.Id;
            entry.Records.Clear();
            changed = true;
        }

        var live = (await _api.RecordsAsync(token, zone.Id, entry.Domain, ct))
            .Where(r => r.Type is "A" or "AAAA" or "CNAME")
            .ToList();
        var ours = entry.Records.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var foreign = live.Where(r => !ours.Contains(r.Id)).ToList();

        // Ours that are gone from Cloudflare, or no longer wanted, come out first: a CNAME cannot
        // stand beside anything else.
        foreach (var record in entry.Records.ToList())
        {
            var exists = live.FirstOrDefault(r => r.Id == record.Id);
            if (exists is null || wanted.All(w => w.Type != record.Type) || foreign.Count > 0 && record.Type == "CNAME")
            {
                if (exists is not null) await _api.DeleteRecordAsync(token, zone.Id, record.Id, ct);
                entry.Records.Remove(record);
                changed = true;
            }
        }

        if (foreign.Count > 0)
            return ("conflict", string.Join(", ", foreign.Select(r => $"{r.Type} {r.Content}")), changed);

        foreach (var (type, content) in wanted)
        {
            if (entry.Records.FirstOrDefault(r => r.Type == type) is { } record)
            {
                var exists = live.First(r => r.Id == record.Id);
                if (!SameContent(type, exists.Content, content) || exists.Proxied != entry.Proxied)
                {
                    await _api.UpdateRecordAsync(token, zone.Id, record.Id, content, entry.Proxied, ct);
                    record.Content = content;
                    changed = true;
                }
            }
            else
            {
                var made = await _api.CreateRecordAsync(token, zone.Id, type, entry.Domain, content, entry.Proxied, comment, ct);
                entry.Records.Add(new DnsRecordEntry { Id = made.Id, Type = type, Content = content });
                changed = true;
                await _audit.WriteAsync("dns.record.create", "server", new { domain = entry.Domain, type, content, entry.Proxied });
            }
        }

        return ("ok", null, changed);
    }

    /// <summary>The records each site should have: one CNAME to the target, or an A and an AAAA to the addresses.</summary>
    private static IReadOnlyList<(string Type, string Content)> Wanted(DnsSettings settings)
    {
        if (settings.Target is { Length: > 0 } target) return [("CNAME", target)];
        var wanted = new List<(string, string)>();
        if (settings.Ipv4 is { Length: > 0 } v4) wanted.Add(("A", v4));
        if (settings.Ipv6 is { Length: > 0 } v6) wanted.Add(("AAAA", v6));
        return wanted;
    }

    private static bool SameContent(string type, string a, string b) => type is "A" or "AAAA"
        ? IPAddress.TryParse(a, out var x) && IPAddress.TryParse(b, out var y) && x.Equals(y)
        : string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    /// <summary>What each record's comment in Cloudflare says, so a person there knows where it came from. 100 characters at most.</summary>
    private string Comment()
    {
        var host = _options.ManagementHosts.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h))?.Trim();
        var text = host is null ? "Made by the site server for this site; removed with it." : $"Made by the site server at {host}; removed with the site.";
        return text.Length <= 100 ? text : text[..100];
    }

    private static KeyedText Problem(SiteDnsStatus status) => status.State switch
    {
        "noZone" => new KeyedText("dns.state.noZone"),
        "noAddress" => new KeyedText("dns.state.noAddress"),
        "conflict" => new KeyedText("dns.state.conflict", new { records = status.Detail }),
        _ => new KeyedText("dns.error.cloudflare", new { problem = status.Detail ?? status.State })
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---------------------------------------------------------------- DNS-01

    /// <summary>
    /// DNS-01 through Cloudflare: a TXT record at <c>_acme-challenge.</c> each name, holding the key
    /// authorization's hash. A wildcard and its bare name share one such name, with two values.
    /// Waits until a public resolver sees them all, up to two minutes, then lets the ACME server check.
    /// </summary>
    private sealed class CloudflareChallengeSolver(
        ICloudflareApi api, string token, IReadOnlyList<CloudflareZone> zones, IHttpClientFactory http, ILogger logger) : IAcmeChallengeSolver
    {
        private readonly List<(string ZoneId, string RecordId, string Name, string Value)> _made = [];

        public string Type => "dns-01";

        public async Task PrepareAsync(string identifier, string acmeToken, string keyAuthorization, CancellationToken ct)
        {
            var zone = ZoneFor(identifier, zones) ?? throw new CloudflareException($"No Cloudflare zone this token can edit holds {identifier}.");
            var name = "_acme-challenge." + identifier.TrimStart('*', '.');
            var value = AcmeClient.DnsTxtValue(keyAuthorization);
            var record = await api.CreateRecordAsync(token, zone.Id, "TXT", name, value, proxied: false, comment: "ACME check; removed when it is done.", ct);
            _made.Add((zone.Id, record.Id, name, value));
        }

        public async Task ReadyAsync(CancellationToken ct)
        {
            // Cloudflare's own servers have a new record within seconds; a resolver asked too early
            // remembers that it was not there, so the first look waits a little.
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            var client = http.CreateClient(DohClientName);
            foreach (var group in _made.GroupBy(m => m.Name))
            {
                while (true)
                {
                    var seen = await TxtValuesAsync(client, group.Key, ct);
                    if (group.All(m => seen.Contains(m.Value))) break;
                    if (DateTimeOffset.UtcNow > deadline)
                    {
                        logger.LogInformation("The TXT record at {Name} is not visible through 1.1.1.1 yet; asking Let's Encrypt to check anyway", group.Key);
                        return;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                }
            }
        }

        public async Task CleanupAsync(CancellationToken ct)
        {
            foreach (var (zoneId, recordId, name, _) in _made)
            {
                try
                {
                    await api.DeleteRecordAsync(token, zoneId, recordId, ct);
                }
                catch (Exception ex) when (ex is CloudflareException or HttpRequestException)
                {
                    logger.LogWarning("Could not delete the ACME check record at {Name}: {Error}", name, ex.Message);
                }
            }

            _made.Clear();
        }

        /// <summary>The TXT values at a name, through Cloudflare's DNS-over-HTTPS resolver.</summary>
        private static async Task<HashSet<string>> TxtValuesAsync(HttpClient client, string name, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(name)}&type=TXT");
                request.Headers.Accept.ParseAdd("application/dns-json");
                using var response = await client.SendAsync(request, ct);
                var answer = await response.Content.ReadFromJsonAsync<DohAnswer>(ct);
                return (answer?.Answer ?? []).Select(a => a.Data.Trim('"')).ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
            {
                return [];
            }
        }

        private sealed record DohAnswer([property: JsonPropertyName("Answer")] List<DohRecord>? Answer);

        private sealed record DohRecord([property: JsonPropertyName("data")] string Data);
    }
}
