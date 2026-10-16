namespace StaticSiteHost.Pages;

/// <summary>
/// Which sections a person has closed, kept in a cookie so the page arrives with them closed
/// rather than opening and then snapping shut. A section is named for what it holds ("proxy",
/// "headers"), not for the site, so closing Header rules on one site closes it on every site.
/// Sections are open unless listed. The name starts with <c>ssh.</c>, so proxy routes never
/// pass it on. app.js writes it as sections open and close.
/// </summary>
public static class Sections
{
    public const string CookieName = "ssh.collapsed";

    public static bool IsOpen(HttpContext context, string section) =>
        !Closed(context).Contains(section);

    private static HashSet<string> Closed(HttpContext context)
    {
        const string key = "ssh.collapsed-sections";
        if (context.Items[key] is HashSet<string> cached) return cached;

        var closed = (context.Request.Cookies[CookieName] ?? "")
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Where(name => name.Length <= 40 && name.All(c => c is >= 'a' and <= 'z' or '-'))
            .ToHashSet(StringComparer.Ordinal);
        context.Items[key] = closed;
        return closed;
    }
}
