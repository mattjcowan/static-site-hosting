using System.Net;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;

namespace StaticSiteHost.Tests;

public class ProxyRouteRulesTests
{
    private static ProxyRoute Route(string path = "/api", string target = "http://orders:8080") =>
        new() { Path = path, Target = target };

    [Theory]
    [InlineData("api", "/api")]
    [InlineData("/api/", "/api")]
    [InlineData("  /api//  ", "/api")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("///", "/")]
    [InlineData("/a/b", "/a/b")]
    public void Paths_are_stored_with_one_leading_slash_and_no_trailing_one(string input, string expected) =>
        Assert.Equal(expected, ProxyRouteRules.NormalizePath(input));

    [Fact]
    public void A_plain_route_is_accepted() =>
        Assert.Empty(ProxyRouteRules.Validate("app.example.com", [Route(), Route("/", "https://web:3000/base")]));

    [Theory]
    [InlineData("/_host", "/_host/")]
    [InlineData("/_HOST/realtime", "/_host/")]
    [InlineData("/healthz", "/healthz")]
    [InlineData("/a/../b", ". or ..")]
    [InlineData("/a?b", "may not contain")]
    [InlineData("/a b", "may not contain")]
    [InlineData("/a//b", "empty segment")]
    [InlineData("api", "must start with /")]
    public void Paths_that_belong_to_the_host_or_are_not_paths_are_refused(string path, string expected)
    {
        var errors = ProxyRouteRules.Validate("app.example.com", [Route(path)]);
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Two_routes_may_not_share_a_path_whatever_its_case()
    {
        var errors = ProxyRouteRules.Validate("app.example.com", [Route("/api"), Route("/API")]);
        Assert.Contains(errors, e => e.Contains("another route already has this path", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", "a target URL is required")]
    [InlineData("orders:8080", "http:// or https://")]
    [InlineData("ftp://orders/", "http:// or https://")]
    [InlineData("http://user:secret@orders/", "user name or password")]
    [InlineData("http://orders/?a=1", "query string")]
    [InlineData("http://orders/#top", "fragment")]
    public void Targets_must_be_plain_http_urls(string target, string expected)
    {
        var errors = ProxyRouteRules.Validate("app.example.com", [Route(target: target)]);
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void A_target_naming_an_environment_variable_is_read_from_it()
    {
        const string name = "SSH_TEST_PROXY_TARGET_RULES";
        Environment.SetEnvironmentVariable(name, "http://from-env:9000/v2");
        try
        {
            var (target, error) = ProxyRouteRules.ResolveTarget("${env:" + name + "}");
            Assert.Null(error);
            Assert.Equal("http://from-env:9000/v2", target!.AbsoluteUri);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        // Saving is allowed while it is unset, as for variables; the route answers 502 until it is set.
        Assert.Empty(ProxyRouteRules.Validate("app.example.com", [Route(target: "${env:" + name + "}")]));
        Assert.Contains(name, ProxyRouteRules.ResolveTarget("${env:" + name + "}").Error);
    }

    [Theory]
    [InlineData("example.com", null)]
    [InlineData("app.example.com", null)]
    [InlineData("other.com", "is not app.example.com or a domain it belongs to")]
    [InlineData("com", "too broad")]
    [InlineData("", "name the parent domain")]
    public void A_parent_cookie_domain_must_contain_the_site(string parent, string? expected)
    {
        var route = Route();
        route.CookieMode = ProxyCookieModes.Parent;
        route.CookieParentDomain = parent;

        var errors = ProxyRouteRules.Validate("app.example.com", [route]);
        if (expected is null) Assert.Empty(errors);
        else Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Cookie")]
    [InlineData("host")]
    [InlineData("X-Forwarded-For")]
    [InlineData("Transfer-Encoding")]
    public void Headers_the_proxy_manages_cannot_be_set(string name)
    {
        var route = Route();
        route.RequestHeaders = [new ProxyHeader { Name = name, Value = "x" }];

        Assert.Contains(ProxyRouteRules.Validate("app.example.com", [route]), e => e.Contains("managed by the server", StringComparison.Ordinal));
    }

    [Fact]
    public void Settings_out_of_range_are_refused()
    {
        var route = Route();
        route.HttpVersion = "3";
        route.TimeoutSeconds = 0;
        route.CookieMode = "everywhere";

        var errors = ProxyRouteRules.Validate("app.example.com", [route]);
        Assert.Contains(errors, e => e.Contains("HTTP version", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("timeout", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("cookie mode", StringComparison.Ordinal));
    }

    [Fact]
    public void Request_headers_read_from_text_and_back()
    {
        Assert.True(ProxyRouteRules.TryParseHeaders("X-Api-Key: ${env:KEY}\n# comment\n\nX-Debug:\r\n", out var headers, out _));
        Assert.Equal(2, headers.Count);
        Assert.Equal(("X-Api-Key", "${env:KEY}"), (headers[0].Name, headers[0].Value));
        Assert.Equal(("X-Debug", ""), (headers[1].Name, headers[1].Value));
        Assert.Equal("X-Api-Key: ${env:KEY}\nX-Debug:", ProxyRouteRules.FormatHeaders(headers));

        Assert.False(ProxyRouteRules.TryParseHeaders("no colon here", out _, out var error));
        Assert.Contains("line 1", error);
    }

    [Fact]
    public void The_servers_own_cookies_never_reach_the_backend() =>
        Assert.Equal("a=1; b=2",
            ProxyTransformer.WithoutServerCookies(new Microsoft.Extensions.Primitives.StringValues(["ssh.auth=x; a=1", "SSH.pass=y; b=2"])));

    private static ProxyTransformer Transformer(ProxyRoute route, string target = "http://orders:8080") =>
        new(route, route.Path == "/" ? "" : route.Path, new Uri(target), []);

    [Fact]
    public void Cookies_are_scoped_as_the_route_says_and_ones_named_like_the_servers_are_dropped()
    {
        var cookies = new Microsoft.Extensions.Primitives.StringValues(
            ["a=1; Path=/; Domain=orders.internal; HttpOnly", "ssh.auth=forged; Domain=example.com"]);

        var route = Route();
        Assert.Equal(["a=1; Path=/; HttpOnly"], Transformer(route).RewriteCookies(cookies, "app.example.com"));

        route.CookieMode = ProxyCookieModes.Keep;
        Assert.Equal(["a=1; Path=/; Domain=orders.internal; HttpOnly"], Transformer(route).RewriteCookies(cookies, "app.example.com"));

        route.CookieMode = ProxyCookieModes.Parent;
        route.CookieParentDomain = "example.com";
        Assert.Equal(["a=1; Path=/; HttpOnly; Domain=example.com"], Transformer(route).RewriteCookies(cookies, "app.example.com"));

        // A site renamed out from under its parent domain gets host-only cookies, not someone else's domain.
        Assert.Equal(["a=1; Path=/; HttpOnly"], Transformer(route).RewriteCookies(cookies, "app.other.com"));
    }

    [Theory]
    [InlineData("/api", true, "http://orders:8080", "http://orders:8080/login?next=1", "/api/login?next=1")]
    [InlineData("/api", true, "http://orders:8080", "http://orders:8080/", "/api/")]
    [InlineData("/api", true, "http://orders:8080/v2", "http://orders:8080/v2/login", "/api/login")]
    [InlineData("/api", true, "http://orders:8080/v2", "http://orders:8080/v2", "/api")]
    [InlineData("/api", true, "http://orders:8080/v2", "http://orders:8080/v1/login", "http://orders:8080/v1/login")]
    [InlineData("/api", false, "http://orders:8080", "http://orders:8080/api/login", "/api/login")]
    [InlineData("/", true, "http://web:3000", "http://web:3000/dashboard", "/dashboard")]
    [InlineData("/api", true, "http://orders:8080", "https://orders:8080/login", "https://orders:8080/login")]
    [InlineData("/api", true, "http://orders:8080", "https://accounts.example.com/sign-in", "https://accounts.example.com/sign-in")]
    [InlineData("/api", true, "http://orders:8080", "/login", "/login")]
    public void Redirects_to_the_target_come_back_as_paths_on_the_site(
        string path, bool strip, string target, string location, string expected)
    {
        var route = Route(path, target);
        route.StripPath = strip;
        Assert.Equal(expected, Transformer(route, target).RewriteLocation(location));
    }

    [Fact]
    public void Link_local_is_blocked_by_default_and_loopback_and_private_networks_are_not()
    {
        // No server here, so no ports of its own to refuse: ProxyForwardingTests covers those.
        using var invokers = new ProxyInvokers(Microsoft.Extensions.Options.Options.Create(new Configuration.SiteHostingOptions()));

        Assert.Contains("ProxyBlockedNetworks", invokers.Refusal(IPAddress.Parse("169.254.169.254"), 80));
        Assert.Contains("ProxyBlockedNetworks", invokers.Refusal(IPAddress.Parse("fe80::1"), 80));
        Assert.Null(invokers.Refusal(IPAddress.Loopback, 3000));
        Assert.Null(invokers.Refusal(IPAddress.IPv6Loopback, 3000));
        Assert.Null(invokers.Refusal(IPAddress.Parse("10.1.2.3"), 80));
        Assert.Null(invokers.Refusal(IPAddress.Parse("172.18.0.5"), 8080));
        Assert.Null(invokers.Refusal(IPAddress.Parse("192.168.1.10"), 443));
    }

    [Fact]
    public void A_blocked_network_that_cannot_be_read_stops_the_server()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ProxyInvokers.ParseNetworks("10.0.0.0/8, nonsense"));
        Assert.Contains("'nonsense'", error.Message);
        Assert.Empty(ProxyInvokers.ParseNetworks(""));
    }
}
