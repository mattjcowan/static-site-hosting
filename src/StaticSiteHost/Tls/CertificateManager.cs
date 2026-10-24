using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Dns;

namespace StaticSiteHost.Tls;

/// <summary>Answers Let's Encrypt's HTTP checks: the tokens of the orders in progress and what each must answer.</summary>
public sealed class AcmeChallenges
{
    public const string PathPrefix = "/.well-known/acme-challenge/";

    private readonly ConcurrentDictionary<string, string> _answers = new(StringComparer.Ordinal);

    public void Publish(string token, string keyAuthorization) => _answers[token] = keyAuthorization;

    public void Withdraw(string token) => _answers.TryRemove(token, out _);

    public string? AnswerFor(string token) => _answers.GetValueOrDefault(token);
}

/// <summary>One host as the HTTPS pages show it: what it asks for, what it serves, and how renewing it goes.</summary>
/// <param name="Wanted">What the site chose (<see cref="CertificateSources"/>); automatic for a management host.</param>
/// <param name="Effective">What that comes to: a shared certificate or the server's default for automatic.</param>
/// <param name="Problem">Why it cannot have what it asks for, when it cannot.</param>
public sealed record HostCertificateStatus(
    string Host, bool IsSite, string Wanted, string Effective, ServedCertificate? Served, string? ServedSource,
    HostCertificateState State, bool Busy, string? Problem)
{
    /// <summary>When the served certificate is renewed, for one this server renews (Let's Encrypt, self-signed).</summary>
    public DateTimeOffset? RenewsAt { get; init; }

    /// <summary>For one of a site's other domains, the site's domain; null otherwise.</summary>
    public string? AliasOf { get; init; }
}

/// <summary>
/// HTTPS for every host the server answers: each site's domain and exact other domains, and each
/// management host. A name under a site's wildcard other domain gets no certificate of its own: it is
/// served the shared certificate that covers it (<see cref="UnderWildcard"/>). Decides
/// which certificate each serves (see <see cref="Effective"/>), keeps them in memory for the TLS
/// handshake (<see cref="Select"/>), and in the background issues and renews the ones that come
/// from Let's Encrypt or are self-signed. It runs only when SiteHosting:Tls:Enabled is on.
///
/// The background loop wakes hourly, whenever a site changes, and when asked to renew. Each pass
/// first serves whatever is on disk, then issues what is missing or due, one host at a time. A
/// failure is retried after 1, 2, 4… up to 24 hours, so a name whose DNS is not ready yet never
/// runs into Let's Encrypt's limits; Renew now skips the wait.
///
/// With a Cloudflare token (<see cref="DnsService"/>), Let's Encrypt checks a name in one of its
/// zones through DNS (DNS-01) instead of over port 80, which also allows wildcard certificates; and
/// Cloudflare Origin certificates can be made for sites and as shared certificates.
/// </summary>
public sealed class CertificateManager : BackgroundService
{
    /// <summary>The name of the HttpClient that talks to the ACME server.</summary>
    public const string HttpClientName = "acme";

    /// <summary>Top-level domains no public certificate authority issues for, which get a self-signed certificate when automatic.</summary>
    private static readonly string[] PrivateSuffixes = [".localhost", ".test", ".local", ".internal", ".invalid", ".example", ".lan", ".home.arpa"];

    private readonly SiteHostingOptions _options;
    private readonly SiteStore _sites;
    private readonly CertificateStore _store;
    private readonly AcmeChallenges _challenges;
    private readonly DnsService _dns;
    private readonly IHttpClientFactory _http;
    private readonly AuditLog _audit;
    private readonly ILogger<CertificateManager> _logger;

    private readonly ConcurrentDictionary<string, (ServedCertificate Certificate, string Source)> _served = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>For each host served a Let's Encrypt certificate, the ACME directory it came from.</summary>
    private readonly ConcurrentDictionary<string, string> _servedOrigin = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Management hosts already reported as unusable, so the warning is logged once.</summary>
    private readonly ConcurrentDictionary<string, byte> _skippedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _renewNow = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _renewShared = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile IReadOnlyList<(SharedCertificateInfo Info, ServedCertificate Certificate)> _shared = [];
    private readonly Lazy<ServedCertificate> _fallback;
    private volatile string? _issuing;

