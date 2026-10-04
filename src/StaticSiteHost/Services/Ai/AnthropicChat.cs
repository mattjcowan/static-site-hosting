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
/// of content blocks, of which only the <c>text</c> and <c>tool_use</c> ones are read: thinking and
/// anything else a model adds is left out.
///
/// A request with tools in it goes in the API's block form: an assistant turn's calls become
/// <c>tool_use</c> blocks after its text, and the results that answer them <c>tool_result</c> blocks
/// in one user turn, with any user text that follows after them. A request without tools goes as
/// plain strings, exactly as before tools existed.
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

        var body = new Dictionary<string, object>
        {
            ["model"] = call.Model,
            ["max_tokens"] = request.MaxTokens ?? DefaultMaxTokens,
            ["messages"] = AiToolWire.UsesTools(request) ? BlockTurns(request.Messages, system) : TextTurns(request.Messages, system)
        };

        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = tools
                .Select(tool => new { name = tool.Name, description = tool.Description, input_schema = tool.InputSchema })
                .ToList();
        }

        if (request.ToolChoice is { } choice)
        {
            body["tool_choice"] = choice.Mode switch
            {
                "tool" => new Dictionary<string, object?> { ["type"] = "tool", ["name"] = choice.Name },
                "required" => new Dictionary<string, object?> { ["type"] = "any" },
                _ => new Dictionary<string, object?> { ["type"] = choice.Mode }
            };
        }

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

    /// <summary>
    /// Turns as strings, with the system messages taken out into <paramref name="system"/> and
    /// neighbours from the same speaker joined, since taking a system message out of the middle can
    /// leave two user turns side by side.
    /// </summary>
    private static List<object> TextTurns(IReadOnlyList<AiMessage> messages, List<string> system)
    {
        var turns = new List<(string Role, StringBuilder Content)>();
        foreach (var message in messages)
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

        return turns.Select(turn => (object)new { role = turn.Role, content = turn.Content.ToString() }).ToList();
    }

    /// <summary>
    /// Turns as lists of content blocks, for a request with tools. Tool results are user turns to
    /// this API, so they join the user text around them, and neighbours from the same speaker are
    /// joined as in <see cref="TextTurns"/>. Empty text is left out, since the API refuses an empty
    /// text block.
    /// </summary>
    private static List<object> BlockTurns(IReadOnlyList<AiMessage> messages, List<string> system)
    {
        var turns = new List<(string Role, List<object> Blocks)>();
        foreach (var message in messages)
        {
            if (message.Role == AiMessage.SystemRole)
            {
                system.Add(message.Content);
                continue;
            }

            var role = message.Role == AiMessage.ToolRole ? AiMessage.UserRole : message.Role;
            if (turns.Count == 0 || turns[^1].Role != role) turns.Add((role, []));
            var blocks = turns[^1].Blocks;

            if (message.Role == AiMessage.ToolRole)
            {
                var result = new Dictionary<string, object?>
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = message.ToolCallId,
                    ["content"] = message.Content
                };
                if (message.IsError) result["is_error"] = true;
                blocks.Add(result);
                continue;
            }

            if (message.Content.Length > 0) blocks.Add(new { type = "text", text = message.Content });

            foreach (var toolCall in message.ToolCalls ?? [])
            {
                blocks.Add(new
                {
                    type = "tool_use",
                    id = toolCall.Id,
                    name = toolCall.Name,
                    input = toolCall.Arguments.ValueKind == JsonValueKind.Object ? toolCall.Arguments : AiToolWire.EmptyObject()
                });
            }
        }

        return turns.Select(turn => (object)new { role = turn.Role, content = turn.Blocks }).ToList();
    }

    public AiChatResponse ReadResponse(JsonElement body, AiCall call)
    {
        if (body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            throw new AiChatException("The AI provider's answer had no content in it. Check that the provider's kind is right.");
        }

        var text = new StringBuilder();
        var calls = new List<AiToolCall>();
        foreach (var block in content.EnumerateArray())
        {
            switch (block.StringOf("type"))
            {
                case "text":
                    text.Append(block.StringOf("text"));
                    break;

                case "tool_use" when block.StringOf("id") is { Length: > 0 } id && block.StringOf("name") is { Length: > 0 } name:
                    calls.Add(new AiToolCall(id, name, block.ObjectOf("input") ?? AiToolWire.EmptyObject()));
                    break;
            }
        }

        var usage = body.ObjectOf("usage");
        var answer = new AiChatResponse(
            text.ToString(),
            body.StringOf("model") ?? call.Model,
            usage is { } u ? InputTokens(u) ?? 0 : 0,
            usage?.IntOf("output_tokens") ?? 0,
            body.StringOf("stop_reason"));

        return AiToolWire.Settle(answer, calls, call.Request);
    }

    public IAiStream StartStream(AiCall call) => new StreamedAnswer(call.Model, call.Request);

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
    /// the stop reason and the output tokens so far, and <c>message_stop</c> ends it. A tool call is
    /// a <c>content_block_start</c> of type <c>tool_use</c> with its id and name, then
    /// <c>input_json_delta</c> pieces of its input, joined here by block index. <c>ping</c>, the
    /// block stop events and deltas of any other type carry nothing to read. An <c>error</c> event
    /// can arrive at any point, such as when the API is overloaded.
    /// </summary>
    private sealed class StreamedAnswer(string model, AiChatRequest request) : IAiStream
    {
        private readonly StringBuilder _text = new();
        private readonly SortedDictionary<int, (string Id, string Name, StringBuilder Input)> _calls = [];
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

                case "content_block_start":
                    var block = item.ObjectOf("content_block");
                    if (block?.StringOf("type") == "tool_use" && item.IntOf("index") is { } start &&
                        block.Value.StringOf("id") is { Length: > 0 } id && block.Value.StringOf("name") is { Length: > 0 } name)
                    {
                        _calls[start] = (id, name, new StringBuilder());
                    }
                    return null;

                case "content_block_delta":
                    var delta = item.ObjectOf("delta");
                    switch (delta?.StringOf("type"))
                    {
                        case "text_delta":
                            var text = delta.Value.StringOf("text");
                            if (!string.IsNullOrEmpty(text)) _text.Append(text);
                            return text;

                        case "input_json_delta" when item.IntOf("index") is { } index && _calls.TryGetValue(index, out var call):
                            call.Input.Append(delta.Value.StringOf("partial_json"));
                            return null;

                        default:
                            return null;
                    }

                case "message_delta":
                    _stopReason = item.ObjectOf("delta")?.StringOf("stop_reason") ?? _stopReason;
                    if (item.ObjectOf("usage") is { } deltaUsage) Count(deltaUsage);
                    return null;

                case "message_stop":
                    IsDone = true;
                    return null;

                case "error":
                    var reason = Instance.ReadError(item);
                    throw new AiChatException($"The AI provider failed part way through its answer: {reason ?? "it gave no reason"}")
                    {
                        Reason = AiFailures.ClassifyStreamed(item.ObjectOf("error")?.StringOf("type"), reason, request.Tools is { Count: > 0 })
                    };

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

        public AiChatResponse Result()
        {
            var calls = _calls.Values
                .Select(call => new AiToolCall(call.Id, call.Name, AiToolWire.ParseArguments(call.Input.ToString())))
                .ToList();

            return AiToolWire.Settle(new AiChatResponse(_text.ToString(), _model, _inputTokens, _outputTokens, _stopReason), calls, request);
        }
    }
}
