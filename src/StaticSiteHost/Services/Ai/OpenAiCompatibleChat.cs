using System.Text;
using System.Text.Json;
using StaticSiteHost.Functions;

namespace StaticSiteHost.Services.Ai;

/// <summary>
/// OpenAI's chat completions API, <c>POST {BaseUrl}/chat/completions</c>, which LiteLLM, Ollama,
/// OpenRouter and most self-hosted model servers copy. The key, when there is one, goes as a
/// bearer token; a local Ollama needs none.
///
/// Only the core of the API is used, since that is what the copies agree on: messages with a role
/// and a string, the model, a token limit and a temperature. The system prompt travels as the first
/// <c>system</c> message. A streamed answer asks for a closing usage chunk
/// (<c>stream_options.include_usage</c>) and gets by without one, since not every server sends it,
/// reporting zero tokens instead.
/// </summary>
internal sealed class OpenAiCompatibleChat : IAiWireFormat
{
    public static readonly OpenAiCompatibleChat Instance = new();

    private OpenAiCompatibleChat()
    {
    }

    public HttpRequestMessage CreateRequest(AiCall call, bool stream)
    {
        var request = call.Request;
        var messages = new List<object>(request.Messages.Count + 1);

        if (!string.IsNullOrEmpty(request.System)) messages.Add(new { role = AiMessage.SystemRole, content = request.System });
        foreach (var message in request.Messages) messages.Add(new { role = message.Role, content = message.Content });

        var body = new Dictionary<string, object>
        {
            ["model"] = call.Model,
            ["messages"] = messages
        };

        if (request.MaxTokens is { } maxTokens) body[TokenLimitField(call)] = maxTokens;
        if (request.Temperature is { } temperature) body["temperature"] = temperature;

        if (stream)
        {
            body["stream"] = true;
            body["stream_options"] = new { include_usage = true };
        }

        var http = new HttpRequestMessage(HttpMethod.Post, call.Provider.BaseUrl + "/chat/completions")
        {
            Content = AiJson.Content(body)
        };

        http.Headers.Accept.ParseAdd(stream ? "text/event-stream" : "application/json");

        // Without validation: the store has already refused a key with a line break in it, and the
        // header parser would otherwise reject some providers' key formats outright.
        if (!string.IsNullOrEmpty(call.ApiKey)) http.Headers.TryAddWithoutValidation("Authorization", "Bearer " + call.ApiKey);

        return http;
    }

    /// <summary>
    /// OpenAI itself has replaced <c>max_tokens</c> with <c>max_completion_tokens</c> and its
    /// reasoning models refuse the old name, while the servers that copy the API still read
    /// <c>max_tokens</c> (LiteLLM and OpenRouter translate it for OpenAI's models). So the new name
    /// goes to OpenAI and the old one everywhere else.
    /// </summary>
    private static string TokenLimitField(AiCall call) =>
        Uri.TryCreate(call.Provider.BaseUrl, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)
            ? "max_completion_tokens"
            : "max_tokens";

    public AiChatResponse ReadResponse(JsonElement body, AiCall call)
    {
        if (body.FirstOf("choices") is not { } choice)
            throw new AiChatException("The AI provider's answer had no choices in it. Check that the provider's kind is right.");

        var usage = body.ObjectOf("usage");
        return new AiChatResponse(
            choice.ObjectOf("message")?.StringOf("content") ?? "",
            body.StringOf("model") ?? call.Model,
            usage?.IntOf("prompt_tokens") ?? 0,
            usage?.IntOf("completion_tokens") ?? 0,
            choice.StringOf("finish_reason"));
    }

    public IAiStream StartStream(AiCall call) => new StreamedAnswer(call.Model);

    /// <summary>
    /// OpenAI's <c>{"error":{"message":…}}</c>, Ollama's <c>{"error":"…"}</c>, or the
    /// <c>message</c> or <c>detail</c> string some proxies answer with.
    /// </summary>
    public string? ReadError(JsonElement body) =>
        body.ObjectOf("error")?.StringOf("message")
        ?? body.StringOf("error")
        ?? body.StringOf("message")
        ?? body.StringOf("detail");

    /// <summary>
    /// A streamed completion: each <c>data:</c> line is a chunk whose first choice carries a piece
    /// of the text in <c>delta.content</c>, the last carries <c>finish_reason</c>, an optional chunk
    /// after it carries <c>usage</c>, and <c>data: [DONE]</c> ends the stream.
    /// </summary>
    private sealed class StreamedAnswer(string model) : IAiStream
    {
        private readonly StringBuilder _text = new();
        private string _model = model;
        private string? _stopReason;
        private int _inputTokens;
        private int _outputTokens;

        public bool IsDone { get; private set; }

        public bool IsComplete => IsDone || _stopReason is not null;

        public string? Take(string eventType, string data)
        {
            if (data.Trim() == "[DONE]")
            {
                IsDone = true;
                return null;
            }

            using var document = AiJson.Parse(data);
            var chunk = document.RootElement;

            if (chunk.ValueKind == JsonValueKind.Object &&
                chunk.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                throw new AiChatException(
                    $"The AI provider failed part way through its answer: {Instance.ReadError(chunk) ?? "it gave no reason"}");
            }

            _model = chunk.StringOf("model") ?? _model;

            if (chunk.ObjectOf("usage") is { } usage)
            {
                _inputTokens = usage.IntOf("prompt_tokens") ?? _inputTokens;
                _outputTokens = usage.IntOf("completion_tokens") ?? _outputTokens;
            }

            if (chunk.FirstOf("choices") is not { } choice) return null;

            _stopReason = choice.StringOf("finish_reason") ?? _stopReason;

            var text = choice.ObjectOf("delta")?.StringOf("content");
            if (!string.IsNullOrEmpty(text)) _text.Append(text);
            return text;
        }

        public AiChatResponse Result() => new(_text.ToString(), _model, _inputTokens, _outputTokens, _stopReason);
    }
}
