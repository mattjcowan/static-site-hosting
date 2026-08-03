using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StaticSiteHost.Models;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages.Account;

public class IndexModel : PageModel
{
    private readonly UserStore _users;
    private readonly ApiKeyStore _apiKeys;
    private readonly AuditLog _audit;

    public IndexModel(UserStore users, ApiKeyStore apiKeys, AuditLog audit)
    {
        _users = users;
        _apiKeys = apiKeys;
        _audit = audit;
    }

    public UserRecord Account { get; private set; } = null!;

    public IReadOnlyList<ApiKeyRecord> ApiKeys { get; private set; } = [];

    /// <summary>Shown once, immediately after creation — the secret is not recoverable later.</summary>
    public string? NewApiKey { get; private set; }

    private string? UserId => User.FindFirstValue(ClaimTypes.NameIdentifier);

    public async Task<IActionResult> OnGetAsync()
    {
        NewApiKey = TempData["NewApiKey"] as string;
        return await LoadAsync() ? Page() : SignOutRedirect();
    }

    public async Task<IActionResult> OnPostProfileAsync(string? displayName)
    {
        if (UserId is null) return SignOutRedirect();

        displayName = displayName?.Trim();
        if (string.IsNullOrEmpty(displayName))
        {
            TempData["ErrorMessage"] = "Enter a display name.";
            return RedirectToPage();
        }

        if (displayName.Length > 128)
        {
            TempData["ErrorMessage"] = "Display names must be 128 characters or fewer.";
            return RedirectToPage();
        }

        await _users.UpdateAsync(UserId, user => user.DisplayName = displayName);
        TempData["StatusMessage"] = "Your display name was updated.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateKeyAsync(string? name, int? expiresInDays)
    {
        if (UserId is null) return SignOutRedirect();

        if (expiresInDays is < 1 or > 3650)
        {
            TempData["ErrorMessage"] = "Expiry must be between 1 and 3650 days, or left blank.";
            return RedirectToPage();
        }

        var expires = expiresInDays is { } days ? DateTimeOffset.UtcNow.AddDays(days) : (DateTimeOffset?)null;
        var (record, plainText) = await _apiKeys.CreateAsync(UserId, name ?? "API key", expires);

        await _audit.WriteAsync("apikey.create", User.Identity?.Name, new { keyId = record.Id, record.Name });

        TempData["NewApiKey"] = plainText;
        TempData["StatusMessage"] = "API key created. Copy it now — it is not shown again.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeKeyAsync(string keyId)
    {
        if (UserId is null) return SignOutRedirect();

        if (await _apiKeys.RevokeAsync(keyId, UserId))
        {
            await _audit.WriteAsync("apikey.revoke", User.Identity?.Name, new { keyId });
            TempData["StatusMessage"] = "That API key was revoked.";
        }
        else
        {
            TempData["ErrorMessage"] = "That API key could not be revoked.";
        }

        return RedirectToPage();
    }

    private async Task<bool> LoadAsync()
    {
        var user = await _users.FindByIdAsync(UserId);
        if (user is null) return false;

        Account = user;
        ApiKeys = await _apiKeys.ListForUserAsync(user.Id);
        return true;
    }

    private IActionResult SignOutRedirect() => RedirectToPage("/Logout");
}
