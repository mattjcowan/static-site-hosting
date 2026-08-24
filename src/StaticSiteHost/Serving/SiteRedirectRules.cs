using System.Text;
using StaticSiteHost.Models;

namespace StaticSiteHost.Serving;

/// <summary>What a matching redirect rule decided to do with a request.</summary>
public readonly record struct RedirectMatch(string Target, int Status, bool IsRedirect);

/// <summary>
/// A site's redirect and rewrite rules, compiled once and reused for every request it
/// serves. Unlike header rules these are mutually exclusive — the first rule that matches
/// answers the request — so the site's own rules are compiled ahead of the release's.
/// </summary>
public sealed class SiteRedirectRules
{
    public const int MaxRules = 100;
    public const int MaxTargetLength = 1000;

    /// <summary>Statuses that send the visitor somewhere else.</summary>
    public static readonly int[] RedirectStatuses = [301, 302, 303, 307, 308];

    /// <summary>Statuses that serve a different file at the address that was asked for.</summary>
    public static readonly int[] RewriteStatuses = [200, 404];

    public static readonly SiteRedirectRules None = new([]);

    private readonly Compiled[] _rules;

    private SiteRedirectRules(Compiled[] rules) => _rules = rules;

    public bool IsEmpty => _rules.Length == 0;

    public static SiteRedirectRules Compile(params IEnumerable<RedirectRule>?[] sources)
    {
        var compiled = new List<Compiled>();

        foreach (var source in sources)
        {
            foreach (var rule in source ?? [])
            {
                if (Problems(rule).Count > 0) continue;
                if (!PathGlob.TryCompile(rule.From, out var glob, out _) || glob is null) continue;

                compiled.Add(new Compiled(glob, rule.To.Trim(), rule.Status, IsRedirect(rule.Status), rule.Force));
            }
        }

        return compiled.Count == 0 ? None : new SiteRedirectRules([.. compiled]);
    }

    /// <summary>
    /// Finds the rule that answers a request, if any.
    ///
    /// <paramref name="exists"/> reports whether the site really has a file at a path. It is
    /// only consulted for a rule that matched and was not forced, so a request that matches
    /// nothing costs no disk access: a rule steps aside for real content unless its author
    /// said otherwise, which is what keeps a broad <c>/**</c> rewrite from swallowing the
    /// assets it is meant to sit behind.
    /// </summary>
    public bool TryMatch(string requestPath, Func<string, bool> exists, out RedirectMatch match)
    {
        bool? occupied = null;

        foreach (var rule in _rules)
        {
            if (!rule.Glob.TryMatch(requestPath, out var captures)) continue;

            // The answer is the same for every rule, so it is asked for at most once, and
            // only once a rule has actually matched.
            if (!rule.Force && (occupied ??= exists(requestPath))) continue;

            match = new RedirectMatch(
                Expand(rule.Target, captures, encode: rule.IsRedirect),
                rule.Status,
                rule.IsRedirect);
            return true;
        }

        match = default;
        return false;
    }

    public static IReadOnlyList<string> Validate(IReadOnlyList<RedirectRule> rules)
    {
        var errors = new List<string>();

        if (rules.Count > MaxRules)
        {
            errors.Add($"A site can have at most {MaxRules} rules; this is {rules.Count}.");
            return errors;
        }

        for (var i = 0; i < rules.Count; i++)
        {
            var label = string.IsNullOrWhiteSpace(rules[i].From) ? $"Rule {i + 1}" : $"Rule {i + 1} ({rules[i].From.Trim()})";
            errors.AddRange(Problems(rules[i]).Select(problem => $"{label}: {problem}."));
        }

        return errors;
    }

    public static bool IsRedirect(int status) => RedirectStatuses.Contains(status);

