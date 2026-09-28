using Microsoft.AspNetCore.Http;
using StaticSiteHost.Serving;

namespace StaticSiteHost.Tests;

/// <summary>The three steps that decide whether a request under the realtime hub comes from one of the site's own pages.</summary>
public class RealtimeRequestCheckTests
{
    private const string Site = "blog.example.com";

    private static string? Check(bool isWebSocket = false, params (string Name, string Value)[] headers)
    {
        var dictionary = new HeaderDictionary();
        foreach (var (name, value) in headers) dictionary.Append(name, value);
        return RealtimeRequestCheck.Refusal(dictionary, isWebSocket, Site);
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("none")]
    [InlineData("Same-Origin")]
    public void Fetch_metadata_from_the_site_itself_is_let_through(string value) =>
        Assert.Null(Check(false, ("Sec-Fetch-Site", value)));

    [Theory]
    [InlineData("same-site")]
    [InlineData("cross-site")]
    [InlineData("something-new")]
    public void Fetch_metadata_from_anywhere_else_is_refused(string value) =>
        Assert.Contains("came from a page somewhere else", Check(false, ("Sec-Fetch-Site", value)));

    [Fact]
    public void Fetch_metadata_decides_before_the_origin()
    {
        // A sibling site's no-cors GET, the long-polling attack: its Origin could be anything or nothing.
        Assert.NotNull(Check(false, ("Sec-Fetch-Site", "same-site"), ("Origin", $"https://{Site}")));
        Assert.NotNull(Check(true, ("Sec-Fetch-Site", "same-site")));
        Assert.Null(Check(false, ("Sec-Fetch-Site", "same-origin"), ("Origin", "https://elsewhere.example.com")));
    }

    [Theory]
    [InlineData("https://blog.example.com")]
    [InlineData("http://BLOG.example.com:8080")]
    [InlineData("https://blog.example.com.")]
    public void Without_fetch_metadata_an_origin_naming_the_site_is_let_through(string origin) =>
        Assert.Null(Check(false, ("Origin", origin)));

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("null")]
    [InlineData("not a url")]
    public void Without_fetch_metadata_any_other_origin_is_refused(string origin) =>
        Assert.Equal($"Only pages on {Site} can connect to its realtime hub.", Check(true, ("Origin", origin)));

    [Fact]
    public void Two_origins_are_refused() =>
        Assert.NotNull(Check(false, ("Origin", $"https://{Site}"), ("Origin", "https://evil.example.com")));

    [Fact]
    public void With_neither_a_websocket_is_let_through_as_no_browser_sends_one_so() =>
        Assert.Null(Check(true));

    [Fact]
    public void With_neither_anything_else_needs_the_header_signalr_sends()
    {
        Assert.Null(Check(false, ("X-Requested-With", "XMLHttpRequest")));
        Assert.Contains("X-Requested-With: XMLHttpRequest", Check(false));
        Assert.NotNull(Check(false, ("X-Requested-With", "fetch")));
    }

    [Fact]
    public void Recognises_a_websocket_upgrade_from_the_request_itself()
    {
        var upgrade = new DefaultHttpContext();
        upgrade.Request.Method = "GET";
        upgrade.Request.Headers.Upgrade = "websocket";
        Assert.True(RealtimeRequestCheck.IsWebSocketUpgrade(upgrade));

        var poll = new DefaultHttpContext();
        poll.Request.Method = "GET";
        Assert.False(RealtimeRequestCheck.IsWebSocketUpgrade(poll));

        var post = new DefaultHttpContext();
        post.Request.Method = "POST";
        post.Request.Headers.Upgrade = "websocket";
        Assert.False(RealtimeRequestCheck.IsWebSocketUpgrade(post));
    }
}
