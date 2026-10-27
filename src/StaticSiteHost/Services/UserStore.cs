using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Security;

namespace StaticSiteHost.Services;

public sealed partial class UserStore
{
    public const string MustChangePasswordClaim = "ssh:must_change_password";
    public const string SecurityStampClaim = "ssh:security_stamp";

    private readonly JsonFileStore<List<UserRecord>> _store;
    private readonly SiteHostingOptions _options;

    public UserStore(DataPaths paths, IOptions<SiteHostingOptions> options)
    {
        _store = new JsonFileStore<List<UserRecord>>(paths.UsersFile);
        _options = options.Value;
    }

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._@+-]{1,62}[A-Za-z0-9])?$")]
    private static partial Regex UsernamePattern();

    public async Task<IReadOnlyList<UserRecord>> ListAsync()
    {
        var users = await _store.ReadAsync();
        return users.OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<int> CountAsync() => (await _store.ReadAsync()).Count;

    public async Task<UserRecord?> FindByIdAsync(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var users = await _store.ReadAsync();
        return users.FirstOrDefault(u => u.Id == id);
    }

    public async Task<UserRecord?> FindByUsernameAsync(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        var users = await _store.ReadAsync();
        return users.FirstOrDefault(u => string.Equals(u.Username, username.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public async Task<UserRecord?> FindByInviteTokenAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var hash = Tokens.Sha256Hex(token);
        var users = await _store.ReadAsync();
        var user = users.FirstOrDefault(u => u.InviteTokenHash == hash);
        if (user is null || user.IsDisabled) return null;
        if (user.InviteExpiresUtc is not { } expires || expires <= DateTimeOffset.UtcNow) return null;
        return user;
    }

    public static string? ValidateUsername(string? username)
    {
        username = username?.Trim();
        if (string.IsNullOrEmpty(username)) return ServerText.Get("users.error.usernameRequired");
        if (username.Length is < 3 or > 64) return ServerText.Get("users.error.usernameLength");
        if (!UsernamePattern().IsMatch(username))
            return ServerText.Get("users.error.usernameCharacters");
        return null;
    }

    public string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password)) return ServerText.Get("users.error.passwordRequired");
        if (password.Length < _options.MinPasswordLength)
            return ServerText.Plural("users.error.passwordShort", _options.MinPasswordLength);
        if (password.Length > 256) return ServerText.Get("users.error.passwordLong");
        return null;
    }

    public async Task<(UserRecord? User, string? Error)> CreateAsync(
        string username, string? displayName, string role, string? password)
    {
        var usernameError = ValidateUsername(username);
        if (usernameError is not null) return (null, usernameError);
        if (!Roles.IsValid(role)) return (null, ServerText.Get("users.error.unknownRole"));
        if (password is not null && ValidatePassword(password) is { } passwordError) return (null, passwordError);

        username = username.Trim();

        return await _store.UpdateAsync<(UserRecord? User, string? Error)>(users =>
        {
            if (users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
                return ((null, ServerText.Get("users.error.usernameTaken")), false);

            var user = new UserRecord
            {
                Username = username,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim(),
                Role = role,
                PasswordHash = password is null ? null : PasswordHasher.Hash(password),
                MustChangePassword = password is not null
            };

            users.Add(user);
            return ((user, null), true);
        });
    }

    /// <summary>
    /// Issues a single-use invitation token and returns it in plaintext. Only the hash is stored,
    /// so the link can be shown to the administrator exactly once — at generation time.
    /// </summary>
    public async Task<string?> CreateInviteAsync(string userId)
    {
        var token = Tokens.New();
        var updated = await _store.UpdateAsync(users =>
        {
            var user = users.FirstOrDefault(u => u.Id == userId);
            if (user is null) return (false, false);

            user.InviteTokenHash = Tokens.Sha256Hex(token);
            user.InviteCreatedUtc = DateTimeOffset.UtcNow;
            user.InviteExpiresUtc = DateTimeOffset.UtcNow.AddHours(_options.InviteLifetimeHours);
            return (true, true);
        });

        return updated ? token : null;
    }

    public Task<bool> RevokeInviteAsync(string userId) =>
        UpdateAsync(userId, user =>
        {
            user.InviteTokenHash = null;
            user.InviteCreatedUtc = null;
            user.InviteExpiresUtc = null;
        });

    /// <summary>Sets a password, rotates the security stamp and clears any outstanding invitation.</summary>
    public Task<bool> SetPasswordAsync(string userId, string password, bool mustChange = false) =>
        UpdateAsync(userId, user =>
        {
            user.PasswordHash = PasswordHasher.Hash(password);
            user.MustChangePassword = mustChange;
            user.PasswordChangedUtc = DateTimeOffset.UtcNow;
            user.SecurityStamp = Guid.NewGuid().ToString("n");
            user.InviteTokenHash = null;
            user.InviteCreatedUtc = null;
            user.InviteExpiresUtc = null;
        });

    public Task<bool> ClearPasswordAsync(string userId) =>
        UpdateAsync(userId, user =>
        {
            user.PasswordHash = null;
            user.MustChangePassword = false;
            user.SecurityStamp = Guid.NewGuid().ToString("n");
        });

    public Task RecordLoginAsync(string userId) =>
        UpdateAsync(userId, user => user.LastLoginUtc = DateTimeOffset.UtcNow);

    public async Task<bool> UpdateAsync(string userId, Action<UserRecord> mutate) =>
        await _store.UpdateAsync(users =>
        {
            var user = users.FirstOrDefault(u => u.Id == userId);
            if (user is null) return (false, false);
            mutate(user);
            return (true, true);
        });

    public async Task<(bool Ok, string? Error)> DeleteAsync(string userId)
    {
        return await _store.UpdateAsync<(bool Ok, string? Error)>(users =>
        {
            var user = users.FirstOrDefault(u => u.Id == userId);
            if (user is null) return ((false, ServerText.Get("users.error.noUser")), false);
            if (user.IsBootstrap) return ((false, ServerText.Get("users.error.bootstrap")), false);
            if (user.IsAdministrator && users.Count(u => u.IsAdministrator && !u.IsDisabled) <= 1)
                return ((false, ServerText.Get("users.error.lastAdministrator")), false);

            users.Remove(user);
            return ((true, null), true);
        });
    }

    /// <summary>Guards against locking everyone out by demoting or disabling the last administrator.</summary>
    public async Task<bool> WouldLeaveNoAdministratorAsync(string userId)
    {
        var users = await _store.ReadAsync();
        var target = users.FirstOrDefault(u => u.Id == userId);
        if (target is null || !target.IsAdministrator || target.IsDisabled) return false;
        return users.Count(u => u.IsAdministrator && !u.IsDisabled) <= 1;
    }

    public static ClaimsPrincipal CreatePrincipal(UserRecord user, string authenticationScheme = CookieAuthenticationDefaults.AuthenticationScheme)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim("display_name", user.DisplayName),
                new Claim(SecurityStampClaim, user.SecurityStamp),
                new Claim(MustChangePasswordClaim, user.MustChangePassword ? "true" : "false")
            ],
            authenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);

        return new ClaimsPrincipal(identity);
    }
}
