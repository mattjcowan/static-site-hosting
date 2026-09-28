using System.Text;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services.Ai;

/// <summary>
/// Saves a site's AI settings (<see cref="SiteRecord.Ai"/>). The site page and the API both come
/// through here, so they validate alike and leave the same audit trail. Callers check that the
/// person is an administrator: the settings decide whose key the site spends and whether strangers
/// may spend it.
/// </summary>
public sealed class SiteAiSettingsService
{
    private readonly SiteStore _sites;
    private readonly AiProviderStore _providers;
    private readonly AuditLog _audit;

    public SiteAiSettingsService(SiteStore sites, AiProviderStore providers, AuditLog audit)
    {
        _sites = sites;
        _providers = providers;
        _audit = audit;
    }

    /// <summary>
    /// Replaces the site's settings. An empty model means the provider's default, and an empty
    /// prompt means none.
    /// </summary>
    public async Task<(bool Ok, string? Error)> SetAsync(
        SiteRecord site, string? providerId, string? model, string? systemPrompt, bool allowVisitors, string actor)
    {
        providerId = providerId?.Trim();
        if (string.IsNullOrEmpty(providerId)) return (false, "Choose a provider, or remove the AI settings instead.");

        var provider = await _providers.FindAsync(providerId);
        if (provider is null) return (false, "That AI provider does not exist. An administrator can add one under AI.");

        model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        if (model is { Length: > SiteAiSettings.MaxModelLength })
            return (false, $"A model name can be at most {SiteAiSettings.MaxModelLength} characters.");
        if (model is not null && model.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return (false, "A model name has no spaces.");

        // A textarea posts CRLF line breaks; the prompt is stored with plain ones.
        systemPrompt = systemPrompt?.ReplaceLineEndings("\n").Trim();
        if (string.IsNullOrEmpty(systemPrompt)) systemPrompt = null;
        if (systemPrompt is not null && Encoding.UTF8.GetByteCount(systemPrompt) > SiteAiSettings.MaxSystemPromptBytes)
            return (false, $"The system prompt can be at most {SiteAiSettings.MaxSystemPromptBytes / 1024} KB.");

        site.Ai = new SiteAiSettings
        {
            ProviderId = provider.Id,
            Model = model,
            SystemPrompt = systemPrompt,
            AllowVisitors = allowVisitors,
            UpdatedUtc = DateTimeOffset.UtcNow,
            UpdatedBy = actor
        };

        await _sites.SaveAsync(site);
        await _audit.WriteAsync("site.ai.set", actor, new
        {
            domain = site.Domain,
            providerId = provider.Id,
            provider = provider.Name,
            model,
            systemPrompt = systemPrompt is not null,
            allowVisitors
        });

        return (true, null);
    }

    /// <summary>Clears the site's settings, so it has no AI. Returns false when it had none.</summary>
    public async Task<bool> RemoveAsync(SiteRecord site, string actor)
    {
        if (site.Ai is null) return false;

        site.Ai = null;
        await _sites.SaveAsync(site);
        await _audit.WriteAsync("site.ai.remove", actor, new { domain = site.Domain });
        return true;
    }
}