    private static List<string> Problems(RedirectRule rule)
    {
        var problems = new List<string>();

        var hasGlob = PathGlob.TryCompile(rule.From, out var glob, out var globError);
        if (!hasGlob) problems.Add(globError!);

        var redirect = IsRedirect(rule.Status);
        if (!redirect && !RewriteStatuses.Contains(rule.Status))
        {
            problems.Add(
                $"{rule.Status} is not a status a rule can use — " +
                $"{string.Join(", ", RedirectStatuses)} redirect, {string.Join(" and ", RewriteStatuses)} rewrite");
        }

        var target = rule.To?.Trim() ?? "";

        if (target.Length == 0)
        {
            problems.Add("a target is required");
        }
        else if (target.Length > MaxTargetLength)
        {
            problems.Add($"targets must be {MaxTargetLength} characters or fewer");
        }
        else if (target.Any(char.IsControl))
        {
            problems.Add($"'{target}' contains a character that cannot appear in a URL");
        }
        else if (IsAbsolute(target))
        {
            // Sending a visitor to another site is a fair thing to want; serving one is not.
            if (!redirect) problems.Add($"'{target}' is another site, which only a redirect can point at");
        }
        else if (!target.StartsWith('/') || target.StartsWith("//"))
        {
            problems.Add($"'{target}' must be a path starting with '/', or an http(s) address");
        }

        if (hasGlob && glob is not null)
        {
            var highest = HighestPlaceholder(target);
            if (highest > glob.Wildcards)
            {
                problems.Add(glob.Wildcards == 0
                    ? $"'{target}' uses a placeholder but '{glob.Pattern}' has no wildcard to fill it"
                    : $"'{target}' uses :{highest} but '{glob.Pattern}' has only " +
                      $"{glob.Wildcards} wildcard{(glob.Wildcards == 1 ? "" : "s")}");
            }
        }

        return problems;
    }

    private static bool IsAbsolute(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>The largest wildcard number the target refers to, counting :splat as the first.</summary>
    private static int HighestPlaceholder(string target)
    {
        var highest = 0;

        for (var i = 0; i < target.Length; i++)
        {
            if (target[i] != ':') continue;

            if (i + 1 < target.Length && char.IsBetween(target[i + 1], '1', '9'))
                highest = Math.Max(highest, target[i + 1] - '0');
            else if (target.AsSpan(i).StartsWith(":splat", StringComparison.OrdinalIgnoreCase))
                highest = Math.Max(highest, 1);
        }

        return highest;
    }

    /// <summary>
    /// Puts what the wildcards matched into the target. A redirect's substitutions are
    /// percent-encoded on the way in: they come from a URL the visitor chose, and they are
    /// about to become a Location header.
    /// </summary>
    private static string Expand(string target, string[] captures, bool encode)
    {
        if (captures.Length == 0 || !target.Contains(':')) return target;

        var builder = new StringBuilder(target.Length);

        for (var i = 0; i < target.Length; i++)
        {
            if (target[i] != ':')
            {
                builder.Append(target[i]);
                continue;
            }

            int index, width;

            if (i + 1 < target.Length && char.IsBetween(target[i + 1], '1', '9'))
            {
                index = target[i + 1] - '1';
                width = 2;
            }
            else if (target.AsSpan(i).StartsWith(":splat", StringComparison.OrdinalIgnoreCase))
            {
                index = 0;
                width = 6;
            }
            else
            {
                builder.Append(target[i]);
                continue;
            }

            if (index >= captures.Length)
            {
                builder.Append(target[i]);
                continue;
            }

            builder.Append(encode ? EncodePath(captures[index]) : captures[index]);
            i += width - 1;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Percent-encodes a decoded path fragment back into something that belongs in a URL,
    /// leaving the characters a path is allowed to contain — '/' among them — alone.
    /// </summary>
    private static string EncodePath(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || "-._~/:@!$&'()*+,;=".Contains(c)) builder.Append(c);
            else builder.Append('%').Append(b.ToString("X2"));
        }

        return builder.ToString();
    }

    private readonly record struct Compiled(PathGlob Glob, string Target, int Status, bool IsRedirect, bool Force);
}
