using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Models;
using StaticSiteHost.Security;
using StaticSiteHost.Services;

namespace StaticSiteHost.Serving;

/// <summary>
/// Everything about a site's visitor passcode lives here: how it is set from the
/// management UI or the API, and the gate a visitor meets when one is set.
///
/// A protected site serves nothing — not a page, not an asset — until the visitor posts
/// the passcode. Success issues a cookie holding a data-protected fingerprint of the
/// stored hash, so replacing or removing the passcode silently invalidates every session
/// that was unlocked with the old one. The cookie carries no Domain attribute, which
/// makes it host-only: unlocking one private site never unlocks another.
///
/// This is a visibility gate, not a vault. It keeps a link that leaks out of being
/// readable by whoever finds it; it is not a substitute for accounts on content that
/// would be damaging to disclose.
/// </summary>
public sealed class SitePasscodeGate
{
    /// <summary>Where the unlock form posts. Only POSTs to it are intercepted, so a
    /// site that happens to ship a file at this path is still reachable.</summary>
    public const string UnlockPath = "/__passcode";

    private const string CookieName = "ssh.pass";
    private const string ProtectorPurpose = "StaticSiteHost.SitePasscode.v1";
    private const string PasscodeField = "passcode";
    private const string ReturnUrlField = "r";
    private const int MaxPasscodeLength = 200;

    private readonly ConcurrentDictionary<string, ITimeLimitedDataProtector> _protectors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Counters of its own rather than the sign-in throttle: a visitor fumbling a site
    /// passcode must not push their office address towards an administrator lockout.
    /// Keyed per domain *and* client address, so one impatient visitor cannot shut a
    /// private site to everybody else.
    /// </summary>
    private readonly LoginThrottle _throttle = new();

    private readonly IDataProtectionProvider _protection;
    private readonly SiteStore _sites;
    private readonly AuditLog _audit;
    private readonly SiteHostingOptions _options;
    private readonly ILogger<SitePasscodeGate> _logger;

    public SitePasscodeGate(
        IDataProtectionProvider protection,
        SiteStore sites,
        AuditLog audit,
        IOptions<SiteHostingOptions> options,
        ILogger<SitePasscodeGate> logger)
    {
        _protection = protection;
        _sites = sites;
        _audit = audit;
        _options = options.Value;
        _logger = logger;
    }

    public int MinLength => Math.Max(1, _options.MinPasscodeLength);

    /// <summary>How long a visitor stays unlocked after entering the passcode.</summary>
    public TimeSpan SessionLifetime => TimeSpan.FromHours(Math.Clamp(_options.PasscodeSessionHours, 1, 24 * 365));

    // ---- request handling ---------------------------------------------------

    /// <summary>
    /// Returns true when the gate answered the request and the site's content must not be
    /// served — either the unlock form was submitted, or the visitor has yet to unlock. Under
    /// the host's reserved <c>/_host/</c> prefix a locked visitor gets a 401 with a JSON body
    /// instead of the form: the caller there is a script, which can do nothing with a form.
    /// </summary>
    public async Task<bool> TryHandleAsync(HttpContext context, SiteRecord site)
    {
        if (!site.IsPasscodeProtected) return false;

        var request = context.Request;

        if (HttpMethods.IsPost(request.Method) &&
            string.Equals(request.Path.Value, UnlockPath, StringComparison.OrdinalIgnoreCase))
        {
            await UnlockAsync(context, site);
            return true;
        }

        if (IsUnlocked(context, site)) return false;

        if (SiteHostEndpoints.IsReserved(request.Path)) await RefuseScriptAsync(context);
        else await ChallengeAsync(context, site, StatusCodes.Status401Unauthorized, null, CurrentUrl(request));

        return true;
    }

    public bool IsUnlocked(HttpContext context, SiteRecord site)
    {
        if (!site.IsPasscodeProtected) return true;

        var cookie = context.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(cookie)) return false;

