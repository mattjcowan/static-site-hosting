using System.Text;
using StaticSiteHost.Models;

namespace StaticSiteHost.Serving;

/// <summary>
/// The format header rules are written in, both in the <c>_headers</c> file at the root of a
/// deployed archive and in the box on a site's page:
///
/// <code>
/// # long-lived assets carry a content hash in the name
/// /assets/**
///   Cache-Control: public, max-age=31536000, immutable
///
/// /*.json
///   Cache-Control: no-cache
/// </code>
///
/// A line starting with '/' opens a rule; the <c>Name: value</c> lines under it are the
/// headers it sets. Blank lines and '#' comments are ignored.
/// </summary>
public static class HeaderRuleText
{
    /// <summary>Name of the file read out of the root of a deployed archive.</summary>
    public const string FileName = "_headers";

    public static bool TryParse(string? text, out List<HeaderRule> rules, out IReadOnlyList<string> errors)
    {
        rules = [];
        var problems = new List<string>();
        errors = problems;

        if (string.IsNullOrWhiteSpace(text)) return true;

        HeaderRule? current = null;
        var lineNumber = 0;

        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (line[0] == '/')
            {
                current = new HeaderRule { For = line };
                rules.Add(current);
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                problems.Add($"Line {lineNumber}: expected a path starting with '/' or a 'Header: value' line.");
                continue;
            }

            if (current is null)
            {
                problems.Add($"Line {lineNumber}: '{line[..colon]}' comes before any path pattern.");
                continue;
            }

            current.Set[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        problems.AddRange(SiteHeaderRules.Validate(rules));
        return problems.Count == 0;
    }

    public static string Format(IEnumerable<HeaderRule>? rules)
    {
        var builder = new StringBuilder();

        foreach (var rule in rules ?? [])
        {
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(rule.For).Append('\n');

            foreach (var (name, value) in rule.Set ?? [])
            {
                builder.Append("  ").Append(name).Append(": ").Append(value).Append('\n');
            }
        }

        return builder.ToString();
    }
}
