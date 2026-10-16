using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using StaticSiteHost.Localization;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages;

/// <summary>
/// The landing page for the private link an administrator hands to a new user.
/// The token is single-use: setting a password clears it.
/// </summary>
[AllowAnonymous]
public class ActivateModel : PageModel
{
    private readonly UserStore _users;
    private readonly AuditLog _audit;
    private readonly IStringLocalizer<Strings> _strings;

    public ActivateModel(UserStore users, AuditLog audit, IStringLocalizer<Strings> strings)
    {
        _users = users;
        _audit = audit;
        _strings = strings;
    }

    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? Username { get; private set; }

    public bool TokenIsValid { get; private set; }

    /// <summary>Both are required and must match: <see cref="OnPostAsync"/> checks them, so the messages are in the viewer's language.</summary>
    public class InputModel
    {
        [DataType(DataType.Password)]
        public string? Password { get; set; } = "";

        [DataType(DataType.Password)]
        public string? ConfirmPassword { get; set; } = "";
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await LoadAsync();
        if (user is null) return Page();

        if (string.IsNullOrWhiteSpace(Input.Password)) ModelState.AddModelError("Input.Password", _strings["auth.activate.noPassword"]);
        if (string.IsNullOrWhiteSpace(Input.ConfirmPassword)) ModelState.AddModelError("Input.ConfirmPassword", _strings["auth.activate.noConfirm"]);
        if (!string.Equals(Input.ConfirmPassword, Input.Password, StringComparison.Ordinal))
            ModelState.AddModelError("Input.ConfirmPassword", _strings["auth.activate.mismatch"]);

        if (_users.ValidatePassword(Input.Password) is { } passwordError)
            ModelState.AddModelError("Input.Password", passwordError);

        if (!ModelState.IsValid) return Page();

        await _users.SetPasswordAsync(user.Id, Input.Password!);
        await _audit.WriteAsync("user.activate", user.Username);

        var refreshed = await _users.FindByIdAsync(user.Id);
        if (refreshed is null) return RedirectToPage("/Login");

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            UserStore.CreatePrincipal(refreshed),
            new AuthenticationProperties { IsPersistent = true });

        TempData["StatusMessage"] = _strings.Text("auth.activate.done");
        return RedirectToPage("/Index");
    }

    private async Task<Models.UserRecord?> LoadAsync()
    {
        var user = await _users.FindByInviteTokenAsync(Token);
        TokenIsValid = user is not null;
        Username = user?.Username;
        return user;
    }
}
