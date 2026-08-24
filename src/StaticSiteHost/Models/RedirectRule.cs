namespace StaticSiteHost.Models;

/// <summary>
/// One redirect or rewrite: a request matching <see cref="From"/> is answered from
/// <see cref="To"/>. <see cref="Status"/> decides which of the two it is — a 3xx sends the
/// visitor to the new address, a 200 or 404 quietly serves a different file at the address
/// they asked for.
/// </summary>
public sealed class RedirectRule
{
    /// <summary>Path glob — see <see cref="Serving.PathGlob"/>.</summary>
    public string From { get; set; } = "";

    /// <summary>
    /// Where it goes. A path on this site, or an absolute http(s) URL when redirecting.
    /// <c>:1</c>…<c>:9</c> stand for what each wildcard in <see cref="From"/> matched, and
    /// <c>:splat</c> is an alias for the first.
    /// </summary>
    public string To { get; set; } = "";

    public int Status { get; set; } = 301;

    /// <summary>
    /// Apply even when the site really has something at the requested path. Off by default,
    /// so a broad rule such as <c>/**</c> cannot swallow the files it is meant to sit behind.
    /// </summary>
    public bool Force { get; set; }
}
