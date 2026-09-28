namespace StaticSiteHost.Models;

/// <summary>
/// One variable a release declares in the <c>_variables.json</c> file at the root of its archive:
/// its name, what it is for, the value it has until the site sets one, and who may see it. Stored
/// on the release, so a rollback brings back the declarations that went with the content, while
/// the values the site set (<see cref="SiteVariable"/>) stay where they are.
/// </summary>
public sealed class VariableDefinition
{
    public string Name { get; set; } = "";

    /// <summary>Shown beside the variable on the site page, so whoever sets it knows what it is for.</summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// The value until the site sets its own, or null for none. A secret's default is stored
    /// protected, like a secret the site sets, so it is never echoed back by the API.
    /// </summary>
    public string? Default { get; set; }

    /// <summary>Sent to the browser in <c>/_host/site.js</c>. Never together with <see cref="Secret"/>.</summary>
    public bool Public { get; set; }

    /// <summary>Stored protected, shown only as set or not set, and never sent to the browser.</summary>
    public bool Secret { get; set; }

    /// <summary>An empty value is reported on the site page and as a deploy warning.</summary>
    public bool Required { get; set; }
}
