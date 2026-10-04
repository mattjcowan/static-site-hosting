namespace StaticSiteHost.Functions;

/// <summary>
/// A chat that could not be had: the site has no AI provider, the provider could not be reached or
/// took too long, or it refused the request. The message says which, in words fit to log; it never
/// holds the provider's key or address.
/// </summary>
public sealed class AiChatException : Exception
{
    /// <summary>Creates an exception with no provider status.</summary>
    public AiChatException(string message) : base(message)
    {
    }

    /// <summary>Creates an exception carrying the status the provider answered with.</summary>
    public AiChatException(string message, int? statusCode, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// The HTTP status the provider answered with, such as 401 for a rejected key or 429 when it is
    /// rate limiting. Null when there was no answer: the site has no provider, the connection
    /// failed, the provider timed out, or it failed part way through a streamed answer.
    /// </summary>
    public int? StatusCode { get; }

    /// <summary>
    /// Why the call failed, when the server can tell: <see cref="ToolsUnsupported"/>,
    /// <see cref="ContextTooLong"/>, <see cref="RateLimited"/>, <see cref="Auth"/> or
    /// <see cref="Unavailable"/>. Null otherwise, which includes a site with no provider (check
    /// <see cref="IAiChat.IsConfigured"/>) and a provider refusing a request for a reason of its own.
    /// </summary>
    /// <remarks>
    /// Providers word their errors differently, so the server reads them for you. The statuses are
    /// sure (429, 401 and 403, 5xx); <see cref="ToolsUnsupported"/> and <see cref="ContextTooLong"/>
    /// are read from the provider's explanation and set only when it says so plainly, so a request
    /// the provider refused for another reason, such as a malformed tool schema, is not taken for one.
    /// </remarks>
    public string? Reason { get; init; }

    /// <summary>
    /// The model cannot use tools: the provider refused a request that offered them, saying so. Set
    /// only for a request with tools. Choosing another model is the fix; a request without tools still works.
    /// </summary>
    public const string ToolsUnsupported = "tools-unsupported";

    /// <summary>The conversation is longer than the model's context window. Send less of it.</summary>
    public const string ContextTooLong = "context-too-long";

    /// <summary>The provider is rate limiting the key (429). Wait, and try again.</summary>
    public const string RateLimited = "rate-limited";

    /// <summary>The provider refused the key (401 or 403). An administrator has to fix the provider's key.</summary>
    public const string Auth = "auth";

    /// <summary>
    /// The provider could not answer: unreachable, timed out, overloaded, failing (5xx), or broken
    /// off part way through an answer. Usually passes; try again later.
    /// </summary>
    public const string Unavailable = "unavailable";
}
