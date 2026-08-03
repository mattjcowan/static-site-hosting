using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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

    public ActivateModel(UserStore users, AuditLog audit)
    {
        _users = users;
        _audit = audit;
    }

    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? Username { get; private set; }

    public bool TokenIsValid { get; private set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Choose a password.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = "";

        [Required(ErrorMessage = "Confirm your password.")]
        [DataType(DataType.Password)]
        [Compare(nameof(Password), ErrorMessage = "The two passwords do not match.")]
        public string ConfirmPassword { get; set; } = "";
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

        if (_users.ValidatePassword(Input.Password) is { } passwordError)
            ModelState.AddModelError("Input.Password", passwordError);

        if (!ModelState.IsValid) return Page();

        await _users.SetPasswordAsync(user.Id, Input.Password);
        await _audit.WriteAsync("user.activate", user.Username);

        var refreshed = await _users.FindByIdAsync(user.Id);
        if (refreshed is null) return RedirectToPage("/Login");

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            UserStore.CreatePrincipal(refreshed),
            new AuthenticationProperties { IsPersistent = true });

        TempData["StatusMessage"] = "Your password is set. Welcome aboard.";
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
