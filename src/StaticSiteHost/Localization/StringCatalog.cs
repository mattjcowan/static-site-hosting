using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace StaticSiteHost.Localization;

/// <summary>
/// Every piece of text the management app and the visitor screens show, in every language this
/// build has: <c>Resources/strings/{language}/*.json</c>, embedded in the assembly. Each file is one
/// flat object of key → text; a language's files are merged, and a key may live in only one of them.
/// English (<c>en</c>) is complete by definition and is what any other language falls back to, key by
/// key, so a half-done translation shows English where it has gaps rather than a key name.
///
/// Adding a language is adding a folder: copy <c>en</c>, translate the values, keep the keys and the
/// <c>{placeholders}</c>. See <c>.claude/skills/localization/SKILL.md</c>.
/// </summary>
public sealed class StringCatalog
{
    public const string Fallback = "en";
    private const string Prefix = "strings/";

    private readonly FrozenDictionary<string, FrozenDictionary<string, string>> _languages;

    public StringCatalog() : this(typeof(StringCatalog).Assembly) { }

    internal StringCatalog(Assembly assembly) : this(Load(assembly)) { }

    internal StringCatalog(IReadOnlyDictionary<string, Dictionary<string, string>> languages)
    {
        if (!languages.ContainsKey(Fallback))
            throw new InvalidOperationException($"The {Fallback} strings are missing from the build (Resources/strings/{Fallback}).");

        _languages = languages.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value.ToFrozenDictionary(StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);

        Languages = _languages.Keys
            .OrderBy(name => name == Fallback ? "" : name, StringComparer.Ordinal)
            .Select(CultureInfo.GetCultureInfo)
            .ToArray();
    }

    /// <summary>The languages this build speaks, English first.</summary>
    public IReadOnlyList<CultureInfo> Languages { get; }

    /// <summary>English: every key there is.</summary>
    public IReadOnlyDictionary<string, string> English => _languages[Fallback];

    /// <summary>The language's own text for a key, else its parent's (fr-CA → fr), else English's; null when no language has it.</summary>
    public string? Find(string key, CultureInfo culture)
    {
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            if (_languages.TryGetValue(c.Name, out var strings) && strings.TryGetValue(key, out var text)) return text;
        }

        return English.TryGetValue(key, out var english) ? english : null;
    }

    /// <summary>The language this build would use for a culture: itself, a parent, or English.</summary>
    public CultureInfo Resolve(CultureInfo culture)
    {
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            if (_languages.ContainsKey(c.Name)) return CultureInfo.GetCultureInfo(c.Name);
        }

        return CultureInfo.GetCultureInfo(Fallback);
    }

    /// <summary>Every key with this prefix, in the culture's text with English filling the gaps (the strings scripts need).</summary>
    public IEnumerable<KeyValuePair<string, string>> WithPrefix(string prefix, CultureInfo culture) =>
        English.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(key => KeyValuePair.Create(key, Find(key, culture)!));

    private static Dictionary<string, Dictionary<string, string>> Load(Assembly assembly)
    {
        var languages = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            // strings/fr/site.json
            var parts = name[Prefix.Length..].Split('/');
            if (parts.Length != 2 || !parts[1].EndsWith(".json", StringComparison.Ordinal)) continue;

            using var stream = assembly.GetManifestResourceStream(name)!;
            var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                          ?? throw new InvalidDataException($"{name} is not a JSON object of strings.");

            if (!languages.TryGetValue(parts[0], out var merged)) languages[parts[0]] = merged = new(StringComparer.Ordinal);
            foreach (var (key, text) in strings)
            {
                if (!merged.TryAdd(key, text))
                    throw new InvalidDataException($"The key '{key}' is in more than one of the {parts[0]} string files ({name}).");
            }
        }

        return languages;
    }
}
