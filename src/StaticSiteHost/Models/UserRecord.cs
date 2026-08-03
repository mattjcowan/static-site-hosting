using System.Text.Json.Serialization;

namespace StaticSiteHost.Models;

public sealed class UserRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>Null until the user completes an invitation or an admin assigns a temporary password.</summary>
    public string? PasswordHash { get; set; }

    public string Role { get; set; } = Roles.Member;
    public bool MustChangePassword { get; set; }
    public bool IsDisabled { get; set; }

    /// <summary>Rotated whenever credentials change, which invalidates existing cookies.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("n");

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginUtc { get; set; }
    public DateTimeOffset? PasswordChangedUtc { get; set; }

    /// <summary>SHA-256 of the outstanding invitation token. The token itself is never stored.</summary>
    public string? InviteTokenHash { get; set; }
    public DateTimeOffset? InviteCreatedUtc { get; set; }
    public DateTimeOffset? InviteExpiresUtc { get; set; }

    /// <summary>True for the administrator seeded from configuration; that account cannot be deleted.</summary>
    public bool IsBootstrap { get; set; }

    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);

    [JsonIgnore]
    public bool HasPendingInvite =>
        InviteTokenHash is not null && InviteExpiresUtc is { } exp && exp > DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsAdministrator => Role == Roles.Administrator;
}
