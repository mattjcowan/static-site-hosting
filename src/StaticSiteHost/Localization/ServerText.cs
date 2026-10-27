using System.Globalization;

namespace StaticSiteHost.Localization;

/// <summary>
/// The server's own messages (refusals, validation errors, results) for code that has no
/// localizer at hand: services, stores and the rule readers. They come in the current UI language,
/// which is the viewer's on a page, and English for the API, MCP, the assistant's tools and
/// background work (<see cref="LocalizationSetup.UseEnglishForApiAndMcp"/>, <see cref="LocalizationSetup.English"/>),
/// whose answers are read by scripts, AI agents and logs. A key no language has comes back as the key.
/// </summary>
public static class ServerText
{
    public static string Get(string key, object? values = null) =>
        TextFormat.Text(Template(key), TextFormat.Values(values is null ? null : [values]));

    /// <summary>A count in words: <c>{key}.one</c> or <c>{key}.other</c>, with the count as <c>{count}</c>.</summary>
    public static string Plural(string key, long count, object? values = null)
    {
        var all = new Dictionary<string, object?>(TextFormat.Values(values is null ? null : [values]), StringComparer.Ordinal) { ["count"] = count };
        var culture = CultureInfo.CurrentUICulture;
        var template = StringCatalog.Shared.Find($"{key}.{TextFormat.PluralCategory(count, culture)}", culture) ?? Template($"{key}.other");
        return TextFormat.Text(template, all);
    }

    /// <summary>Makes text in English whatever the request's language: for a log line or anything stored.</summary>
    public static T English<T>(Func<T> make)
    {
        var before = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = LocalizationSetup.English;
        try
        {
            return make();
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    private static string Template(string key) => StringCatalog.Shared.Find(key, CultureInfo.CurrentUICulture) ?? key;
}
