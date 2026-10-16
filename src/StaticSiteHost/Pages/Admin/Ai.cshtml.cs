using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using StaticSiteHost.Functions;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Ai;

namespace StaticSiteHost.Pages.Admin;

/// <summary>
/// The AI providers sites can chat through. Administrators only, via the /Admin folder policy: a
/// provider holds a key that costs money to use.
/// </summary>
public class AiModel : PageModel
{
    /// <summary>What the Test button sends: as short a round trip as a model will make.</summary>
    public const string TestPrompt = AiProviderTest.Prompt;

    private readonly AiProviderStore _providers;
    private readonly AiChatService _chat;
    private readonly SiteStore _sites;
    private readonly StaticSiteHost.Services.Assistant.AssistantSettingsStore _assistant;
    private readonly StaticSiteHost.Services.Assistant.AssistantUsageStore _usage;
    private readonly IStringLocalizer<Strings> _strings;

    public AiModel(
        AiProviderStore providers, AiChatService chat, SiteStore sites,
        StaticSiteHost.Services.Assistant.AssistantSettingsStore assistant, StaticSiteHost.Services.Assistant.AssistantUsageStore usage,
        IStringLocalizer<Strings> strings)
    {
        _usage = usage;
        _strings = strings;
        _providers = providers;
        _chat = chat;
        _sites = sites;
        _assistant = assistant;
    }

    /// <summary>The management assistant's settings, for its card.</summary>
    public StaticSiteHost.Services.Assistant.AssistantSettings Assistant { get; private set; } = new();

    /// <summary>Who has used the assistant today, and how much, for its card.</summary>
    public IReadOnlyList<StaticSiteHost.Services.Assistant.AssistantUserUsage> UsageToday { get; private set; } = [];

    public string UsageDay { get; private set; } = "";

    /// <summary>
    /// Turns the assistant on with a provider and model, or off with none. Every message is billed
    /// to the provider's key, so administrators decide whether members may use it.
    /// </summary>
    public async Task<IActionResult> OnPostAssistantAsync(string? providerId, string? model, bool membersMayUse, int? memberMessagesPerDay)
    {
        if (await _assistant.SaveAsync(providerId, model, membersMayUse, Actor, memberMessagesPerDay) is { } error)
            TempData["ErrorMessage"] = error;
        else if (string.IsNullOrWhiteSpace(providerId))
            TempData["StatusMessage"] = _strings.Text("ai.assistant.savedOff");
        else
            TempData["StatusMessage"] = _strings.Text("ai.assistant.savedOn");

        return RedirectToPage();
    }

    public IReadOnlyList<AiProviderRecord> Providers { get; private set; } = [];

    /// <summary>How many sites use each provider, by id, so deleting one says what it affects.</summary>
    public IReadOnlyDictionary<string, int> SiteCounts { get; private set; } = new Dictionary<string, int>();

    /// <summary>Why the last "Add a provider" was refused, shown beside the form.</summary>
    public string? AddError { get; private set; }

    /// <summary>What was typed into "Add a provider", put back when it is refused. Never the key.</summary>
    public string? NewName { get; private set; }

    public string NewKind { get; private set; } = AiProviderRecord.KindOpenAi;
    public string? NewBaseUrl { get; private set; }
    public string? NewModel { get; private set; }

    public int Sites(AiProviderRecord provider) => SiteCounts.GetValueOrDefault(provider.Id);

    public async Task OnGetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        Assistant = await _assistant.ReadAsync();
        (UsageDay, UsageToday) = await _usage.TodayAsync();
        Providers = await _providers.ListAsync();
        SiteCounts = _sites.List()
            .Where(site => site.Ai is { HasProvider: true })
            .GroupBy(site => site.Ai!.ProviderId!)
            .ToDictionary(group => group.Key, group => group.Count());
    }

    public async Task<IActionResult> OnPostCreateAsync(string? name, string? kind, string? baseUrl, string? apiKey, string? defaultModel)
    {
        var (provider, error) = await _providers.AddAsync(name, kind, baseUrl, apiKey, defaultModel, Actor);
        if (provider is null)
        {
            // Rendered in place, so what was typed survives. The key has to be entered again.
            await LoadAsync();
            AddError = error;
            NewName = name;
            NewKind = AiProviderRecord.IsKnownKind(kind) ? kind! : AiProviderRecord.KindOpenAi;
            NewBaseUrl = baseUrl;
            NewModel = defaultModel;
            return Page();
        }

        TempData["StatusMessage"] = _strings.Text("ai.add.done", new { name = provider.Name });
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateAsync(string? id, string? name, string? baseUrl, string? apiKey, string? defaultModel)
    {
        var (provider, error) = await _providers.UpdateAsync(id, name, baseUrl, apiKey, defaultModel, Actor);

        if (provider is null) TempData["ErrorMessage"] = error;
        else if (string.IsNullOrWhiteSpace(apiKey)) TempData["StatusMessage"] = _strings.Text("ai.providers.saved", new { name = provider.Name });
        else TempData["StatusMessage"] = _strings.Text("ai.providers.savedWithKey", new { name = provider.Name });

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string? id)
    {
        var removed = await _providers.DeleteAsync(id, Actor);

        if (removed is null) TempData["ErrorMessage"] = _strings.Text("ai.providers.notFound");
        else TempData["StatusMessage"] = _strings.Text("ai.providers.deleted", new { name = removed.Name });

        return RedirectToPage();
    }

    /// <summary>
    /// Sends <see cref="TestPrompt"/> and reports what came back, how long it took and what it
    /// cost in tokens, or why it failed. One short request, billed like any other.
    /// </summary>
    public async Task<IActionResult> OnPostTestAsync(string? id)
    {
        var provider = await _providers.FindAsync(id);
        if (provider is null)
        {
            TempData["ErrorMessage"] = _strings.Text("ai.providers.notFound");
            return RedirectToPage();
        }

        var test = await AiProviderTest.RunAsync(_chat, provider, HttpContext.RequestAborted);
        if (test.Ok)
        {
            var text = test.Text ?? "";
            var said = text.Length == 0 ? _strings.Text("ai.test.noText") : _strings.Text("ai.test.quoted", new { text = text.Length > 200 ? text[..200] + "…" : text });

            TempData["StatusMessage"] = _strings.Text(test.StopReason is null ? "ai.test.answered" : "ai.test.answeredWithStop", new
            {
                name = provider.Name,
                ms = test.ElapsedMs.ToString("N0"),
                answer = said,
                model = test.Model,
                inputTokens = test.InputTokens.ToString("N0"),
                outputTokens = test.OutputTokens.ToString("N0"),
                stopReason = test.StopReason
            });
        }
        else
        {
            TempData["ErrorMessage"] = _strings.Text("ai.test.failed", new { name = provider.Name, ms = test.ElapsedMs.ToString("N0"), error = test.Error });
        }

        return RedirectToPage();
    }

    private string Actor => User.Identity?.Name ?? "unknown";
}
