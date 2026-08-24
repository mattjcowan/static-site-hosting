namespace StaticSiteHost.Models;

/// <summary>
/// One header rule: every response whose path matches <see cref="For"/> gets the headers in
/// <see cref="Set"/>. Rules reach a site from two places — a <c>_headers</c> file in the
/// deployed archive (stored on the release, so it rolls back with it) and the site's own list
/// in site.json (edited in the UI or over the API, so it survives a rollback).
/// </summary>
public sealed class HeaderRule
{
    /// <summary>Path glob — see <see cref="Serving.PathGlob"/>.</summary>
    public string For { get; set; } = "";

    public Dictionary<string, string> Set { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
