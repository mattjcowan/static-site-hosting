using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Models;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Dns;
using StaticSiteHost.Tls;

namespace StaticSiteHost.Tests;

/// <summary>
/// HTTPS in the server: certificates and their checks, which certificate each host gets, the
/// store on disk, the ACME signing, and the per-request part. The whole order against an ACME
/// server is checked against Pebble outside the unit tests (see docs/design/site-proxy.md).
/// </summary>
public sealed class TlsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ssh-tls-tests-" + Guid.NewGuid().ToString("n"));
    private readonly DataPaths _paths;
    private readonly SiteStore _sites;
    private readonly EphemeralDataProtectionProvider _protection = new();

    public TlsTests()
    {
        _paths = new DataPaths(Options.Create(new SiteHostingOptions { DataRoot = _root }));
        _sites = new SiteStore(_paths, NullLogger<SiteStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private CertificateManager Manager(Action<TlsOptions>? configure = null, params string[] managementHosts) =>
        Manager(true, configure, managementHosts);

    private CertificateManager Manager(bool enabled, Action<TlsOptions>? configure = null, params string[] managementHosts) =>
        Manager(enabled, DnsTests.Service(_paths, _sites, new FakeCloudflare(), Options.Create(new SiteHostingOptions { DataRoot = _root })), configure, managementHosts);

    private CertificateManager Manager(bool enabled, DnsService dns, Action<TlsOptions>? configure = null, params string[] managementHosts)
    {
        var options = new SiteHostingOptions { DataRoot = _root, ManagementHosts = managementHosts };
        options.Tls.Enabled = enabled;
        configure?.Invoke(options.Tls);
        return new CertificateManager(Options.Create(options), _sites, new CertificateStore(_paths, _protection), new AcmeChallenges(),
            dns, new NoHttp(), new AuditLog(_paths, NullLogger<AuditLog>.Instance), NullLogger<CertificateManager>.Instance);
    }

    /// <summary>A manager with Cloudflare connected, through a fake of its API.</summary>
    private async Task<(CertificateManager Manager, FakeCloudflare Cloudflare)> WithCloudflare(bool enabled = true)
    {
        var cloudflare = new FakeCloudflare();
        var dns = DnsTests.Service(_paths, _sites, cloudflare, Options.Create(new SiteHostingOptions { DataRoot = _root }));
        Assert.Null(await dns.SetTokenAsync(FakeCloudflare.Token, "ada", default));
        return (Manager(enabled, dns), cloudflare);
    }

    // ---------------------------------------------------------------- Cloudflare

    [Fact]
    public async Task A_site_can_have_a_cloudflare_origin_certificate_which_is_revoked_with_the_site()
    {
        await _sites.SaveAsync(new SiteRecord { Domain = "shop.example.com" });
        var (manager, cloudflare) = await WithCloudflare();

        Assert.Null(await manager.SetSiteAsync(_sites.TryGet("shop.example.com")!, "cloudflare-origin", null, null, "ada"));
        await manager.RunOnceAsync(CancellationToken.None);

        Assert.True(manager.Serves("shop.example.com"));
        var status = manager.StatusOf("shop.example.com");
        Assert.Equal(CertificateSources.CloudflareOrigin, status.ServedSource);
        Assert.Equal("CN=Fake Origin CA", status.Served!.Leaf.Issuer);
        Assert.Equal(["shop.example.com"], Assert.Single(cloudflare.Created));

        // Asked again on the next pass? No: it lasts 15 years.
        await manager.RunOnceAsync(CancellationToken.None);
        Assert.Single(cloudflare.Created);

        await _sites.DeleteAsync("shop.example.com");
        await manager.RunOnceAsync(CancellationToken.None);
        Assert.Single(cloudflare.Revoked);
    }

    [Fact]
    public async Task A_shared_wildcard_from_cloudflare_covers_its_sites_and_its_bare_name_and_is_revoked_when_removed()
    {
        await _sites.SaveAsync(new SiteRecord { Domain = "blog.apps.example.com" });
        var (manager, cloudflare) = await WithCloudflare();

        var (info, error) = await manager.RequestSharedAsync("*.apps.example.com", "cloudflare-origin", "ada", default);
        Assert.Null(error);
        Assert.Equal(["*.apps.example.com", "apps.example.com"], info!.Requested);

        await manager.RunOnceAsync(CancellationToken.None);
        var (shared, certificate, _) = Assert.Single(manager.SharedAll());
        Assert.NotNull(certificate);
        Assert.True(certificate.Covers("blog.apps.example.com") && certificate.Covers("apps.example.com"));
        Assert.Equal(CertificateSources.Shared, manager.StatusOf("blog.apps.example.com").Effective);
        Assert.True(manager.Serves("blog.apps.example.com"));

        Assert.True(await manager.RemoveSharedAsync(shared.Id, "ada"));
        Assert.Equal([shared.OriginId!], cloudflare.Revoked);
    }

    [Fact]
    public void A_self_signed_certificate_has_a_name_no_other_certificate_has()
    {
        // A trusted certificate with the same name (the .NET developer one is CN=localhost) would
        // otherwise be taken for its issuer, and building its chain would fail.
        var (first, firstKey) = CertificateMaterial.CreateSelfSigned("localhost");
        var (second, secondKey) = CertificateMaterial.CreateSelfSigned("localhost");
        var one = CertificateMaterial.Load(first, firstKey);
        var other = CertificateMaterial.Load(second, secondKey);

        Assert.StartsWith("CN=localhost, O=Self-signed ", one.Leaf.Subject);
        Assert.NotEqual(one.Leaf.Subject, other.Leaf.Subject);
        Assert.Equal(["localhost"], one.Names);
    }

    [Fact]
    public async Task Automatic_prefers_a_shared_certificate_browsers_trust_over_a_cloudflare_origin_one()
    {
        await _sites.SaveAsync(new SiteRecord { Domain = "blog.apps.example.com" });
        var (manager, _) = await WithCloudflare();

        // A Cloudflare Origin wildcard, then a browser-trusted one (here uploaded) that expires sooner.
        Assert.Null((await manager.RequestSharedAsync("*.apps.example.com", "cloudflare-origin", "ada", default)).Error);
        await manager.RunOnceAsync(CancellationToken.None);
        var (chain, key) = CertificateMaterial.CreateSelfSigned("*.apps.example.com", TimeSpan.FromDays(90));
        Assert.Null((await manager.AddSharedAsync(chain, key, "ada")).Error);
        await manager.RunOnceAsync(CancellationToken.None);

        var served = manager.StatusOf("blog.apps.example.com").Served!;
        Assert.NotEqual("CN=Fake Origin CA", served.Leaf.Issuer);
    }

    [Fact]
    public async Task A_shared_certificate_is_asked_for_only_where_it_can_be_got()
    {
        var (manager, cloudflare) = await WithCloudflare();

        Assert.Equal("https.request.error.name", (await manager.RequestSharedAsync("*.*.example.com", "letsencrypt", "ada", default)).Error?.Key);
        Assert.Equal("https.request.error.name", (await manager.RequestSharedAsync("*.shop.test", "letsencrypt", "ada", default)).Error?.Key);
        Assert.Equal("https.request.error.noNames", (await manager.RequestSharedAsync(" ", "letsencrypt", "ada", default)).Error?.Key);
        Assert.Equal("https.request.error.source", (await manager.RequestSharedAsync("*.example.com", "uploaded", "ada", default)).Error?.Key);
        Assert.Equal("https.request.error.noZone", (await manager.RequestSharedAsync("*.example.org", "letsencrypt", "ada", default)).Error?.Key);

        cloudflare.AllowOrigin = false;
        Assert.Equal("https.request.error.noOrigin", (await manager.RequestSharedAsync("*.example.com", "cloudflare-origin", "ada", default)).Error?.Key);

        var (off, _) = await WithCloudflare(enabled: false);
        Assert.Equal("https.request.error.off", (await off.RequestSharedAsync("*.example.com", "letsencrypt", "ada", default)).Error?.Key);
        Assert.Equal("https.request.error.noCloudflare", (await Manager().RequestSharedAsync("*.example.com", "letsencrypt", "ada", default)).Error?.Key);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No network in these tests.");
    }

    // ---------------------------------------------------------------- certificates

    [Theory]
    [InlineData("shop.example.com", "shop.example.com", true)]
    [InlineData("*.example.com", "shop.example.com", true)]
    [InlineData("*.example.com", "a.b.example.com", false)]
    [InlineData("*.example.com", "example.com", false)]
    [InlineData("*.EXAMPLE.com", "shop.example.COM", true)]
    [InlineData("shop.example.com", "other.example.com", false)]
    public void A_wildcard_covers_one_level_as_browsers_match_it(string name, string host, bool covers) =>
        Assert.Equal(covers, CertificateMaterial.Matches(name, host));

    [Fact]
    public void A_self_signed_certificate_serves_its_host_and_an_address_gets_an_address()
    {
        var (chain, key) = CertificateMaterial.CreateSelfSigned("shop.test");
        var certificate = CertificateMaterial.Load(chain, key);
        Assert.True(certificate.SelfSigned);
        Assert.True(certificate.Covers("shop.test"));
        Assert.True(certificate.Leaf.HasPrivateKey);
        Assert.InRange((certificate.NotAfter - DateTimeOffset.UtcNow).TotalDays, 360, 366);

        var (ipChain, ipKey) = CertificateMaterial.CreateSelfSigned("127.0.0.1");
        Assert.Equal(["127.0.0.1"], CertificateMaterial.Load(ipChain, ipKey).Names);
    }

    [Fact]
    public void An_upload_is_checked_for_its_key_its_host_and_its_dates()
    {
        var (chain, key) = CertificateMaterial.CreateSelfSigned("shop.example.com");
        var (_, otherKey) = CertificateMaterial.CreateSelfSigned("shop.example.com");
        var (expiredChain, expiredKey) = Expired("shop.example.com");

        Assert.NotNull(CertificateMaterial.Check(chain, key, "shop.example.com").Certificate);
        Assert.Contains("not shop.other.com", CertificateMaterial.Check(chain, key, "shop.other.com").Error);
        Assert.Contains("not this certificate's own", CertificateMaterial.Check(chain, otherKey, "shop.example.com").Error);
        Assert.Contains("expired", CertificateMaterial.Check(expiredChain, expiredKey, "shop.example.com").Error);
        Assert.Contains("PEM", CertificateMaterial.Check("hello", key, null).Error);
        Assert.Contains("private key", CertificateMaterial.Check(chain, "", null).Error);
    }

    [Fact]
    public void The_intermediate_after_the_leaf_is_sent_with_it()
    {
        // A root, an intermediate it signs, and a leaf the intermediate signs, as Let's Encrypt's are.
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=Test Root", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(60));

        using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intermediateRequest = new CertificateRequest("CN=Test Intermediate", intermediateKey, HashAlgorithmName.SHA256);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var intermediatePublic = intermediateRequest.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(40), [5, 6, 7, 8]);
        using var intermediate = intermediatePublic.CopyWithPrivateKey(intermediateKey);

        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafRequest = new CertificateRequest("CN=shop.example.com", leafKey, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("shop.example.com");
        leafRequest.CertificateExtensions.Add(names.Build());
        using var leaf = leafRequest.Create(intermediate, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(20), [1, 2, 3, 4]);

        var served = CertificateMaterial.Load(CertificateMaterial.Pem(leaf) + CertificateMaterial.Pem(intermediatePublic), leafKey.ExportPkcs8PrivateKeyPem());
        Assert.Equal("Test Intermediate", served.Issuer);
        Assert.False(served.SelfSigned);
        Assert.Equal("CN=Test Intermediate", Assert.Single(served.Context.IntermediateCertificates).Subject);
    }

    private static (string Chain, string Key) Expired(string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1));
        return (CertificateMaterial.Pem(certificate), key.ExportPkcs8PrivateKeyPem());
    }

    // ---------------------------------------------------------------- when to renew, and to retry

    [Fact]
    public void Renewal_comes_30_days_before_or_a_third_of_the_lifetime_before_for_a_short_one()
    {
        var ninety = Lifetime(TimeSpan.FromDays(90));
        Assert.Equal(ninety.NotAfter.AddDays(-30), CertificateManager.RenewAt(ninety, 30));

        var six = Lifetime(TimeSpan.FromDays(6));
        Assert.Equal(six.NotAfter - (six.NotAfter - six.NotBefore) / 3, CertificateManager.RenewAt(six, 30));
        Assert.InRange((six.NotAfter - CertificateManager.RenewAt(six, 30)).TotalDays, 1.99, 2.01);
    }

    private static ServedCertificate Lifetime(TimeSpan lifetime)
    {
        var (chain, key) = CertificateMaterial.CreateSelfSigned("shop.example.com", lifetime);
        return CertificateMaterial.Load(chain, key);
    }

    [Fact]
    public void A_failure_waits_longer_each_time_up_to_a_day() =>
        Assert.Equal([1, 2, 4, 8, 16, 24, 24], Enumerable.Range(1, 7).Select(n => CertificateManager.Backoff(n).TotalHours));

    [Theory]
    [InlineData("shop.example.com", true)]
    [InlineData("example.com", true)]
    [InlineData("localhost", false)]
    [InlineData("shop.localhost", false)]
    [InlineData("shop.test", false)]
    [InlineData("printer.local", false)]
    [InlineData("db.internal", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("intranet", false)]
    public void Only_public_names_go_to_lets_encrypt(string host, bool isPublic) =>
        Assert.Equal(isPublic, CertificateManager.IsPublicName(host));

    // ---------------------------------------------------------------- which certificate each host gets

    [Fact]
    public async Task Automatic_is_a_covering_shared_certificate_then_the_default_and_self_signed_for_private_names()
    {
        var manager = Manager();
        Assert.Equal((CertificateSources.LetsEncrypt, null), manager.Effective("shop.example.com", CertificateSources.Automatic));
        Assert.Equal((CertificateSources.SelfSigned, null), manager.Effective("shop.test", CertificateSources.Automatic));
        Assert.NotNull(manager.Effective("shop.test", CertificateSources.LetsEncrypt).Problem);
        Assert.NotNull(manager.Effective("shop.example.com", CertificateSources.Shared).Problem);

        var (chain, key) = CertificateMaterial.CreateSelfSigned("*.example.com");
        Assert.Null((await manager.AddSharedAsync(chain, key, "ada")).Error);
        Assert.Equal((CertificateSources.Shared, null), manager.Effective("shop.example.com", CertificateSources.Automatic));
        Assert.Equal((CertificateSources.LetsEncrypt, null), manager.Effective("a.b.example.com", CertificateSources.Automatic));

        var selfSigned = Manager(tls => tls.DefaultSource = "selfsigned");
        Assert.Equal((CertificateSources.SelfSigned, null), selfSigned.Effective("shop.example.org", CertificateSources.Automatic));
    }

    [Fact]
    public async Task Each_host_serves_what_it_asks_for_and_a_deleted_site_takes_its_certificates_with_it()
    {
        await _sites.SaveAsync(new SiteRecord { Domain = "shop.test" });
        var manager = Manager(managementHosts: "admin.localhost");

        await manager.RunOnceAsync(CancellationToken.None);
        Assert.True(manager.Serves("shop.test"));
        Assert.True(manager.Serves("admin.localhost"));
        Assert.StartsWith("CN=shop.test,", manager.Select("SHOP.test.").TargetCertificate.Subject);
        Assert.StartsWith("CN=static-site-host,", manager.Select("nobody.test").TargetCertificate.Subject);
        Assert.StartsWith("CN=static-site-host,", manager.Select(null).TargetCertificate.Subject);

        // A second pass, or a restart, serves the same certificate rather than making another.
        var thumbprint = manager.Select("shop.test").TargetCertificate.Thumbprint;
        await manager.RunOnceAsync(CancellationToken.None);
        Assert.Equal(thumbprint, manager.Select("shop.test").TargetCertificate.Thumbprint);
        var restarted = Manager(managementHosts: "admin.localhost");
        await restarted.RunOnceAsync(CancellationToken.None);
        Assert.Equal(thumbprint, restarted.Select("shop.test").TargetCertificate.Thumbprint);

        // None stops at once, without waiting for the loop.
        var site = _sites.TryGet("shop.test")!;
        Assert.Null(await restarted.SetSiteAsync(site, "none", null, null, "ada"));
        Assert.False(restarted.Serves("shop.test"));
        Assert.NotNull(await restarted.SetSiteAsync(site, "uploaded", null, null, "ada"));
        Assert.NotNull(await restarted.SetSiteAsync(site, "sometimes", null, null, "ada"));

        // Back to automatic: the self-signed one on disk is served again, the same one.
        Assert.Null(await restarted.SetSiteAsync(site, "automatic", null, null, "ada"));
        Assert.Equal(thumbprint, restarted.Select("shop.test").TargetCertificate.Thumbprint);

        await _sites.DeleteAsync("shop.test");
        await restarted.RunOnceAsync(CancellationToken.None);
        Assert.False(restarted.Serves("shop.test"));
        Assert.DoesNotContain("shop.test", new CertificateStore(_paths, _protection).StoredHosts());
    }

    [Fact]
    public async Task An_uploaded_certificate_is_chosen_by_uploading_it_and_removing_it_goes_back_to_automatic()
    {
        await _sites.SaveAsync(new SiteRecord { Domain = "shop.test" });
        var manager = Manager();
        var site = _sites.TryGet("shop.test")!;

        var (chain, key) = CertificateMaterial.CreateSelfSigned("shop.test");
        Assert.NotNull(await manager.UploadForSiteAsync(site, chain, key.Replace("PRIVATE", "PUBLIC"), "ada"));
        Assert.Null(await manager.UploadForSiteAsync(site, chain, key, "ada"));
        Assert.Equal(CertificateSources.Uploaded, _sites.TryGet("shop.test")!.Tls!.Source);
        var uploaded = manager.Select("shop.test").TargetCertificate.Thumbprint;

        await manager.RunOnceAsync(CancellationToken.None);
        Assert.Equal(uploaded, manager.Select("shop.test").TargetCertificate.Thumbprint);
        Assert.Equal(CertificateSources.Uploaded, manager.StatusOf("shop.test").ServedSource);

        await manager.RemoveUploadedAsync(site, "ada");
        Assert.Equal(CertificateSources.Automatic, _sites.TryGet("shop.test")!.Tls!.Source);
        await manager.RunOnceAsync(CancellationToken.None);
        Assert.NotEqual(uploaded, manager.Select("shop.test").TargetCertificate.Thumbprint);
        Assert.Equal(CertificateSources.SelfSigned, manager.StatusOf("shop.test").ServedSource);
    }

    // ---------------------------------------------------------------- on disk

    [Fact]
    public async Task Each_source_keeps_its_own_certificate_and_a_name_never_leaves_the_folder()
    {
        var store = new CertificateStore(_paths, _protection);
        var (a, aKey) = CertificateMaterial.CreateSelfSigned("shop.example.com");
        var (b, bKey) = CertificateMaterial.CreateSelfSigned("shop.example.com");

        await store.SaveHostAsync("shop.example.com", CertificateSources.LetsEncrypt, a, aKey, origin: "https://acme.example/dir");
        await store.SaveHostAsync("shop.example.com", CertificateSources.SelfSigned, b, bKey);

        Assert.NotNull(store.LoadHost("shop.example.com", CertificateSources.LetsEncrypt, "https://acme.example/dir"));
        Assert.Null(store.LoadHost("shop.example.com", CertificateSources.LetsEncrypt, "https://other.example/dir"));
        Assert.NotEqual(
            store.LoadHost("shop.example.com", CertificateSources.LetsEncrypt)!.Thumbprint,
            store.LoadHost("shop.example.com", CertificateSources.SelfSigned)!.Thumbprint);

        // The key is not on disk as PEM.
        var keyFile = Directory.EnumerateFiles(Path.Combine(_paths.ConfigDir, "certs"), "key.bin", SearchOption.AllDirectories).First();
        Assert.DoesNotContain("PRIVATE KEY", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(keyFile)));

        Assert.Null(store.LoadHost("../../etc", CertificateSources.SelfSigned));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveHostAsync("../x", CertificateSources.SelfSigned, a, aKey));
        Assert.Null(store.LoadHost("shop.example.com", "../../keys"));
    }

    [Fact]
    public void The_account_key_is_made_once_and_kept()
    {
        var store = new CertificateStore(_paths, _protection);
        var (first, url) = store.Account("https://acme.example/dir");
        Assert.Null(url);
        store.SaveAccountUrl("https://acme.example/dir", "https://acme.example/acct/1");

        var (again, savedUrl) = store.Account("https://acme.example/dir");
        Assert.Equal("https://acme.example/acct/1", savedUrl);
        Assert.Equal(AcmeClient.Thumbprint(first), AcmeClient.Thumbprint(again));

        // An empty URL (an account the server forgot) counts as none.
        store.SaveAccountUrl("https://acme.example/dir", "");
        Assert.Null(store.Account("https://acme.example/dir").Url);
    }

    // ---------------------------------------------------------------- ACME signing

    [Fact]
    public void The_key_authorization_is_the_token_and_the_jwk_thumbprint()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var client = new AcmeClient(new HttpClient(), "https://acme.example/dir", key);

        var point = key.ExportParameters(false).Q;
        var canonical = $$"""{"crv":"P-256","kty":"EC","x":"{{AcmeClient.Base64Url(point.X!)}}","y":"{{AcmeClient.Base64Url(point.Y!)}}"}""";
        var expected = AcmeClient.Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        Assert.Equal("token-1." + expected, client.KeyAuthorization("token-1"));
        Assert.Equal(43, expected.Length);
        Assert.DoesNotContain('=', expected);
    }

    [Fact]
    public void A_certificate_request_names_every_host()
    {
        var (key, csr) = CertificateRequests.Create(["shop.example.com", "www.shop.example.com"]);
        using (key)
        {
            var request = CertificateRequest.LoadSigningRequest(csr, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
            var names = request.CertificateExtensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames();
            Assert.Equal(["shop.example.com", "www.shop.example.com"], names);
        }
    }

    // ---------------------------------------------------------------- per request

    [Fact]
    public async Task Requests_get_the_check_answer_the_redirect_and_hsts_as_their_site_says()
    {
        await _sites.SaveAsync(new SiteRecord { Domain = "shop.test" });
        await _sites.SaveAsync(new SiteRecord { Domain = "strict.test", Tls = new SiteTlsSettings { Hsts = true } });
        await _sites.SaveAsync(new SiteRecord { Domain = "plain.test", Tls = new SiteTlsSettings { RedirectToHttps = false } });
        var manager = Manager();
        await manager.RunOnceAsync(CancellationToken.None);

        var challenges = new AcmeChallenges();
        challenges.Publish("tok", "tok.thumb");
        var options = new SiteHostingOptions();
        options.Tls.Enabled = true;
        var passedOn = false;
        var middleware = new TlsMiddleware(_ => { passedOn = true; return Task.CompletedTask; }, challenges, manager, _sites, Options.Create(options));

        async Task<HttpContext> Send(string host, string path, bool https = false)
        {
            passedOn = false;
            var context = new DefaultHttpContext();
            context.Request.Host = new HostString(host);
            context.Request.Path = path;
            context.Request.QueryString = new QueryString("?a=1");
            context.Request.IsHttps = https;
            context.Response.Body = new MemoryStream();
            context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(new StartingResponse());
            await middleware.InvokeAsync(context);
            return context;
        }

        var check = await Send("anything.example", "/.well-known/acme-challenge/tok");
        check.Response.Body.Position = 0;
        Assert.Equal("tok.thumb", await new StreamReader(check.Response.Body).ReadToEndAsync());
        Assert.False(passedOn);

        var redirect = await Send("shop.test", "/cart");
        Assert.Equal(308, redirect.Response.StatusCode);
        Assert.Equal("https://shop.test/cart?a=1", redirect.Response.Headers.Location.ToString());

        await Send("shop.test", "/healthz");
        Assert.True(passedOn);
        await Send("plain.test", "/");
        Assert.True(passedOn);
        await Send("unknown.test", "/");
        Assert.True(passedOn);
        await Send("shop.test", "/.well-known/acme-challenge/unknown-token");
        Assert.Equal(308, (await Send("shop.test", "/.well-known/acme-challenge/unknown-token")).Response.StatusCode);

        var strict = await Send("strict.test", "/", https: true);
        await ((StartingResponse)strict.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>()!).StartAsync();
        Assert.Equal("max-age=31536000", strict.Response.Headers.StrictTransportSecurity.ToString());
        var relaxed = await Send("shop.test", "/", https: true);
        await ((StartingResponse)relaxed.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>()!).StartAsync();
        Assert.Empty(relaxed.Response.Headers.StrictTransportSecurity.ToString());
    }

    /// <summary>A response that runs its OnStarting callbacks when told to start, as a server's does.</summary>
    private sealed class StartingResponse : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _starting = [];

        public override void OnStarting(Func<object, Task> callback, object state) => _starting.Add((callback, state));

        public async Task StartAsync()
        {
            foreach (var (callback, state) in _starting) await callback(state);
        }
    }

    // ---------------------------------------------------------------- from the review

    [Fact]
    public async Task A_lets_encrypt_failure_never_holds_up_a_self_signed_certificate_or_shows_under_it()
    {
        await _sites.SaveAsync(new SiteRecord { Domain = "shop.example.com" });
        var manager = Manager();

        // Let's Encrypt cannot be reached here, so the first pass records a failure and a wait.
        await manager.RunOnceAsync(CancellationToken.None);
        var failed = manager.StatusOf("shop.example.com");
        Assert.NotNull(failed.State.LastError);
        Assert.NotNull(failed.State.NextAttemptUtc);
        Assert.False(manager.Serves("shop.example.com"));

        Assert.Null(await manager.SetSiteAsync(_sites.TryGet("shop.example.com")!, "selfsigned", null, null, "ada"));
        await manager.RunOnceAsync(CancellationToken.None);
        Assert.True(manager.Serves("shop.example.com"));
        Assert.Null(manager.StatusOf("shop.example.com").State.LastError);
    }

    [Fact]
    public async Task One_host_that_cannot_be_handled_never_stops_the_others()
    {
        await _sites.SaveAsync(new SiteRecord { Domain = "shop.test" });
        var manager = Manager(managementHosts: ["[::1]", "admin.example.com/", "admin.localhost"]);

        await manager.RunOnceAsync(CancellationToken.None);
        Assert.True(manager.Serves("shop.test"));
        Assert.True(manager.Serves("admin.localhost"));
        Assert.DoesNotContain(manager.Status(), s => s.Host.Contains('[') || s.Host.Contains('/'));
    }

    [Fact]
    public async Task A_certificate_and_key_that_do_not_belong_together_on_disk_count_as_missing()
    {
        var store = new CertificateStore(_paths, _protection);
        var (chain, _) = CertificateMaterial.CreateSelfSigned("shop.test");
        var (_, otherKey) = CertificateMaterial.CreateSelfSigned("shop.test");
        await store.SaveHostAsync("shop.test", CertificateSources.SelfSigned, chain, otherKey);
        Assert.Null(store.LoadHost("shop.test", CertificateSources.SelfSigned));

        // And a pass simply makes a good one.
        await _sites.SaveAsync(new SiteRecord { Domain = "shop.test" });
        var manager = Manager();
        await manager.RunOnceAsync(CancellationToken.None);
        Assert.True(manager.Serves("shop.test"));
    }

    [Fact]
    public void An_account_key_that_cannot_be_decrypted_is_replaced()
    {
        new CertificateStore(_paths, _protection).Account("https://acme.example/dir");
        new CertificateStore(_paths, _protection).SaveAccountUrl("https://acme.example/dir", "https://acme.example/acct/1");

        // Another key ring, as after restoring certs/ without config/keys.
        var (key, url) = new CertificateStore(_paths, new EphemeralDataProtectionProvider()).Account("https://acme.example/dir");
        using (key)
        {
            Assert.Null(url);
            Assert.Equal(43, AcmeClient.Thumbprint(key).Length);
        }
    }

    [Fact]
    public async Task A_site_whose_site_json_cannot_be_read_keeps_its_certificates()
    {
        var store = new CertificateStore(_paths, _protection);
        var (chain, key) = CertificateMaterial.CreateSelfSigned("broken.test");
        await store.SaveHostAsync("broken.test", CertificateSources.Uploaded, chain, key);
        Directory.CreateDirectory(_paths.SiteDir("broken.test"));
        await File.WriteAllTextAsync(_paths.SiteMetaFile("broken.test"), "{ not json");
        _sites.Load();
        Assert.Contains("broken.test", _sites.Unreadable);

        await Manager().RunOnceAsync(CancellationToken.None);
        Assert.NotNull(store.LoadHost("broken.test", CertificateSources.Uploaded));
    }

    [Fact]
    public async Task An_address_with_no_name_in_the_handshake_gets_its_own_certificate()
    {
        var manager = Manager(managementHosts: "127.0.0.1");
        await manager.RunOnceAsync(CancellationToken.None);

        Assert.StartsWith("CN=127.0.0.1,", manager.Select(null, System.Net.IPAddress.Loopback).TargetCertificate.Subject);
        Assert.StartsWith("CN=127.0.0.1,", manager.Select("", System.Net.IPAddress.Parse("::ffff:127.0.0.1")).TargetCertificate.Subject);
        Assert.StartsWith("CN=static-site-host,", manager.Select(null, System.Net.IPAddress.Parse("10.1.2.3")).TargetCertificate.Subject);
    }

    [Fact]
    public async Task A_removed_shared_certificate_stops_being_served_at_once()
    {
        await _sites.SaveAsync(new SiteRecord { Domain = "shop.example.com" });
        var manager = Manager();
        var (chain, key) = CertificateMaterial.CreateSelfSigned("*.example.com");
        var (info, _) = await manager.AddSharedAsync(chain, key, "ada");
        Assert.Equal(CertificateSources.Shared, manager.StatusOf("shop.example.com").ServedSource);

        Assert.True(await manager.RemoveSharedAsync(info!.Id, "ada"));
        // Its own cannot be had here (no network), and the removed one is not served meanwhile.
        Assert.False(manager.Serves("shop.example.com"));
    }

    [Fact]
    public async Task Shared_certificates_are_listed_while_https_is_off_and_the_default_ignores_case()
    {
        var (chain, key) = CertificateMaterial.CreateSelfSigned("*.example.com");
        await Manager().AddSharedAsync(chain, key, "ada");
        Assert.Single(Manager(enabled: false).Shared);

        Assert.Equal((CertificateSources.SelfSigned, null),
            Manager(tls => tls.DefaultSource = "SelfSigned").Effective("shop.example.org", CertificateSources.Automatic));
    }
}
