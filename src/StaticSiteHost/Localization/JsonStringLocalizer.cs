using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Html;
using Microsoft.Extensions.Localization;

namespace StaticSiteHost.Localization;

/// <summary>The type pages and services ask for: <c>IStringLocalizer&lt;Strings&gt;</c>. All text lives in one catalogue.</summary>
public sealed class Strings;

/// <summary>
/// The standard ASP.NET localizer interface over <see cref="StringCatalog"/>, in the request's UI
/// culture. A key no language has comes back as the key itself, marked not found, and is logged
/// once, so a missing string shows up in testing rather than as a blank.
/// </summary>
public sealed class JsonStringLocalizer : IStringLocalizer<Strings>
{
    private readonly StringCatalog _catalog;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, bool> _reported = new(StringComparer.Ordinal);

    public JsonStringLocalizer(StringCatalog catalog, ILogger<JsonStringLocalizer> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    public LocalizedString this[string name] => this[name, []];

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var template = Template(name, out var found);
            return new LocalizedString(name, TextFormat.Text(template, TextFormat.Values(arguments)), resourceNotFound: !found);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
        _catalog.English.Keys.Select(key => this[key]);

    /// <summary>The text as HTML: see <see cref="TextFormat"/> for what a string may contain.</summary>
    public IHtmlContent Html(string name, object? values = null) =>
        TextFormat.Html(Template(name, out _), TextFormat.Values(values is null ? null : [values]));

    /// <summary>
    /// A count in words: <c>{name}.one</c> or <c>{name}.other</c> as the language's plural rules
    /// choose, with the count as <c>{count}</c> beside any other values.
    /// </summary>
    public string Plural(string name, long count, object? values = null) =>
        TextFormat.Text(PluralTemplate(name, count), WithCount(values, count));

    public IHtmlContent PluralHtml(string name, long count, object? values = null) =>
        TextFormat.Html(PluralTemplate(name, count), WithCount(values, count));

    private string PluralTemplate(string name, long count)
    {
        var category = TextFormat.PluralCategory(count, CultureInfo.CurrentUICulture);
        var key = $"{name}.{category}";
        return _catalog.Find(key, CultureInfo.CurrentUICulture) is { } text ? text : Template($"{name}.other", out _);
    }

    private static IReadOnlyDictionary<string, object?> WithCount(object? values, long count)
    {
        var all = new Dictionary<string, object?>(TextFormat.Values(values is null ? null : [values]), StringComparer.Ordinal)
        {
            ["count"] = count
        };
        return all;
    }

    private string Template(string name, out bool found)
    {
        if (_catalog.Find(name, CultureInfo.CurrentUICulture) is { } text)
        {
            found = true;
            return text;
        }

        found = false;
        if (_reported.TryAdd(name, true))
            _logger.LogWarning("No language has the string {Key}; its key is shown instead", name);
        return name;
    }
}

public sealed class JsonStringLocalizerFactory : IStringLocalizerFactory
{
    private readonly JsonStringLocalizer _localizer;

    public JsonStringLocalizerFactory(JsonStringLocalizer localizer) => _localizer = localizer;

    public IStringLocalizer Create(Type resourceSource) => _localizer;

    public IStringLocalizer Create(string baseName, string location) => _localizer;
}

/// <summary>The same calls on <c>IStringLocalizer&lt;Strings&gt;</c>, which is what pages inject.</summary>
public static class StringLocalizerExtensions
{
    private static JsonStringLocalizer Json(IStringLocalizer localizer) =>
        localizer as JsonStringLocalizer ?? throw new InvalidOperationException("Strings come from JsonStringLocalizer; register it with AddSiteHostStrings.");

    public static IHtmlContent Html(this IStringLocalizer localizer, string name, object? values = null) =>
        Json(localizer).Html(name, values);

    public static string Text(this IStringLocalizer localizer, string name, object? values = null) =>
        localizer[name, values is null ? [] : [values]].Value;

    public static string Plural(this IStringLocalizer localizer, string name, long count, object? values = null) =>
        Json(localizer).Plural(name, count, values);

    public static IHtmlContent PluralHtml(this IStringLocalizer localizer, string name, long count, object? values = null) =>
        Json(localizer).PluralHtml(name, count, values);
}
