using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using StaticSiteHost.Localization;
using StaticSiteHost.Security;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages.Account;

public class PasswordModel : PageModel
{
    private readonly UserStore _users;
    private readonly AuditLog _audit;
    private readonly IStringLocalizer<Strings> _strings;

    public PasswordModel(UserStore users, AuditLog audit, IStringLocalizer<Strings> strings)
    {
        _users = users;
        _audit = audit;
        _strings = strings;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>True when an administrator assigned this password and it has to be replaced.</summary>
    [BindProperty(SupportsGet = true)]
    public bool Required { get; set; }

    /// <summary>All three are required, and the new password and its confirmation must match: <see cref="OnPostAsync"/> checks them, so the messages are in the viewer's language.</summary>
    public class InputModel
    {
        [DataType(DataType.Password)]
        public string? CurrentPassword { get; set; } = "";

        [DataType(DataType.Password)]
        public string? NewPassword { get; set; } = "";

        [DataType(DataType.Password)]
        public string? ConfirmPassword { get; set; } = "";
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await _users.FindByIdAsync(userId);
        if (user is null) return RedirectToPage("/Logout");

        if (string.IsNullOrWhiteSpace(Input.CurrentPassword)) ModelState.AddModelError("Input.CurrentPassword", _strings["account.changePassword.noCurrent"]);
        if (string.IsNullOrWhiteSpace(Input.NewPassword)) ModelState.AddModelError("Input.NewPassword", _strings["account.changePassword.noNew"]);
        if (string.IsNullOrWhiteSpace(Input.ConfirmPassword)) ModelState.AddModelError("Input.ConfirmPassword", _strings["account.changePassword.noConfirm"]);
        if (!string.Equals(Input.ConfirmPassword, Input.NewPassword, StringComparison.Ordinal))
            ModelState.AddModelError("Input.ConfirmPassword", _strings["account.changePassword.mismatch"]);

        if (!ModelState.IsValid) return Page();

        if (!PasswordHasher.Verify(Input.CurrentPassword!, user.PasswordHash))
        {
            ModelState.AddModelError("Input.CurrentPassword", _strings["account.changePassword.wrongCurrent"]);
            return Page();
        }

        if (_users.ValidatePassword(Input.NewPassword) is { } error)
        {
            ModelState.AddModelError("Input.NewPassword", error);
            return Page();
        }

        if (PasswordHasher.Verify(Input.NewPassword!, user.PasswordHash))
        {
            ModelState.AddModelError("Input.NewPassword", _strings["account.changePassword.same"]);
            return Page();
        }

        await _users.SetPasswordAsync(user.Id, Input.NewPassword!);
        await _audit.WriteAsync("user.password.change", user.Username);

        // The security stamp just rotated, so the current cookie is no longer valid.
        var refreshed = await _users.FindByIdAsync(user.Id);
        if (refreshed is null) return RedirectToPage("/Logout");

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            UserStore.CreatePrincipal(refreshed),
            new AuthenticationProperties { IsPersistent = true });

        TempData["StatusMessage"] = _strings.Text("account.changePassword.done");
        return RedirectToPage("/Index");
    }
}
