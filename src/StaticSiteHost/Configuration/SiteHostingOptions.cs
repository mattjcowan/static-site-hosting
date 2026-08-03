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

    /// <summary>
    /// Honour X-Forwarded-Host/-Proto/-For. Turn this on only when the app sits behind a
    /// trusted reverse proxy: routing is driven by the Host header, so a spoofable
    /// X-Forwarded-Host would let a caller pick which site answers.
    /// </summary>
    public bool TrustForwardedHeaders { get; set; }

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
