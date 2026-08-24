using System.Text;
using StaticSiteHost.Models;

namespace StaticSiteHost.Serving;

/// <summary>
/// The format redirects and rewrites are written in, both in the <c>_redirects</c> file at
/// the root of a deployed archive and in the box on a site's page — one rule per line,
/// columns separated by spaces:
///
/// <code>
/// /old-page          /new-page              301
/// /blog/*            /articles/:1           301
/// /docs/**           https://docs.example.com/:1
/// /app/*             /app/index.html        200
/// /removed           /gone.html             404
/// /always            /elsewhere             301!
/// </code>
///
/// The status is optional and defaults to 301. A '!' after it forces the rule to apply even
/// where the site has a real file. Blank lines and '#' comments are ignored.
/// </summary>
public static class RedirectRuleText
{
    /// <summary>Name of the file read out of the root of a deployed archive.</summary>
    public const string FileName = "_redirects";

    public const int DefaultStatus = 301;

    public static bool TryParse(string? text, out List<RedirectRule> rules, out IReadOnlyList<string> errors)
    {
        rules = [];
        var problems = new List<string>();
        errors = problems;

        if (string.IsNullOrWhiteSpace(text)) return true;

        var lineNumber = 0;

        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (columns.Length is < 2 or > 3)
            {
                problems.Add($"Line {lineNumber}: expected 'from to [status]', found {columns.Length} column(s).");
                continue;
            }

            var rule = new RedirectRule { From = columns[0], To = columns[1] };

            if (columns.Length == 3)
            {
                var status = columns[2];

                if (status.EndsWith('!'))
                {
                    rule.Force = true;
                    status = status[..^1];
                }

                if (!int.TryParse(status, out var parsed))
                {
                    problems.Add($"Line {lineNumber}: '{columns[2]}' is not a status code.");
                    continue;
                }

                rule.Status = parsed;
            }
            else
            {
                rule.Status = DefaultStatus;
            }

            rules.Add(rule);
        }

        problems.AddRange(SiteRedirectRules.Validate(rules));
        return problems.Count == 0;
    }

    public static string Format(IEnumerable<RedirectRule>? rules)
    {
        var list = (rules ?? []).ToList();
        if (list.Count == 0) return "";

        // Padded to the widest entry so the columns line up when the list comes back.
        var fromWidth = list.Max(r => r.From.Length);
        var toWidth = list.Max(r => r.To.Length);
        var builder = new StringBuilder();

        foreach (var rule in list)
        {
            builder
                .Append(rule.From.PadRight(fromWidth))
                .Append("  ")
                .Append(rule.To.PadRight(toWidth))
                .Append("  ")
                .Append(rule.Status)
                .Append(rule.Force ? "!" : "")
                .Append('\n');
        }

        return builder.ToString();
    }
}
