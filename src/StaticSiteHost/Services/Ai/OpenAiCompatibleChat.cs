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
///
/// Tool calling uses the API's function tools, which LiteLLM passes on to whatever it fronts and
/// Ollama serves for the models that support them: <c>tools</c> and <c>tool_choice</c> in the
/// request, <c>tool_calls</c> on assistant messages, one <c>tool</c> message per result, and the
/// calls read back from <c>message.tool_calls</c>, or assembled from <c>delta.tool_calls</c>
/// fragments when streamed. A request without tools is sent exactly as before, without the fields.
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
        foreach (var message in request.Messages) messages.Add(Message(message));

        var body = new Dictionary<string, object>
        {
            ["model"] = call.Model,
            ["messages"] = messages
        };

        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = tools.Select(tool => new
            {
                type = "function",
                function = new { name = tool.Name, description = tool.Description, parameters = tool.InputSchema }
            }).ToList();
        }

        if (request.ToolChoice is { } choice)
        {
            body["tool_choice"] = choice.Mode == "tool"
                ? new { type = "function", function = new { name = choice.Name } }
                : choice.Mode;
        }

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
    /// One message on the wire. A turn without tool calls is the plain role and content it always
    /// was. A turn that called tools carries them, with the arguments as the JSON string the API
    /// expects and no content when the model wrote none. A result names its call, and since the API
    /// has no error mark, a failure says so in its content.
    /// </summary>
    private static object Message(AiMessage message)
    {
        if (message.Role == AiMessage.ToolRole)
        {
            return new
            {
                role = AiMessage.ToolRole,
                tool_call_id = message.ToolCallId,
                content = message.IsError ? "Error: " + message.Content : message.Content
            };
        }

        if (message.ToolCalls is { Count: > 0 } calls)
        {
            return new
            {
                role = message.Role,
                content = message.Content.Length > 0 ? message.Content : null,
                tool_calls = calls.Select(call => new
                {
                    id = call.Id,
                    type = "function",
                    function = new { name = call.Name, arguments = ArgumentsText(call.Arguments) }
                }).ToList()
            };
        }

        return new { role = message.Role, content = message.Content };
    }

    /// <summary>
    /// A call's input as the string the API carries it in: the raw text again for input that was
    /// not JSON, and an empty object for none.
    /// </summary>
    private static string ArgumentsText(JsonElement arguments) => arguments.ValueKind switch
    {
        JsonValueKind.Undefined => "{}",
        JsonValueKind.String => arguments.GetString() ?? "",
        _ => arguments.GetRawText()
    };

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

        var message = choice.ObjectOf("message");
        var usage = body.ObjectOf("usage");
        var answer = new AiChatResponse(
            message?.StringOf("content") ?? "",
            body.StringOf("model") ?? call.Model,
            usage?.IntOf("prompt_tokens") ?? 0,
            usage?.IntOf("completion_tokens") ?? 0,
            choice.StringOf("finish_reason"));

        var calls = new List<AiToolCall>();
        if (message is { } m && m.TryGetProperty("tool_calls", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var function = item.ObjectOf("function");
                if (function?.StringOf("name") is not { Length: > 0 } name) continue;

                calls.Add(new AiToolCall(
                    item.StringOf("id") ?? MadeUpId(calls.Count),
                    name,
                    function.Value.TryGetProperty("arguments", out var arguments) ? Arguments(arguments) : AiToolWire.EmptyObject()));
            }
        }

        return AiToolWire.Settle(answer, calls, call.Request);
    }

    /// <summary>
    /// A call's input as the response carries it: a JSON string, as the API has it, or an object,
    /// as some servers that copy it send.
    /// </summary>
    private static JsonElement Arguments(JsonElement arguments) => arguments.ValueKind switch
    {
        JsonValueKind.String => AiToolWire.ParseArguments(arguments.GetString()),
        JsonValueKind.Object => arguments.Clone(),
        _ => AiToolWire.EmptyObject()
    };

    /// <summary>
    /// An id for a call that came without one, which a few servers that copy the API do. It only
    /// has to pair the call with its result in the next request.
    /// </summary>
    private static string MadeUpId(int index) => $"call_{index}_{Guid.NewGuid():N}";

    public IAiStream StartStream(AiCall call) => new StreamedAnswer(call.Model, call.Request);

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
    /// after it carries <c>usage</c>, and <c>data: [DONE]</c> ends the stream. Tool calls come as
    /// <c>delta.tool_calls</c> fragments: the first for a call carries its <c>index</c>, <c>id</c> and
    /// name, and the rest pieces of its arguments, joined here by index. A server that sends a whole
    /// call at once, or leaves out the index, is read the same way.
    /// </summary>
    private sealed class StreamedAnswer(string model, AiChatRequest request) : IAiStream
    {
        private readonly StringBuilder _text = new();
        private readonly List<CallInProgress> _calls = [];
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

            var delta = choice.ObjectOf("delta");
            if (delta is { } d && d.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            {
                var position = 0;
                foreach (var fragment in calls.EnumerateArray()) Add(fragment, position++);
            }

            var text = delta?.StringOf("content");
            if (!string.IsNullOrEmpty(text)) _text.Append(text);
            return text;
        }

        /// <summary>Adds a fragment to the call it belongs to: by index, else by id, else by its place in the chunk.</summary>
        private void Add(JsonElement fragment, int position)
        {
            var index = fragment.IntOf("index");
            var id = fragment.StringOf("id");

            var call = index is { } i ? _calls.Find(c => c.Index == i)
                : id is not null ? _calls.Find(c => c.Id == id)
                : _calls.Find(c => c.Index == position);

            if (call is null)
            {
                call = new CallInProgress(index ?? (id is null ? position : _calls.Count));
                _calls.Add(call);
            }

            if (!string.IsNullOrEmpty(id)) call.Id = id;

            if (fragment.ObjectOf("function") is not { } function) return;
            if (function.StringOf("name") is { Length: > 0 } name) call.Name = name;

            if (function.TryGetProperty("arguments", out var arguments))
            {
                if (arguments.ValueKind == JsonValueKind.String) call.Arguments.Append(arguments.GetString());
                else if (arguments.ValueKind == JsonValueKind.Object) call.Arguments.Append(arguments.GetRawText());
            }
        }

        public AiChatResponse Result()
        {
            var calls = _calls
                .Where(call => call.Name is not null)
                .OrderBy(call => call.Index)
                .Select((call, i) => new AiToolCall(call.Id ?? MadeUpId(i), call.Name!, AiToolWire.ParseArguments(call.Arguments.ToString())))
                .ToList();

            return AiToolWire.Settle(new AiChatResponse(_text.ToString(), _model, _inputTokens, _outputTokens, _stopReason), calls, request);
        }

        private sealed class CallInProgress(int index)
        {
            public int Index { get; } = index;
            public string? Id { get; set; }
            public string? Name { get; set; }
            public StringBuilder Arguments { get; } = new();
        }
    }
}
