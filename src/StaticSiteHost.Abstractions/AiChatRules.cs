using System.Text.Json;

namespace StaticSiteHost.Functions;

/// <summary>
/// What makes a chat request one that can be sent: the server checks every request against these
/// rules before it calls a provider, and <see cref="Testing.FakeAiChat"/> checks against the same
/// ones, so a handler's tests fail where the server would. Internal: the server reaches it as a
/// friend assembly, and nothing about it is part of the package's surface.
/// </summary>
internal static class AiChatRules
{
    /// <summary>The most tools one request may offer, which is OpenAI's limit and well within Anthropic's.</summary>
    public const int MaxTools = 128;

    /// <summary>
    /// Checks that a request can be sent at all: at least one message, every message with a known
    /// role and some content, at least one of them from the user or the assistant, and sensible
    /// numbers. With tools, also that every tool is well formed and every call is answered; see
    /// <see cref="ValidateTools"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The request is not a chat that can be sent; the message says why.</exception>
    public static void Validate(AiChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Messages is not { Count: > 0 } messages)
            throw new ArgumentException("A chat needs at least one message.", nameof(request));

        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not { } message)
                throw new ArgumentException($"Message {i + 1} is null.", nameof(request));

            if (message.Role is not (AiMessage.UserRole or AiMessage.AssistantRole or AiMessage.SystemRole or AiMessage.ToolRole))
            {
                throw new ArgumentException(
                    $"Message {i + 1} has the role \"{message.Role}\". Use \"user\", \"assistant\", \"system\" or \"tool\", or " +
                    "AiMessage.User, AiMessage.Assistant, AiMessage.System and AiMessage.ToolResult.", nameof(request));
            }

            if (message.Content is null)
                throw new ArgumentException($"Message {i + 1} has no content.", nameof(request));
        }

        if (messages.All(message => message.Role == AiMessage.SystemRole))
            throw new ArgumentException("A chat needs a user message: system messages alone give the model nothing to answer.", nameof(request));

        if (request.MaxTokens is < 1)
            throw new ArgumentException("MaxTokens must be at least 1, or null to leave it to the provider.", nameof(request));

        if (request.Temperature is { } temperature && (double.IsNaN(temperature) || temperature is < 0 or > 2))
            throw new ArgumentException("Temperature must be between 0 and 2, or null to leave it to the provider.", nameof(request));

        ValidateTools(request);
    }

    /// <summary>
    /// The rules for tools, the same for every provider so a function that works with one works
    /// with the others. Tools: at most <see cref="MaxTools"/>, each with a name of letters, digits,
    /// <c>_</c> and <c>-</c> up to 64 characters, unique, and an object schema; a tool choice names
    /// one of them. Calls: only on assistant turns, each with an id and a name, the ids unique.
    /// Results: each answers a call of the assistant turn before it, once, and every call is
    /// answered before anything else is said, a system message included, since both APIs refuse a
    /// conversation that breaks off between a call and its result. And a conversation with calls in
    /// it sends its tools, which Anthropic requires.
    /// </summary>
    private static void ValidateTools(AiChatRequest request)
    {
        var tools = request.Tools ?? [];
        if (tools.Count > MaxTools)
            throw new ArgumentException($"A request can offer at most {MaxTools} tools; this one has {tools.Count}.", nameof(request));

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < tools.Count; i++)
        {
            if (tools[i] is not { } tool)
                throw new ArgumentException($"Tool {i + 1} is null.", nameof(request));

            if (!IsToolName(tool.Name))
            {
                throw new ArgumentException(
                    $"Tool {i + 1} is named \"{tool.Name}\". A tool name is 1 to 64 letters, digits, '_' or '-'.", nameof(request));
            }

            if (!names.Add(tool.Name))
                throw new ArgumentException($"Two tools are named \"{tool.Name}\". Each tool needs its own name.", nameof(request));

            if (tool.Description is null)
                throw new ArgumentException($"Tool \"{tool.Name}\" has no description.", nameof(request));

            if (tool.InputSchema.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    $"Tool \"{tool.Name}\" has an input schema that is not a JSON object. Use an object schema, such as " +
                    "{\"type\":\"object\",\"properties\":{}} for a tool that takes nothing.", nameof(request));
            }
        }

        if (request.ToolChoice is { } choice)
        {
            if (tools.Count == 0)
                throw new ArgumentException("ToolChoice is set but there are no Tools to choose from.", nameof(request));

            if (choice.Mode is not ("auto" or "none" or "required" or "tool"))
            {
                throw new ArgumentException(
                    $"ToolChoice has the mode \"{choice.Mode}\". Use AiToolChoice.Auto, None, Required or Tool(name).", nameof(request));
            }

            if (choice.Mode == "tool" && (choice.Name is null || !names.Contains(choice.Name)))
                throw new ArgumentException($"ToolChoice names the tool \"{choice.Name}\", which is not one of the request's Tools.", nameof(request));
        }

        var messages = request.Messages;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var waiting = new List<string>();
        var callingTurn = 0;
        var anyCalls = false;

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];

            if (message.Role == AiMessage.ToolRole)
            {
                if (string.IsNullOrEmpty(message.ToolCallId))
                    throw new ArgumentException($"Message {i + 1} is a tool result with no ToolCallId. Make it with AiMessage.ToolResult.", nameof(request));

                if (!waiting.Remove(message.ToolCallId))
                {
                    throw new ArgumentException(
                        $"Message {i + 1} answers the tool call \"{message.ToolCallId}\", which the assistant turn before it did not make, " +
                        "or which already has its result.", nameof(request));
                }

                continue;
            }

            if (message.ToolCallId is not null)
                throw new ArgumentException($"Message {i + 1} has a ToolCallId but is not a tool result.", nameof(request));

            if (waiting.Count > 0)
            {
                throw new ArgumentException(
                    $"Message {i + 1} comes before every tool call of message {callingTurn} has its result: \"{string.Join("\", \"", waiting)}\" " +
                    "still need one. Answer each call with AiMessage.ToolResult right after the turn that made it.", nameof(request));
            }

            if (message.ToolCalls is not { Count: > 0 } calls) continue;

            if (message.Role != AiMessage.AssistantRole)
                throw new ArgumentException($"Message {i + 1} has tool calls, which only an assistant turn can make.", nameof(request));

            anyCalls = true;
            callingTurn = i + 1;
            foreach (var call in calls)
            {
                if (call is null || string.IsNullOrEmpty(call.Id) || string.IsNullOrEmpty(call.Name))
                    throw new ArgumentException($"Message {i + 1} has a tool call with no id or no name.", nameof(request));

                if (!seen.Add(call.Id))
                    throw new ArgumentException($"The tool call id \"{call.Id}\" is used twice. Each call needs its own id.", nameof(request));

                waiting.Add(call.Id);
            }
        }

        if (waiting.Count > 0)
        {
            throw new ArgumentException(
                $"The tool calls of message {callingTurn} need their results before the chat is sent: \"{string.Join("\", \"", waiting)}\".",
                nameof(request));
        }

        if (anyCalls && tools.Count == 0)
        {
            throw new ArgumentException(
                "The conversation has tool calls in it but the request has no Tools. Keep sending the tools, with " +
                "ToolChoice = AiToolChoice.None to have the model answer in text.", nameof(request));
        }
    }

    /// <summary>1 to 64 ASCII letters, digits, '_' or '-', which both APIs accept.</summary>
    public static bool IsToolName(string? name) =>
        name is { Length: >= 1 and <= 64 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
