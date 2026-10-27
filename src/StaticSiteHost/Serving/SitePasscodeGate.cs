using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Localization;
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
/// At a name under one of the site's wildcards (<see cref="SiteRecord.Aliases"/>), a second
/// cookie with the same value goes on the domain the wildcard sits on, so one unlock covers
/// every name the wildcard answers. It is named for the site, so it never shadows another
/// site's cookie under that domain, and its value is good for this site alone. The host-only
/// cookie is set as well, for a browser that will not take a cookie on that domain.
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
    private readonly IStringLocalizer<Strings> _strings;

    public SitePasscodeGate(
        IDataProtectionProvider protection,
        SiteStore sites,
        AuditLog audit,
        IOptions<SiteHostingOptions> options,
        ILogger<SitePasscodeGate> logger,
        IStringLocalizer<Strings> strings)
    {
        _protection = protection;
        _sites = sites;
        _audit = audit;
        _options = options.Value;
        _logger = logger;
        _strings = strings;
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

        var cookies = context.Request.Cookies;
        return Accepts(site, cookies[CookieName]) || Accepts(site, cookies[SharedCookieName(site)]);
    }

    private bool Accepts(SiteRecord site, string? cookie)
    {
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
                _strings.Plural("visitor.passcode.tooManyTries", (long)Math.Ceiling(retryAfter.TotalMinutes)), returnUrl);
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
                _strings.Text("visitor.passcode.wrong"), returnUrl);
            return;
        }

        _throttle.Reset(throttleKey, address);

        var lifetime = SessionLifetime;
        var token = Protector(site.Domain).Protect(Stamp(site), lifetime);
        context.Response.Cookies.Append(CookieName, token, SessionCookie(request, lifetime, null));

        if (SharedDomain(site, SiteHostingMiddleware.HostOf(context)) is { } shared)
            context.Response.Cookies.Append(SharedCookieName(site), token, SessionCookie(request, lifetime, shared));

        context.Response.Redirect(returnUrl);
    }

    private static CookieOptions SessionCookie(HttpRequest request, TimeSpan lifetime, string? domain) => new()
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Lax,
        Secure = request.IsHttps,
        Path = "/",
        MaxAge = lifetime,
        Domain = domain
    };

    /// <summary>
    /// The domain the shared cookie goes on when a visitor unlocks at <paramref name="name"/>: the one
    /// a wildcard of the site's sits on (<c>app.example.com</c> for <c>*.app.example.com</c>), when the
    /// name is that domain or under it, the widest when there are several. Null when there is none.
    /// </summary>
    internal static string? SharedDomain(SiteRecord site, string name) =>
        site.Aliases
            .Where(SiteAliases.IsWildcard)
            .Select(alias => alias[2..])
            .Where(parent => name.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
                             name.EndsWith("." + parent, StringComparison.OrdinalIgnoreCase))
            .MinBy(parent => parent.Length);

    /// <summary>The shared cookie's name. It starts with <c>ssh.</c>, so proxy routes never pass it on.</summary>
    internal static string SharedCookieName(SiteRecord site) => $"{CookieName}.{site.Domain}";

    // ---- administration -----------------------------------------------------

    public string? Validate(string? passcode)
    {
        passcode = passcode?.Trim();

        if (string.IsNullOrEmpty(passcode)) return _strings.Text("site.visibility.error.required");
        if (passcode.Length < MinLength) return _strings.Plural("site.visibility.error.short", MinLength);
        if (passcode.Length > MaxPasscodeLength) return _strings.Text("site.visibility.error.long", new { max = MaxPasscodeLength });
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

    /// <summary>The passcode form, in the visitor's language. <paramref name="error"/> is text; it is encoded here.</summary>
    private async Task ChallengeAsync(
        HttpContext context, SiteRecord site, int statusCode, string? error, string returnUrl)
    {
        if (context.Response.HasStarted) return;

        var response = context.Response;
        response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        response.Headers["Referrer-Policy"] = "no-referrer";

        // The name the visitor is at, which for one of the site's other domains is not its domain.
        var name = SiteHostingMiddleware.HostOf(context);

        var alert = error is null
            ? ""
            : $"""<p class="error" role="alert">{WebUtility.HtmlEncode(error)}</p>""";

        var body = $$"""
            <div class="head">
              <svg class="icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"
                   stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <rect x="3" y="11" width="18" height="11" rx="2" /><path d="M7 11V7a5 5 0 0 1 10 0v4" />
              </svg>
              <h1>{{Encoded("visitor.passcode.title")}}</h1>
              <p>{{WebUtility.HtmlEncode(name)}}</p>
              <p>{{Encoded("visitor.passcode.hint")}}</p>
            </div>
            <form method="post" action="{{UnlockPath}}">
              {{alert}}
              <input type="hidden" name="{{ReturnUrlField}}" value="{{WebUtility.HtmlEncode(returnUrl)}}">
              <label for="passcode">{{Encoded("visitor.passcode.label")}}</label>
              <input id="passcode" name="{{PasscodeField}}" type="password" autocomplete="current-password"
                     maxlength="{{MaxPasscodeLength}}" autofocus required>
              <button type="submit">{{Encoded("visitor.passcode.submit")}}</button>
            </form>
            """;

        await VisitorPage.WriteAsync(context, statusCode, _strings.Text("visitor.passcode.pageTitle", new { domain = name }), body);
    }

    /// <summary>A string in the visitor's language, encoded for HTML.</summary>
    private string Encoded(string key, object? values = null) => WebUtility.HtmlEncode(_strings.Text(key, values));
}