    public CertificateManager(
        IOptions<SiteHostingOptions> options, SiteStore sites, CertificateStore store, AcmeChallenges challenges, DnsService dns,
        IHttpClientFactory http, AuditLog audit, ILogger<CertificateManager> logger)
    {
        _dns = dns;
        _options = options.Value;
        _sites = sites;
        _store = store;
        _challenges = challenges;
        _http = http;
        _audit = audit;
        _logger = logger;

        // For a handshake that names no host this server has a certificate for: something to
        // finish the handshake with, so the visitor sees a certificate warning rather than a
        // connection that drops for no stated reason.
        _fallback = new Lazy<ServedCertificate>(() =>
        {
            var (chain, key) = CertificateMaterial.CreateSelfSigned("static-site-host");
            return CertificateMaterial.Load(chain, key);
        });

        // Read whether HTTPS is on or not, so the HTTPS page lists what is on disk either way.
        _shared = _store.LoadShared();
        if (Enabled) _sites.Changed += Nudge;
    }

    private TlsOptions Tls => _options.Tls;

    public bool Enabled => Tls.Enabled;

    // ---------------------------------------------------------------- the handshake

    /// <summary>
    /// The certificate for the host a TLS handshake names (SNI): its own, or the fallback. A browser
    /// sends no name for an address (https://203.0.113.5), so then the address the connection came
    /// in on picks the certificate of a host that is that address.
    /// </summary>
    public SslStreamCertificateContext Select(string? serverName, IPAddress? localAddress = null)
    {
        var host = string.IsNullOrEmpty(serverName)
            ? (localAddress is { IsIPv4MappedToIPv6: true } ? localAddress.MapToIPv4() : localAddress)?.ToString()
            : serverName.TrimEnd('.').ToLowerInvariant();
        if (host is null) return _fallback.Value.Context;
        if (_served.TryGetValue(host, out var served)) return served.Certificate.Context;
        return UnderWildcard(host)?.Context ?? _fallback.Value.Context;
    }

    /// <summary>Whether the host has a certificate to serve, so sending HTTP visitors to HTTPS works.</summary>
    public bool Serves(string host) => _served.ContainsKey(host) || UnderWildcard(host) is not null;

    /// <summary>
    /// For a name that reaches a site only through one of its wildcard other domains, the shared
    /// certificate that covers it, unless the site has no HTTPS. Such names get no certificate of
    /// their own: any name a visitor tried would mean another one to get.
    /// </summary>
    private ServedCertificate? UnderWildcard(string host) =>
        _sites.TryGet(host) is null &&
        _sites.AliasOwner(host) is { Wildcard: true } owner &&
        _sites.TryGet(owner.Domain) is { } site &&
        (site.Tls?.Source ?? CertificateSources.Automatic) != CertificateSources.None
            ? SharedFor(host)
            : null;

    /// <summary>The shared certificate that covers a name, such as every name under a wildcard other domain (<c>*.app.example.com</c>), if one does.</summary>
    public ServedCertificate? SharedCovering(string name) => SharedFor(name);

    // ---------------------------------------------------------------- what each host gets

    /// <summary>
    /// The hosts the server answers, with what each asks for: management hosts, then sites, then
    /// sites' exact other domains, each with the site's domain. An other domain asks for what its
    /// site does (<see cref="ForAliases"/>). Wildcard other domains are not hosts here (see
    /// <see cref="UnderWildcard"/>).
    /// </summary>
    /// <summary>
    /// Where a site's exact other domain gets its certificate, given the site's source: the same, except
    /// that an uploaded certificate is for the site's domain alone, and a shared one may not cover the
    /// other domain. Those become automatic, which still takes a shared certificate that covers it.
    /// </summary>
    private static string ForAliases(string source) =>
        source is CertificateSources.Uploaded or CertificateSources.Shared ? CertificateSources.Automatic : source;

    private Dictionary<string, (string Wanted, bool IsSite, string? AliasOf)> Desired()
    {
        var hosts = new Dictionary<string, (string, bool, string?)>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _options.ManagementHosts.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            var host = name.Trim().TrimEnd('.').ToLowerInvariant();
            if (CertificateStore.IsHostName(host)) hosts[host] = (CertificateSources.Automatic, false, null);
            else if (_skippedHosts.TryAdd(host, 0))
                _logger.LogWarning("No certificate for the management host '{Host}': it is not a plain host name (a name or an IPv4 address)", host);
        }

        var sites = _sites.List();
        foreach (var site in sites)
            hosts[site.Domain] = (site.Tls?.Source ?? CertificateSources.Automatic, true, null);
        foreach (var site in sites)
        {
            var wanted = ForAliases(site.Tls?.Source ?? CertificateSources.Automatic);
            foreach (var alias in site.Aliases.Where(a => !SiteAliases.IsWildcard(a)))
                hosts.TryAdd(alias, (wanted, true, site.Domain));
        }

