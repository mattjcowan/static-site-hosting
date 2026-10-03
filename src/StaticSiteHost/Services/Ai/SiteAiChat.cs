using System.Runtime.CompilerServices;
using StaticSiteHost.Functions;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services.Ai;

/// <summary>
/// Makes the <see cref="IAiChat"/> for a site. Singleton; the chats it makes are cheap and hold
/// nothing but the site record, or the domain and the store to find it in.
/// </summary>
public sealed class SiteAiChatFactory
{
    private readonly AiChatService _chat;
    private readonly AiProviderStore _providers;
    private readonly SiteStore _sites;

    public SiteAiChatFactory(AiChatService chat, AiProviderStore providers, SiteStore sites)
    {
        _chat = chat;
        _providers = providers;
        _sites = sites;
    }

    /// <summary>
    /// The chat for <paramref name="site"/>, as its functions and browsers use it. The function
    /// pipeline hands this to functions as <c>ISite.Ai</c> (and to parameters of type
    /// <see cref="IAiChat"/>), and <c>/_host/ai/chat</c> answers browsers with it, so both see the
    /// same provider, model and pinned system prompt.
    /// </summary>
    /// <remarks>
    /// The result reads the site's settings and its provider afresh on every call, so one kept for
    /// longer than a request follows an administrator's changes, and reads as not configured once
    /// its provider is deleted. It holds a reference to the site record only: nothing in a function
    /// bundle, and nothing that keeps one alive.
    /// </remarks>
    public SiteAiChat Create(SiteRecord site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return new SiteAiChat(site.Domain, () => site, _chat, _providers);
    }

    /// <summary>
    /// The chat for the site at <paramref name="domain"/>, which looks the site up afresh on every
    /// call: for code that lives longer than a request, such as a job, a background service or a
    /// service the functions registered. It reads as not configured while no site has the domain.
    /// </summary>
    /// <remarks>
    /// It follows the domain, not the site: after a rename it reaches nothing, as the functions
    /// holding it are retired with the old name and loaded again under the new one.
    /// </remarks>
    /// <param name="check">
    /// Run before every use, and throws when the caller should no longer reach the site: a set's own
    /// context passes its retirement check, so a service that kept this cannot spend a later site's
    /// key under the same domain (see <see cref="SiteContext"/>).
    /// </param>
    public SiteAiChat Create(string domain, Action? check = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(domain);
        return new SiteAiChat(domain, () =>
        {
            check?.Invoke();
            return _sites.TryGet(domain);
        }, _chat, _providers);
    }

    /// <summary>The provider the site's settings name, or null when it has none or it was deleted.</summary>
    public AiProviderRecord? ProviderOf(SiteRecord site) =>
        site.Ai is { HasProvider: true } settings ? _providers.Find(settings.ProviderId) : null;
}

/// <summary>
/// A site's <see cref="IAiChat"/>: its provider, with the site's model as the default and its
/// pinned system prompt ahead of whatever the caller adds. Made by <see cref="SiteAiChatFactory"/>.
/// </summary>
public sealed class SiteAiChat : IAiChat
{
    private readonly string _domain;
    private readonly Func<SiteRecord?> _site;
    private readonly AiChatService _chat;
    private readonly AiProviderStore _providers;

    /// <param name="domain">The site's domain, for messages.</param>
    /// <param name="site">The site's record as it is now; null when there is no such site.</param>
    internal SiteAiChat(string domain, Func<SiteRecord?> site, AiChatService chat, AiProviderStore providers)
    {
        _domain = domain;
        _site = site;
        _chat = chat;
        _providers = providers;
    }

    public bool IsConfigured => Resolve() is not null;

    public string? Model => Resolve() is { } resolved ? resolved.Settings.Model ?? resolved.Provider.DefaultModel : null;

    /// <summary>
    /// True when the site lets any visitor's browser chat through <c>/_host/ai/chat</c>. False when
    /// it has no provider.
    /// </summary>
    public bool AllowsVisitors => Resolve() is { Settings.AllowVisitors: true };

    public Task<AiChatResponse> CompleteAsync(AiChatRequest request, CancellationToken ct = default)
    {
        var (settings, provider) = Require();
        return _chat.CompleteAsync(provider, Apply(settings, request), ct);
    }

    public async IAsyncEnumerable<AiChatChunk> StreamAsync(
        AiChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (settings, provider) = Require();

        await foreach (var chunk in _chat.StreamAsync(provider, Apply(settings, request), ct))
            yield return chunk;
    }

    /// <summary>
    /// The site's settings and provider as they are now, or null when it has no provider. The
    /// settings are read once, and are replaced rather than edited on a change, so the pair always
    /// belongs together.
    /// </summary>
    private (SiteAiSettings Settings, AiProviderRecord Provider)? Resolve()
    {
        var settings = _site()?.Ai;
        if (settings is not { HasProvider: true }) return null;

        return _providers.Find(settings.ProviderId) is { } provider ? (settings, provider) : null;
    }

    private (SiteAiSettings Settings, AiProviderRecord Provider) Require() =>
        Resolve() ?? throw new AiChatException(
            $"AI is not set up for {_domain}. An administrator can choose a provider under Sites → {_domain} → AI.");

    /// <summary>
    /// The request as it goes to the provider: the site's pinned prompt first and the caller's
    /// after it, so a caller can add instructions but never take the site's away, and the site's
    /// model unless the caller names one. Everything else, the tools included, passes through as
    /// the caller sent it.
    /// </summary>
    internal static AiChatRequest Apply(SiteAiSettings settings, AiChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parts = new[] { settings.SystemPrompt, request.System }.Where(part => !string.IsNullOrWhiteSpace(part));
        var system = string.Join("\n\n", parts);

        return new AiChatRequest
        {
            Messages = request.Messages,
            System = system.Length > 0 ? system : null,
            Model = string.IsNullOrWhiteSpace(request.Model) ? settings.Model : request.Model,
            MaxTokens = request.MaxTokens,
            Temperature = request.Temperature,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice
        };
    }
}
