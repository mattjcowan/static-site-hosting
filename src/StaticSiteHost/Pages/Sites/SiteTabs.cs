namespace StaticSiteHost.Pages.Sites;

/// <summary>
/// The site page's tabs. Each has its own address (<c>/sites/details/{domain}/{tab}</c>, none for
/// the first), so a link or the back button lands on it. A form posts to the page's handler, and
/// the tab the answer shows comes from the handler, not the address the form was on: a tab switched
/// to in place keeps the address it was loaded with until the page reloads.
/// </summary>
public static class SiteTabs
{
    public const string Default = "overview";

    private static readonly (string Id, string LabelKey)[] All =
    [
        (Default, "site.tabs.overview"),
        ("files", "site.tabs.files"),
        ("security", "site.tabs.security"),
        ("routing", "site.tabs.routing"),
        ("app", "site.tabs.app"),
        ("releases", "site.tabs.releases"),
        ("settings", "site.tabs.settings")
    ];

    /// <summary>Which tab each form's answer belongs on.</summary>
    private static readonly Dictionary<string, string> ByHandler = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SetPasscode"] = "security", ["RemovePasscode"] = "security",
        ["Tls"] = "security", ["UploadCertificate"] = "security", ["RemoveCertificate"] = "security", ["RenewCertificate"] = "security",
        ["SaveProxyRoute"] = "routing", ["RemoveProxyRoute"] = "routing", ["TestProxyRoute"] = "routing", ["ServerRoute"] = "routing",
        ["Headers"] = "routing", ["Redirects"] = "routing",
        ["Functions"] = "app", ["RemoveFunctionFile"] = "app", ["RemoveFunctions"] = "app",
        ["SetVariable"] = "app", ["RemoveVariable"] = "app", ["Ai"] = "app", ["RemoveAi"] = "app",
        ["Rollback"] = "releases", ["Duplicate"] = "releases",
        ["Rename"] = "settings", ["Branding"] = "settings", ["RemoveBranding"] = "settings"
    };

    /// <summary>The tabs a person sees: Settings (renaming and deleting) is for administrators.</summary>
    public static IEnumerable<(string Id, string LabelKey)> For(bool administrator) =>
        All.Where(tab => administrator || tab.Id != "settings");

    /// <summary>The tab an address names, or the first when it names none this person may see.</summary>
    public static string Normalize(string? tab, bool administrator) =>
        For(administrator).Any(t => t.Id.Equals(tab, StringComparison.OrdinalIgnoreCase)) ? tab!.ToLowerInvariant() : Default;

    /// <summary>The tab a form handler's answer shows, or null for one that leaves the page.</summary>
    public static string? ForHandler(string? handler) =>
        handler is not null && ByHandler.TryGetValue(handler, out var tab) ? tab : null;
}
