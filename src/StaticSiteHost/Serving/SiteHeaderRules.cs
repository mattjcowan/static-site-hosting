using StaticSiteHost.Models;

namespace StaticSiteHost.Serving;

/// <summary>
/// A site's header rules, compiled once and reused for every response it serves.
///
/// Rules run in order and each matching rule sets its headers, so when two rules name the
/// same header the later one wins. A site's own rules are compiled after the ones that came
/// with the release, which is what lets someone fix caching in the UI without a redeploy.
/// </summary>
public sealed class SiteHeaderRules
{
    public const int MaxRules = 100;
    public const int MaxHeadersPerRule = 25;
    public const int MaxNameLength = 64;
    public const int MaxValueLength = 2048;

    /// <summary>
    /// Headers a rule may not set. Most are the transport's business and setting one either
    /// corrupts the response or is quietly ignored; the last three are load-bearing here:
    /// Content-Encoding belongs to the compression middleware, Set-Cookie would hand a static
    /// site a way to write cookies across sibling subdomains, and X-Content-Type-Options is
    /// asserted on every response on purpose.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Content-Length", "Content-Range", "Date", "Host", "Keep-Alive",
        "Proxy-Authenticate", "Proxy-Authorization", "Server", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade",
        "Content-Encoding", "Set-Cookie", "X-Content-Type-Options"
    };

    public static readonly SiteHeaderRules None = new([]);

    private readonly (PathGlob Glob, KeyValuePair<string, string>[] Headers)[] _rules;

    private SiteHeaderRules((PathGlob, KeyValuePair<string, string>[])[] rules) => _rules = rules;

    public bool IsEmpty => _rules.Length == 0;

    /// <summary>
    /// Compiles every source into one ordered list, earliest source first. Anything that does
    /// not validate is dropped rather than thrown: these rules were accepted when they were
    /// saved, and a request is the wrong place to discover that a limit has since changed.
    /// </summary>
    public static SiteHeaderRules Compile(params IEnumerable<HeaderRule>?[] sources)
    {
        var compiled = new List<(PathGlob, KeyValuePair<string, string>[])>();

        foreach (var source in sources)
        {
            foreach (var rule in source ?? [])
            {
                if (!PathGlob.TryCompile(rule.For, out var glob, out _) || glob is null) continue;

                var headers = (rule.Set ?? [])
                    .Where(header => IsAllowed(header.Key, header.Value, out _))
                    .ToArray();

                if (headers.Length > 0) compiled.Add((glob, headers));
            }
        }

        return compiled.Count == 0 ? None : new SiteHeaderRules(compiled.ToArray());
    }

    /// <summary>
    /// Applies the rules to a response. A rule matches on the URL that was requested or on the
    /// file that ended up being served — <c>/blog/</c> is served from <c>/blog/index.html</c>,
    /// and a rule written against either spelling should find it.
    /// </summary>
    public void Apply(IHeaderDictionary headers, string requestPath, string? servedPath)
    {
        foreach (var (glob, values) in _rules)
        {
            if (!glob.IsMatch(requestPath) &&
                (servedPath is null || servedPath == requestPath || !glob.IsMatch(servedPath)))
                continue;

            foreach (var (name, value) in values) headers[name] = value;
        }
    }

    /// <summary>Human-readable reasons a set of rules cannot be saved. Empty means it can.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<HeaderRule> rules)
    {
        var errors = new List<string>();

        if (rules.Count > MaxRules)
        {
            errors.Add($"A site can have at most {MaxRules} rules; this is {rules.Count}.");
            return errors;
        }

        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var label = $"Rule {i + 1}";

            if (!PathGlob.TryCompile(rule.For, out _, out var globError))
                errors.Add($"{label}: {globError}.");
            else
                label = $"Rule {i + 1} ({rule.For.Trim()})";

            var set = rule.Set ?? [];

            if (set.Count == 0)
                errors.Add($"{label}: sets no headers.");
            else if (set.Count > MaxHeadersPerRule)
                errors.Add($"{label}: sets {set.Count} headers, more than the {MaxHeadersPerRule} allowed.");

            foreach (var (name, value) in set)
            {
                if (!IsAllowed(name, value, out var headerError)) errors.Add($"{label}: {headerError}.");
            }
        }

        return errors;
    }

    private static bool IsAllowed(string? name, string? value, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "a header name is required";
            return false;
        }

        name = name.Trim();

        if (name.Length > MaxNameLength)
        {
            error = $"header names must be {MaxNameLength} characters or fewer";
            return false;
        }

        if (!name.All(IsTokenChar))
        {
            error = $"'{name}' is not a valid header name";
            return false;
        }

        if (Reserved.Contains(name))
        {
            error = $"'{name}' cannot be set by a rule";
            return false;
        }

        value ??= "";

        if (value.Length > MaxValueLength)
        {
            error = $"the value for '{name}' is longer than the {MaxValueLength} character limit";
            return false;
        }

        // A newline here would let a rule append headers of its own to the response.
        if (!value.All(c => char.IsBetween(c, ' ', '~')))
        {
            error = $"the value for '{name}' may only contain printable ASCII";
            return false;
        }

        return true;
    }

    /// <summary>RFC 9110 token characters — what a header name is allowed to be made of.</summary>
    private static bool IsTokenChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);
}
