using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StaticSiteHost.Security;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages.Account;

public class PasswordModel : PageModel
{
    private readonly UserStore _users;
    private readonly AuditLog _audit;

    public PasswordModel(UserStore users, AuditLog audit)
    {
        _users = users;
        _audit = audit;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>True when an administrator assigned this password and it has to be replaced.</summary>
    [BindProperty(SupportsGet = true)]
    public bool Required { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Enter your current password.")]
        [DataType(DataType.Password)]
        public string CurrentPassword { get; set; } = "";

        [Required(ErrorMessage = "Choose a new password.")]
        [DataType(DataType.Password)]
        public string NewPassword { get; set; } = "";

        [Required(ErrorMessage = "Confirm your new password.")]
        [DataType(DataType.Password)]
        [Compare(nameof(NewPassword), ErrorMessage = "The two passwords do not match.")]
        public string ConfirmPassword { get; set; } = "";
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await _users.FindByIdAsync(userId);
        if (user is null) return RedirectToPage("/Logout");

        if (!ModelState.IsValid) return Page();

        if (!PasswordHasher.Verify(Input.CurrentPassword, user.PasswordHash))
        {
            ModelState.AddModelError("Input.CurrentPassword", "That is not your current password.");
            return Page();
        }

        if (_users.ValidatePassword(Input.NewPassword) is { } error)
        {
            ModelState.AddModelError("Input.NewPassword", error);
            return Page();
        }

        if (PasswordHasher.Verify(Input.NewPassword, user.PasswordHash))
        {
            ModelState.AddModelError("Input.NewPassword", "Choose a password you have not used here before.");
            return Page();
        }

        await _users.SetPasswordAsync(user.Id, Input.NewPassword);
        await _audit.WriteAsync("user.password.change", user.Username);

        // The security stamp just rotated, so the current cookie is no longer valid.
        var refreshed = await _users.FindByIdAsync(user.Id);
        if (refreshed is null) return RedirectToPage("/Logout");

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            UserStore.CreatePrincipal(refreshed),
            new AuthenticationProperties { IsPersistent = true });

        TempData["StatusMessage"] = "Your password was changed.";
        return RedirectToPage("/Index");
    }
}
