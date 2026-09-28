namespace StaticSiteHost.Functions;

/// <summary>One piece of a streamed answer from <see cref="IAiChat.StreamAsync"/>.</summary>
/// <param name="Text">The text this piece adds to the answer, or null on the last chunk.</param>
/// <param name="Final">The whole response, set on the last chunk only.</param>
public sealed record AiChatChunk(string? Text, AiChatResponse? Final);