        return hosts;
    }

    /// <summary>
    /// What a host's choice comes to. Automatic is a shared certificate that covers the host if
    /// there is one, otherwise the server's default, except that a name no public authority issues
    /// for (*.localhost, *.test…) is self-signed. Returns why not, when the choice cannot be met.
    /// </summary>
    public (string Effective, string? Problem) Effective(string host, string wanted)
    {
        var shared = SharedFor(host);
        switch (wanted)
        {
            case CertificateSources.Automatic:
                if (shared is not null) return (CertificateSources.Shared, null);
                var configured = (Tls.DefaultSource ?? "").Trim().ToLowerInvariant();
                var fallback = CertificateSources.Defaults.Contains(configured) ? configured : CertificateSources.LetsEncrypt;
                return fallback == CertificateSources.LetsEncrypt && !IsPublicName(host) ? (CertificateSources.SelfSigned, null) : (fallback, null);
            case CertificateSources.Shared:
                return shared is null
                    ? (CertificateSources.Shared, "No shared certificate covers this host. Upload one under HTTPS, or choose another source.")
                    : (CertificateSources.Shared, null);
            case CertificateSources.LetsEncrypt:
                return IsPublicName(host)
                    ? (wanted, null)
                    : (wanted, "Let's Encrypt only issues for public names, and this is not one. Choose self-signed or upload one.");
            case CertificateSources.CloudflareOrigin:
                return IsPublicName(host)
                    ? (wanted, null)
                    : (wanted, "Cloudflare only makes Origin certificates for names in your zones, and this is not one.");
            default:
                return (wanted, null);
        }
    }

    /// <summary>Whether a public certificate authority could issue for the name: a dotted name, not an address, not a private suffix.</summary>
    public static bool IsPublicName(string host) =>
        host.Contains('.') && !IPAddress.TryParse(host, out _) &&
        !PrivateSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || host == suffix[1..]);

    private ServedCertificate? SharedFor(string host) =>
        _shared.Where(s => s.Certificate.Covers(host) && s.Certificate.NotAfter > DateTimeOffset.UtcNow)
            .OrderByDescending(s => s.Certificate.Names.Contains(host, StringComparer.OrdinalIgnoreCase)) // an exact name over a wildcard
            // One browsers trust over a Cloudflare Origin certificate, which only Cloudflare trusts:
            // right whether or not the site goes through Cloudflare's proxy.
            .ThenBy(s => s.Info.Source == CertificateSources.CloudflareOrigin)
            .ThenByDescending(s => s.Certificate.NotAfter)
            .Select(s => s.Certificate)
            .FirstOrDefault();

    // ---------------------------------------------------------------- what the pages show

    public IReadOnlyList<HostCertificateStatus> Status() =>
        Desired().Select(pair => StatusOf(pair.Key, pair.Value.Wanted, pair.Value.IsSite) with { AliasOf = pair.Value.AliasOf })
            .OrderBy(s => s.IsSite).ThenBy(s => s.Host, StringComparer.Ordinal)
            .ToList();

    public HostCertificateStatus StatusOf(string host)
    {
        var desired = Desired();
        return desired.TryGetValue(host, out var wanted)
            ? StatusOf(host, wanted.Wanted, wanted.IsSite) with { AliasOf = wanted.AliasOf }
            : StatusOf(host, CertificateSources.None, isSite: true);
    }

    private HostCertificateStatus StatusOf(string host, string wanted, bool isSite)
    {
        var (effective, problem) = Effective(host, wanted);
        var served = _served.TryGetValue(host, out var s) ? s : default;
        // The last attempt and its error are Let's Encrypt's or Cloudflare's; under any other source they are history.
        var state = effective is CertificateSources.LetsEncrypt or CertificateSources.CloudflareOrigin ? _store.ReadState(host) : new HostCertificateState();
        if (problem is null && effective == CertificateSources.Uploaded && served.Source != CertificateSources.Uploaded)
            problem = "No certificate has been uploaded for this site.";
        return new HostCertificateStatus(host, isSite, wanted, effective, served.Certificate, served.Source, state, _issuing == host, problem)
        {
            RenewsAt = served.Certificate is { } certificate && served.Source is CertificateSources.LetsEncrypt or CertificateSources.SelfSigned or CertificateSources.CloudflareOrigin
                ? RenewAt(certificate, Tls.RenewDaysBefore)
                : null
        };
    }

    public IReadOnlyList<(SharedCertificateInfo Info, ServedCertificate Certificate)> Shared => _shared;

    /// <summary>Every shared certificate, including ones asked for and not got yet, with the one served if any.</summary>
    public IReadOnlyList<(SharedCertificateInfo Info, ServedCertificate? Certificate, bool Busy)> SharedAll()
    {
        var served = _shared.ToDictionary(s => s.Info.Id, s => s.Certificate, StringComparer.Ordinal);
        return _store.SharedInfos()
            .Select(info => (info, served.GetValueOrDefault(info.Id), _issuing == "shared:" + info.Id))
            .OrderBy(s => string.Join(",", s.info.Names), StringComparer.Ordinal)
            .ToList();
    }

    // ---------------------------------------------------------------- changes

    /// <summary>Asks the background loop to look again soon: a site was added, renamed or changed.</summary>
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

    /// <summary>Asks for a new certificate for the host now, whatever its expiry and however recently the last attempt failed.</summary>
    public void RenewNow(string host)
    {
        _renewNow[host] = 0;
        Nudge();
    }

    /// <summary>Saves an uploaded certificate for a site and makes it the site's choice. Returns why not.</summary>
    public async Task<string?> UploadForSiteAsync(SiteRecord site, string? chainPem, string? keyPem, string actor)
    {
        var (certificate, error) = CertificateMaterial.Check(chainPem, keyPem, site.Domain);
        if (certificate is null) return error;

        await _store.SaveHostAsync(site.Domain, CertificateSources.Uploaded, chainPem!, keyPem!);

        var settings = Copy(site.Tls);
        settings.Source = CertificateSources.Uploaded;
        site.Tls = settings;
        await _sites.SaveAsync(site);

        if (Enabled) _served[site.Domain] = (certificate, CertificateSources.Uploaded);
        await _audit.WriteAsync("tls.upload", actor, new { domain = site.Domain, names = certificate.Names, notAfter = certificate.NotAfter });
        return null;
    }

    /// <summary>Removes a site's uploaded certificate; the site goes back to automatic.</summary>
    public async Task RemoveUploadedAsync(SiteRecord site, string actor)
    {
        _store.DeleteHost(site.Domain, CertificateSources.Uploaded);
        if (_served.TryGetValue(site.Domain, out var served) && served.Source == CertificateSources.Uploaded) _served.TryRemove(site.Domain, out _);

        if (site.Tls?.Source == CertificateSources.Uploaded)
        {
            var settings = Copy(site.Tls);
            settings.Source = CertificateSources.Automatic;
            site.Tls = settings;
            await _sites.SaveAsync(site);
        }

        await _audit.WriteAsync("tls.upload.remove", actor, new { domain = site.Domain });
        ApplyAll();
    }

    /// <summary>Saves a certificate for the whole server, used for every host it covers. Returns it, or why not.</summary>
    public async Task<(SharedCertificateInfo? Info, string? Error)> AddSharedAsync(string? chainPem, string? keyPem, string actor)
    {
        var (certificate, error) = CertificateMaterial.Check(chainPem, keyPem, host: null);
        if (certificate is null) return (null, error);

        var info = await _store.AddSharedAsync(chainPem!, keyPem!, certificate, actor);
        _shared = _store.LoadShared();
        await _audit.WriteAsync("tls.shared.add", actor, new { info.Id, info.Names, notAfter = info.NotAfterUtc });
        ApplyAll();
        return (info, null);
    }

    /// <summary>
    /// Asks for a shared certificate got and renewed here, such as <c>*.apps.example.com</c>, from
    /// Let's Encrypt (checked through Cloudflare DNS) or as a Cloudflare Origin certificate. A
    /// wildcard also covers its bare name. It is got within a minute. Returns it, or why not.
    /// </summary>
    public async Task<(SharedCertificateInfo? Info, KeyedText? Error)> RequestSharedAsync(string? names, string? source, string actor, CancellationToken ct)
    {
        if (!Enabled) return (null, new KeyedText("https.request.error.off"));
        source = source?.Trim().ToLowerInvariant();
        if (source is not (CertificateSources.LetsEncrypt or CertificateSources.CloudflareOrigin))
            return (null, new KeyedText("https.request.error.source"));

        var list = new List<string>();
        foreach (var raw in (names ?? "").Split([',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.Trim().TrimEnd('.').ToLowerInvariant();
            var bare = name.StartsWith("*.", StringComparison.Ordinal) ? name[2..] : name;
            if (!CertificateStore.IsHostName(bare) || !IsPublicName(bare)) return (null, new KeyedText("https.request.error.name", new { name = raw }));
            if (!list.Contains(name)) list.Add(name);
            // A wildcard covers one level only: its bare name comes with it.
            if (bare != name && !list.Contains(bare)) list.Add(bare);
        }

        if (list.Count == 0) return (null, new KeyedText("https.request.error.noNames"));
        if (!await _dns.ConnectedAsync(ct)) return (null, new KeyedText("https.request.error.noCloudflare"));
        foreach (var name in list)
        {
            var allowed = source == CertificateSources.LetsEncrypt
                ? await _dns.CanCheckThroughDnsAsync(name, ct)
                : await _dns.CanMakeOriginCertificatesAsync(name, ct);
            if (!allowed)
                return (null, new KeyedText(source == CertificateSources.LetsEncrypt ? "https.request.error.noZone" : "https.request.error.noOrigin", new { name }));
        }

        var info = await _store.AddSharedRequestAsync(list, source, actor);
        await _audit.WriteAsync("tls.shared.request", actor, new { info.Id, names = list, source });
        _renewShared[info.Id] = 0;
        Nudge();
        return (info, null);
    }

    /// <summary>Gets a shared certificate got here again now, whatever its expiry and however recently the last attempt failed.</summary>
    public bool RenewSharedNow(string id)
    {
        if (_store.SharedInfos().All(i => i.Id != id || i.Source == CertificateSources.Uploaded)) return false;
        _renewShared[id] = 0;
        Nudge();
        return true;
    }

    public async Task<bool> RemoveSharedAsync(string id, string actor)
    {
        var removed = _shared.FirstOrDefault(s => s.Info.Id == id).Certificate?.Thumbprint;
        var originId = _store.SharedInfos().FirstOrDefault(i => i.Id == id)?.OriginId;
        if (!_store.RemoveShared(id)) return false;
        if (originId is not null) await _dns.RevokeOriginCertificateAsync(originId, CancellationToken.None);

        _shared = _store.LoadShared();
        // Not kept serving until each host's own arrives: it may have been removed because its key leaked.
        foreach (var (host, served) in _served)
        {
            if (served.Source == CertificateSources.Shared && served.Certificate.Thumbprint == removed) _served.TryRemove(host, out _);
        }

        await _audit.WriteAsync("tls.shared.remove", actor, new { id });
        ApplyAll();
        return true;
    }

    /// <summary>Changes a site's HTTPS settings; null leaves a setting as it is. Returns why not.</summary>
    public async Task<string?> SetSiteAsync(SiteRecord site, string? source, bool? redirectToHttps, bool? hsts, string actor)
    {
        var settings = Copy(site.Tls);
        if (source is not null)
        {
            source = source.Trim().ToLowerInvariant();
            if (!CertificateSources.ForSites.Contains(source))
                return $"The source is one of: {string.Join(", ", CertificateSources.ForSites)}.";
            if (source == CertificateSources.Uploaded && !_store.HasHost(site.Domain, CertificateSources.Uploaded))
                return "Upload a certificate for this site first; uploading one chooses it.";
            settings.Source = source;
        }

        if (redirectToHttps is { } redirect) settings.RedirectToHttps = redirect;
        if (hsts is { } strict) settings.Hsts = strict;

        site.Tls = settings;
        await _sites.SaveAsync(site);
        await _audit.WriteAsync("tls.settings", actor, new { domain = site.Domain, settings.Source, settings.RedirectToHttps, settings.Hsts });

        // What can change at once does, for the site's other domains too; a certificate to get waits
        // for the background loop.
        if (Enabled)
        {
            Apply(site.Domain, settings.Source, forced: false);
            foreach (var alias in site.Aliases.Where(a => !SiteAliases.IsWildcard(a))) Apply(alias, ForAliases(settings.Source), forced: false);
        }
        Nudge();
        return null;
    }

    private static SiteTlsSettings Copy(SiteTlsSettings? settings) => new()
    {
        Source = settings?.Source ?? CertificateSources.Automatic,
        RedirectToHttps = settings?.RedirectToHttps ?? true,
        Hsts = settings?.Hsts ?? false
    };

    // ---------------------------------------------------------------- the loop

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Checking certificates failed; trying again later");
            }

            try
            {
                await _wake.WaitAsync(TimeSpan.FromHours(1), stoppingToken);
                // Several changes often come together (a new site, then its settings): one pass for them all.
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One pass: serve what is on disk, drop what is no longer wanted, then issue what is missing or due.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var desired = Desired();

        foreach (var host in _served.Keys.Where(h => !desired.ContainsKey(h)).ToList()) _served.TryRemove(host, out _);

        // A site whose site.json could not be read is still there: its certificates stay too.
        foreach (var host in _store.StoredHosts().Where(h => !desired.ContainsKey(h) && !_sites.Unreadable.Contains(h)))
        {
            try
            {
                if (OriginId(_store.OriginOf(host, CertificateSources.CloudflareOrigin)) is { } originId)
                    await _dns.RevokeOriginCertificateAsync(originId, ct);
                _store.DeleteHost(host);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not remove the certificates of {Host}, which this server no longer answers for", host);
            }
        }

        // Each host on its own: one that fails never stops the others.
        var toIssue = new List<(string Host, string Source)>();
        foreach (var (host, (wanted, _, _)) in desired)
        {
            try
            {
                if (Apply(host, wanted, forced: _renewNow.TryRemove(host, out _)) is { } issue) toIssue.Add(issue);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not work out the certificate for {Host}", host);
            }
        }

        // Self-signed first: they take a moment, and Let's Encrypt can take a while.
        foreach (var (host, source) in toIssue.OrderBy(i => i.Source != CertificateSources.SelfSigned))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (source == CertificateSources.SelfSigned) await CreateSelfSignedAsync(host);
                else await IssueAsync(host, source, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Could not get a certificate for {Host}", host);
            }
        }

        // Shared certificates got here: each when it is missing or due.
        var now = DateTimeOffset.UtcNow;
        foreach (var info in _store.SharedInfos().Where(i => i.Source != CertificateSources.Uploaded))
        {
            ct.ThrowIfCancellationRequested();
            var forced = _renewShared.TryRemove(info.Id, out _);
            var current = _shared.FirstOrDefault(s => s.Info.Id == info.Id).Certificate;
            var due = current is null || now >= RenewAt(current, Tls.RenewDaysBefore) || forced;
            var waiting = !forced && info.State.NextAttemptUtc is { } next && next > now;
            if (!due || waiting) continue;

            try
            {
                await IssueSharedAsync(info, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Could not get the shared certificate for {Names}", string.Join(", ", info.Requested));
            }
        }
    }

    /// <summary>
    /// Serves what the host should serve now, from what is at hand: a shared certificate, one on
    /// disk, or nothing. Quick, so a change of settings shows at once. Returns the certificate to
    /// get, when one is missing or due and may be asked for now.
    /// </summary>
    private (string Host, string Source)? Apply(string host, string wanted, bool forced)
    {
        var (effective, problem) = Effective(host, wanted);
        var now = DateTimeOffset.UtcNow;

        switch (effective)
        {
            case CertificateSources.Shared:
                if (SharedFor(host) is { } shared) _served[host] = (shared, CertificateSources.Shared);
                else _served.TryRemove(host, out _);
                return null;

            case CertificateSources.Uploaded:
                if (Own(host, CertificateSources.Uploaded) is { } uploaded) _served[host] = (uploaded, CertificateSources.Uploaded);
                else _served.TryRemove(host, out _);
                return null;

            case CertificateSources.SelfSigned:
            case CertificateSources.LetsEncrypt:
            case CertificateSources.CloudflareOrigin:
                var own = Own(host, effective);
                // Keep serving what it has, even from another source, until the new one is ready.
                if (own is not null) _served[host] = (own, effective);

                var due = own is null || now >= RenewAt(own, Tls.RenewDaysBefore) || forced;
                // The wait after a failure is Let's Encrypt's and Cloudflare's: a self-signed one is made at once.
                var waiting = !forced && effective != CertificateSources.SelfSigned &&
                              _store.ReadState(host).NextAttemptUtc is { } next && next > now;
                return due && problem is null && !waiting ? (host, effective) : null;

            default:
                _served.TryRemove(host, out _);
                return null;
        }
    }

    /// <summary>Applies the current settings to every host at once, then lets the loop get what is missing.</summary>
    private void ApplyAll()
    {
        if (!Enabled) return;
        foreach (var (host, (wanted, _, _)) in Desired()) Apply(host, wanted, forced: false);
        Nudge();
    }

    /// <summary>The host's own certificate from the given source: the one served, or the one on disk.</summary>
    private ServedCertificate? Own(string host, string source)
    {
        if (_served.TryGetValue(host, out var served) && served.Source == source &&
            (source != CertificateSources.LetsEncrypt || _servedOrigin.GetValueOrDefault(host) == Tls.AcmeDirectoryUrl))
            return served.Certificate;

        // A Let's Encrypt certificate counts only if it came from the directory in use now, so moving
        // from staging to production gets every host a real one.
        var origin = source == CertificateSources.LetsEncrypt ? Tls.AcmeDirectoryUrl : null;
        var loaded = _store.LoadHost(host, source, origin);
        if (loaded is not null && origin is not null) _servedOrigin[host] = origin;
        return loaded;
    }

    private async Task CreateSelfSignedAsync(string host)
    {
        var (chain, key) = CertificateMaterial.CreateSelfSigned(host);
        await _store.SaveHostAsync(host, CertificateSources.SelfSigned, chain, key);
        _served[host] = (CertificateMaterial.Load(chain, key), CertificateSources.SelfSigned);
        _logger.LogInformation("Made a self-signed certificate for {Host}", host);
    }

    /// <summary>
    /// Gets a certificate for the host, from the ACME server (checked through DNS when Cloudflare can,
    /// else over HTTP) or as a Cloudflare Origin certificate, and serves it. A failure is recorded,
    /// with when to try again.
    /// </summary>
    private async Task IssueAsync(string host, string source, CancellationToken ct)
    {
        var state = _store.ReadState(host);
        _issuing = host;
        try
        {
            var (chain, keyPem, origin, check) = source == CertificateSources.CloudflareOrigin
                ? await OriginCertificateAsync([host], ct)
                : await LetsEncryptCertificateAsync([host], ct);
            var certificate = CertificateMaterial.Load(chain, keyPem);

            var previous = source == CertificateSources.CloudflareOrigin ? OriginId(_store.OriginOf(host, source)) : null;
            await _store.SaveHostAsync(host, source, chain, keyPem, origin);
            await _store.SaveStateAsync(host, new HostCertificateState { LastAttemptUtc = DateTimeOffset.UtcNow, Check = check });
            _served[host] = (certificate, source);
            if (source == CertificateSources.LetsEncrypt) _servedOrigin[host] = origin;
            if (previous is not null) await _dns.RevokeOriginCertificateAsync(previous, ct);

            _logger.LogInformation("Got a certificate for {Host} ({Source}, {Check}), valid until {NotAfter:u}", host, source, check, certificate.NotAfter);
            await _audit.WriteAsync("tls.issue", "server", new { host, source, check, notAfter = certificate.NotAfter });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await RecordFailureAsync(host, state, ex, s => _store.SaveStateAsync(host, s));
        }
        finally
        {
            _issuing = null;
        }
    }

    /// <summary>Gets a shared certificate asked for here, and serves it for every host it covers.</summary>
    private async Task IssueSharedAsync(SharedCertificateInfo info, CancellationToken ct)
    {
        _issuing = "shared:" + info.Id;
        try
        {
            var (chain, keyPem, origin, check) = info.Source == CertificateSources.CloudflareOrigin
                ? await OriginCertificateAsync(info.Requested, ct)
                : await LetsEncryptCertificateAsync(info.Requested, ct);
            var certificate = CertificateMaterial.Load(chain, keyPem);

            var previous = info.OriginId;
            info.OriginId = info.Source == CertificateSources.CloudflareOrigin ? OriginId(origin) : null;
            info.State = new HostCertificateState { LastAttemptUtc = DateTimeOffset.UtcNow, Check = check };
            await _store.SaveSharedCertificateAsync(info, chain, keyPem, certificate);
            if (previous is not null && previous != info.OriginId) await _dns.RevokeOriginCertificateAsync(previous, ct);

            _shared = _store.LoadShared();
            ApplyAll();
            _logger.LogInformation("Got the shared certificate for {Names} ({Source}), valid until {NotAfter:u}",
                string.Join(", ", info.Requested), info.Source, certificate.NotAfter);
            await _audit.WriteAsync("tls.shared.issue", "server", new { info.Id, names = info.Requested, info.Source, notAfter = certificate.NotAfter });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await RecordFailureAsync(string.Join(", ", info.Requested), info.State, ex, s =>
            {
                info.State = s;
                return _store.SaveSharedInfoAsync(info);
            });
        }
        finally
        {
            _issuing = null;
        }
    }

    /// <summary>
    /// A certificate for the names from the ACME server: checked through DNS (Cloudflare) when every
    /// name is in a zone the token can edit, as a wildcard must be; otherwise over HTTP on port 80.
    /// </summary>
    private async Task<(string Chain, string Key, string Origin, string Check)> LetsEncryptCertificateAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        var directory = Tls.AcmeDirectoryUrl;
        var throughDns = true;
        foreach (var name in names) throughDns &= await _dns.CanCheckThroughDnsAsync(name, ct);
        if (!throughDns && names.Any(n => n.StartsWith("*.", StringComparison.Ordinal)))
            throw new AcmeException("A wildcard certificate is checked through DNS, and no Cloudflare zone the token can edit holds all its names.");

        try
        {
            var (accountKey, accountUrl) = _store.Account(directory);
            using (accountKey)
            {
                var client = new AcmeClient(_http.CreateClient(HttpClientName), directory, accountKey, accountUrl);
                var account = await client.EnsureAccountAsync(Tls.AcmeEmail, ct);
                if (account != accountUrl) _store.SaveAccountUrl(directory, account);

                var solver = throughDns ? await _dns.ChallengeSolverAsync(ct) : new HttpChallengeSolver(_challenges);
                var (certificateKey, csr) = CertificateRequests.Create(names);
                using (certificateKey)
                {
                    var chain = await client.OrderCertificateAsync(names, csr, solver, ct);
                    return (chain, certificateKey.ExportPkcs8PrivateKeyPem(), directory, solver.Type);
                }
            }
        }
        catch (AcmeException ex) when (ex.Type == "urn:ietf:params:acme:error:accountDoesNotExist")
        {
            _store.SaveAccountUrl(directory, "");
            throw;
        }
    }

    /// <summary>A Cloudflare Origin CA certificate for the names, with a key made here. Its id is kept, to revoke it later.</summary>
    private async Task<(string Chain, string Key, string Origin, string Check)> OriginCertificateAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        var (certificateKey, csr) = CertificateRequests.Create(names);
        using (certificateKey)
        {
            var made = await _dns.CreateOriginCertificateAsync(names, PemEncoding.WriteString("CERTIFICATE REQUEST", csr), ct);
            return (made.CertificatePem, certificateKey.ExportPkcs8PrivateKeyPem(), OriginPrefix + made.Id, "cloudflare");
        }
    }

    /// <summary>What the store keeps beside a Cloudflare Origin certificate: this prefix and its id.</summary>
    private const string OriginPrefix = "cloudflare-origin:";

    private static string? OriginId(string? origin) =>
        origin is not null && origin.StartsWith(OriginPrefix, StringComparison.Ordinal) ? origin[OriginPrefix.Length..] : null;

    /// <summary>Records a failed attempt, and when to try again: 1, 2, 4… up to 24 hours later.</summary>
    private async Task RecordFailureAsync(string what, HostCertificateState state, Exception ex, Func<HostCertificateState, Task> save)
    {
        state.Failures++;
        state.LastAttemptUtc = DateTimeOffset.UtcNow;
        state.LastError = ex is TaskCanceledException ? "The certificate authority did not answer in time." : ex.Message;
        state.NextAttemptUtc = DateTimeOffset.UtcNow + Backoff(state.Failures);
        try
        {
            await save(state);
        }
        catch (Exception saveError) when (saveError is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _logger.LogWarning(saveError, "Could not record the failure for {What}", what);
        }

        if (ex is AcmeException or CloudflareException or HttpRequestException or TaskCanceledException)
            _logger.LogWarning("Could not get a certificate for {What}: {Error}. Trying again after {Next:u}", what, state.LastError, state.NextAttemptUtc);
        else
            _logger.LogError(ex, "Could not get a certificate for {What}. Trying again after {Next:u}", what, state.NextAttemptUtc);
        await _audit.WriteAsync("tls.error", "server", new { what, error = state.LastError, failures = state.Failures });
    }

    /// <summary>
    /// When a certificate is renewed: <paramref name="renewDaysBefore"/> days before it expires, or a
    /// third of its lifetime before, whichever comes later. So a 90-day certificate renews at 60
    /// days, and a short-lived 6-day one at 4, instead of on every pass.
    /// </summary>
    public static DateTimeOffset RenewAt(ServedCertificate certificate, int renewDaysBefore)
    {
        var lifetime = certificate.NotAfter - certificate.NotBefore;
        var before = TimeSpan.FromTicks(Math.Min(TimeSpan.FromDays(Math.Max(1, renewDaysBefore)).Ticks, lifetime.Ticks / 3));
        return certificate.NotAfter - before;
    }

    /// <summary>How long to wait after the nth failure in a row: 1, 2, 4, 8, 16 hours, then a day.</summary>
    public static TimeSpan Backoff(int failures) => TimeSpan.FromHours(Math.Min(24, Math.Pow(2, Math.Max(0, failures - 1))));
}
