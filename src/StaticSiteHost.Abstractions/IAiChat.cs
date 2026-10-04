namespace StaticSiteHost.Functions;

/// <summary>
/// Chat with the AI provider an administrator chose for the site: the same provider, model and
/// pinned system prompt that answer the site's browsers at <c>/_host/ai/chat</c>, with the key
/// kept on the server.
/// </summary>
/// <remarks>
/// <para>
/// Only Static Site Host implements this interface, and <see cref="Testing.FakeAiChat"/> stands
/// in for it outside the server. Later versions may add members, so do not implement it in your
/// own code.
/// </para>
/// <para>
/// Every call is a request to a paid API, billed to whoever owns the provider's key. Nothing here
/// limits how often a function calls it, so a handler that anyone on the internet can reach should
/// decide for itself who may spend that money, and how much.
/// </para>
/// </remarks>
public interface IAiChat
{
    /// <summary>
    /// True when the site has a provider. When it is false, both methods throw
    /// <see cref="AiChatException"/>, so check it first to offer something else instead.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// The model a request uses when it names none: the site's choice, or else the provider's
    /// default. Null when <see cref="IsConfigured"/> is false.
    /// </summary>
    string? Model { get; }

    /// <summary>
    /// The kind of the site's provider: <c>openai</c> for the OpenAI-compatible API (OpenAI itself,
    /// LiteLLM, Ollama and the rest), or <c>anthropic</c>. Null when <see cref="IsConfigured"/> is
    /// false. The provider's name, address and key are the server's own and never shown.
    /// </summary>
    string? ProviderKind { get; }

    /// <summary>Sends the conversation and waits for the whole answer.</summary>
    /// <exception cref="AiChatException">
    /// The site has no provider, the provider could not be reached or did not answer in time,
    /// or it refused the request; <see cref="AiChatException.StatusCode"/> carries its status.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The request has no messages, a message has an unknown role, or its tools or tool calls break
    /// the rules on <see cref="AiChatRequest.Tools"/> and <see cref="AiMessage.ToolRole"/>. Checked
    /// before anything is sent.
    /// </exception>
    Task<AiChatResponse> CompleteAsync(AiChatRequest request, CancellationToken ct = default);

    /// <summary>
    /// Sends the conversation and yields the answer as it is written: a chunk per piece of text,
    /// then a last chunk whose <see cref="AiChatChunk.Final"/> holds the whole response.
    /// </summary>
    /// <remarks>
    /// Nothing is sent until the enumeration starts, and stopping it early (or cancelling
    /// <paramref name="ct"/>) closes the connection to the provider. The exceptions are those of
    /// <see cref="CompleteAsync"/>, thrown from the enumeration, and a failure part way through
    /// ends it with an <see cref="AiChatException"/>.
    /// </remarks>
    IAsyncEnumerable<AiChatChunk> StreamAsync(AiChatRequest request, CancellationToken ct = default);
}
