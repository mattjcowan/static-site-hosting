using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services.Ai;

/// <summary>One model a provider offers.</summary>
/// <param name="Id">The name to put in a model setting, exactly as the provider wants it.</param>
/// <param name="Name">A friendlier name, when the provider gives one.</param>
/// <param name="Fast">A small, quick, inexpensive model (Haiku, mini, nano, flash…), listed first.</param>
public sealed record AiModelInfo(string Id, string? Name, bool Fast);

/// <summary>What a provider said it offers, or why it could not say.</summary>
public sealed record AiModelList(IReadOnlyList<AiModelInfo> Models, string? Error)
{
    public static AiModelList Failed(string error) => new([], error);
}

/// <summary>
/// The models a provider offers, asked of the provider itself with its own key: Anthropic's
/// <c>GET /v1/models</c>, and <c>GET {base}/models</c> for OpenAI and everything that copies its
/// API (OpenRouter, Ollama, LiteLLM). That list is exact for the key, names models the way the
/// provider wants them, and includes local and proxied models no public catalogue knows.
///
/// For suggestions only: a model setting still takes any name. Lists are kept for
/// <see cref="Lifetime"/> per provider, and dropped when the provider is changed.
/// </summary>
public sealed partial class AiModelCatalog
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The most models one list holds; OpenRouter alone offers several hundred.</summary>
    public const int MaxModels = 500;

    private readonly ConcurrentDictionary<string, (AiModelList List, DateTimeOffset At)> _cache = new();
    private readonly IHttpClientFactory _http;
    private readonly AiProviderStore _providers;
    private readonly ILogger<AiModelCatalog> _logger;

    public AiModelCatalog(IHttpClientFactory http, AiProviderStore providers, ILogger<AiModelCatalog> logger)
    {
        _http = http;
        _providers = providers;
        _logger = logger;
    }

    /// <summary>A saved provider's models, from the cache when it is fresh.</summary>
    public async Task<AiModelList> ListAsync(AiProviderRecord provider, CancellationToken ct)
    {
        // Keyed on when the provider last changed too, so a new key or base URL is asked afresh.
        var key = $"{provider.Id}|{provider.UpdatedUtc?.UtcTicks}";
        if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < Lifetime && cached.List.Error is null)
            return cached.List;

        var list = await FetchAsync(provider.Kind, provider.BaseUrl, _providers.UnprotectKey(provider), ct);
        _cache[key] = (list, DateTimeOffset.UtcNow);
        return list;
    }

    /// <summary>
    /// The models of a provider that is not saved yet, from what is typed into the form that adds
    /// one: so the default model can be chosen from a list before anything is saved. Never cached.
    /// </summary>
    public Task<AiModelList> ProbeAsync(string? kind, string? baseUrl, string? apiKey, CancellationToken ct)
    {
        kind = kind?.Trim().ToLowerInvariant();
        if (!AiProviderRecord.IsKnownKind(kind)) return Task.FromResult(AiModelList.Failed("Choose a kind first."));

        var (url, error) = AiProviderStore.NormalizeBaseUrl(string.IsNullOrWhiteSpace(baseUrl) ? AiProviderStore.DefaultBaseUrl(kind!) : baseUrl);
        return url is null
            ? Task.FromResult(AiModelList.Failed(error ?? "The base URL cannot be used."))
            : FetchAsync(kind!, url, string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim(), ct);
    }

    private async Task<AiModelList> FetchAsync(string kind, string baseUrl, string? apiKey, CancellationToken ct)
    {
        var anthropic = kind == AiProviderRecord.KindAnthropic;
        var root = baseUrl.TrimEnd('/');
        var endpoint = anthropic
            ? (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root : root + "/v1") + "/models?limit=1000"
            : root + "/models";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (anthropic)
            {
                request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicChat.ApiVersion);
                if (apiKey is not null) request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            }
            else if (apiKey is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await _http.CreateClient(AiChatService.HttpClientName).SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return AiModelList.Failed((int)response.StatusCode switch
                {
                    401 or 403 => "The provider refused the key, so it would not list its models.",
                    404 => "This provider does not list its models. Type the model's name instead.",
                    var status => $"The provider answered {status} when asked for its models."
                });
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
            var openAiItself = !anthropic && Uri.TryCreate(root, UriKind.Absolute, out var uri) &&
                               uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);
            return new AiModelList(Order(Read(document.RootElement), openAiItself), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return AiModelList.Failed("The provider did not list its models in time.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogInformation(ex, "Could not list the models of the AI provider at {BaseUrl}", baseUrl);
            return AiModelList.Failed("The provider could not be reached to list its models.");
        }
    }

    /// <summary>
    /// Reads either shape, which differ only in the friendly name: <c>{ data: [{ id, display_name }] }</c>
    /// from Anthropic, <c>{ data: [{ id, created }] }</c> from OpenAI and most copies, with
    /// <c>name</c> from OpenRouter.
    /// </summary>
    internal static IEnumerable<(string Id, string? Name, long Created)> Read(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) yield break;

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("id", out var idElement) || idElement.GetString() is not { Length: > 0 } id)
                continue;

            var name = (item.TryGetProperty("display_name", out var display) ? display.GetString() : null)
                       ?? (item.TryGetProperty("name", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString() : null);

            long created = 0;
            if (item.TryGetProperty("created", out var number) && number.ValueKind == JsonValueKind.Number) created = number.GetInt64();
            else if (item.TryGetProperty("created_at", out var date) && date.TryGetDateTimeOffset(out var at)) created = at.ToUnixTimeSeconds();

            yield return (id, name == id ? null : name, created);
        }
    }

    /// <summary>
    /// Models this server can chat with, fast ones first; within each, plain names before dated
    /// snapshots, then newest first. Left out: embedding, speech, image and moderation models, and
    /// the ones that answer only on APIs this server does not use (OpenAI's codex, deep-research,
    /// search and completions-only models; on OpenAI itself, <paramref name="openAiItself"/>, its
    /// "-pro" models too, which a "-pro" behind a proxy such as gemini-2.5-pro is not).
    /// </summary>
    internal static IReadOnlyList<AiModelInfo> Order(IEnumerable<(string Id, string? Name, long Created)> models, bool openAiItself = false) =>
        models
            .Where(m => !NotChat().IsMatch(m.Id) && !(openAiItself && OpenAiResponsesOnly().IsMatch(m.Id)))
            .DistinctBy(m => m.Id)
            .Select(m => (Model: new AiModelInfo(m.Id, m.Name, FastModel().IsMatch(m.Id)), m.Created, Dated: Snapshot().IsMatch(m.Id)))
            .OrderBy(m => m.Model.Fast ? 0 : 1)
            .ThenBy(m => m.Dated ? 1 : 0)
            .ThenByDescending(m => m.Created)
            .ThenBy(m => m.Model.Id, StringComparer.Ordinal)
            .Select(m => m.Model)
            .Take(MaxModels)
            .ToList();

    [GeneratedRegex(@"(embed|whisper|tts|dall-e|moderation|transcribe|audio|realtime|image|davinci|babbage|sora|computer-use|search-preview|search-api|deep-research|codex|instruct)", RegexOptions.IgnoreCase)]
    private static partial Regex NotChat();

    [GeneratedRegex(@"(^|-)pro($|-)", RegexOptions.IgnoreCase)]
    private static partial Regex OpenAiResponsesOnly();

    // A whole part of the name, so "gemini" is not taken for "mini".
    [GeneratedRegex(@"(^|[-_/.:])(haiku|mini|nano|flash|lite|small|tiny)($|[-_/.:])", RegexOptions.IgnoreCase)]
    private static partial Regex FastModel();

    [GeneratedRegex(@"(\d{4}-\d{2}-\d{2}|\d{8}|-\d{4}$)")]
    private static partial Regex Snapshot();
}
