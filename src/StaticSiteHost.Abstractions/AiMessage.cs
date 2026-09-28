namespace StaticSiteHost.Functions;

/// <summary>One turn of a conversation: who said it, and what they said.</summary>
/// <param name="Role"><see cref="UserRole"/>, <see cref="AssistantRole"/> or <see cref="SystemRole"/>.</param>
/// <param name="Content">The text of the turn.</param>
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

    /// <summary>A message from the person asking.</summary>
    public static AiMessage User(string content) => new(UserRole, content);

    /// <summary>An earlier answer from the model, to carry a conversation on.</summary>
    public static AiMessage Assistant(string content) => new(AssistantRole, content);

    /// <summary>An instruction to the model.</summary>
    public static AiMessage System(string content) => new(SystemRole, content);
}
