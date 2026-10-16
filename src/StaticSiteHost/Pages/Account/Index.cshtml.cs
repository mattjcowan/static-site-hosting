using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages.Account;

public class IndexModel : PageModel
{
    private readonly UserStore _users;
    private readonly ApiKeyStore _apiKeys;
    private readonly AuditLog _audit;
    private readonly IStringLocalizer<Strings> _strings;

    public IndexModel(UserStore users, ApiKeyStore apiKeys, AuditLog audit, IStringLocalizer<Strings> strings)
    {
        _users = users;
        _apiKeys = apiKeys;
        _audit = audit;
        _strings = strings;
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
            TempData["ErrorMessage"] = _strings.Text("account.profile.noName");
            return RedirectToPage();
        }

        if (displayName.Length > 128)
        {
            TempData["ErrorMessage"] = _strings.Text("account.profile.tooLong", new { max = 128 });
            return RedirectToPage();
        }

        await _users.UpdateAsync(UserId, user => user.DisplayName = displayName);
        TempData["StatusMessage"] = _strings.Text("account.profile.saved", new { name = displayName });
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateKeyAsync(string? name, int? expiresInDays)
    {
        if (UserId is null) return SignOutRedirect();

        if (expiresInDays is < 1 or > 3650)
        {
            TempData["ErrorMessage"] = _strings.Text("account.keys.badExpiry", new { min = 1, max = 3650 });
            return RedirectToPage();
        }

        var expires = expiresInDays is { } days ? DateTimeOffset.UtcNow.AddDays(days) : (DateTimeOffset?)null;
        var (record, plainText) = await _apiKeys.CreateAsync(UserId, name ?? "API key", expires);

        await _audit.WriteAsync("apikey.create", User.Identity?.Name, new { keyId = record.Id, record.Name });

        TempData["NewApiKey"] = plainText;
        TempData["StatusMessage"] = _strings.Text("account.keys.created", new { name = record.Name });
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeKeyAsync(string keyId)
    {
        if (UserId is null) return SignOutRedirect();

        if (await _apiKeys.RevokeAsync(keyId, UserId))
        {
            await _audit.WriteAsync("apikey.revoke", User.Identity?.Name, new { keyId });
            TempData["StatusMessage"] = _strings.Text("account.keys.revoked");
        }
        else
        {
            TempData["ErrorMessage"] = _strings.Text("account.keys.revokeFailed");
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
