using StaticSiteHost.Services.Ai;

namespace StaticSiteHost.Services.Assistant;

/// <summary>
/// How the management assistant runs. Persisted as config/assistant.json. Off until an
/// administrator chooses a provider for it under AI.
/// </summary>
public sealed class AssistantSettings
{
    /// <summary>The AI provider the assistant talks through, or null for no assistant.</summary>
    public string? ProviderId { get; set; }

    /// <summary>The model, or null for the provider's default.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Let members use it too. Off by default: every message is billed to the provider's key. A
    /// member's assistant can do only what a member can.
    /// </summary>
    public bool MembersMayUse { get; set; }

    /// <summary>
    /// The most messages one member may send the assistant in a UTC day; 0 is no limit.
    /// Administrators are never limited. Counted by <see cref="AssistantUsageStore"/>.
    /// </summary>
    public int MemberMessagesPerDay { get; set; } = DefaultMemberMessagesPerDay;

    public const int DefaultMemberMessagesPerDay = 50;

    /// <summary>The highest limit that can be set; above it, choose no limit.</summary>
    public const int MaxMemberMessagesPerDay = 10_000;

    public DateTimeOffset? UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Reads and saves <see cref="AssistantSettings"/>, and says who may use the assistant.</summary>
public sealed class AssistantSettingsStore
{
    private readonly JsonFileStore<AssistantSettings> _store;
    private readonly AiProviderStore _providers;
    private readonly AuditLog _audit;

    public AssistantSettingsStore(DataPaths paths, AiProviderStore providers, AuditLog audit)
    {
        _store = new JsonFileStore<AssistantSettings>(Path.Combine(paths.ConfigDir, "assistant.json"));
        _providers = providers;
        _audit = audit;
    }

    public Task<AssistantSettings> ReadAsync() => _store.ReadAsync();

    /// <summary>Whether this user may open the assistant: it has a provider that still exists, and they are allowed.</summary>
    public async Task<bool> IsAvailableToAsync(bool isAdministrator)
    {
        var settings = await ReadAsync();
        return settings.ProviderId is not null &&
               _providers.Find(settings.ProviderId) is not null &&
               (isAdministrator || settings.MembersMayUse);
    }

    /// <summary>
    /// Saves new settings. A null or empty provider turns the assistant off; a null daily limit keeps
    /// the one set. Returns why not, or null.
    /// </summary>
    public async Task<string?> SaveAsync(string? providerId, string? model, bool membersMayUse, string actor, int? memberMessagesPerDay = null)
    {
        providerId = string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim();
        model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();

        if (providerId is not null && _providers.Find(providerId) is null) return "That AI provider no longer exists.";
        if (model is { Length: > AiProviderStore.MaxModelLength }) return $"The model name must be {AiProviderStore.MaxModelLength} characters or fewer.";
        if (memberMessagesPerDay is < 0 or > AssistantSettings.MaxMemberMessagesPerDay)
            return $"The daily limit for members is 0 (no limit) to {AssistantSettings.MaxMemberMessagesPerDay:N0} messages.";

        await _store.UpdateAsync(settings =>
        {
            settings.ProviderId = providerId;
            settings.Model = model;
            settings.MembersMayUse = membersMayUse;
            if (memberMessagesPerDay is { } limit) settings.MemberMessagesPerDay = limit;
            settings.UpdatedUtc = DateTimeOffset.UtcNow;
            settings.UpdatedBy = actor;
            return (true, true);
        });

        await _audit.WriteAsync("assistant.settings", actor, new { providerId, model, membersMayUse, memberMessagesPerDay });
        return null;
    }
}
