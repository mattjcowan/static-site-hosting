using StaticSiteHost.Models;
using StaticSiteHost.Security;

namespace StaticSiteHost.Services;

/// <summary>What creating a user hands back once: a temporary password, or an invitation token for a private link.</summary>
public sealed record CreatedUser(UserRecord User, string? TemporaryPassword, string? InviteToken);

/// <summary>
/// Administering accounts: create, invite, change role, disable, delete. Shared by the Users page
/// and the API, so both keep the same guards: nobody changes, disables or deletes their own account
/// from here, and at least one active administrator always remains. Callers check that the person
/// acting is an administrator.
/// </summary>
public sealed class UserAdminService
{
    private readonly UserStore _users;
    private readonly ApiKeyStore _apiKeys;
    private readonly AuditLog _audit;

    public UserAdminService(UserStore users, ApiKeyStore apiKeys, AuditLog audit)
    {
        _users = users;
        _apiKeys = apiKeys;
        _audit = audit;
    }

    /// <param name="temporaryPassword">
    /// True gives the account a generated password it must change at first sign-in. False gives it
    /// none, and an invitation for a private link instead.
    /// </param>
    public async Task<(CreatedUser? Created, string? Error)> CreateAsync(
        string? username, string? displayName, string? role, bool temporaryPassword, string actor)
    {
        role = Roles.IsValid(role) ? role! : Roles.Member;
        var password = temporaryPassword ? Tokens.GeneratePassword() : null;

        var (user, error) = await _users.CreateAsync(username ?? "", displayName, role, password);
        if (user is null) return (null, error);

        await _audit.WriteAsync("user.create", actor, new { user.Username, user.Role, credential = temporaryPassword ? "password" : "link" });

        if (temporaryPassword) return (new CreatedUser(user, password, null), null);

        var (token, inviteError) = await InviteAsync(user.Id, actor);
        return token is null ? (null, inviteError) : (new CreatedUser(user, null, token), null);
    }

    /// <summary>A new invitation token, which replaces any earlier one. Also how a password is reset.</summary>
    public async Task<(string? Token, string? Error)> InviteAsync(string? userId, string actor)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null) return (null, "No such user.");

        var token = await _users.CreateInviteAsync(user.Id);
        if (token is null) return (null, "The invitation could not be created.");

        await _audit.WriteAsync("user.invite", actor, new { user.Username });
        return (token, null);
    }

    /// <summary>Null when the role was set, otherwise why not.</summary>
    public async Task<string?> SetRoleAsync(string? userId, string? role, string? actingUserId, string actor)
    {
        if (!Roles.IsValid(role)) return "Unknown role.";
        if (userId == actingUserId) return "You cannot change your own role.";
        if (role != Roles.Administrator && userId is not null && await _users.WouldLeaveNoAdministratorAsync(userId))
            return "At least one active administrator must remain.";

        var user = await _users.FindByIdAsync(userId);
        if (user is null || !await _users.UpdateAsync(user.Id, u => u.Role = role!)) return "No such user.";

        await _audit.WriteAsync("user.role", actor, new { user.Username, role });
        return null;
    }

    /// <summary>
    /// Disables or re-enables an account. Either way the security stamp changes, which ends any open
    /// session at once. Null when done, otherwise why not.
    /// </summary>
    public async Task<string?> SetDisabledAsync(string? userId, bool disabled, string? actingUserId, string actor)
    {
        if (userId == actingUserId) return "You cannot disable your own account.";
        if (disabled && userId is not null && await _users.WouldLeaveNoAdministratorAsync(userId))
            return "At least one active administrator must remain.";

        var user = await _users.FindByIdAsync(userId);
        if (user is null) return "No such user.";

        await _users.UpdateAsync(user.Id, u =>
        {
            u.IsDisabled = disabled;
            u.SecurityStamp = Guid.NewGuid().ToString("n");
        });

        await _audit.WriteAsync(disabled ? "user.disable" : "user.enable", actor, new { user.Username });
        return null;
    }

    /// <summary>Deletes an account and its API keys. Returns the user name, or why not.</summary>
    public async Task<(string? Username, string? Error)> DeleteAsync(string? userId, string? actingUserId, string actor)
    {
        if (userId == actingUserId) return (null, "You cannot delete your own account.");

        var user = await _users.FindByIdAsync(userId);
        if (user is null) return (null, "No such user.");

        var (ok, error) = await _users.DeleteAsync(user.Id);
        if (!ok) return (null, error);

        await _apiKeys.DeleteForUserAsync(user.Id);
        await _audit.WriteAsync("user.delete", actor, new { username = user.Username });
        return (user.Username, null);
    }
}
