namespace StaticSiteHost.Functions;

/// <summary>One turn of a conversation: who said it, and what they said.</summary>
/// <param name="Role"><see cref="UserRole"/>, <see cref="AssistantRole"/>, <see cref="SystemRole"/> or <see cref="ToolRole"/>.</param>
/// <param name="Content">
/// The text of the turn. It may be empty on an assistant turn that only calls tools, and on a tool
/// result.
/// </param>
public sealed record AiMessage(string Role, string Content)
{
    /// <summary>A turn written by the person asking.</summary>
    public const string UserRole = "user";

    /// <summary>A turn the model wrote earlier in the conversation.</summary>
    public const string AssistantRole = "assistant";

    /// <summary>
    /// An instruction to the model rather than a turn of the conversation. Providers that take the
    /// system prompt apart from the messages, as Anthropic's does, receive it joined to the
    /// request's <see cref="AiChatRequest.System"/>; the others receive it where it stands.
    /// </summary>
    public const string SystemRole = "system";

    /// <summary>
    /// The result of a tool the model called, made with <see cref="ToolResult"/>. It must answer a
    /// call in an earlier assistant turn (<see cref="ToolCalls"/>), and every call in that turn
    /// needs its result before the conversation goes on.
    /// </summary>
    public const string ToolRole = "tool";

    /// <summary>
    /// On an assistant turn: the tools the model called in it, as <see cref="AiChatResponse.ToolCalls"/>
    /// gave them. Null or empty on every other turn.
    /// </summary>
    public IReadOnlyList<AiToolCall>? ToolCalls { get; init; }

    /// <summary>On a tool result: the <see cref="AiToolCall.Id"/> of the call it answers.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>
    /// On a tool result: true when the tool failed, and <see cref="Content"/> says how. Anthropic
    /// marks the result as an error; OpenAI-compatible providers have no such mark, so the content
    /// goes to them as <c>Error: …</c>.
    /// </summary>
    public bool IsError { get; init; }

    /// <summary>A message from the person asking.</summary>
    public static AiMessage User(string content) => new(UserRole, content);

    /// <summary>An earlier answer from the model, to carry a conversation on.</summary>
    public static AiMessage Assistant(string content) => new(AssistantRole, content);

    /// <summary>An instruction to the model.</summary>
    public static AiMessage System(string content) => new(SystemRole, content);

    /// <summary>
    /// The model's turn that called tools, to append to the conversation before the results:
    /// pass the response's <see cref="AiChatResponse.Text"/> and <see cref="AiChatResponse.ToolCalls"/>.
    /// </summary>
    public static AiMessage AssistantToolCalls(string? text, IReadOnlyList<AiToolCall> calls) =>
        new(AssistantRole, text ?? "") { ToolCalls = calls };

    /// <summary>What a tool returned, or why it failed when <paramref name="isError"/> is true.</summary>
    /// <param name="toolCallId">The <see cref="AiToolCall.Id"/> of the call this answers.</param>
    /// <param name="content">The result as text; JSON is fine.</param>
    /// <param name="isError">True when the tool failed and <paramref name="content"/> says how.</param>
    public static AiMessage ToolResult(string toolCallId, string content, bool isError = false) =>
        new(ToolRole, content) { ToolCallId = toolCallId, IsError = isError };
}
