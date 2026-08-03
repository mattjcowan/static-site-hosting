using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Models;
using StaticSiteHost.Security;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages.Admin;

public class UsersModel : PageModel
{
    private readonly UserStore _users;
    private readonly ApiKeyStore _apiKeys;
    private readonly AuditLog _audit;
    private readonly SiteHostingOptions _options;

    public UsersModel(UserStore users, ApiKeyStore apiKeys, AuditLog audit, IOptions<SiteHostingOptions> options)
    {
        _users = users;
        _apiKeys = apiKeys;
        _audit = audit;
        _options = options.Value;
    }

    public IReadOnlyList<UserRecord> Users { get; private set; } = [];

    /// <summary>Set right after an invitation is generated, so the administrator can copy the link.</summary>
    public string? InviteLink { get; private set; }

    public string? TemporaryPassword { get; private set; }

    public string? SecretFor { get; private set; }

    public int InviteLifetimeHours => _options.InviteLifetimeHours;

    private string? CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier);

    public async Task OnGetAsync()
    {
        InviteLink = TempData["InviteLink"] as string;
        TemporaryPassword = TempData["TemporaryPassword"] as string;
        SecretFor = TempData["SecretFor"] as string;
        Users = await _users.ListAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync(string? username, string? displayName, string? role, string? credential)
    {
        role = Roles.IsValid(role) ? role! : Roles.Member;
        var useTemporaryPassword = credential == "password";
        var password = useTemporaryPassword ? Tokens.GeneratePassword() : null;

        var (user, error) = await _users.CreateAsync(username ?? "", displayName, role, password);
        if (user is null)
        {
            TempData["ErrorMessage"] = error;
            return RedirectToPage();
        }

        await _audit.WriteAsync("user.create", User.Identity?.Name, new { user.Username, user.Role, credential });

        if (useTemporaryPassword)
        {
            TempData["TemporaryPassword"] = password;
            TempData["SecretFor"] = user.Username;
            TempData["StatusMessage"] = $"{user.Username} was created. Share the temporary password below — they must change it at first sign-in.";
        }
        else
        {
            await IssueInviteAsync(user, $"{user.Username} was created. Send them the private link below.");
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostInviteAsync(string userId)
    {
        var user = await _users.FindByIdAsync(userId);
        if (user is null)
        {
            TempData["ErrorMessage"] = "No such user.";
            return RedirectToPage();
        }

        await IssueInviteAsync(user, $"A new private link for {user.Username} is ready to send.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRoleAsync(string userId, string role)
    {
        if (!Roles.IsValid(role))
        {
            TempData["ErrorMessage"] = "Unknown role.";
            return RedirectToPage();
        }

        if (userId == CurrentUserId)
        {
            TempData["ErrorMessage"] = "You cannot change your own role.";
            return RedirectToPage();
        }

        if (role != Roles.Administrator && await _users.WouldLeaveNoAdministratorAsync(userId))
        {
            TempData["ErrorMessage"] = "At least one active administrator must remain.";
            return RedirectToPage();
        }

        var user = await _users.FindByIdAsync(userId);
        if (user is null || !await _users.UpdateAsync(userId, u => u.Role = role))
        {
            TempData["ErrorMessage"] = "No such user.";
            return RedirectToPage();
        }

        await _audit.WriteAsync("user.role", User.Identity?.Name, new { user.Username, role });
        TempData["StatusMessage"] = $"{user.Username} is now a{(role == Roles.Administrator ? "n" : "")} {role}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDisableAsync(string userId, bool disabled)
    {
        if (userId == CurrentUserId)
        {
            TempData["ErrorMessage"] = "You cannot disable your own account.";
            return RedirectToPage();
        }

        if (disabled && await _users.WouldLeaveNoAdministratorAsync(userId))
        {
            TempData["ErrorMessage"] = "At least one active administrator must remain.";
            return RedirectToPage();
        }

        var user = await _users.FindByIdAsync(userId);
        if (user is null)
        {
            TempData["ErrorMessage"] = "No such user.";
            return RedirectToPage();
        }

        // Disabling rotates the security stamp, which drops any open session immediately.
        await _users.UpdateAsync(userId, u =>
        {
            u.IsDisabled = disabled;
            u.SecurityStamp = Guid.NewGuid().ToString("n");
        });

        await _audit.WriteAsync(disabled ? "user.disable" : "user.enable", User.Identity?.Name, new { user.Username });
        TempData["StatusMessage"] = $"{user.Username} was {(disabled ? "disabled" : "re-enabled")}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string userId)
    {
        if (userId == CurrentUserId)
        {
            TempData["ErrorMessage"] = "You cannot delete your own account.";
            return RedirectToPage();
        }

        var user = await _users.FindByIdAsync(userId);
        var (ok, error) = await _users.DeleteAsync(userId);

        if (!ok)
        {
            TempData["ErrorMessage"] = error;
            return RedirectToPage();
        }

        await _apiKeys.DeleteForUserAsync(userId);
        await _audit.WriteAsync("user.delete", User.Identity?.Name, new { username = user?.Username });
        TempData["StatusMessage"] = $"{user?.Username} and their API keys were removed.";
        return RedirectToPage();
    }

    private async Task IssueInviteAsync(UserRecord user, string statusMessage)
    {
        var token = await _users.CreateInviteAsync(user.Id);
        if (token is null)
        {
            TempData["ErrorMessage"] = "The invitation could not be created.";
            return;
        }

        TempData["InviteLink"] = Url.Page("/Activate", null, new { token }, Request.Scheme, Request.Host.Value);
        TempData["SecretFor"] = user.Username;
        TempData["StatusMessage"] = statusMessage;

        await _audit.WriteAsync("user.invite", User.Identity?.Name, new { user.Username });
    }
}
