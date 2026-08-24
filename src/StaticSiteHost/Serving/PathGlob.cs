using System.Text;
using System.Text.RegularExpressions;

namespace StaticSiteHost.Serving;

/// <summary>
/// The glob syntax rules match paths with:
///
///   <c>*</c>   any run of characters except '/'
///   <c>**</c>  any run of characters, '/' included
///   <c>?</c>   a single character except '/'
///
/// Everything else is literal. Patterns are anchored at both ends and matched
/// case-insensitively, so a rule applies whichever way the URL was typed.
///
/// Each <c>*</c> and <c>**</c> is captured, in order, so a redirect target can put what
/// they matched back into the new path.
///
/// Compiled with <see cref="RegexOptions.NonBacktracking"/>: patterns are written by site
/// owners and evaluated on every response, so matching has to stay linear in the length of
/// the path no matter what was typed.
/// </summary>
public sealed class PathGlob
{
    public const int MaxPatternLength = 400;

    /// <summary>Most wildcards a pattern may hold — one more than <c>:1</c>…<c>:9</c> can name.</summary>
    public const int MaxWildcards = 9;

    private readonly Regex _regex;

    private PathGlob(string pattern, Regex regex, int wildcards)
    {
        Pattern = pattern;
        _regex = regex;
        Wildcards = wildcards;
    }

    public string Pattern { get; }

    /// <summary>How many <c>*</c>/<c>**</c> the pattern has, and so how many captures it yields.</summary>
    public int Wildcards { get; }

    public static bool TryCompile(string? pattern, out PathGlob? glob, out string? error)
    {
        glob = null;
        error = null;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "a path pattern is required";
            return false;
        }

        pattern = pattern.Trim();

        if (pattern[0] != '/')
        {
            error = $"'{pattern}' must start with '/' (for example /*.json)";
            return false;
        }

        if (pattern.Length > MaxPatternLength)
        {
            error = $"path patterns must be {MaxPatternLength} characters or fewer";
            return false;
        }

        if (pattern.Any(c => char.IsControl(c) || c == '\\'))
        {
            error = $"'{pattern}' contains a character that cannot appear in a URL path";
            return false;
        }

        var translated = Translate(pattern, out var wildcards);

        if (wildcards > MaxWildcards)
        {
            error = $"'{pattern}' has {wildcards} wildcards; {MaxWildcards} is the most a pattern may have";
            return false;
        }

        glob = new PathGlob(pattern, new Regex(
            translated,
            RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), wildcards);
        return true;
    }

    public bool IsMatch(string path) => _regex.IsMatch(path);

    /// <summary>Matches, handing back what each wildcard stood for, in pattern order.</summary>
    public bool TryMatch(string path, out string[] captures)
    {
        var match = _regex.Match(path);
        if (!match.Success)
        {
            captures = [];
            return false;
        }

        captures = new string[Wildcards];
        for (var i = 0; i < Wildcards; i++) captures[i] = match.Groups[i + 1].Value;
        return true;
    }

    private static string Translate(string pattern, out int wildcards)
    {
        var builder = new StringBuilder(pattern.Length * 2).Append('^');
        wildcards = 0;

        for (var i = 0; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '*' when i + 1 < pattern.Length && pattern[i + 1] == '*':
                    builder.Append("(.*)");
                    wildcards++;
                    i++;
                    break;
                case '*':
                    builder.Append("([^/]*)");
                    wildcards++;
                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                default:
                    builder.Append(Regex.Escape(pattern[i].ToString()));
                    break;
            }
        }

        return builder.Append('$').ToString();
    }
}
