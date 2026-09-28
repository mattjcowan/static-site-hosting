namespace StaticSiteHost.Functions;

/// <summary>A complete answer from the site's AI provider.</summary>
/// <param name="Text">What the model wrote. Empty when it wrote nothing, such as when it ran out of tokens first.</param>
/// <param name="Model">The model that answered, as the provider names it.</param>
/// <param name="InputTokens">Tokens the request was billed for, or 0 when the provider did not say.</param>
/// <param name="OutputTokens">Tokens the answer was billed for, or 0 when the provider did not say.</param>
/// <param name="StopReason">
/// Why the model stopped, in the provider's own words: <c>stop</c> or <c>length</c> from
/// OpenAI-compatible providers, <c>end_turn</c> or <c>max_tokens</c> from Anthropic. Null when the
/// provider did not say.
/// </param>
public sealed record AiChatResponse(string Text, string Model, int InputTokens, int OutputTokens, string? StopReason);
