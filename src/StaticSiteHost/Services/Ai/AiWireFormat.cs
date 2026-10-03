using System.Net.Http.Headers;
using System.Text.Json;
using StaticSiteHost.Functions;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services.Ai;

/// <summary>
/// One provider API's shape on the wire: how a chat becomes an HTTP request, and how its answer,
/// whole or streamed, and its errors are read back. <see cref="AiChatService"/> does the sending,
/// the timeouts and the error handling that are the same for every provider.
/// </summary>
internal interface IAiWireFormat
{
    /// <summary>The HTTP request for a call. <paramref name="stream"/> asks for server-sent events.</summary>
    HttpRequestMessage CreateRequest(AiCall call, bool stream);

    /// <summary>Reads a whole answer from a successful response's JSON body.</summary>
    /// <exception cref="AiChatException">The body is not an answer in this format.</exception>
    AiChatResponse ReadResponse(JsonElement body, AiCall call);

    /// <summary>Starts reading a streamed answer, one event at a time.</summary>
    IAiStream StartStream(AiCall call);

    /// <summary>The provider's own explanation in an error body, or null when it gives none.</summary>
    string? ReadError(JsonElement body);
}

/// <summary>A streamed answer being read, one server-sent event at a time.</summary>
internal interface IAiStream
{
    /// <summary>Takes one event and returns the text it adds to the answer, if any.</summary>
    /// <exception cref="AiChatException">The event reports an error, or is not in this format.</exception>
    string? Take(string eventType, string data);

    /// <summary>True once the provider has said it is done, so nothing after this needs reading.</summary>
    bool IsDone { get; }

    /// <summary>
    /// True when the stream so far makes a whole answer: either <see cref="IsDone"/>, or the model
    /// has said why it stopped and only the bookkeeping that follows is missing. Checked when the
    /// connection ends, to tell an answer from one cut off part way through.
    /// </summary>
    bool IsComplete { get; }

    /// <summary>The whole answer, for the last chunk.</summary>
    AiChatResponse Result();
}

/// <summary>
/// One chat on its way to a provider: the provider, its key in the clear, the model the chat
/// settled on, and the request. A class rather than a record, so there is no generated
/// <c>ToString</c> to put the key in a log line.
/// </summary>
internal sealed class AiCall(AiProviderRecord provider, string? apiKey, string model, AiChatRequest request)
{
    public AiProviderRecord Provider { get; } = provider;
    public string? ApiKey { get; } = apiKey;
    public string Model { get; } = model;
    public AiChatRequest Request { get; } = request;
}

/// <summary>Small JSON helpers the wire formats share.</summary>
internal static class AiJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A request body, serialised up front so it goes with a Content-Length: some proxies in front
    /// of model servers refuse a chunked request.
    /// </summary>
    public static ByteArrayContent Content(object body)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, Options));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    /// <summary>Parses one streamed event's data.</summary>
    /// <exception cref="AiChatException">It is not JSON.</exception>
    public static JsonDocument Parse(string data)
    {
        try
        {
            return JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            throw new AiChatException("The AI provider sent part of its answer in a form this server cannot read.");
        }
    }

    public static string? StringOf(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static JsonElement? ObjectOf(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    /// <summary>A whole number, or null when it is missing, not a number, or out of range.</summary>
    public static int? IntOf(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>The first element of an array property, or null.</summary>
    public static JsonElement? FirstOf(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Array &&
        value.GetArrayLength() > 0
            ? value[0]
            : null;
}

/// <summary>What the wire formats share about tool calls.</summary>
internal static class AiToolWire
{
    /// <summary>
    /// True when a request goes in the tool-calling shape: it offers tools, or its conversation
    /// has calls or results in it. Otherwise it goes exactly as it did before tools existed.
    /// </summary>
    public static bool UsesTools(AiChatRequest request) =>
        request.Tools is { Count: > 0 } ||
        request.Messages.Any(message => message.ToolCalls is { Count: > 0 } || message.Role == AiMessage.ToolRole);

    /// <summary>
    /// A call's input from the text a model wrote. Empty text is an empty object, which is what a
    /// model means by it for a tool that takes nothing. Text that is not JSON comes back as a JSON
    /// string holding the raw text (see <see cref="AiToolCall.Arguments"/>), so the caller can answer
    /// it with an error rather than the chat failing.
    /// </summary>
    public static JsonElement ParseArguments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return EmptyObject();

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(text);
        }
    }

    public static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The answer with its calls, settled the same way for every provider. Calls that ran into the
    /// token limit are all dropped, since some of them are cut off, and the stop reason stays the
    /// provider's own (<c>length</c> or <c>max_tokens</c>) to say why. Otherwise a turn with calls
    /// stops for <see cref="AiChatResponse.ToolCallsStopReason"/>, whatever the provider called it:
    /// Anthropic's <c>tool_use</c>, and the <c>stop</c> some OpenAI-compatible servers send. A
    /// request that offered no tools gets its answer exactly as before tools existed, whatever the
    /// provider sent: there is nothing it could call.
    /// </summary>
    public static AiChatResponse Settle(AiChatResponse answer, IReadOnlyList<AiToolCall> calls, AiChatRequest request)
    {
        if (request.Tools is not { Count: > 0 }) return answer;
        if (calls.Count == 0 || answer.StopReason is "length" or "max_tokens") return answer;
        return answer with { StopReason = AiChatResponse.ToolCallsStopReason, ToolCalls = calls };
    }
}
