using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Models;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Dns;
using StaticSiteHost.Tls;

namespace StaticSiteHost.Tests;

/// <summary>Cloudflare's API in memory: zones, records and Origin certificates, and every call counted.</summary>
public sealed class FakeCloudflare : ICloudflareApi
{
    public const string Token = "good-token";

    public List<CloudflareZone> Zones { get; } = [new("z1", "example.com"), new("z2", "apps.example.com")];
    public Dictionary<string, (string ZoneId, CloudflareRecord Record)> Records { get; } = new(StringComparer.Ordinal);
    public bool AllowOrigin { get; set; } = true;
    public List<string> Revoked { get; } = [];
    public int Calls { get; private set; }
    private int _next;

    private void Check(string token)
    {
        Calls++;
        if (token != Token) throw new CloudflareException("Invalid token", System.Net.HttpStatusCode.Unauthorized);
    }

    public Task<string?> VerifyAsync(string token, CancellationToken ct) => Task.FromResult(token == Token ? null : "Invalid API Token (Cloudflare error 1000)");

    public Task<IReadOnlyList<CloudflareZone>> ZonesAsync(string token, CancellationToken ct)
    {
        Check(token);
        return Task.FromResult<IReadOnlyList<CloudflareZone>>([.. Zones]);
    }

    public Task<IReadOnlyList<CloudflareRecord>> RecordsAsync(string token, string zoneId, string name, CancellationToken ct)
    {
        Check(token);
        return Task.FromResult<IReadOnlyList<CloudflareRecord>>(
            Records.Values.Where(r => r.ZoneId == zoneId && r.Record.Name == name).Select(r => r.Record).ToList());
    }

    public Task<CloudflareRecord> CreateRecordAsync(string token, string zoneId, string type, string name, string content, bool proxied, string? comment, CancellationToken ct)
    {
        Check(token);
        var record = new CloudflareRecord($"r{++_next}", type, name, content, proxied, comment);
        Records[record.Id] = (zoneId, record);
        return Task.FromResult(record);
    }

    public Task<CloudflareRecord> UpdateRecordAsync(string token, string zoneId, string recordId, string content, bool proxied, CancellationToken ct)
    {
        Check(token);
        var record = Records[recordId].Record with { Content = content, Proxied = proxied };
        Records[recordId] = (zoneId, record);
        return Task.FromResult(record);
    }

    public Task DeleteRecordAsync(string token, string zoneId, string recordId, CancellationToken ct)
    {
        Check(token);
        Records.Remove(recordId);
        return Task.CompletedTask;
    }

    public Task<bool> CanMakeOriginCertificatesAsync(string token, string zoneId, CancellationToken ct)
    {
        Check(token);
        return Task.FromResult(AllowOrigin);
    }

    public Task<CloudflareOriginCertificate> CreateOriginCertificateAsync(string token, IReadOnlyList<string> hostnames, string csrPem, CancellationToken ct)
    {
        Check(token);
        Created.Add([.. hostnames]);

        // Signed for the key in the request, by a CA of its own, as Cloudflare's Origin CA does.
        var request = System.Security.Cryptography.X509Certificates.CertificateRequest.LoadSigningRequestPem(csrPem,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.X509Certificates.CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        using var caKey = System.Security.Cryptography.ECDsa.Create();
        var caRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Fake Origin CA", caKey, System.Security.Cryptography.HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        using var leaf = request.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddYears(15), System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
        return Task.FromResult(new CloudflareOriginCertificate($"o{++_next}", leaf.ExportCertificatePem(), DateTimeOffset.UtcNow.AddYears(15)));
    }

    /// <summary>The names of each Origin certificate made.</summary>
    public List<List<string>> Created { get; } = [];

    public Task RevokeOriginCertificateAsync(string token, string certificateId, CancellationToken ct)
    {
        Check(token);
        Revoked.Add(certificateId);
        return Task.CompletedTask;
    }

    /// <summary>A record made by someone else, at a name.</summary>
    public void Foreign(string zoneId, string type, string name, string content) =>
        Records[$"f{++_next}"] = (zoneId, new CloudflareRecord($"f{_next}", type, name, content, false, null));

    public IEnumerable<CloudflareRecord> At(string name) => Records.Values.Select(r => r.Record).Where(r => r.Name == name).OrderBy(r => r.Type);
}

/// <summary>
/// DNS records that follow sites through Cloudflare: made where wanted, kept in line with the
/// settings, deleted with the site, and never a record this server did not make.
/// </summary>
public sealed class DnsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ssh-dns-tests-" + Guid.NewGuid().ToString("n"));
    private readonly DataPaths _paths;
    private readonly SiteStore _sites;
    private readonly FakeCloudflare _cloudflare = new();
    private readonly DnsService _dns;

    public DnsTests()
    {
        var options = Options.Create(new SiteHostingOptions { DataRoot = _root, ManagementHosts = ["deploy.example.com"] });
        _paths = new DataPaths(options);
        _sites = new SiteStore(_paths, NullLogger<SiteStore>.Instance);
        _dns = Service(_paths, _sites, _cloudflare, options);
    }

