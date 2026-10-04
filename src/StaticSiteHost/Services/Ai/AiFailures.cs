using System.Text.RegularExpressions;
using StaticSiteHost.Functions;

namespace StaticSiteHost.Services.Ai;

/// <summary>
/// Reads why a provider refused a request, for <see cref="AiChatException.Reason"/>. The statuses
/// say most of it. What they cannot say, that the model does not take tools or that the
/// conversation is too long for it, is read from the provider's explanation, and only from wording
/// that says so plainly: a tool schema the provider found malformed is the caller's mistake, and
/// must not read as a model to replace, so a bare mention of tools is not enough.
/// </summary>
internal static partial class AiFailures
{
    /// <summary>The reason for an error status, or null when there is none to tell.</summary>
    /// <param name="status">The provider's HTTP status.</param>
    /// <param name="detail">The provider's explanation, when it gave one.</param>
    /// <param name="sentTools">Whether the request offered tools; only then can tools be the reason.</param>
    public static string? Classify(int status, string? detail, bool sentTools)
    {
        if (sentTools && detail is not null && ToolsUnsupportedText().IsMatch(detail)) return AiChatException.ToolsUnsupported;
        if (status == 413 || (detail is not null && status is >= 400 and < 500 && ContextTooLongText().IsMatch(detail)))
            return AiChatException.ContextTooLong;

        return status switch
        {
            429 => AiChatException.RateLimited,
            401 or 403 => AiChatException.Auth,
            408 or >= 500 => AiChatException.Unavailable,
            _ => null
        };
    }

    /// <summary>
    /// The reason for an error event in a streamed answer, which has no status: Anthropic's error
    /// type when it sent one, else what its message says.
    /// </summary>
    public static string? ClassifyStreamed(string? errorType, string? detail, bool sentTools) => errorType switch
    {
        "overloaded_error" or "api_error" => AiChatException.Unavailable,
        "rate_limit_error" => AiChatException.RateLimited,
        "authentication_error" or "permission_error" => AiChatException.Auth,
        _ => sentTools && detail is not null && ToolsUnsupportedText().IsMatch(detail) ? AiChatException.ToolsUnsupported
            : detail is not null && ContextTooLongText().IsMatch(detail) ? AiChatException.ContextTooLong
            : null
    };

    /// <summary>
    /// Ollama's "does not support tools", LiteLLM's "does not support parameters: ['tools']", "tools
    /// are not supported", "function calling is not supported", and vLLM's "tool choice requires"
    /// (a server started without tool parsing).
    /// </summary>
    [GeneratedRegex(
        @"does not support (?:the )?(?:tools?|tool[ _-]?(?:use|calling|calls?)|function[ _-]?call(?:ing|s)?|functions)\b" +
        @"|does not support param(?:eter)?s?\b[^.]*\btools?\b" +
        @"|\b(?:tools?|tool[ _-]?(?:use|calling|calls?)|function[ _-]?call(?:ing|s)?|functions)\b (?:is|are) not supported" +
        @"|\btool[ _]choice requires\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ToolsUnsupportedText();

    /// <summary>
    /// OpenAI's "maximum context length" and <c>context_length_exceeded</c>, Anthropic's "prompt is
    /// too long", and the "context window" and "too many tokens" the servers that copy them say.
    /// </summary>
    [GeneratedRegex(
        @"context[ _]length|maximum context|context window|prompt is too long|input is too long|too many (?:input )?tokens",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContextTooLongText();
}
