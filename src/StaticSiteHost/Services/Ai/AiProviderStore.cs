using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using StaticSiteHost.Functions;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Security;

namespace StaticSiteHost.Services.Ai;

/// <summary>
/// The AI providers administrators have added, in config/ai-providers.json. The admin page and the
/// API come through here, so both validate alike and leave the same audit trail.
///
/// An API key is protected with Data Protection before it is written and is decrypted only by
/// <see cref="UnprotectKey"/>, to make a call. Nothing here hands one back to a page or a response.
///
/// Reads come from an in-memory snapshot, so a request resolving its site's provider never waits on
/// the file lock. Every change builds new records and a new snapshot rather than editing either in
/// place, so a chat that picked up a provider keeps a consistent copy of it while an administrator
/// edits it.
/// </summary>
public sealed class AiProviderStore
{
    public const int MaxNameLength = 64;
    public const int MaxModelLength = 128;
    public const int MaxApiKeyBytes = 1024;
    public const int MaxBaseUrlLength = 2048;

    private const string ProtectorPurpose = "StaticSiteHost.AiProviders";

    private readonly JsonFileStore<List<AiProviderRecord>> _store;
    private readonly IDataProtector _protector;
    private readonly AuditLog _audit;
    private readonly ILogger<AiProviderStore> _logger;

    // The snapshot is replaced under this gate, after the file is written, so two changes cannot
    // publish their snapshots out of order.
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private volatile AiProviderRecord[]? _snapshot;

    public AiProviderStore(DataPaths paths, IDataProtectionProvider protection, AuditLog audit, ILogger<AiProviderStore> logger)
    {
        _store = new JsonFileStore<List<AiProviderRecord>>(paths.AiProvidersFile);
        _protector = protection.CreateProtector(ProtectorPurpose);
        _audit = audit;
        _logger = logger;
    }

    /// <summary>Reads the file, once. Called at startup, so <see cref="Find"/> has something to find.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_snapshot is not null) return;

