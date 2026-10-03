namespace StaticSiteHost.Functions;

/// <summary>A complete answer from the site's AI provider.</summary>
/// <param name="Text">What the model wrote. Empty when it wrote nothing, such as when it ran out of tokens first.</param>
/// <param name="Model">The model that answered, as the provider names it.</param>
/// <param name="InputTokens">Tokens the request was billed for, or 0 when the provider did not say.</param>
/// <param name="OutputTokens">Tokens the answer was billed for, or 0 when the provider did not say.</param>
/// <param name="StopReason">
/// Why the model stopped, in the provider's own words: <c>stop</c> or <c>length</c> from
/// OpenAI-compatible providers, <c>end_turn</c> or <c>max_tokens</c> from Anthropic. Null when the
/// provider did not say. The one exception is a turn that ends in tool calls, which is
/// <c>tool_calls</c> from every provider (Anthropic says <c>tool_use</c>), so a loop can test for it
/// in one way.
/// </param>
public sealed record AiChatResponse(string Text, string Model, int InputTokens, int OutputTokens, string? StopReason)
{
    /// <summary>The <see cref="StopReason"/> of a turn that ended in tool calls, whatever the provider.</summary>
    public const string ToolCallsStopReason = "tool_calls";

    /// <summary>
    /// The tools the model asked to call, in order; empty when it answered in text only. Run them,
    /// then send the conversation again with <see cref="AiMessage.AssistantToolCalls"/> and a
    /// <see cref="AiMessage.ToolResult"/> for each.
    /// </summary>
    /// <remarks>
    /// When the model ran out of tokens part way through its calls (<see cref="StopReason"/> is
    /// <c>length</c> or <c>max_tokens</c>), this is empty, even of the calls it finished: half a
    /// turn of calls cannot be answered faithfully. Raise <see cref="AiChatRequest.MaxTokens"/> and ask again.
    /// </remarks>
    public IReadOnlyList<AiToolCall> ToolCalls { get; init; } = [];
}
