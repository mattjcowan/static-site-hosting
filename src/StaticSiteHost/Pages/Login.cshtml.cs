using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StaticSiteHost.Security;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly UserStore _users;
    private readonly LoginThrottle _throttle;
    private readonly AuditLog _audit;

    public LoginModel(UserStore users, LoginThrottle throttle, AuditLog audit)
    {
        _users = users;
        _throttle = throttle;
        _audit = audit;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Enter your username.")]
        [Display(Name = "Username")]
        public string Username { get; set; } = "";

        [Required(ErrorMessage = "Enter your password.")]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; } = "";
    }

    public IActionResult OnGet(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToPage("/Index");

        ReturnUrl = returnUrl;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        ReturnUrl = returnUrl;
        if (!ModelState.IsValid) return Page();

        var username = Input.Username.Trim();

        if (_throttle.IsLocked(username, out var retryAfter))
        {
            ModelState.AddModelError(string.Empty,
                $"Too many failed attempts. Try again in {Math.Ceiling(retryAfter.TotalMinutes)} minute(s).");
            return Page();
        }

        var user = await _users.FindByUsernameAsync(username);
        var passwordOk = user is not null && PasswordHasher.Verify(Input.Password, user.PasswordHash);

        if (user is null || !passwordOk || user.IsDisabled)
        {
            _throttle.RecordFailure(username);
            await _audit.WriteAsync("auth.login.failed", username, new { reason = user?.IsDisabled == true ? "disabled" : "credentials" });

            // Deliberately identical for unknown users, wrong passwords and disabled accounts.
            ModelState.AddModelError(string.Empty, "That username and password combination was not recognised.");
            return Page();
        }

        _throttle.Reset(username);
        await _users.RecordLoginAsync(user.Id);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            UserStore.CreatePrincipal(user),
            new AuthenticationProperties { IsPersistent = true });

        await _audit.WriteAsync("auth.login", user.Username);

        if (user.MustChangePassword) return RedirectToPage("/Account/Password", new { required = true });
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToPage("/Index");
    }
}
