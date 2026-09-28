using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Realtime;

namespace StaticSiteHost.Tests;

/// <summary>The per-address counting behind SiteHosting:RealtimeMaxConnectionsPerAddress, and the addresses it counts by.</summary>
public class RealtimeLimitsTests
{
    private const string Site = "blog.example.com";

    private static RealtimeRegistry Registry(int perSite = 1000, int perAddress = 2) =>
        new(Options.Create(new SiteHostingOptions
        {
            RealtimeMaxConnectionsPerSite = perSite,
            RealtimeMaxConnectionsPerAddress = perAddress
        }));

    private sealed class Caller(string id) : HubCallerContext
    {
        public override string ConnectionId { get; } = id;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public bool Aborted { get; private set; }
        public override void Abort() => Aborted = true;
    }

    [Fact]
    public void An_address_gets_its_share_and_no_more()
    {
        var registry = Registry(perAddress: 2);

        Assert.Equal(RealtimeRegistry.AddResult.Added, registry.TryAdd(Site, new Caller("a1"), null, "10.0.0.1"));
        Assert.False(registry.IsAddressFull(Site, "10.0.0.1"));
        Assert.Equal(RealtimeRegistry.AddResult.Added, registry.TryAdd(Site, new Caller("a2"), null, "10.0.0.1"));
        Assert.True(registry.IsAddressFull(Site, "10.0.0.1"));
        Assert.Equal(RealtimeRegistry.AddResult.AddressFull, registry.TryAdd(Site, new Caller("a3"), null, "10.0.0.1"));

        // Another address, and the same address on another site, are counted apart.
        Assert.Equal(RealtimeRegistry.AddResult.Added, registry.TryAdd(Site, new Caller("b1"), null, "10.0.0.2"));
        Assert.Equal(RealtimeRegistry.AddResult.Added, registry.TryAdd("other.example.com", new Caller("c1"), null, "10.0.0.1"));

        Assert.Equal(2, registry.ConnectionsFrom(Site, "10.0.0.1"));
        Assert.Equal(3, registry.ConnectionCount(Site));
    }

    [Fact]
    public void A_connection_that_ends_gives_its_place_back()
    {
        var registry = Registry(perAddress: 1);
        var first = new Caller("a1");

        Assert.Equal(RealtimeRegistry.AddResult.Added, registry.TryAdd(Site, first, null, "10.0.0.1"));
        Assert.Equal(RealtimeRegistry.AddResult.AddressFull, registry.TryAdd(Site, new Caller("a2"), null, "10.0.0.1"));

        Assert.True(registry.Remove(Site, "a1"));
        Assert.Equal(RealtimeRegistry.AddResult.Added, registry.TryAdd(Site, new Caller("a3"), null, "10.0.0.1"));

        Assert.True(registry.Abort(Site, "a3"));
        Assert.Equal(0, registry.ConnectionsFrom(Site, "10.0.0.1"));
        Assert.Equal(RealtimeRegistry.AddResult.Added, registry.TryAdd(Site, new Caller("a4"), null, "10.0.0.1"));

        Assert.Equal(1, registry.DisconnectSite(Site));
        Assert.Equal(0, registry.ConnectionsFrom(Site, "10.0.0.1"));
    }

    [Fact]
    public void A_full_site_is_full_whoever_asks_and_zero_sets_no_address_limit()
    {
        var full = Registry(perSite: 1, perAddress: 5);
        Assert.Equal(RealtimeRegistry.AddResult.Added, full.TryAdd(Site, new Caller("a1"), null, "10.0.0.1"));
        Assert.Equal(RealtimeRegistry.AddResult.SiteFull, full.TryAdd(Site, new Caller("b1"), null, "10.0.0.2"));

        var open = Registry(perAddress: 0);
        for (var i = 0; i < 50; i++)
            Assert.Equal(RealtimeRegistry.AddResult.Added, open.TryAdd(Site, new Caller($"a{i}"), null, "10.0.0.1"));
        Assert.False(open.IsAddressFull(Site, "10.0.0.1"));
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
    public void Counts_an_ipv6_client_by_its_64(string address, string key) =>
        Assert.Equal(key, ClientAddress.Key(IPAddress.Parse(address)));

    [Fact]
    public void Counts_an_unknown_client_as_one() => Assert.Equal("unknown", ClientAddress.Key(null));

    [Fact]
    public void The_negotiation_limiter_counts_a_minute_per_site_and_address()
    {
        var limiter = new RealtimeNegotiationLimiter();
        var address = IPAddress.Parse("198.51.100.1");

        for (var i = 0; i < 3; i++) Assert.True(limiter.TryAcquire(Site, address, 3, out _));
        Assert.False(limiter.TryAcquire(Site, address, 3, out var retryAfter));
        Assert.InRange(retryAfter, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryAcquire("other.example.com", address, 3, out _));
        Assert.True(limiter.TryAcquire(Site, IPAddress.Parse("198.51.100.2"), 3, out _));
        Assert.True(limiter.TryAcquire(Site, address, 0, out _));
    }
}
