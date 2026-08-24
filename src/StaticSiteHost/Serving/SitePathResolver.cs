using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;

namespace StaticSiteHost.Serving;

public enum SiteResolutionKind
{
    NotFound,
    Serve,
    Redirect
}

public readonly record struct SiteResolution(
    SiteResolutionKind Kind,
    string? RelativePath,
    string? Location,
    int StatusCode,
    bool IsFallback)
{
    public static readonly SiteResolution NotFound = new(SiteResolutionKind.NotFound, null, null, 404, false);

    public static SiteResolution Serve(string relativePath, bool isFallback = false, int statusCode = 200) =>
        new(SiteResolutionKind.Serve, relativePath, null, statusCode, isFallback);

    public static SiteResolution RedirectTo(string location) =>
        new(SiteResolutionKind.Redirect, null, location, 301, false);
}

/// <summary>
/// Maps an incoming request path onto a file inside a release directory.
///
/// Order of preference for <c>/a/b/page</c>:
///   1. the exact file            → /a/b/page
///   2. the .html sibling         → /a/b/page.html
///   3. a directory of that name  → 301 to /a/b/page/
///   4. index.html walking up     → /a/b/index.html, /a/index.html, /index.html
///   5. /404.html, if present     → served with a 404 status
///
/// Step 4 is skipped for requests that clearly want an asset (an extension the browser
/// did not ask HTML for), so a missing .js 404s instead of receiving HTML.
/// </summary>
public sealed class SitePathResolver
{
    private const string IndexFile = "index.html";
    private const string NotFoundFile = "404.html";

    /// <summary>
    /// Deepest path this will consider. Resolution walks up the tree one directory at a
    /// time, building a path and stat-ing it at each level, so an 8 KB URL of one-character
    /// segments would buy thousands of syscalls and megabytes of allocation from a single
    /// unauthenticated GET. Real sites nest fewer than ten deep.
    /// </summary>
    public const int MaxPathSegments = 64;

    private readonly SiteHostingOptions _options;

    public SitePathResolver(IOptions<SiteHostingOptions> options) => _options = options.Value;

    public SiteResolution Resolve(string root, string requestPath, string? acceptHeader)
    {
        requestPath ??= "/";
        var endsWithSlash = requestPath.EndsWith('/');

        if (!TrySplit(requestPath, out var segments)) return SiteResolution.NotFound;

        var isDirectoryRequest = endsWithSlash || segments.Length == 0;

        if (!isDirectoryRequest)
        {
            var full = Combine(root, segments);
            if (full is null) return SiteResolution.NotFound;

            if (File.Exists(full)) return SiteResolution.Serve(ToRelative(segments));

            var last = segments[^1];
            if (!Path.HasExtension(last))
            {
                var htmlSegments = Replace(segments, last + ".html");
                var htmlPath = Combine(root, htmlSegments);
                if (htmlPath is not null && File.Exists(htmlPath))
                    return SiteResolution.Serve(ToRelative(htmlSegments));
            }

            if (Directory.Exists(full))
                return SiteResolution.RedirectTo(requestPath + "/");
        }

        if (!ShouldFallBackToIndex(segments, isDirectoryRequest, acceptHeader))
            return NotFoundOrCustomPage(root);

        // Walk up from the requested directory to the site root looking for an index.html.
        var directorySegments = isDirectoryRequest ? segments : segments[..^1];
        for (var depth = directorySegments.Length; depth >= 0; depth--)
        {
            var candidateSegments = Append(directorySegments[..depth], IndexFile);
            var candidate = Combine(root, candidateSegments);
            if (candidate is not null && File.Exists(candidate))
            {
                var isFallback = !isDirectoryRequest || depth != directorySegments.Length;
                return SiteResolution.Serve(ToRelative(candidateSegments), isFallback);
            }
        }

        return NotFoundOrCustomPage(root);
    }

    /// <summary>
    /// Whether the site really has something at this path — the question a redirect rule
    /// steps aside for unless it was forced.
    ///
    /// Deliberately narrower than <see cref="Resolve"/>: it asks "is there something here",
    /// not "what would answer this request". The index.html walk-up means almost every path
    /// resolves to something, and if that counted as occupied no unforced rule would ever
    /// fire.
    /// </summary>
    public bool Exists(string root, string requestPath)
    {
        requestPath ??= "/";
        if (!TrySplit(requestPath, out var segments)) return false;

        if (requestPath.EndsWith('/') || segments.Length == 0)
        {
            var index = Combine(root, Append(segments, IndexFile));
            return index is not null && File.Exists(index);
        }

        var full = Combine(root, segments);
        if (full is null) return false;
        if (File.Exists(full)) return true;

        var last = segments[^1];
        if (!Path.HasExtension(last))
        {
            var html = Combine(root, Replace(segments, last + ".html"));
            if (html is not null && File.Exists(html)) return true;
        }

        // A directory answers with a 301 to its slash form, which is still an answer.
        return Directory.Exists(full);
    }

    private static SiteResolution NotFoundOrCustomPage(string root) =>
        File.Exists(Path.Combine(root, NotFoundFile))
            ? SiteResolution.Serve("/" + NotFoundFile, isFallback: true, statusCode: 404)
            : SiteResolution.NotFound;

    private bool ShouldFallBackToIndex(string[] segments, bool isDirectoryRequest, string? acceptHeader)
    {
        if (_options.SpaFallbackForAllRequests) return true;
        if (isDirectoryRequest || segments.Length == 0) return true;

        var extension = Path.GetExtension(segments[^1]);
        if (extension.Length == 0) return true;
        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)) return true;

        // A browser navigation asks for text/html explicitly; an <img>/fetch for an asset does not.
        return acceptHeader is not null && acceptHeader.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrySplit(string requestPath, out string[] segments)
    {
        segments = [];
        var parts = requestPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > MaxPathSegments) return false;

        var result = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            if (part is "." or "..") return false;
            if (part.Length > 255) return false;
            if (part.Any(c => char.IsControl(c) || c == '\\')) return false;
            result.Add(part);
        }

        segments = result.ToArray();
        return true;
    }

    private static string? Combine(string root, string[] segments)
    {
        var full = Path.GetFullPath(Path.Combine([root, .. segments]));
        return PathHelpers.IsInside(root, full) ? full : null;
    }

    private static string ToRelative(string[] segments) => "/" + string.Join('/', segments);

    private static string[] Replace(string[] segments, string last)
    {
        var copy = segments.ToArray();
        copy[^1] = last;
        return copy;
    }

    private static string[] Append(string[] segments, string extra) => [.. segments, extra];
}
