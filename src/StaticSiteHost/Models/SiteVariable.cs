namespace StaticSiteHost.Models;

/// <summary>
/// A value the site itself sets for a variable, in site.json (edited on the site page or over
/// the API). It overrides the default the release declared, and because it lives on the site
/// rather than the release it survives deploys and rollbacks, like the site's own header rules.
/// A site may also hold variables that no release declares.
/// </summary>
public sealed class SiteVariable
{
    public string Name { get; set; } = "";

    /// <summary>
    /// The value as saved. For a secret this is Data Protection output behind a <c>dp:</c>
    /// prefix, so site.json never holds the secret itself; see <see cref="Services.SiteVariableService"/>.
    /// </summary>
    public string Value { get; set; } = "";

    /// <summary>
    /// Whether the value was public when it was saved. A declared variable takes its visibility
    /// from the release, but a value that reads the server's environment is only ever published
    /// if it was saved as public.
    /// </summary>
    public bool Public { get; set; }

    /// <summary>
    /// Whether <see cref="Value"/> was saved protected. A value saved as a secret stays one until it
    /// is saved again, even if a later release declares the name public.
    /// </summary>
    public bool Secret { get; set; }

    public DateTimeOffset SetUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? SetBy { get; set; }
}
