using Microsoft.Extensions.Localization;

namespace StaticSiteHost.Localization;

/// <summary>
/// A message a service gives back as a string key and its values, so a page shows it in the
/// viewer's language and the API and the tools in English.
/// </summary>
public sealed record KeyedText(string Key, object? Values = null)
{
    public string Text(IStringLocalizer localizer) => localizer.Text(Key, Values);

    /// <summary>The message in English, for the API and the tools, whose answers stay English.</summary>
    public string English(StringCatalog catalog) =>
        TextFormat.Text(catalog.English.TryGetValue(Key, out var template) ? template : Key, TextFormat.Values([Values]));
}
