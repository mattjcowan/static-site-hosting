using System.Text.Json.Serialization;

namespace StaticSiteHost.Models;

/// <summary>
/// An AI service the server can call on a site's behalf, persisted in config/ai-providers.json.
/// Providers are global and only administrators manage them; a site picks one by
/// <see cref="Id"/>. Records are replaced, never edited in place, so a chat that picked one up
/// keeps a consistent copy even while it is being changed.
/// </summary>
public sealed class AiProviderRecord
{
    /// <summary>
    /// Any service that speaks OpenAI's chat completions API: OpenAI itself, and the LiteLLM,
    /// Ollama and OpenRouter endpoints that copy it.
    /// </summary>
    public const string KindOpenAi = "openai";

    /// <summary>Anthropic's Messages API.</summary>
    public const string KindAnthropic = "anthropic";

    public static readonly string[] Kinds = [KindOpenAi, KindAnthropic];

    public static bool IsKnownKind(string? kind) => kind is KindOpenAi or KindAnthropic;

    /// <summary>Short random letters and digits, like an API key's id.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary><see cref="KindOpenAi"/> or <see cref="KindAnthropic"/>. Fixed once the provider is added.</summary>
    public string Kind { get; set; } = KindOpenAi;

    /// <summary>
    /// Where its API lives, without a trailing slash: <c>https://api.openai.com/v1</c>,
    /// <c>http://litellm:4000/v1</c>, <c>https://api.anthropic.com</c>.
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// The API key, protected with Data Protection. Null when the provider needs none, as a local
    /// Ollama does. It is decrypted only to make a call, and never sent back to a browser or the API.
    /// </summary>
    public string? ApiKeyProtected { get; set; }

    /// <summary>The model used when neither the site nor the request names one.</summary>
    public string DefaultModel { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedUtc { get; set; }

    [JsonIgnore]
    public bool HasApiKey => !string.IsNullOrEmpty(ApiKeyProtected);

    /// <summary>A copy to change, so the record other threads may be holding stays as it was.</summary>
    public AiProviderRecord Clone() => (AiProviderRecord)MemberwiseClone();
}
