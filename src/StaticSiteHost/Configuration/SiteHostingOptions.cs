namespace StaticSiteHost.Configuration;

/// <summary>
/// Everything that controls where content lives and how it is served.
/// Bound from the "SiteHosting" configuration section.
/// </summary>
public sealed class SiteHostingOptions
{
    public const string SectionName = "SiteHosting";

    /// <summary>Root of the data volume. Holds config/, sites/ and tmp/.</summary>
    public string DataRoot { get; set; } = "/data";

    /// <summary>
    /// Hostnames that serve the management UI/API instead of a hosted site.
    /// When empty, the management app answers any host that has no site.
    /// </summary>
    public string[] ManagementHosts { get; set; } = [];

    /// <summary>Largest accepted upload (compressed archive) in bytes.</summary>
    public long MaxUploadBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Largest accepted total size after extraction — zip-bomb guard.</summary>
    public long MaxExtractedBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Largest accepted single file after extraction.</summary>
    public long MaxEntryBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Largest accepted number of entries in an archive.</summary>
    public int MaxEntries { get; set; } = 50_000;

    /// <summary>How many previous releases to keep on disk for rollback.</summary>
    public int ReleasesToKeep { get; set; } = 3;

    /// <summary>max-age applied to non-HTML assets served from a site.</summary>
    public int AssetCacheSeconds { get; set; } = 3600;

    /// <summary>
    /// When true, every unresolved request falls back to index.html — including
    /// .js/.css/.png. Off by default so missing assets 404 instead of being
    /// served HTML with the wrong content type.
    /// </summary>
    public bool SpaFallbackForAllRequests { get; set; }

    /// <summary>Lifetime of an invitation / password-reset link.</summary>
    public int InviteLifetimeHours { get; set; } = 168;

    /// <summary>Minimum length enforced when a user sets a password.</summary>
    public int MinPasswordLength { get; set; } = 12;

    /// <summary>Minimum length enforced when a site is given a visitor passcode.</summary>
    public int MinPasscodeLength { get; set; } = 8;

    /// <summary>
    /// How long a visitor stays unlocked after entering a site's passcode. The cookie is
    /// per-domain, so unlocking one private site never unlocks another.
    /// </summary>
    public int PasscodeSessionHours { get; set; } = 168;

    /// <summary>
    /// Honour X-Forwarded-Host/-Proto/-For. Turn this on only when the app sits behind a
    /// trusted reverse proxy: routing is driven by the Host header, so a spoofable
    /// X-Forwarded-Host would let a caller pick which site answers.
    /// </summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>
    /// How long a call to an AI provider may take: the whole answer when it is not streamed, and
    /// the longest silence between two pieces of one that is.
    /// </summary>
    public int AiTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// How many chat requests one address may make to one site's <c>/_host/ai/chat</c> in any
    /// minute. Every one of them is billed to the provider's key. 0 turns the limit off.
    /// </summary>
    public int AiVisitorRequestsPerMinute { get; set; } = 20;

    /// <summary>Largest body <c>/_host/ai/chat</c> accepts, in bytes: the conversation a browser sends.</summary>
    public int AiMaxRequestBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// The longest answer, in tokens, that a browser's chat at <c>/_host/ai/chat</c> may get, so a
    /// visitor cannot run up the bill with one question. 0 leaves it to the provider: Anthropic's
    /// 1024, or the model's own limit for an OpenAI-compatible one. Functions calling
    /// <c>ISite.Ai</c> set their own.
    /// </summary>
    public int AiVisitorMaxTokens { get; set; } = 1024;

    /// <summary>
    /// How many pages may be connected to one site's realtime hub at once. The next is refused until
    /// one leaves. 0 refuses every connection, which turns realtime off.
    /// </summary>
    public int RealtimeMaxConnectionsPerSite { get; set; } = 1000;

    /// <summary>
    /// How many pages one client address may have connected to one site's realtime hub at once, so
    /// that one client cannot take every place a site has. An IPv6 client is counted by its /64.
    /// 0 sets no limit.
    /// </summary>
    public int RealtimeMaxConnectionsPerAddress { get; set; } = 20;

    /// <summary>
    /// How many new connections one client address may start to one site's realtime hub in any
    /// minute. Each runs the site's <c>[RealtimeConnect]</c> hook, if it has one. 0 turns the limit off.
    /// </summary>
    public int RealtimeNegotiationsPerMinute { get; set; } = 60;

    /// <summary>
    /// How many groups one site may have at once. A group exists while it has a member, so the
    /// limit is on groups in use, not on names ever used.
    /// </summary>
    public int RealtimeMaxGroupsPerSite { get; set; } = 1000;

    /// <summary>How many groups one connection, which is one open page, may be in at once.</summary>
    public int RealtimeMaxGroupsPerConnection { get; set; } = 100;

    private HashSet<string>? _managementHostSet;

    public bool IsManagementHost(string host)
    {
        _managementHostSet ??= new HashSet<string>(
            ManagementHosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
        return _managementHostSet.Contains(host);
    }

    public bool HasManagementHosts => ManagementHosts.Any(h => !string.IsNullOrWhiteSpace(h));
}
