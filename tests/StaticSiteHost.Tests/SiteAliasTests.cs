using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Endpoints;
using StaticSiteHost.Hubs;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Security;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Dns;
using StaticSiteHost.Tls;
using StaticSiteHost.Tools;

namespace StaticSiteHost.Tests;

/// <summary>
/// A site's other domains: which site a name reaches, the rules for adding one, and how
/// certificates, DNS records and the realtime check follow them.
/// </summary>
public sealed class SiteAliasTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ssh-alias-tests-" + Guid.NewGuid().ToString("n"));
    private readonly DataPaths _paths;
    private readonly SiteStore _sites;
    private readonly EphemeralDataProtectionProvider _protection = new();

    public SiteAliasTests()
    {
        _paths = new DataPaths(Options.Create(new SiteHostingOptions { DataRoot = _root }));
        _sites = new SiteStore(_paths, NullLogger<SiteStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private Task Site(string domain, params string[] aliases) =>
        _sites.SaveAsync(new SiteRecord { Domain = domain, Aliases = [.. aliases] });

    private static SiteHostingOptions Managed(params string[] managementHosts) => new() { ManagementHosts = managementHosts };

    private (List<string> Aliases, List<string> Errors) Check(string domain, string[] aliases, SiteHostingOptions? options = null, string? managementHost = null) =>
        SiteAliases.Check(_sites.TryGet(domain)!, aliases, _sites, options ?? Managed("admin.example.com"), managementHost);

    // ---------------------------------------------------------------- which site answers

    [Fact]
    public async Task A_name_reaches_the_site_at_it_then_an_exact_alias_then_a_wildcard_one_level_up()
    {
        await Site("app.example.com", "www.app.example.com", "*.app.example.com");
        await Site("admin.app.example.com");

        Assert.Equal("app.example.com", _sites.Resolve("app.example.com")?.Domain);
        Assert.Equal("app.example.com", _sites.Resolve("www.app.example.com")?.Domain);
        Assert.Equal("app.example.com", _sites.Resolve("abc.app.example.com")?.Domain);
        Assert.Equal("app.example.com", _sites.Resolve("ABC.app.example.com")?.Domain);

        // A site at a name of its own keeps it under the wildcard.
        Assert.Equal("admin.app.example.com", _sites.Resolve("admin.app.example.com")?.Domain);

        // One level only, as a wildcard certificate covers.
        Assert.Null(_sites.Resolve("x.abc.app.example.com"));
        Assert.Null(_sites.Resolve("example.com"));
        Assert.Null(_sites.Resolve(""));

        // The API and the pages go by the site's domain only.
        Assert.Null(_sites.TryGet("www.app.example.com"));
        Assert.Equal(("app.example.com", false), _sites.AliasOwner("www.app.example.com"));
        Assert.Equal(("app.example.com", true), _sites.AliasOwner("abc.app.example.com"));
        Assert.Equal(("app.example.com", true), _sites.AliasOwner("*.app.example.com"));
    }

    [Fact]
    public async Task Aliases_are_found_again_after_a_restart_and_follow_a_rename_and_a_delete()
    {
        await Site("app.example.com", "*.app.example.com");

        var restarted = new SiteStore(_paths, NullLogger<SiteStore>.Instance);
        restarted.Load();
        Assert.Equal("app.example.com", restarted.Resolve("abc.app.example.com")?.Domain);

        Assert.True((await _sites.RenameAsync("app.example.com", "shop.example.com")).Ok);
        Assert.Equal("shop.example.com", _sites.Resolve("abc.app.example.com")?.Domain);

        Assert.True(await _sites.DeleteAsync("shop.example.com"));
        Assert.Null(_sites.Resolve("abc.app.example.com"));
    }

    // ---------------------------------------------------------------- the rules

    [Fact]
    public async Task Aliases_are_normalized_kept_in_order_and_without_repeats()
    {
        await Site("app.example.com");

        var (aliases, errors) = Check("app.example.com", ["https://WWW.Example.com/", " *.App.Example.com ", "www.example.com", "", "münchen.example.com"]);

        Assert.Empty(errors);
        Assert.Equal(["www.example.com", "*.app.example.com", "xn--mnchen-3ya.example.com"], aliases);
    }

    [Theory]
    [InlineData("app.example.com", "this site's own domain")]
    [InlineData("blog.example.com", "the domain of another site")]
    [InlineData("old.example.com", "already a domain of blog.example.com")]
    [InlineData("*.blog.example.com", "already a domain of blog.example.com")]
    [InlineData("admin.example.com", "reserved for the management interface")]
    [InlineData("*.com", "too broad")]
    [InlineData("*.", "not a domain")]
    [InlineData("a b.example.com", "not a domain")]
    [InlineData("*.*.example.com", "not a domain")]
    public async Task An_alias_that_cannot_be_used_is_refused_with_why(string alias, string why)
    {
        await Site("app.example.com");
        await Site("blog.example.com", "old.example.com", "*.blog.example.com");

        var (aliases, errors) = Check("app.example.com", [alias]);

        Assert.Empty(aliases);
        Assert.Contains(why, Assert.Single(errors));
    }

    [Fact]
    public async Task An_exact_alias_may_take_a_name_from_under_another_sites_wildcard_and_a_wildcard_may_cover_a_management_host()
    {
        await Site("app.example.com");
        await Site("blog.example.com", "*.blog.example.com");

        var (aliases, errors) = Check("app.example.com", ["news.blog.example.com", "*.example.com"]);

        Assert.Empty(errors);
        Assert.Equal(["news.blog.example.com", "*.example.com"], aliases);
    }

    [Fact]
    public async Task With_no_management_hosts_an_alias_may_not_take_the_name_the_management_app_is_open_at()
    {
        await Site("app.example.com");

        var (_, errors) = Check("app.example.com", ["*.example.com"], Managed(), managementHost: "deploy.example.com");
        Assert.Contains("SiteHosting:ManagementHosts", Assert.Single(errors));

        (_, errors) = Check("app.example.com", ["deploy.example.com"], Managed(), managementHost: "Deploy.Example.com.");
        Assert.Single(errors);

        // Set, the management host is matched first, so nothing is taken from it.
        (_, errors) = Check("app.example.com", ["*.example.com"], Managed("deploy.example.com"), managementHost: "deploy.example.com");
        Assert.Empty(errors);
    }

    [Fact]
    public async Task A_site_has_at_most_fifty_other_domains()
    {
        await Site("app.example.com");

        var (_, errors) = Check("app.example.com", [.. Enumerable.Range(0, SiteAliases.MaxPerSite + 1).Select(i => $"n{i}.example.com")]);

        Assert.Contains("at most 50", Assert.Single(errors));
    }

    [Fact]
    public async Task A_new_site_never_takes_another_sites_exact_alias_and_only_an_administrator_takes_a_name_under_a_wildcard()
    {
        await Site("app.example.com", "www.app.example.com", "*.app.example.com");

        Assert.Contains("Remove it from that site's other domains", SiteAliases.Taken(_sites, "www.app.example.com", administrator: true));
        Assert.Contains("Only an administrator", SiteAliases.Taken(_sites, "admin.app.example.com", administrator: false));
        Assert.Null(SiteAliases.Taken(_sites, "admin.app.example.com", administrator: true));
        Assert.Null(SiteAliases.Taken(_sites, "shop.example.com", administrator: false));

        // A site renamed to one of its own other domains takes it.
        Assert.Null(SiteAliases.Taken(_sites, "www.app.example.com", administrator: true, self: "app.example.com"));
    }

    // ---------------------------------------------------------------- HTTPS

    private CertificateManager Certificates(DnsService? dns = null, params string[] managementHosts)
    {
        var options = new SiteHostingOptions { DataRoot = _root, ManagementHosts = managementHosts };
        options.Tls.Enabled = true;
        dns ??= DnsTests.Service(_paths, _sites, new FakeCloudflare(), Options.Create(options));
        return new CertificateManager(Options.Create(options), _sites, new CertificateStore(_paths, _protection),
            new AcmeChallenges(), dns, new NoHttp(), new AuditLog(_paths, NullLogger<AuditLog>.Instance), NullLogger<CertificateManager>.Instance);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No network in these tests.");
    }

    [Fact]
    public async Task An_exact_alias_gets_a_certificate_of_its_own_and_loses_it_with_the_alias()
    {
        await Site("shop.test", "www.shop.test");
        var manager = Certificates();
        await manager.RunOnceAsync(CancellationToken.None);

        Assert.True(manager.Serves("www.shop.test"));
        Assert.StartsWith("CN=www.shop.test,", manager.Select("www.shop.test").TargetCertificate.Subject);
        var status = manager.Status().Single(s => s.Host == "www.shop.test");
        Assert.Equal("shop.test", status.AliasOf);
        Assert.Null(manager.Status().Single(s => s.Host == "shop.test").AliasOf);

        await Site("shop.test");
        await manager.RunOnceAsync(CancellationToken.None);
        Assert.False(manager.Serves("www.shop.test"));
        Assert.DoesNotContain("www.shop.test", new CertificateStore(_paths, _protection).StoredHosts());
    }

    [Fact]
    public async Task An_alias_asks_for_what_its_site_does_but_automatic_for_an_uploaded_or_shared_certificate()
    {
        await _sites.SaveAsync(new SiteRecord { Domain = "shop.test", Aliases = ["www.shop.test"], Tls = new SiteTlsSettings { Source = CertificateSources.None } });
        var manager = Certificates();
        await manager.RunOnceAsync(CancellationToken.None);
        Assert.False(manager.Serves("www.shop.test"));

        await _sites.SaveAsync(new SiteRecord { Domain = "shop.test", Aliases = ["www.shop.test"], Tls = new SiteTlsSettings { Source = CertificateSources.Uploaded } });
        Assert.Equal(CertificateSources.Automatic, manager.StatusOf("www.shop.test").Wanted);

        // A shared certificate need not cover the other domain; automatic still takes one that does.
        await _sites.SaveAsync(new SiteRecord { Domain = "shop.test", Aliases = ["www.shop.test"], Tls = new SiteTlsSettings { Source = CertificateSources.Shared } });
        Assert.Equal(CertificateSources.Automatic, manager.StatusOf("www.shop.test").Wanted);
        Assert.Equal(CertificateSources.Shared, manager.StatusOf("shop.test").Wanted);
    }

    [Fact]
    public async Task A_name_under_a_wildcard_alias_is_served_the_shared_certificate_that_covers_it()
    {
        await Site("app.example.com", "*.app.example.com");
        await Site("admin.app.example.com");
        await _sites.SaveAsync(new SiteRecord { Domain = "plain.app.example.com", Tls = new SiteTlsSettings { Source = CertificateSources.None } });
        var manager = Certificates();
        await manager.RunOnceAsync(CancellationToken.None);

        // What the API and get_tls say about the wildcard.
        JsonElement Described() => JsonSerializer.SerializeToElement(TlsEndpoints.DescribeSite(_sites.TryGet("app.example.com")!, manager))
            .GetProperty("aliases").EnumerateArray().Single();

        // Without one, the stand-in, and HTTP is not sent to HTTPS.
        Assert.False(manager.Serves("abc.app.example.com"));
        Assert.StartsWith("CN=static-site-host,", manager.Select("abc.app.example.com").TargetCertificate.Subject);
        Assert.Null(manager.SharedCovering("*.app.example.com"));
        Assert.Contains("No shared certificate covers", Described().GetProperty("problem").GetString(), StringComparison.Ordinal);

        var (chain, key) = CertificateMaterial.CreateSelfSigned("*.app.example.com");
        Assert.Null((await manager.AddSharedAsync(chain, key, "ada")).Error);

        Assert.True(manager.Serves("abc.app.example.com"));
        Assert.StartsWith("CN=*.app.example.com,", manager.Select("abc.app.example.com").TargetCertificate.Subject);
        Assert.NotNull(manager.SharedCovering("*.app.example.com"));

        // A site at a name of its own under the wildcard goes by its own choice: here, no HTTPS.
        Assert.False(manager.Serves("plain.app.example.com"));
        // A name no site answers is not served it either.
        Assert.False(manager.Serves("abc.other.example.com"));

        // A site with no HTTPS has none at its wildcard's names.
        var app = _sites.TryGet("app.example.com")!;
        Assert.Null(await manager.SetSiteAsync(app, CertificateSources.None, null, null, "ada"));
        Assert.False(manager.Serves("abc.app.example.com"));

        // And nothing is said to be missing: it has none on purpose.
        var plain = Described();
        Assert.Equal(CertificateSources.None, plain.GetProperty("effective").GetString());
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("certificate").ValueKind);
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("problem").ValueKind);
    }

    [Fact]
    public async Task Requests_at_an_alias_are_sent_to_https_as_its_site_says()
    {
        await _sites.SaveAsync(new SiteRecord
        {
            Domain = "plain.test", Aliases = ["www.plain.test", "*.plain.test"], Tls = new SiteTlsSettings { RedirectToHttps = false, Hsts = true }
        });
        await Site("shop.test", "www.shop.test");
        var manager = Certificates(null, "admin.plain.test");
        await manager.RunOnceAsync(CancellationToken.None);

        var options = new SiteHostingOptions { ManagementHosts = ["admin.plain.test"] };
        options.Tls.Enabled = true;
        var middleware = new TlsMiddleware(_ => Task.CompletedTask, new AcmeChallenges(), manager, _sites, Options.Create(options));

        async Task<HttpContext> Send(string host)
        {
            var context = new DefaultHttpContext();
            context.Request.Host = new HostString(host);
            context.Request.Path = "/cart";
            await middleware.InvokeAsync(context);
            return context;
        }

        var redirected = await Send("www.shop.test");
        Assert.Equal(308, redirected.Response.StatusCode);
        Assert.Equal("https://www.shop.test/cart", redirected.Response.Headers.Location.ToString());
        Assert.Equal(200, (await Send("www.plain.test")).Response.StatusCode);

        // A management host under the site's wildcard keeps its own: sent to HTTPS, and no HSTS from the site.
        Assert.Equal(308, (await Send("admin.plain.test")).Response.StatusCode);

        async Task<string> HstsAt(string host)
        {
            var context = new DefaultHttpContext();
            var response = new TlsTests.StartingResponse();
            context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(response);
            context.Request.Scheme = "https";
            context.Request.Host = new HostString(host);
            await middleware.InvokeAsync(context);
            await response.StartAsync();
            return context.Response.Headers.StrictTransportSecurity.ToString();
        }

        Assert.NotEmpty(await HstsAt("abc.plain.test"));
        Assert.Empty(await HstsAt("admin.plain.test"));
    }

    // ---------------------------------------------------------------- DNS

    [Fact]
    public async Task An_alias_gets_records_while_its_site_has_them_proxied_as_the_sites_are()
    {
        var cloudflare = new FakeCloudflare();
        var options = Options.Create(new SiteHostingOptions { DataRoot = _root, ManagementHosts = ["deploy.example.com"] });
        var dns = DnsTests.Service(_paths, _sites, cloudflare, options);
        await Site("shop.example.com", "www.example.com", "*.shop.example.com", "elsewhere.org");
        Assert.Null(await dns.SetTokenAsync(FakeCloudflare.Token, "ann", default));
        Assert.Null(await dns.SetRecordsAsync(false, "203.0.113.5", null, null, proxied: true, "ann", default));
        Assert.Empty(cloudflare.Records);

        Assert.Null(await dns.AddSiteAsync("shop.example.com", proxied: false, "ann", default));
        Assert.False(Assert.Single(cloudflare.At("shop.example.com")).Proxied);
        Assert.False(Assert.Single(cloudflare.At("www.example.com")).Proxied);
        Assert.Equal("203.0.113.5", Assert.Single(cloudflare.At("*.shop.example.com")).Content);
        Assert.Equal("noZone", dns.Status["elsewhere.org"].State);

        // The API and the tools say whose each name is.
        Assert.Equal("shop.example.com", dns.Status["*.shop.example.com"].AliasOf);
        Assert.Equal("shop.example.com", dns.Status["elsewhere.org"].AliasOf);
        Assert.Null(dns.Status["shop.example.com"].AliasOf);

        // The site's choice of proxy is its other domains' too.
        Assert.Null(await dns.AddSiteAsync("shop.example.com", proxied: true, "ann", default));
        Assert.True(Assert.Single(cloudflare.At("*.shop.example.com")).Proxied);

        // An alias removed loses its records; the site's stay.
        await Site("shop.example.com", "*.shop.example.com");
        await dns.ReconcileAsync(default);
        Assert.Empty(cloudflare.At("www.example.com"));
        Assert.Single(cloudflare.At("shop.example.com"));

        // The site's records removed by hand: its other domains' go too.
        Assert.Null(await dns.RemoveSiteAsync("shop.example.com", "ann", default));
        Assert.Empty(cloudflare.Records);
        Assert.Equal("excluded", dns.Status["*.shop.example.com"].State);
    }

    // ---------------------------------------------------------------- realtime

    [Fact]
    public void A_page_connects_to_the_hub_at_its_own_name_only()
    {
        static string? Check(string host, string origin)
        {
            var headers = new HeaderDictionary { ["Origin"] = origin };
            return RealtimeRequestCheck.Refusal(headers, isWebSocket: false, host);
        }

        Assert.Null(Check("abc.app.example.com", "https://abc.app.example.com"));
        Assert.NotNull(Check("abc.app.example.com", "https://def.app.example.com"));
        Assert.NotNull(Check("abc.app.example.com", "https://app.example.com"));
    }

    [Fact]
    public async Task A_realtime_connection_is_kept_at_every_name_its_site_answers_and_only_while_it_does()
    {
        await Private("app.example.com", "*.app.example.com", "www.example.org");
        var gate = Passcodes();
        var site = _sites.TryGet("app.example.com")!;
        var cookie = NameAndValue((await UnlockAsync(gate, "abc.app.example.com"))[1]);

        bool Served(string host)
        {
            var context = new DefaultHttpContext();
            context.Request.Host = new HostString(host);
            context.Request.Headers.Cookie = cookie;
            return SiteHub.IsStillServed(_sites, gate, context, site);
        }

        Assert.True(Served("app.example.com"));
        Assert.True(Served("abc.app.example.com"));
        Assert.True(Served("www.example.org"));

        // A site of its own under the wildcard takes the name.
        await Site("abc.app.example.com");
        Assert.False(Served("abc.app.example.com"));
        Assert.True(Served("def.app.example.com"));

        // The wildcard removed, and then the passcode changed.
        site.Aliases = ["www.example.org"];
        await _sites.SaveAsync(site);
        Assert.False(Served("def.app.example.com"));
        Assert.True(Served("www.example.org"));

        site.PasscodeHash = PasswordHasher.Hash("another-horse");
        await _sites.SaveAsync(site);
        Assert.False(Served("www.example.org"));
    }

    // ---------------------------------------------------------------- passcode

    private SitePasscodeGate Passcodes() => new(
        _protection, _sites, new AuditLog(_paths, NullLogger<AuditLog>.Instance),
        Options.Create(new SiteHostingOptions { DataRoot = _root }), NullLogger<SitePasscodeGate>.Instance,
        new JsonStringLocalizer(new StringCatalog(), NullLogger<JsonStringLocalizer>.Instance));

    private Task Private(string domain, params string[] aliases) =>
        _sites.SaveAsync(new SiteRecord { Domain = domain, Aliases = [.. aliases], PasscodeHash = PasswordHasher.Hash("correct-horse") });

    /// <summary>The Set-Cookie headers of a right passcode posted at <paramref name="host"/>.</summary>
    private async Task<string[]> UnlockAsync(SitePasscodeGate gate, string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        context.Request.Path = SitePasscodeGate.UnlockPath;
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Body = new MemoryStream("passcode=correct-horse&r=%2F"u8.ToArray());

        Assert.True(await gate.TryHandleAsync(context, _sites.Resolve(host)!));
        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        return context.Response.Headers.SetCookie.ToArray()!;
    }

    private static bool UnlockedWith(SitePasscodeGate gate, SiteRecord site, string host, string cookie)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Request.Headers.Cookie = cookie;
        return gate.IsUnlocked(context, site);
    }

    private static string NameAndValue(string setCookie) => setCookie[..setCookie.IndexOf(';')];

    [Fact]
    public async Task A_passcode_entered_under_a_wildcard_unlocks_every_name_it_answers()
    {
        await Private("app.example.com", "*.app.example.com", "www.example.org");
        var gate = Passcodes();
        var site = _sites.TryGet("app.example.com")!;

        var cookies = await UnlockAsync(gate, "abc.app.example.com");
        Assert.Equal(2, cookies.Length);
        Assert.DoesNotContain("domain=", cookies[0], StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("ssh.pass.app.example.com=", cookies[1], StringComparison.Ordinal);
        Assert.Contains("domain=app.example.com", cookies[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookies[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookies[1], StringComparison.OrdinalIgnoreCase);

        // The browser sends the shared cookie to every name under the domain; each one is unlocked.
        var shared = NameAndValue(cookies[1]);
        Assert.True(UnlockedWith(gate, site, "def.app.example.com", shared));
        Assert.True(UnlockedWith(gate, site, "app.example.com", shared));

        // At the site's domain, the wildcard's own domain, the same goes.
        Assert.Contains(await UnlockAsync(gate, "app.example.com"), c => c.Contains("domain=app.example.com", StringComparison.OrdinalIgnoreCase));

        // An exact alias is under no wildcard: its cookie stays host-only.
        Assert.Single(await UnlockAsync(gate, "www.example.org"));

        // A new passcode turns the shared cookie off with the rest.
        site.PasscodeHash = PasswordHasher.Hash("correct-horse");
        await _sites.SaveAsync(site);
        Assert.False(UnlockedWith(gate, site, "def.app.example.com", shared));
    }

    [Fact]
    public async Task A_shared_passcode_cookie_never_unlocks_another_site_under_the_same_domain()
    {
        await Private("app.example.com", "*.app.example.com");
        await Private("admin.app.example.com");
        var gate = Passcodes();

        var value = NameAndValue((await UnlockAsync(gate, "abc.app.example.com"))[1]).Split('=', 2)[1];
        var other = _sites.TryGet("admin.app.example.com")!;

        // Its name is the site's, so it is not the other site's cookie; under the other site's names its value is no good either.
        Assert.False(UnlockedWith(gate, other, "admin.app.example.com", "ssh.pass.app.example.com=" + value));
        Assert.False(UnlockedWith(gate, other, "admin.app.example.com", "ssh.pass=" + value));
        Assert.False(UnlockedWith(gate, other, "admin.app.example.com", "ssh.pass.admin.app.example.com=" + value));
    }

    [Theory]
    [InlineData("abc.app.example.com", "example.com")]
    [InlineData("app.example.com", "example.com")]
    [InlineData("abc.example.com", "example.com")]
    [InlineData("www.example.org", null)]
    [InlineData("site.example.net", null)]
    public void The_shared_cookie_goes_on_the_widest_wildcard_domain_over_the_name(string name, string? expected)
    {
        var site = new SiteRecord { Domain = "site.example.net", Aliases = ["*.app.example.com", "*.example.com", "www.example.org"] };
        Assert.Equal(expected, SitePasscodeGate.SharedDomain(site, name));
    }

    // ---------------------------------------------------------------- proxy cookies

    [Fact]
    public void A_proxy_route_may_share_cookies_on_a_domain_one_of_the_sites_other_domains_belongs_to()
    {
        static ProxyRoute Route(string parent) => new()
        {
            Path = "/api", Target = "http://api:8080", CookieMode = ProxyCookieModes.Parent, CookieParentDomain = parent
        };

        string[] aliases = ["*.tenants.example.org", "www.example.net"];

        Assert.Empty(ProxyRouteRules.Validate("app.example.com", [Route("example.com")], aliases));
        Assert.Empty(ProxyRouteRules.Validate("app.example.com", [Route("tenants.example.org")], aliases));
        Assert.Empty(ProxyRouteRules.Validate("app.example.com", [Route("example.org")], aliases));
        Assert.Empty(ProxyRouteRules.Validate("app.example.com", [Route("example.net")], aliases));

        Assert.Contains(ProxyRouteRules.Validate("app.example.com", [Route("other.example.org")], aliases),
            e => e.Contains("one of its other domains", StringComparison.Ordinal));
        Assert.Contains(ProxyRouteRules.Validate("app.example.com", [Route("tenants.example.org")]),
            e => e.Contains("a domain it belongs to", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the assistant and MCP tools

    private IServiceProvider SignedIn(string role)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "ann"), new Claim(ClaimTypes.Role, role)], "test"));
        return new ServiceCollection()
            .AddSingleton(_sites)
            .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } })
            .BuildServiceProvider();
    }

    [Fact]
    public async Task Tools_that_create_a_missing_site_never_take_a_name_another_site_answers_at()
    {
        await Site("app.example.com", "*.app.example.com", "www.example.org");
        var services = SignedIn(Roles.Administrator);

        // A model asked to change abc.app.example.com means the site that answers there.
        var route = await SiteHostTools.SetProxyRoute(services, "abc.app.example.com", new ProxyRoute { Path = "/api", Target = "http://api:8080" });
        var error = route.GetProperty("error").GetString()!;
        Assert.Contains("another domain of app.example.com", error, StringComparison.Ordinal);
        Assert.Contains("create_site", error, StringComparison.Ordinal);

        var deploy = await SiteHostTools.DeployFiles(services, "www.example.org", [new SiteFileInput("index.html", "hi")]);
        Assert.Contains("another domain of app.example.com", deploy.GetProperty("error").GetString(), StringComparison.Ordinal);

        Assert.False(_sites.Exists("abc.app.example.com"));
        Assert.False(_sites.Exists("www.example.org"));
        Assert.Equal("app.example.com", _sites.Resolve("abc.app.example.com")?.Domain);

        // A wildcard given where a site is asked for names its site too.
        var site = SiteHostTools.GetSite(services, "*.app.example.com");
        Assert.Contains("another domain of app.example.com", site.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_assistant_is_told_each_sites_other_domains()
    {
        await Site("app.example.com", "*.app.example.com", "www.example.org");
        await Site("plain.example.com");

        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "ann")], "test"));
        var context = AssistantEndpoints.Context(user, isAdministrator: true, _sites, [], "app.example.com");

        Assert.Contains("- app.example.com: also answers at *.app.example.com, www.example.org", context, StringComparison.Ordinal);
        Assert.Contains("- plain.example.com: empty", context, StringComparison.Ordinal);
    }
}
