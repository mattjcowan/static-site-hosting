using StaticSiteHost.Models;
using StaticSiteHost.Serving;

namespace StaticSiteHost.Tests;

public class ServerProxyRouteTests
{
    private static ServerProxyRoute Server(string sites, string path = "/api", string target = "http://api:8080", bool enabled = true) =>
        new() { Id = Guid.NewGuid().ToString("n")[..8], Sites = sites, Path = path, Target = target, Enabled = enabled };

    [Theory]
    [InlineData("*", "shop.example.com", true)]
    [InlineData("*.apps.example.com", "shop.apps.example.com", true)]
    [InlineData("*.apps.example.com", "a.b.apps.example.com", true)]
    [InlineData("*.apps.example.com", "apps.example.com", false)]
    [InlineData("*.apps.example.com", "shopapps.example.com", false)]
    [InlineData("shop.example.com", "SHOP.example.com", true)]
    [InlineData("shop.example.com, *.apps.example.com", "x.apps.example.com", true)]
    [InlineData("shop.example.com", "other.example.com", false)]
    public void A_pattern_names_the_sites_it_should(string sites, string domain, bool matches) =>
        Assert.Equal(matches, ProxySites.Match(sites, domain) is not null);

    [Fact]
    public void A_domain_names_a_site_more_closely_than_a_wildcard_and_a_longer_wildcard_than_a_shorter_one()
    {
        var exact = ProxySites.Match("shop.apps.example.com", "shop.apps.example.com");
        var deep = ProxySites.Match("*.apps.example.com", "shop.apps.example.com");
        var shallow = ProxySites.Match("*.example.com", "shop.apps.example.com");
        var all = ProxySites.Match("*", "shop.apps.example.com");

        Assert.True(exact > deep && deep > shallow && shallow > all);
    }

    [Theory]
    [InlineData("*", null)]
    [InlineData(" *.Apps.Example.com , shop.example.com ", null)]
    [InlineData("", "say which sites")]
    [InlineData("*.", "is not")]
    [InlineData("**.example.com", "is not")]
    [InlineData("shop.*.com", "is not")]
    [InlineData("https://x.example.com/", "is not")]
    public void Patterns_are_checked(string sites, string? expected)
    {
        var error = ProxySites.Check(sites);
        if (expected is null) Assert.Null(error);
        else Assert.Contains(expected, error);
    }

    [Fact]
    public void Patterns_are_stored_lowercase_and_tidy() =>
        Assert.Equal("*.apps.example.com, shop.example.com", ProxySites.Normalize(" *.Apps.Example.com ,shop.example.com,"));

    [Fact]
    public void A_site_gets_the_server_wide_routes_it_does_not_override_or_turn_off()
    {
        List<ProxyRoute> own = [new() { Path = "/", Target = "http://web:3000" }];
        var api = Server("*.apps.example.com", "/api");
        var docs = Server("*", "/docs");
        var admin = Server("*", "/admin");

        var effective = ProxySites.Effective("shop.apps.example.com", own, [api, docs, admin], ["/ADMIN"]);
        Assert.Equal(["/", "/api", "/docs"], effective.Select(r => r.Path));

        // The site's own route at the same path wins.
        List<ProxyRoute> overriding = [new() { Path = "/api", Target = "http://staging-api:8080" }];
        Assert.Equal("http://staging-api:8080", Assert.Single(ProxySites.Effective("shop.apps.example.com", overriding, [api], []), r => r.Path == "/api").Target);

        // A site the pattern does not name gets none of it.
        Assert.Empty(ProxySites.Effective("other.example.com", [], [api], []));
    }

    [Fact]
    public void When_server_wide_routes_share_a_path_the_closest_pattern_wins_and_one_switched_off_steps_aside()
    {
        var everywhere = Server("*", "/api", "http://general:8080");
        var apps = Server("*.apps.example.com", "/api", "http://apps-api:8080");
        var shop = Server("shop.apps.example.com", "/api", "http://shop-api:8080");

        Assert.Equal("http://shop-api:8080", ProxySites.Effective("shop.apps.example.com", [], [everywhere, apps, shop], []).Single().Target);
        Assert.Equal("http://apps-api:8080", ProxySites.Effective("blog.apps.example.com", [], [everywhere, apps, shop], []).Single().Target);
        Assert.Equal("http://general:8080", ProxySites.Effective("blog.example.com", [], [everywhere, apps, shop], []).Single().Target);

        shop.Enabled = false;
        Assert.Equal("http://apps-api:8080", ProxySites.Effective("shop.apps.example.com", [], [everywhere, apps, shop], []).Single().Target);

        // A site's own route that is switched off does not override either.
        List<ProxyRoute> ownOff = [new() { Path = "/api", Target = "http://x:1", Enabled = false }];
        Assert.Contains(ProxySites.Effective("blog.apps.example.com", ownOff, [apps], []), r => r.Target == "http://apps-api:8080");
    }

    [Fact]
    public void A_sites_page_says_why_each_server_wide_route_is_or_is_not_in_effect()
    {
        List<ProxyRoute> own = [new() { Path = "/api", Target = "http://staging:1" }];
        var states = ProxySites.ForSite("shop.apps.example.com", own,
            [Server("*", "/api"), Server("*", "/docs"), Server("*", "/admin"), Server("*", "/old", enabled: false), Server("other.example.com", "/x")],
            ["/admin"]).ToDictionary(r => r.Route.Path, r => r.State);

        Assert.Equal(4, states.Count); // /x names another site, so it is not listed
        Assert.Equal("overridden by this site's own route", states["/api"]);
        Assert.Equal("on", states["/docs"]);
        Assert.Equal("off for this site", states["/admin"]);
        Assert.Equal("switched off for every site", states["/old"]);
    }

    [Fact]
    public void Server_wide_routes_may_share_a_path_for_different_sites_but_not_for_the_same()
    {
        Assert.Empty(ProxyRouteRules.Validate(null, [Server("*.a.example.com"), Server("*.b.example.com")]));
        Assert.Contains(ProxyRouteRules.Validate(null, [Server("*.a.example.com"), Server("*.A.example.com ")]),
            e => e.Contains("for the same sites", StringComparison.Ordinal));
        Assert.Contains(ProxyRouteRules.Validate(null, [Server("")]), e => e.Contains("say which sites", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("*.apps.example.com", "example.com", null)]
    [InlineData("*.apps.example.com", "apps.example.com", null)]
    [InlineData("*.apps.example.com", "other.com", "does not cover")]
    [InlineData("*", "example.com", null)]
    public void A_parent_cookie_domain_must_cover_every_site_the_pattern_names(string sites, string parent, string? expected)
    {
        var route = Server(sites);
        route.CookieMode = ProxyCookieModes.Parent;
        route.CookieParentDomain = parent;

        var errors = ProxyRouteRules.Validate(null, [route]);
        if (expected is null) Assert.Empty(errors);
        else Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }
}
