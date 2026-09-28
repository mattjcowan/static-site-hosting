using System.Text.Json;
using StaticSiteHost.Services.Realtime;

namespace StaticSiteHost.Tests;

public class RealtimeNamesTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("post.published")]
    [InlineData("comments:42")]
    [InlineData("Editors_2026-q3")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123")] // 64
    public void Accepts_letters_digits_and_four_marks_up_to_64(string name)
    {
        Assert.True(RealtimeNames.IsValid(name));
        Assert.Null(RealtimeNames.EventError(name));
        Assert.Null(RealtimeNames.GroupError(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/name")]
    [InlineData("new\nline")]
    [InlineData("ends-with-newline\n")]
    [InlineData("café")]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")] // 65
    public void Refuses_anything_else(string name)
    {
        Assert.False(RealtimeNames.IsValid(name));
        Assert.NotNull(RealtimeNames.EventError(name));
        Assert.NotNull(RealtimeNames.GroupError(name));
    }

    [Fact]
    public void Says_which_kind_of_name_is_missing()
    {
        Assert.StartsWith("An event name is required", RealtimeNames.EventError(null));
        Assert.StartsWith("A group name is required", RealtimeNames.GroupError(""));
        Assert.StartsWith("\"has space\" cannot be a group name", RealtimeNames.GroupError("has space"));
    }

    [Fact]
    public void Keeps_each_sites_groups_under_its_domain()
    {
        Assert.Equal("site:blog.localhost:all", RealtimeNames.AllGroup("blog.localhost"));
        Assert.Equal("site:blog.localhost:g:journal", RealtimeNames.Group("blog.localhost", "journal"));
        Assert.Equal("site:blog.localhost:g:all", RealtimeNames.Group("blog.localhost", "all"));
    }

    [Fact]
    public void Carries_a_payload_of_up_to_256_KB_of_json()
    {
        Assert.Null(RealtimeNames.PayloadError(default));
        Assert.Null(RealtimeNames.PayloadError(Json("""{"slug":"orion"}""")));

        // A string's JSON is its characters and two quotes.
        Assert.Null(RealtimeNames.PayloadError(Json(Quoted(RealtimeNames.MaxPayloadBytes - 2))));

        var error = RealtimeNames.PayloadError(Json(Quoted(RealtimeNames.MaxPayloadBytes - 1)));
        Assert.NotNull(error);
        Assert.Contains("at most 256 KB", error);
    }

    private static string Quoted(int length) => $"\"{new string('x', length)}\"";

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