    internal static DnsService Service(DataPaths paths, SiteStore sites, ICloudflareApi api, IOptions<SiteHostingOptions> options) =>
        new(paths, new EphemeralDataProtectionProvider(), api, sites, new AuditLog(paths, NullLogger<AuditLog>.Instance),
            new NoHttp(), options, NullLogger<DnsService>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No network in these tests.");
    }

    private async Task Sites(params string[] domains)
    {
        foreach (var domain in domains) await _sites.SaveAsync(new SiteRecord { Domain = domain });
    }

    private async Task Connect() => Assert.Null(await _dns.SetTokenAsync(FakeCloudflare.Token, "ann", default));

    private SiteDnsStatus Status(string domain) => _dns.Status[domain];

    [Fact]
    public async Task A_token_is_checked_and_kept_encrypted()
    {
        Assert.Equal("dns.error.tokenRefused", (await _dns.SetTokenAsync("wrong", "ann", default))?.Key);
        Assert.False(await _dns.ConnectedAsync());

        await Connect();
        Assert.True(await _dns.ConnectedAsync());
        Assert.DoesNotContain(FakeCloudflare.Token, await File.ReadAllTextAsync(Path.Combine(_paths.ConfigDir, "dns.json")));

        _cloudflare.Zones.Clear();
        Assert.Equal("dns.error.noZones", (await _dns.SetTokenAsync(FakeCloudflare.Token, "ann", default))?.Key);
    }

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("shop.example.com", "example.com")]
    [InlineData("shop.apps.example.com", "apps.example.com")]
    [InlineData("*.apps.example.com", "apps.example.com")]
    [InlineData("notexample.com", null)]
    [InlineData("example.org", null)]
    public void A_name_belongs_to_the_longest_zone_that_holds_it(string name, string? zone) =>
        Assert.Equal(zone, DnsService.ZoneFor(name, _cloudflare.Zones)?.Name);

    [Fact]
    public void The_create_token_link_fills_in_the_three_permissions_and_the_name()
    {
        // Cloudflare drops a key it does not know without saying so, so the keys are pinned here:
        // zone, dns and ssl_and_certificates, as its own working template links use them.
        var url = new Uri(CloudflareApi.CreateTokenUrl("Site server at admin.example.com"));
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);

        Assert.Equal("https://dash.cloudflare.com/profile/api-tokens", url.GetLeftPart(UriPartial.Path));
        Assert.Equal(
            """[{"key":"zone","type":"read"},{"key":"dns","type":"edit"},{"key":"ssl_and_certificates","type":"edit"}]""",
            query["permissionGroupKeys"]);
        Assert.Equal("*", query["accountId"]);
        Assert.Equal("all", query["zoneId"]);
        Assert.Equal("Site server at admin.example.com", query["name"]);
    }

    [Fact]
    public async Task Every_site_gets_its_records_and_a_name_outside_the_zones_is_left_alone()
    {
        await Sites("shop.example.com", "blog.apps.example.com", "other.org");
        await Connect();

        Assert.Null(await _dns.SetRecordsAsync(true, "203.0.113.5", "2001:db8::5", null, proxied: true, "ann", default));

        Assert.Equal(["A 203.0.113.5", "AAAA 2001:db8::5"], _cloudflare.At("shop.example.com").Select(r => $"{r.Type} {r.Content}"));
        Assert.All(_cloudflare.At("shop.example.com"), r => Assert.True(r.Proxied));
        Assert.Contains("deploy.example.com", _cloudflare.At("shop.example.com").First().Comment);
        Assert.Equal("ok", Status("blog.apps.example.com").State);
        Assert.Equal("apps.example.com", Status("blog.apps.example.com").Zone);
        Assert.Equal("noZone", Status("other.org").State);
        Assert.Empty(_cloudflare.At("other.org"));
    }

    [Fact]
    public async Task Records_follow_the_settings_and_a_cname_replaces_addresses()
    {
        await Sites("shop.example.com");
        await Connect();
        await _dns.SetRecordsAsync(true, "203.0.113.5", null, null, proxied: false, "ann", default);

        await _dns.SetRecordsAsync(true, "198.51.100.7", null, null, proxied: true, "ann", default);
        var record = Assert.Single(_cloudflare.At("shop.example.com"));
        Assert.Equal(("A", "198.51.100.7", false), (record.Type, record.Content, record.Proxied));

        await _dns.SetRecordsAsync(true, null, null, "lb.example.net", proxied: true, "ann", default);
        record = Assert.Single(_cloudflare.At("shop.example.com"));
        Assert.Equal(("CNAME", "lb.example.net"), (record.Type, record.Content));

        // Nothing changed: nothing is written.
        var calls = _cloudflare.Calls;
        await _dns.ReconcileAsync(default);
        Assert.Equal(2, _cloudflare.Calls - calls);
    }

    [Fact]
    public async Task A_record_made_elsewhere_is_never_touched()
    {
        await Sites("shop.example.com");
        _cloudflare.Foreign("z1", "A", "shop.example.com", "192.0.2.9");
        await Connect();

        await _dns.SetRecordsAsync(true, "203.0.113.5", null, null, proxied: true, "ann", default);

        var record = Assert.Single(_cloudflare.At("shop.example.com"));
        Assert.Equal("192.0.2.9", record.Content);
        Assert.Equal("conflict", Status("shop.example.com").State);
        Assert.Contains("192.0.2.9", Status("shop.example.com").Detail);
    }

    [Fact]
    public async Task A_deleted_sites_records_go_and_only_those()
    {
        await Sites("shop.example.com", "blog.example.com");
        _cloudflare.Foreign("z1", "TXT", "shop.example.com", "v=spf1 -all");
        await Connect();
        await _dns.SetRecordsAsync(true, "203.0.113.5", null, null, proxied: true, "ann", default);

        await _sites.DeleteAsync("shop.example.com");
        await _dns.ReconcileAsync(default);

        Assert.Equal(["TXT"], _cloudflare.At("shop.example.com").Select(r => r.Type));
        Assert.Single(_cloudflare.At("blog.example.com"));
        Assert.DoesNotContain((await _dns.SettingsAsync()).Sites, e => e.Domain == "shop.example.com");
    }

    [Fact]
    public async Task A_record_deleted_by_hand_is_not_made_again_until_asked()
    {
        await Sites("shop.example.com");
        await Connect();
        await _dns.SetRecordsAsync(true, "203.0.113.5", null, null, proxied: true, "ann", default);

        Assert.Null(await _dns.RemoveSiteAsync("shop.example.com", "ann", default));
        await _dns.ReconcileAsync(default);
        Assert.Empty(_cloudflare.At("shop.example.com"));
        Assert.Equal("excluded", Status("shop.example.com").State);

        Assert.Null(await _dns.AddSiteAsync("shop.example.com", proxied: false, "ann", default));
        Assert.False(Assert.Single(_cloudflare.At("shop.example.com")).Proxied);
    }

    [Fact]
    public async Task One_site_can_have_a_record_when_not_every_site_does()
    {
        await Sites("shop.example.com", "blog.example.com");
        await Connect();
        await _dns.SetRecordsAsync(false, "203.0.113.5", null, null, proxied: true, "ann", default);
        Assert.Empty(_cloudflare.Records);

        Assert.Null(await _dns.AddSiteAsync("shop.example.com", null, "ann", default));
        Assert.Single(_cloudflare.At("shop.example.com"));
        Assert.Empty(_cloudflare.At("blog.example.com"));
        Assert.Equal("off", Status("blog.example.com").State);
    }

    [Fact]
    public async Task A_renamed_sites_record_moves_with_it()
    {
        await Sites("old.example.com");
        await Connect();
        await _dns.SetRecordsAsync(false, "203.0.113.5", null, null, proxied: true, "ann", default);
        await _dns.AddSiteAsync("old.example.com", proxied: false, "ann", default);

        Assert.True((await _sites.RenameAsync("old.example.com", "new.example.com")).Ok);
        await _dns.SiteRenamedAsync("old.example.com", "new.example.com");
        await _dns.ReconcileAsync(default);

        Assert.Empty(_cloudflare.At("old.example.com"));
        Assert.False(Assert.Single(_cloudflare.At("new.example.com")).Proxied);
    }

    [Fact]
    public async Task Without_a_token_nothing_is_asked_of_cloudflare()
    {
        await Sites("shop.example.com");
        await _dns.ReconcileAsync(default);

        Assert.Equal(0, _cloudflare.Calls);
        Assert.Equal("notConnected", Status("shop.example.com").State);
        Assert.Equal("dns.error.notConnected", (await _dns.AddSiteAsync("shop.example.com", null, "ann", default))?.Key);
    }

    [Theory]
    [InlineData("300.1.1.1", null, null, "dns.error.badIpv4")]
    [InlineData(null, "203.0.113.5", null, "dns.error.badIpv6")]
    [InlineData(null, null, "not a host", "dns.error.badTarget")]
    [InlineData("203.0.113.5", null, "lb.example.net", "dns.error.targetAndAddress")]
    [InlineData(null, null, null, "dns.error.noAddress")]
    public async Task What_records_point_at_is_checked(string? ipv4, string? ipv6, string? target, string error) =>
        Assert.Equal(error, (await _dns.SetRecordsAsync(true, ipv4, ipv6, target, true, "ann", default))?.Key);

    [Fact]
    public void A_dns_check_holds_the_key_authorizations_hash() =>
        // RFC 8555 §8.4: base64url(SHA-256(key authorization)), no padding.
        Assert.Equal("47DEQpj8HBSa-_TImW-5JCeuQeRkm5NMpJWZG3hSuFU", AcmeClient.DnsTxtValue(""));
}