        try
        {
            return Tokens.FixedTimeEquals(Protector(site.Domain).Unprotect(cookie), Stamp(site));
        }
        catch (CryptographicException)
        {
            // Tampered, expired, or issued under a passcode that has since been replaced.
            return false;
        }
    }

    private async Task UnlockAsync(HttpContext context, SiteRecord site)
    {
        var request = context.Request;
        var address = context.Connection.RemoteIpAddress;
        var throttleKey = $"{site.Domain}|{address}";

        string? passcode = null;
        var returnUrl = "/";

        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(context.RequestAborted);
            passcode = form[PasscodeField];
            returnUrl = SafeReturnUrl(form[ReturnUrlField]);
        }

        if (_throttle.IsLocked(throttleKey, address, out var retryAfter))
        {
            context.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            await ChallengeAsync(context, site, StatusCodes.Status429TooManyRequests,
                $"Too many attempts. Try again in {Math.Ceiling(retryAfter.TotalMinutes)} minute(s).", returnUrl);
            return;
        }

        var accepted = passcode is { Length: > 0 and <= MaxPasscodeLength } &&
                       PasswordHasher.Verify(passcode.Trim(), site.PasscodeHash);

        if (!accepted)
        {
            _throttle.RecordFailure(throttleKey, address);
            _logger.LogInformation("Rejected passcode for {Domain} from {Address}", site.Domain, address);

            // Escalating delay, so serial guessing costs more with every attempt.
            var backoff = _throttle.GetBackoff(throttleKey);
            if (backoff > TimeSpan.Zero) await Task.Delay(backoff, context.RequestAborted);

            await ChallengeAsync(context, site, StatusCodes.Status401Unauthorized,
                "That passcode was not recognised.", returnUrl);
            return;
        }

        _throttle.Reset(throttleKey, address);

        var lifetime = SessionLifetime;
        context.Response.Cookies.Append(CookieName, Protector(site.Domain).Protect(Stamp(site), lifetime), new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = request.IsHttps,
            Path = "/",
            MaxAge = lifetime
        });

        context.Response.Redirect(returnUrl);
    }

    // ---- administration -----------------------------------------------------

    public string? Validate(string? passcode)
    {
        passcode = passcode?.Trim();

        if (string.IsNullOrEmpty(passcode)) return "A passcode is required.";
        if (passcode.Length < MinLength) return $"Passcodes must be at least {MinLength} characters.";
        if (passcode.Length > MaxPasscodeLength) return $"Passcodes must be {MaxPasscodeLength} characters or fewer.";
        return null;
    }

    /// <summary>
    /// Stores a new passcode. Surrounding whitespace is trimmed here and on the way in, so
    /// a value that picks up a stray space when it is copied still works.
    /// </summary>
    public async Task<(bool Ok, string? Error)> SetAsync(SiteRecord site, string? passcode, string actor)
    {
        if (Validate(passcode) is { } error) return (false, error);

        site.PasscodeHash = PasswordHasher.Hash(passcode!.Trim());
        site.PasscodeSetUtc = DateTimeOffset.UtcNow;
        site.PasscodeSetBy = actor;

        await _sites.SaveAsync(site);
        await _audit.WriteAsync("site.passcode.set", actor, new { domain = site.Domain });
        return (true, null);
    }

    /// <summary>Makes the site public again. Returns false when it already was.</summary>
    public async Task<bool> ClearAsync(SiteRecord site, string actor)
    {
        if (!site.IsPasscodeProtected) return false;

        site.PasscodeHash = null;
        site.PasscodeSetUtc = null;
        site.PasscodeSetBy = null;

        await _sites.SaveAsync(site);
        await _audit.WriteAsync("site.passcode.clear", actor, new { domain = site.Domain });
        return true;
    }

    // ---- helpers ------------------------------------------------------------

    private ITimeLimitedDataProtector Protector(string domain) =>
        _protectors.GetOrAdd(domain, d => _protection.CreateProtector(ProtectorPurpose, d).ToTimeLimitedDataProtector());

    /// <summary>
    /// Fingerprint of the stored hash. The hash carries a random salt, so setting a
    /// passcode — even the same one again — produces a new fingerprint and every cookie
    /// issued under the previous value stops matching.
    /// </summary>
    private static string Stamp(SiteRecord site) => Tokens.Sha256Hex(site.PasscodeHash!)[..16];

    /// <summary>
    /// Where to send the visitor once they unlock. Re-encoded rather than taken from the
    /// decoded path, so a name holding a '?' or a quote survives the round trip intact.
    /// </summary>
    private static string CurrentUrl(HttpRequest request) =>
        string.Equals(request.Path.Value, UnlockPath, StringComparison.OrdinalIgnoreCase)
            ? "/"
            : SafeReturnUrl(request.Path.ToUriComponent() + request.QueryString.ToUriComponent());

    /// <summary>
    /// Only a path on this same site is ever redirected to — never an absolute URL, a
    /// protocol-relative one, or anything carrying control characters.
    /// </summary>
    private static string SafeReturnUrl(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2048) return "/";
        if (value[0] != '/') return "/";
        if (value.Length > 1 && (value[1] == '/' || value[1] == '\\')) return "/";
        foreach (var c in value)
        {
            if (char.IsControl(c)) return "/";
        }

        return value;
    }

    /// <summary>The locked answer under <c>/_host/</c>: the form's status and caching rules, with a JSON body.</summary>
    private static async Task RefuseScriptAsync(HttpContext context)
    {
        if (context.Response.HasStarted) return;

        var response = context.Response;
        response.StatusCode = StatusCodes.Status401Unauthorized;
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        SiteHostEndpoints.MarkSameOrigin(response);

        await response.WriteAsJsonAsync(new { error = "This site needs a passcode." }, context.RequestAborted);
    }

    private static async Task ChallengeAsync(
        HttpContext context, SiteRecord site, int statusCode, string? error, string returnUrl)
    {
        if (context.Response.HasStarted) return;

        var response = context.Response;
        response.StatusCode = statusCode;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";

        var alert = error is null
            ? ""
            : $"""<p class="error" role="alert">{WebUtility.HtmlEncode(error)}</p>""";

        var html = $$"""
            <!doctype html>
            <html lang="en">
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex, nofollow">
            <title>Passcode required — {{WebUtility.HtmlEncode(site.Domain)}}</title>
            <style>
              :root {
                color-scheme: light dark;
                --bg: #f6f7f9; --surface: #fff; --border: #dfe3ea; --text: #1a1d24;
                --muted: #626b7d; --accent: #3b5bdb; --accent-text: #fff;
                --danger: #c92a2a; --danger-soft: #fff0f0;
              }
              @media (prefers-color-scheme: dark) {
                :root {
                  --bg: #0f1115; --surface: #171a21; --border: #2a2f3a; --text: #e4e7ee;
                  --muted: #8b93a5; --accent: #6b8afd; --accent-text: #0f1115;
                  --danger: #ff8787; --danger-soft: #2a1a1c;
                }
              }
              * { box-sizing: border-box; }
              body { margin: 0; min-height: 100vh; display: grid; place-items: center; padding: 1.5rem;
                     background: var(--bg); color: var(--text);
                     font: 15px/1.6 ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
              main { width: 100%; max-width: 24rem; }
              .head { text-align: center; margin-bottom: 1.25rem; }
              .head svg { width: 30px; height: 30px; color: var(--muted); }
              h1 { font-size: 1.35rem; margin: 0.4rem 0 0.25rem; letter-spacing: -0.02em; }
              .head p { margin: 0; color: var(--muted); font-size: 0.9rem; word-break: break-all; }
              form { background: var(--surface); border: 1px solid var(--border); border-radius: 10px; padding: 1.25rem; }
              label { display: block; font-weight: 550; font-size: 0.875rem; margin-bottom: 0.35rem; }
              input { width: 100%; padding: 0.55rem 0.7rem; border: 1px solid var(--border); border-radius: 8px;
                      background: var(--bg); color: var(--text); font: inherit; }
              input:focus-visible, button:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
              button { width: 100%; margin-top: 1rem; padding: 0.55rem 0.9rem; border: 1px solid var(--accent);
                       border-radius: 8px; background: var(--accent); color: var(--accent-text);
                       font: inherit; font-weight: 550; cursor: pointer; }
              button:hover { filter: brightness(1.08); }
              .error { margin: 0 0 1rem; padding: 0.6rem 0.8rem; border-radius: 8px; font-size: 0.875rem;
                       background: var(--danger-soft); border: 1px solid var(--danger); color: var(--danger); }
            </style>
            <main>
              <div class="head">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"
                     stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                  <rect x="3" y="11" width="18" height="11" rx="2" /><path d="M7 11V7a5 5 0 0 1 10 0v4" />
                </svg>
                <h1>This site is private</h1>
                <p>{{WebUtility.HtmlEncode(site.Domain)}}</p>
              </div>
              <form method="post" action="{{UnlockPath}}">
                {{alert}}
                <input type="hidden" name="{{ReturnUrlField}}" value="{{WebUtility.HtmlEncode(returnUrl)}}">
                <label for="passcode">Passcode</label>
                <input id="passcode" name="{{PasscodeField}}" type="password" autocomplete="current-password"
                       maxlength="{{MaxPasscodeLength}}" autofocus required>
                <button type="submit">View site</button>
              </form>
            </main>
            """;

        await response.WriteAsync(html, Encoding.UTF8);
    }
}
