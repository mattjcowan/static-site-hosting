namespace StaticSiteHost.Pages;

/// <summary>
/// Light or dark: the system's choice unless the viewer picked one, kept in a cookie so the page
/// arrives in the right colours rather than flashing the other ones first. Its name starts with
/// <c>ssh.</c>, so proxy routes never pass it on.
/// </summary>
public static class ThemePreference
{
    public const string CookieName = "ssh.theme";

    /// <summary><c>light</c> or <c>dark</c> when the viewer chose one, otherwise null for the system's.</summary>
    public static string? Of(HttpContext context) =>
        context.Request.Cookies[CookieName] is "light" or "dark" ? context.Request.Cookies[CookieName] : null;
}
