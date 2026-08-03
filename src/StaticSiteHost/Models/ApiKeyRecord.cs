using System.Text.Json.Serialization;

namespace StaticSiteHost.Models;

public sealed class ApiKeyRecord
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>SHA-256 of the secret half of the key. The full key is shown once, at creation.</summary>
    public string SecretHash { get; set; } = "";

    /// <summary>Masked form kept for display, e.g. <c>sshost_a1b2c3d4e5f6_••••</c>.</summary>
    public string Display { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedUtc { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public bool Revoked { get; set; }

    [JsonIgnore]
    public bool IsExpired => ExpiresUtc is { } exp && exp <= DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsUsable => !Revoked && !IsExpired;
}
