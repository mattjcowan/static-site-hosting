using System.Text.Json.Serialization;

namespace StaticSiteHost.Models;

/// <summary>
/// Persisted as sites/&lt;domain&gt;/site.json. Deploys write a new immutable release
/// directory and then flip <see cref="CurrentRelease"/>, so serving never observes
/// a half-extracted archive.
/// </summary>
public sealed class SiteRecord
{
    public string Domain { get; set; } = "";
    public string? CurrentRelease { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedUtc { get; set; }
    public string? CreatedBy { get; set; }
    public string? LastDeployedBy { get; set; }
    public List<ReleaseRecord> Releases { get; set; } = [];

    /// <summary>
    /// PBKDF2 hash of the visitor passcode, or null when the site is public. Never leaves
    /// the server: the API and the management UI report only whether one is set.
    /// </summary>
    public string? PasscodeHash { get; set; }

    public DateTimeOffset? PasscodeSetUtc { get; set; }
    public string? PasscodeSetBy { get; set; }

    [JsonIgnore]
    public bool IsPasscodeProtected => !string.IsNullOrEmpty(PasscodeHash);

    [JsonIgnore]
    public ReleaseRecord? Current => Releases.FirstOrDefault(r => r.Id == CurrentRelease);
}

public sealed class ReleaseRecord
{
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? DeployedBy { get; set; }

    /// <summary>"web" or "api".</summary>
    public string Source { get; set; } = "web";

    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public string? ArchiveName { get; set; }

    /// <summary>True when the archive wrapped everything in a single folder that was unwrapped.</summary>
    public bool StrippedRootFolder { get; set; }

    public bool HasRootIndex { get; set; }
}
