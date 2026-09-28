using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StaticSiteHost.Functions;
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
    public const string TestPrompt = "Reply with the single word OK.";

    private readonly AiProviderStore _providers;
    private readonly AiChatService _chat;
    private readonly SiteStore _sites;

    public AiModel(AiProviderStore providers, AiChatService chat, SiteStore sites)
    {
        _providers = providers;
        _chat = chat;
        _sites = sites;
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

        TempData["StatusMessage"] = $"{provider.Name} was added. Test it below, then choose it for a site on the site's page.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateAsync(string? id, string? name, string? baseUrl, string? apiKey, string? defaultModel)
    {
        var (provider, error) = await _providers.UpdateAsync(id, name, baseUrl, apiKey, defaultModel, Actor);

        if (provider is null) TempData["ErrorMessage"] = error;
        else if (string.IsNullOrWhiteSpace(apiKey)) TempData["StatusMessage"] = $"{provider.Name} was saved. Its key is unchanged.";
        else TempData["StatusMessage"] = $"{provider.Name} was saved with its new key.";

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string? id)
    {
        var removed = await _providers.DeleteAsync(id, Actor);

        if (removed is null) TempData["ErrorMessage"] = "That provider no longer exists.";
        else TempData["StatusMessage"] = $"{removed.Name} and its key were removed. Sites that used it have no AI until they are given another provider.";

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
            TempData["ErrorMessage"] = "That provider no longer exists.";
            return RedirectToPage();
        }

        var clock = Stopwatch.StartNew();
        try
        {
            var answer = await _chat.CompleteAsync(provider, new AiChatRequest
            {
                Messages = [AiMessage.User(TestPrompt)],
                MaxTokens = 16
            }, HttpContext.RequestAborted);

            var text = answer.Text.Trim();
            var said = text.Length == 0 ? "no text" : $"“{(text.Length > 200 ? text[..200] + "…" : text)}”";
            var stop = answer.StopReason is null ? "" : $", stopping on {answer.StopReason}";

            TempData["StatusMessage"] =
                $"{provider.Name} answered in {clock.ElapsedMilliseconds:N0} ms with {said}: model {answer.Model}, " +
                $"{answer.InputTokens:N0} tokens in and {answer.OutputTokens:N0} out{stop}.";
        }
        catch (AiChatException ex)
        {
            TempData["ErrorMessage"] = $"{provider.Name} failed the test after {clock.ElapsedMilliseconds:N0} ms. {ex.Message}";
        }

        return RedirectToPage();
    }

    private string Actor => User.Identity?.Name ?? "unknown";
}
