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