        // Under the gate, so the copy is never taken while a change is editing the list.
        await _writeGate.WaitAsync(ct);
        try
        {
            _snapshot ??= [.. await _store.ReadAsync(ct)];
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Every provider, by name.</summary>
    public async Task<IReadOnlyList<AiProviderRecord>> ListAsync()
    {
        await LoadAsync();
        return [.. _snapshot!.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<AiProviderRecord?> FindAsync(string? id)
    {
        await LoadAsync();
        return Find(id);
    }

    /// <summary>
    /// The provider with this id, or null. Synchronous, from the snapshot <see cref="LoadAsync"/>
    /// read at startup, for code that resolves a site's provider on the request path.
    /// </summary>
    public AiProviderRecord? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : _snapshot?.FirstOrDefault(p => p.Id == id);

    // ---- changes ------------------------------------------------------------

    public async Task<(AiProviderRecord? Provider, string? Error)> AddAsync(
        string? name, string? kind, string? baseUrl, string? apiKey, string? defaultModel, string actor)
    {
        kind = kind?.Trim().ToLowerInvariant();
        if (!AiProviderRecord.IsKnownKind(kind))
            return (null, ServerText.Get("ai.error.kind", new { openai = AiProviderRecord.KindOpenAi, anthropic = AiProviderRecord.KindAnthropic }));

        // A new provider with no base URL gets its kind's own: the common case, OpenAI or Anthropic
        // themselves, needs only a key. A proxy or a local server names its own.
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = DefaultBaseUrl(kind!);

        var (fields, error) = Check(name, baseUrl, defaultModel);
        if (error is not null) return (null, error);
        if (CheckKey(apiKey) is { } keyError) return (null, keyError);

        var record = new AiProviderRecord
        {
            Id = Tokens.NewAlphanumeric(12),
            Name = fields.Name,
            Kind = kind!,
            BaseUrl = fields.BaseUrl,
            ApiKeyProtected = Protect(apiKey),
            DefaultModel = fields.Model,
            CreatedBy = actor
        };

        var added = await ChangeAsync(providers =>
        {
            if (NameTaken(providers, record.Name, exceptId: null)) return (null, NameTakenError(record.Name));
            providers.Add(record);
            return (record, null);
        });

        if (added.Error is null)
            await _audit.WriteAsync("ai.provider.add", actor, new { id = record.Id, name = record.Name, kind = record.Kind });

        return added;
    }

    /// <summary>Changes a provider. An empty <paramref name="apiKey"/> keeps the key it has.</summary>
    public async Task<(AiProviderRecord? Provider, string? Error)> UpdateAsync(
        string? id, string? name, string? baseUrl, string? apiKey, string? defaultModel, string actor)
    {
        var (fields, error) = Check(name, baseUrl, defaultModel);
        if (error is not null) return (null, error);
        if (CheckKey(apiKey) is { } keyError) return (null, keyError);

        var newKey = string.IsNullOrWhiteSpace(apiKey) ? null : Protect(apiKey);

        var updated = await ChangeAsync(providers =>
        {
            var index = providers.FindIndex(p => p.Id == id);
            if (index < 0) return (null, ServerText.Get("ai.error.providerGone"));
            if (NameTaken(providers, fields.Name, exceptId: id)) return (null, NameTakenError(fields.Name));

            var record = providers[index].Clone();
            record.Name = fields.Name;
            record.BaseUrl = fields.BaseUrl;
            record.DefaultModel = fields.Model;
            record.ApiKeyProtected = newKey ?? record.ApiKeyProtected;
            record.UpdatedUtc = DateTimeOffset.UtcNow;

            providers[index] = record;
            return (record, null);
        });

        if (updated.Provider is { } provider)
            await _audit.WriteAsync("ai.provider.update", actor, new { id = provider.Id, name = provider.Name, keyChanged = newKey is not null });

        return updated;
    }

    /// <summary>Removes a provider. Sites that used it read as having no AI. Returns what was removed, or null.</summary>
    public async Task<AiProviderRecord?> DeleteAsync(string? id, string actor)
    {
        var (removed, _) = await ChangeAsync(providers =>
        {
            var index = providers.FindIndex(p => p.Id == id);
            if (index < 0) return (null, null);

            var record = providers[index];
            providers.RemoveAt(index);
            return (record, null);
        });

        if (removed is not null)
            await _audit.WriteAsync("ai.provider.delete", actor, new { id = removed.Id, name = removed.Name });

        return removed;
    }

    /// <summary>
    /// Runs a change to the list under the file lock and, when it returns the provider it added,
    /// changed or removed, writes the file and publishes a new snapshot. A change that returns no
    /// provider, with or without an error, has left the list alone and nothing is written.
    /// </summary>
    private async Task<(AiProviderRecord? Provider, string? Error)> ChangeAsync(
        Func<List<AiProviderRecord>, (AiProviderRecord? Provider, string? Error)> change)
    {
        await _writeGate.WaitAsync();
        try
        {
            AiProviderRecord[]? snapshot = null;
            var outcome = await _store.UpdateAsync(providers =>
            {
                var result = change(providers);
                if (result.Provider is null) return (result, false);

                snapshot = [.. providers];
                return (result, true);
            });

            // Published only once the file is written: a failed write throws past this line.
            if (snapshot is not null) _snapshot = snapshot;
            return outcome;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ---- keys ---------------------------------------------------------------

    /// <summary>
    /// The provider's API key in the clear, to put on one request, or null when it has none. Never
    /// store, log or return what this gives you.
    /// </summary>
    /// <exception cref="AiChatException">The key was stored under data-protection keys this server no longer has.</exception>
    public string? UnprotectKey(AiProviderRecord provider)
    {
        if (!provider.HasApiKey) return null;

        try
        {
            return _protector.Unprotect(provider.ApiKeyProtected!);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "The API key of AI provider {Name} ({Id}) could not be decrypted", provider.Name, provider.Id);
            throw new AiChatException(
                $"The API key saved for the AI provider {provider.Name} can no longer be decrypted, because the server's " +
                "data-protection keys changed. An administrator can enter it again under AI.");
        }
    }

    private string? Protect(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : _protector.Protect(apiKey.Trim());

    // ---- validation ---------------------------------------------------------

    private readonly record struct Fields(string Name, string BaseUrl, string Model);

    /// <summary>The API's own address for a kind of provider.</summary>
    public static string DefaultBaseUrl(string kind) =>
        kind == AiProviderRecord.KindAnthropic ? "https://api.anthropic.com" : "https://api.openai.com/v1";

    private static (Fields Fields, string? Error) Check(string? name, string? baseUrl, string? model)
    {
        name = name?.Trim() ?? "";
        model = model?.Trim() ?? "";

        if (name.Length == 0) return (default, ServerText.Get("ai.error.nameRequired"));
        if (name.Length > MaxNameLength) return (default, ServerText.Get("ai.error.nameLong", new { max = MaxNameLength }));
        if (name.Any(char.IsControl)) return (default, ServerText.Get("ai.error.nameLine"));

        var (url, urlError) = NormalizeBaseUrl(baseUrl);
        if (url is null) return (default, urlError);

        if (model.Length == 0) return (default, ServerText.Get("ai.error.modelRequired"));
        if (model.Length > MaxModelLength) return (default, ServerText.Get("ai.error.modelLong", new { max = MaxModelLength }));
        if (model.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return (default, ServerText.Get("ai.error.modelSpaces"));

        return (new Fields(name, url, model), null);
    }

    private static string? CheckKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var key = apiKey.Trim();
        if (Encoding.UTF8.GetByteCount(key) > MaxApiKeyBytes) return ServerText.Get("ai.error.keyLarge", new { size = MaxApiKeyBytes / 1024 });

        // It goes into a request header, where a line break would be refused, or worse, obeyed.
        return key.Any(char.IsControl) ? ServerText.Get("ai.error.keyLine") : null;
    }

    /// <summary>
    /// An absolute http or https address with no query, fragment or credentials, as the scheme,
    /// host, port and path, without a trailing slash. The endpoint's own path is added to it.
    /// </summary>
    public static (string? Url, string? Error) NormalizeBaseUrl(string? input)
    {
        var value = input?.Trim() ?? "";
        if (value.Length == 0) return (null, ServerText.Get("ai.error.baseUrlRequired"));
        if (value.Length > MaxBaseUrlLength) return (null, ServerText.Get("ai.error.baseUrlLong", new { max = MaxBaseUrlLength }));

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return (null, ServerText.Get("ai.error.baseUrlForm"));
        }

        if (uri.UserInfo.Length > 0) return (null, ServerText.Get("ai.error.baseUrlKey"));
        if (value.Contains('?') || value.Contains('#'))
            return (null, ServerText.Get("ai.error.baseUrlQuery"));

        return (uri.GetLeftPart(UriPartial.Path).TrimEnd('/'), null);
    }

    private static bool NameTaken(List<AiProviderRecord> providers, string name, string? exceptId) =>
        providers.Any(p => p.Id != exceptId && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string NameTakenError(string name) =>
        ServerText.Get("ai.error.nameTaken", new { name });
}
