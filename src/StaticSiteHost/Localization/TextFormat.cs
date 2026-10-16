using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace StaticSiteHost.Localization;

/// <summary>
/// How a string's text becomes what the page shows. A string is plain text with three small
/// additions, so translators never touch HTML:
///
///   <c>{name}</c>          a value from the code, such as a domain or a count;
///   <c>`code`</c>          a file name, path, header or command, shown in a fixed-width font;
///   <c>**words**</c>       words to stress, shown bold;
///   <c>[words](name)</c>   a link: the words are the link's text, <c>name</c> the value holding its address.
///
/// <see cref="Html"/> renders those as HTML, with every value encoded unless the code passed HTML
/// itself (an <see cref="IHtmlContent"/>). <see cref="Text"/> renders them as plain text, for
/// attributes, titles and messages.
/// </summary>
public static partial class TextFormat
{
    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*|\d+)\}")]
    private static partial Regex Placeholder();

    // A link, code, or bold, in that order of precedence: whichever starts first wins.
    [GeneratedRegex(@"\[(?<label>[^\]]+)\]\((?<target>[A-Za-z][A-Za-z0-9]*)\)|`(?<code>[^`]+)`|\*\*(?<bold>.+?)\*\*")]
    private static partial Regex Markup();

    /// <summary>The placeholders a text uses, for the tests that keep translations in step with English.</summary>
    public static IReadOnlySet<string> PlaceholdersOf(string text) =>
        Placeholder().Matches(text).Select(m => m.Groups[1].Value)
            .Concat(Markup().Matches(text).Where(m => m.Groups["target"].Success).Select(m => m.Groups["target"].Value))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The values a call passes: an anonymous object (<c>new { domain }</c>) by property name, or
    /// positional values as <c>{0}</c>, <c>{1}</c>…
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Values(object?[]? arguments)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments is null || arguments.Length == 0) return values;

        if (arguments.Length == 1 && arguments[0] is { } single && IsNamed(single))
        {
            if (single is IReadOnlyDictionary<string, object?> dictionary)
            {
                foreach (var (key, value) in dictionary) values[key] = value;
                return values;
            }

            foreach (var property in single.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                values[property.Name] = property.GetValue(single);
            return values;
        }

        for (var i = 0; i < arguments.Length; i++) values[i.ToString(CultureInfo.InvariantCulture)] = arguments[i];
        return values;
    }

    private static bool IsNamed(object value) =>
        value is IReadOnlyDictionary<string, object?> ||
        (!value.GetType().IsPrimitive && value is not string and not decimal and not DateTime and not DateTimeOffset and not IHtmlContent and not IFormattable);

    /// <summary>The text with its values filled in and its markup removed: <c>`a`</c> → a, <c>**b**</c> → b, <c>[c](x)</c> → c.</summary>
    public static string Text(string template, IReadOnlyDictionary<string, object?> values)
    {
        // The marks come out of the template first, so a value that holds a backtick or ** keeps it.
        var text = new StringBuilder();
        var at = 0;
        foreach (Match m in Markup().Matches(template))
        {
            text.Append(Fill(template[at..m.Index], values, encode: false));
            var inner = m.Groups["label"].Success ? m.Groups["label"].Value : m.Groups["code"].Success ? m.Groups["code"].Value : m.Groups["bold"].Value;
            text.Append(Fill(inner, values, encode: false));
            at = m.Index + m.Length;
        }
        return text.Append(Fill(template[at..], values, encode: false)).ToString();
    }

    /// <summary>The text as HTML: values encoded (or passed through when they are HTML), markup turned into tags.</summary>
    public static IHtmlContent Html(string template, IReadOnlyDictionary<string, object?> values)
    {
        // Markup is found in the template, before values go in, so a value can never become markup.
        var html = new StringBuilder();
        var at = 0;
        foreach (Match m in Markup().Matches(template))
        {
            html.Append(Fill(template[at..m.Index], values, encode: true));
            if (m.Groups["label"].Success)
            {
                var href = values.TryGetValue(m.Groups["target"].Value, out var target) ? Plain(target) : "#";
                html.Append("<a href=\"").Append(WebUtility.HtmlEncode(href)).Append("\">")
                    .Append(Fill(m.Groups["label"].Value, values, encode: true)).Append("</a>");
            }
            else if (m.Groups["code"].Success)
            {
                html.Append("<span class=\"mono\">").Append(Fill(m.Groups["code"].Value, values, encode: true)).Append("</span>");
            }
            else
            {
                html.Append("<strong>").Append(Fill(m.Groups["bold"].Value, values, encode: true)).Append("</strong>");
            }
            at = m.Index + m.Length;
        }

        html.Append(Fill(template[at..], values, encode: true));
        return new HtmlString(html.ToString());
    }

    private static string Fill(string text, IReadOnlyDictionary<string, object?> values, bool encode)
    {
        var filled = Placeholder().Replace(text, m =>
        {
            if (!values.TryGetValue(m.Groups[1].Value, out var value)) return m.Value;
            if (value is IHtmlContent content)
            {
                if (!encode) return WebUtility.HtmlDecode(StripTags(Render(content)));
                return "\u0001" + Render(content) + "\u0002";
            }
            var plain = Plain(value);
            return encode ? "\u0001" + WebUtility.HtmlEncode(plain) + "\u0002" : plain;
        });

        if (!encode) return filled;

        // Encode the template's own text, but not the values put in above (already encoded, or HTML).
        var output = new StringBuilder();
        var start = 0;
        while (true)
        {
            var open = filled.IndexOf('\u0001', start);
            if (open < 0) { output.Append(WebUtility.HtmlEncode(filled[start..])); break; }
            var close = filled.IndexOf('\u0002', open);
            output.Append(WebUtility.HtmlEncode(filled[start..open])).Append(filled, open + 1, close - open - 1);
            start = close + 1;
        }
        return output.ToString();
    }

    private static string Plain(object? value) => value switch
    {
        null => "",
        IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture),
        _ => value.ToString() ?? ""
    };

    private static string Render(IHtmlContent content)
    {
        using var writer = new StringWriter();
        content.WriteTo(writer, System.Text.Encodings.Web.HtmlEncoder.Default);
        return writer.ToString();
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tag();

    private static string StripTags(string html) => Tag().Replace(html, "");

    /// <summary>
    /// The plural form a count takes in a language, as CLDR names them, for the languages this
    /// build has. English and Spanish: one for 1, other otherwise. French: one for 0 and 1. Chinese:
    /// always other. Any other language falls back to the English rule.
    /// </summary>
    public static string PluralCategory(long count, CultureInfo culture) => culture.TwoLetterISOLanguageName switch
    {
        "fr" => count is 0 or 1 ? "one" : "other",
        "zh" or "ja" or "ko" => "other",
        _ => count == 1 ? "one" : "other"
    };
}
