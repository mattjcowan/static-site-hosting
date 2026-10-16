using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using StaticSiteHost.Localization;
using StaticSiteHost.Security;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly UserStore _users;
    private readonly LoginThrottle _throttle;
    private readonly AuditLog _audit;
    private readonly IStringLocalizer<Strings> _strings;

    public LoginModel(UserStore users, LoginThrottle throttle, AuditLog audit, IStringLocalizer<Strings> strings)
    {
        _users = users;
        _throttle = throttle;
        _audit = audit;
        _strings = strings;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    /// <summary>Both are required: <see cref="OnPostAsync"/> checks them, so the messages are in the viewer's language.</summary>
    public class InputModel
    {
        public string? Username { get; set; } = "";

        [DataType(DataType.Password)]
        public string? Password { get; set; } = "";
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
        if (string.IsNullOrWhiteSpace(Input.Username)) ModelState.AddModelError("Input.Username", _strings["auth.signIn.noUsername"]);
        if (string.IsNullOrWhiteSpace(Input.Password)) ModelState.AddModelError("Input.Password", _strings["auth.signIn.noPassword"]);
        if (!ModelState.IsValid) return Page();

        var username = Input.Username!.Trim();
        var address = HttpContext.Connection.RemoteIpAddress;

        if (_throttle.IsLocked(username, address, out var retryAfter))
        {
            await _audit.WriteAsync("auth.login.throttled", username, new { ip = address?.ToString() });
            ModelState.AddModelError(string.Empty,
                _strings.Plural("auth.signIn.locked", (long)Math.Ceiling(retryAfter.TotalMinutes)));
            return Page();
        }

        var user = await _users.FindByUsernameAsync(username);

        // Always verify against something, so an unknown username and an account that has
        // not set a password yet cost the same time as a real check.
        var passwordOk = PasswordHasher.Verify(Input.Password!, user?.PasswordHash ?? PasswordHasher.PlaceholderHash);

        if (user is null || !passwordOk || user.IsDisabled)
        {
            _throttle.RecordFailure(username, address);
            await _audit.WriteAsync("auth.login.failed", username, new
            {
                ip = address?.ToString(),
                reason = user is null ? "unknown" : user.IsDisabled ? "disabled" : "credentials"
            });

            // Escalating delay, so each further guess against this account costs more than
            // the last. Async, so the wait holds no thread.
            var backoff = _throttle.GetBackoff(username);
            if (backoff > TimeSpan.Zero) await Task.Delay(backoff, HttpContext.RequestAborted);

            // Deliberately identical for unknown users, wrong passwords and disabled accounts.
            ModelState.AddModelError(string.Empty, _strings["auth.signIn.failed"]);
            return Page();
        }

        _throttle.Reset(username, address);
        await _users.RecordLoginAsync(user.Id);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            UserStore.CreatePrincipal(user),
            new AuthenticationProperties { IsPersistent = true });

        await _audit.WriteAsync("auth.login", user.Username, new { ip = address?.ToString() });

        if (user.MustChangePassword) return RedirectToPage("/Account/Password", new { required = true });
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToPage("/Index");
    }
}
