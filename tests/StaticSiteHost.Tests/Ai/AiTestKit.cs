using System.Text;
using System.Text.Json;
using StaticSiteHost.Functions;
using StaticSiteHost.Models;
using StaticSiteHost.Services.Ai;

namespace StaticSiteHost.Tests.Ai;

/// <summary>What the wire format tests share: a call to a made-up provider, and the bodies it sends.</summary>
internal static class AiTestKit
{
    public static AiProviderRecord Provider(string kind, string baseUrl = "https://llm.example.com") => new()
    {
        Id = "p1",
        Name = "Test",
        Kind = kind,
        BaseUrl = baseUrl,
        DefaultModel = "default-model"
    };

    public static AiCall Call(string kind, AiChatRequest request, string model = "test-model") =>
        new(Provider(kind), "sk-test", model, request);

    public static IAiWireFormat Format(string kind) =>
        kind == AiProviderRecord.KindAnthropic ? AnthropicChat.Instance : OpenAiCompatibleChat.Instance;

    /// <summary>The request body exactly as it would go on the wire.</summary>
    public static string Body(string kind, AiChatRequest request, bool stream = false)
    {
        using var http = Format(kind).CreateRequest(Call(kind, request), stream);
        return Encoding.UTF8.GetString(http.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult());
    }

    /// <summary>The body parsed, for tests that look at its shape rather than its bytes.</summary>
    public static JsonElement BodyJson(string kind, AiChatRequest request, bool stream = false)
    {
        using var document = JsonDocument.Parse(Body(kind, request, stream));
        return document.RootElement.Clone();
    }

    public static AiChatResponse Read(string kind, string json, AiChatRequest? request = null)
    {
        using var document = JsonDocument.Parse(json);
        return Format(kind).ReadResponse(document.RootElement, Call(kind, request ?? WithTools()));
    }

    /// <summary>
    /// Feeds a recorded stream of server-sent events, as (event type, data) pairs, and returns the
    /// text pieces it yielded and the final response.
    /// </summary>
    public static (List<string> Pieces, AiChatResponse Final) Stream(string kind, params (string Event, string Data)[] events) =>
        Stream(kind, WithTools(), events);

    public static (List<string> Pieces, AiChatResponse Final) Stream(
        string kind, AiChatRequest request, params (string Event, string Data)[] events)
    {
        var stream = Format(kind).StartStream(Call(kind, request));
        var pieces = new List<string>();

        foreach (var (type, data) in events)
        {
            if (stream.Take(type, data) is { Length: > 0 } text) pieces.Add(text);
            if (stream.IsDone) break;
        }

        Assert.True(stream.IsComplete);
        return (pieces, stream.Result());
    }

    public static AiChatRequest Hello() => new() { Messages = [AiMessage.User("Hello")] };

    /// <summary>A request that offers the tools the recorded answers call, so their calls are read.</summary>
    public static AiChatRequest WithTools() => new()
    {
        Messages = [AiMessage.User("Hello")],
        Tools =
        [
            new AiTool("look_up_star", "Finds a star.", Json("""{"type":"object"}""")),
            new AiTool("time_now", "The time.", Json("""{"type":"object"}"""))
        ]
    };

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
