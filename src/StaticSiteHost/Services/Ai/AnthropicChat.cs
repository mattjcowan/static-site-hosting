using System.Text;
using System.Text.Json;
using StaticSiteHost.Functions;

namespace StaticSiteHost.Services.Ai;

/// <summary>
/// Anthropic's Messages API, <c>POST {BaseUrl}/v1/messages</c> with the key in <c>x-api-key</c>
/// and <c>anthropic-version: 2023-06-01</c>. A base URL that already ends in <c>/v1</c> is taken
/// as given.
///
/// The API differs from OpenAI's in three ways that matter here. The system prompt is a top-level
/// field rather than a message, so the request's system prompt and any <c>system</c> messages are
/// joined into it and only user and assistant turns go in <c>messages</c>. A token limit is
/// required, so a request without one gets <see cref="DefaultMaxTokens"/>. And the answer is a list
/// of content blocks, of which only the <c>text</c> ones are read: thinking and anything else a
/// model adds is left out.
/// </summary>
internal sealed class AnthropicChat : IAiWireFormat
{
    public const string ApiVersion = "2023-06-01";

    /// <summary>The token limit for a request that sets none, since the API requires one.</summary>
    public const int DefaultMaxTokens = 1024;

    public static readonly AnthropicChat Instance = new();

    private AnthropicChat()
    {
    }

    public HttpRequestMessage CreateRequest(AiCall call, bool stream)
    {
        var request = call.Request;
        var system = new List<string>();
        if (!string.IsNullOrEmpty(request.System)) system.Add(request.System);

        // Turns in order, with the system messages taken out and neighbours from the same speaker
        // joined, since taking a system message out of the middle can leave two user turns side by side.
        var turns = new List<(string Role, StringBuilder Content)>();
        foreach (var message in request.Messages)
        {
            if (message.Role == AiMessage.SystemRole)
            {
                system.Add(message.Content);
            }
            else if (turns.Count > 0 && turns[^1].Role == message.Role)
            {
                turns[^1].Content.Append("\n\n").Append(message.Content);
            }
            else
            {
                turns.Add((message.Role, new StringBuilder(message.Content)));
            }
        }

        var body = new Dictionary<string, object>
        {
            ["model"] = call.Model,
            ["max_tokens"] = request.MaxTokens ?? DefaultMaxTokens,
            ["messages"] = turns.Select(turn => new { role = turn.Role, content = turn.Content.ToString() }).ToList()
        };

        var joined = string.Join("\n\n", system.Where(part => part.Length > 0));
        if (joined.Length > 0) body["system"] = joined;
        if (request.Temperature is { } temperature) body["temperature"] = temperature;
        if (stream) body["stream"] = true;

        var baseUrl = call.Provider.BaseUrl;
        var endpoint = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl + "/messages" : baseUrl + "/v1/messages";

        var http = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = AiJson.Content(body) };
        http.Headers.Accept.ParseAdd(stream ? "text/event-stream" : "application/json");
        http.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        if (!string.IsNullOrEmpty(call.ApiKey)) http.Headers.TryAddWithoutValidation("x-api-key", call.ApiKey);

        return http;
    }

    public AiChatResponse ReadResponse(JsonElement body, AiCall call)
    {
        if (body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            throw new AiChatException("The AI provider's answer had no content in it. Check that the provider's kind is right.");
        }

        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.StringOf("type") == "text") text.Append(block.StringOf("text"));
        }

        var usage = body.ObjectOf("usage");
        return new AiChatResponse(
            text.ToString(),
            body.StringOf("model") ?? call.Model,
            usage is { } u ? InputTokens(u) ?? 0 : 0,
            usage?.IntOf("output_tokens") ?? 0,
            body.StringOf("stop_reason"));
    }

    public IAiStream StartStream(AiCall call) => new StreamedAnswer(call.Model);

    /// <summary><c>{"type":"error","error":{"type":"…","message":"…"}}</c>.</summary>
    public string? ReadError(JsonElement body) =>
        body.ObjectOf("error")?.StringOf("message") ?? body.StringOf("error") ?? body.StringOf("message");

    /// <summary>
    /// Every input token the request was billed for. Tokens written to or read from the prompt
    /// cache are counted apart from <c>input_tokens</c>, so they are added back in. Null when the
    /// usage has no input count at all.
    /// </summary>
    private static int? InputTokens(JsonElement usage) =>
        usage.IntOf("input_tokens") is { } input
            ? input + (usage.IntOf("cache_creation_input_tokens") ?? 0) + (usage.IntOf("cache_read_input_tokens") ?? 0)
            : null;

    /// <summary>
    /// A streamed message: <c>message_start</c> carries the model and the input tokens, each
    /// <c>content_block_delta</c> of type <c>text_delta</c> a piece of the text, <c>message_delta</c>
    /// the stop reason and the output tokens so far, and <c>message_stop</c> ends it. <c>ping</c>,
    /// the block start and stop events and deltas of any other type carry nothing to read. An
    /// <c>error</c> event can arrive at any point, such as when the API is overloaded.
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
            using var document = AiJson.Parse(data);
            var item = document.RootElement;

            switch (item.StringOf("type") ?? eventType)
            {
                case "message_start":
                    var message = item.ObjectOf("message");
                    _model = message?.StringOf("model") ?? _model;
                    if (message?.ObjectOf("usage") is { } startUsage) Count(startUsage);
                    return null;

                case "content_block_delta":
                    var delta = item.ObjectOf("delta");
                    if (delta?.StringOf("type") != "text_delta") return null;

                    var text = delta.Value.StringOf("text");
                    if (!string.IsNullOrEmpty(text)) _text.Append(text);
                    return text;

                case "message_delta":
                    _stopReason = item.ObjectOf("delta")?.StringOf("stop_reason") ?? _stopReason;
                    if (item.ObjectOf("usage") is { } deltaUsage) Count(deltaUsage);
                    return null;

                case "message_stop":
                    IsDone = true;
                    return null;

                case "error":
                    throw new AiChatException(
                        $"The AI provider failed part way through its answer: {Instance.ReadError(item) ?? "it gave no reason"}");

                default:
                    return null;
            }
        }

        /// <summary>The counts in a usage object are running totals, so the latest one wins.</summary>
        private void Count(JsonElement usage)
        {
            _inputTokens = InputTokens(usage) ?? _inputTokens;
            _outputTokens = usage.IntOf("output_tokens") ?? _outputTokens;
        }

        public AiChatResponse Result() => new(_text.ToString(), _model, _inputTokens, _outputTokens, _stopReason);
    }
}
