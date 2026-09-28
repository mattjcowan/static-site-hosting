namespace StaticSiteHost.Functions;

/// <summary>
/// What to send to the site's AI provider. Only <see cref="Messages"/> is needed; everything else
/// falls back to what an administrator set for the site.
/// </summary>
/// <example>
/// <code>
/// var answer = await ai.CompleteAsync(new AiChatRequest
/// {
///     Messages = [AiMessage.User("Name three constellations visible in October.")],
///     MaxTokens = 300
/// });
/// </code>
/// </example>
public sealed class AiChatRequest
{
    /// <summary>
    /// The conversation so far, oldest first, ending with the turn to answer. It needs at least one
    /// user or assistant message.
    /// </summary>
    public required IReadOnlyList<AiMessage> Messages { get; init; }

    /// <summary>
    /// Instructions for the model. The site's own system prompt, when it has one, always comes
    /// first; this follows it as a new paragraph, so it can add to the site's instructions but not
    /// remove them.
    /// </summary>
    public string? System { get; init; }

    /// <summary>The model to use, in the provider's own naming. Null means <see cref="IAiChat.Model"/>.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// The most tokens the answer may use. Null leaves it to the provider, except for Anthropic,
    /// which requires a limit and gets 1024.
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Sampling temperature. Null leaves it to the provider, which is usually what you want: some
    /// models, including the current Claude models and OpenAI's reasoning models, refuse a request
    /// that sets it.
    /// </summary>
    public double? Temperature { get; init; }
}
