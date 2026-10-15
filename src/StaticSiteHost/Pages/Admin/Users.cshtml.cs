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
    private readonly UserAdminService _admin;
    private readonly SiteHostingOptions _options;

    public UsersModel(UserStore users, UserAdminService admin, IOptions<SiteHostingOptions> options)
    {
        _users = users;
        _admin = admin;
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
        var (created, error) = await _admin.CreateAsync(username, displayName, role, credential == "password", Actor);
        if (created is null)
        {
            TempData["ErrorMessage"] = error;
            return RedirectToPage();
        }

        TempData["SecretFor"] = created.User.Username;
        if (created.TemporaryPassword is not null)
        {
            TempData["TemporaryPassword"] = created.TemporaryPassword;
            TempData["StatusMessage"] = $"{created.User.Username} was created. Share the temporary password below — they must change it at first sign-in.";
        }
        else
        {
            TempData["InviteLink"] = ActivationLink(created.InviteToken!);
            TempData["StatusMessage"] = $"{created.User.Username} was created. Send them the private link below.";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostInviteAsync(string userId)
    {
        var (token, error) = await _admin.InviteAsync(userId, Actor);
        if (token is null)
        {
            TempData["ErrorMessage"] = error;
            return RedirectToPage();
        }

        var user = await _users.FindByIdAsync(userId);
        TempData["InviteLink"] = ActivationLink(token);
        TempData["SecretFor"] = user?.Username;
        TempData["StatusMessage"] = $"A new private link for {user?.Username} is ready to send.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRoleAsync(string userId, string role)
    {
        if (await _admin.SetRoleAsync(userId, role, CurrentUserId, Actor) is { } error)
        {
            TempData["ErrorMessage"] = error;
            return RedirectToPage();
        }

        var user = await _users.FindByIdAsync(userId);
        TempData["StatusMessage"] = $"{user?.Username} is now a{(role == Roles.Administrator ? "n" : "")} {role}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDisableAsync(string userId, bool disabled)
    {
        if (await _admin.SetDisabledAsync(userId, disabled, CurrentUserId, Actor) is { } error)
        {
            TempData["ErrorMessage"] = error;
            return RedirectToPage();
        }

        var user = await _users.FindByIdAsync(userId);
        TempData["StatusMessage"] = $"{user?.Username} was {(disabled ? "disabled" : "re-enabled")}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string userId)
    {
        var (username, error) = await _admin.DeleteAsync(userId, CurrentUserId, Actor);

        if (username is null) TempData["ErrorMessage"] = error;
        else TempData["StatusMessage"] = $"{username} and their API keys were removed.";

        return RedirectToPage();
    }

    private string Actor => User.Identity?.Name ?? "unknown";

    private string? ActivationLink(string token) =>
        Url.Page("/Activate", null, new { token }, Request.Scheme, Request.Host.Value);
}
