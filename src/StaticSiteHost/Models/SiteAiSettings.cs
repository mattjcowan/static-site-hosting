using System.Text.Json.Serialization;

namespace StaticSiteHost.Models;

/// <summary>
/// A site's AI settings, stored in its site.json as <see cref="SiteRecord.Ai"/>. They belong to the
/// site rather than to a release, so deploys and rollbacks leave them alone. Only administrators
/// change them: they decide whose key the site spends, and whether strangers may spend it.
/// </summary>
public sealed class SiteAiSettings
{
    /// <summary>Longest pinned system prompt, in UTF-8 bytes.</summary>
    public const int MaxSystemPromptBytes = 8 * 1024;

    /// <summary>Longest model name.</summary>
    public const int MaxModelLength = 128;

    /// <summary>
    /// Id of the <see cref="AiProviderRecord"/> to use. Null, or the id of a provider that has since
    /// been deleted, means the site has no AI.
    /// </summary>
    public string? ProviderId { get; set; }

    /// <summary>The model to use, in the provider's naming. Null means the provider's default.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Instructions sent ahead of every conversation, from functions and browsers alike. A caller's
    /// own system prompt is appended after it, so a browser can add to it but never replace it.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Whether any visitor's browser may call <c>/_host/ai/chat</c>, for a site whose functions
    /// declare no <c>[AiAccess]</c> hook. Off, such a site refuses browsers and only its functions can
    /// chat. A site whose functions declare the hook lets it decide for each browser instead, and
    /// this is not consulted until a deploy takes the hook away.
    /// </summary>
    public bool AllowVisitors { get; set; }

    public DateTimeOffset? UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>True when a provider was chosen. Whether it still exists is for the provider store to say.</summary>
    [JsonIgnore]
    public bool HasProvider => !string.IsNullOrEmpty(ProviderId);
}
