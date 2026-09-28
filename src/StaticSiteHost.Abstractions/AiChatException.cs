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
}
