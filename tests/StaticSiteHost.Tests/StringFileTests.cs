using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;
using StaticSiteHost.Localization;

namespace StaticSiteHost.Tests;

/// <summary>
/// Keeps the string files (src/StaticSiteHost/Resources/strings) honest: every key the code uses is
/// there, every key there is used, each file holds its own area's keys, and every translation has
/// the same keys and placeholders as English. See .claude/skills/localization.
/// </summary>
public partial class StringFileTests
{
    private static readonly string Source = FindSource();

    private static string FindSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "StaticSiteHost");
            if (Directory.Exists(Path.Combine(candidate, "Resources", "strings"))) return candidate;
        }
        throw new DirectoryNotFoundException("src/StaticSiteHost/Resources/strings not found above the test's folder.");
    }

    private static string StringsRoot => Path.Combine(Source, "Resources", "strings");

    /// <summary>Every language's files: language → file area → key → text.</summary>
    private static Dictionary<string, Dictionary<string, Dictionary<string, string>>> Files() =>
        Directory.GetDirectories(StringsRoot).ToDictionary(
            dir => Path.GetFileName(dir),
            dir => Directory.GetFiles(dir, "*.json").ToDictionary(
                file => Path.GetFileNameWithoutExtension(file),
                file => JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!));

    private static Dictionary<string, string> English() =>
        Files()["en"].SelectMany(f => f.Value).ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void The_build_loads_every_language_and_english_comes_first()
    {
        var catalog = new StringCatalog();
        Assert.Equal("en", catalog.Languages[0].Name);
        Assert.Equal(Files().Keys.Order(), catalog.Languages.Select(c => c.Name).Order());
    }

    [Fact]
    public void Each_file_holds_only_its_own_areas_keys_and_no_key_is_in_two_files()
    {
        foreach (var (language, files) in Files())
        {
            var seen = new HashSet<string>();
            foreach (var (area, strings) in files)
            {
                // js-editor.json holds js.editor.*; site.json holds site.*.
                var prefix = area.Replace('-', '.') + ".";
                foreach (var key in strings.Keys)
                {
                    Assert.True(key.StartsWith(prefix, StringComparison.Ordinal), $"{language}/{area}.json: '{key}' should start with '{prefix}'.");
                    Assert.True(Key().IsMatch(key), $"{language}/{area}.json: '{key}' is not a key (lower camel case segments joined by dots).");
                    Assert.True(seen.Add(key), $"{language}: '{key}' is in more than one file.");
                }
            }
        }
    }

    [Fact]
    public void Every_translation_has_englishs_keys_and_placeholders()
    {
        var english = English();
        foreach (var (language, files) in Files().Where(f => f.Key != "en"))
        {
            var strings = files.SelectMany(f => f.Value).ToDictionary(p => p.Key, p => p.Value);
            Assert.Empty(english.Keys.Except(strings.Keys).Select(k => $"{language} lacks {k}"));
            Assert.Empty(strings.Keys.Except(english.Keys).Select(k => $"{language} has {k}, which English does not"));

            foreach (var (key, text) in strings)
            {
                Assert.True(TextFormat.PlaceholdersOf(english[key]).SetEquals(TextFormat.PlaceholdersOf(text)),
                    $"{language} {key}: its placeholders differ from English's ('{english[key]}').");
                Assert.Equal(Count(english[key], '`'), Count(text, '`'));
            }
        }

        static int Count(string text, char c) => text.Count(x => x == c);
    }

    [Fact]
    public void Counts_have_both_forms()
    {
        var english = English();
        foreach (var key in english.Keys.Where(k => k.EndsWith(".one", StringComparison.Ordinal)))
            Assert.True(english.ContainsKey(key[..^4] + ".other"), $"{key} has no .other");
        foreach (var key in english.Keys.Where(k => k.EndsWith(".other", StringComparison.Ordinal)))
            Assert.True(english.ContainsKey(key[..^6] + ".one"), $"{key} has no .one");
    }

    /// <summary>The code that shows text: the pages, the visitor screens and the scripts.</summary>
    /// <summary>Code that names keys: the pages and scripts, and the services and endpoints whose messages pages show.</summary>
    private static readonly string[] TextCode = ["Pages", "Serving", "Tls", "Services", "Endpoints", Path.Combine("wwwroot", "js")];

    /// <summary>
    /// A key the code names is a quoted literal that looks like one (an area, a dot, more) in the
    /// code that shows text. Audit-log action names look the same ("site.create") and are left out
    /// by their line, file names ("site.js") by their extension. Plural names are the key without
    /// its .one / .other.
    /// </summary>
    private static HashSet<string> KeysInCode()
    {
        var areas = string.Join("|", Files()["en"].Keys.Select(a => Regex.Escape(a.Replace('-', '.'))).OrderByDescending(a => a.Length));
        var literal = new Regex($"""(?<q>["'])(?<key>(?:{areas})\.[A-Za-z0-9.]+)\k<q>""");

        var files = TextCode
            .Select(dir => Path.Combine(Source, dir))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".cshtml") || f.EndsWith(".js")) && !f.EndsWith(".min.js"));

        return files
            .SelectMany(File.ReadLines)
            .Where(line => !line.Contains("audit", StringComparison.OrdinalIgnoreCase))
            .SelectMany(line => literal.Matches(line).Select(m => m.Groups["key"].Value))
            .Where(k => !FileExtensions.Contains(k[(k.LastIndexOf('.') + 1)..]))
            .Where(k => !NotKeys.Contains(k))
            .ToHashSet();
    }

    /// <summary>File names that look like keys ("site.js", "account.bin") end in one of these.</summary>
    private static readonly HashSet<string> FileExtensions = ["js", "json", "bin", "pem", "txt", "cs", "csproj", "html", "css", "zip", "md", "linq", "xml"];

    /// <summary>
    /// Literals in the text code that look like keys and are not: the realtime event the site page's
    /// examples listen for, and the audit actions <c>SiteRuleService</c> passes along.
    /// </summary>
    private static readonly HashSet<string> NotKeys = ["site.deployed", "site.headers", "site.redirects"];

    [Fact]
    public void Every_key_the_code_names_is_in_english()
    {
        var english = English();
        var missing = KeysInCode()
            .Where(k => !english.ContainsKey(k) && !english.ContainsKey(k + ".other"))
            .Order();
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_english_key_is_used()
    {
        var used = KeysInCode();
        var unused = English().Keys
            .Select(k => Regex.Replace(k, @"\.(one|other)$", ""))
            .Distinct()
            .Where(k => !used.Contains(k))
            .Order();
        Assert.Empty(unused);
    }

    [GeneratedRegex(@"^[a-z][a-zA-Z0-9]*(\.[a-z][a-zA-Z0-9]*)+$")]
    private static partial Regex Key();

    // ---------------------------------------------------------------- the format itself

    [Fact]
    public void Values_are_encoded_and_cannot_become_markup()
    {
        var html = Render(TextFormat.Html("Saved for {domain}.", TextFormat.Values([new { domain = "<b>`x`</b>" }])));
        Assert.Equal("Saved for &lt;b&gt;`x`&lt;/b&gt;.", html);
    }

    [Fact]
    public void Marks_become_code_bold_and_links()
    {
        var html = Render(TextFormat.Html("Set `Cache-Control` **now**, or [read more](url) & go.",
            TextFormat.Values([new { url = "/docs?a=1&b=2" }])));
        Assert.Equal("Set <span class=\"mono\">Cache-Control</span> <strong>now</strong>, or <a href=\"/docs?a=1&amp;b=2\">read more</a> &amp; go.", html);
    }

    [Fact]
    public void Html_values_pass_through_and_plain_text_drops_the_marks()
    {
        var values = TextFormat.Values([new { link = new HtmlString("<a href=\"/x\">here</a>") }]);
        Assert.Equal("Go <a href=\"/x\">here</a>.", Render(TextFormat.Html("Go {link}.", values)));
        Assert.Equal("Go here.", TextFormat.Text("Go {link}.", values));
        Assert.Equal("Set Cache-Control now, or read more.", TextFormat.Text("Set `Cache-Control` **now**, or [read more](url).", TextFormat.Values(null)));
        // A value keeps marks of its own, such as a service error that quotes a header.
        Assert.Equal("Failed: `X-Key` is **wrong**.", TextFormat.Text("Failed: {error}.", TextFormat.Values([new { error = "`X-Key` is **wrong**" }])));
    }

    [Fact]
    public void Positional_values_and_counts_work()
    {
        Assert.Equal("a and b", TextFormat.Text("{0} and {1}", TextFormat.Values(["a", "b"])));
        Assert.Equal("one", TextFormat.PluralCategory(1, CultureInfo.GetCultureInfo("en")));
        Assert.Equal("other", TextFormat.PluralCategory(0, CultureInfo.GetCultureInfo("en")));
        Assert.Equal("one", TextFormat.PluralCategory(0, CultureInfo.GetCultureInfo("fr")));
        Assert.Equal("other", TextFormat.PluralCategory(1, CultureInfo.GetCultureInfo("zh-Hans")));
    }

    [Fact]
    public void A_language_falls_back_to_its_parent_then_english()
    {
        var catalog = new StringCatalog(new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = new() { ["common.a"] = "A", ["common.b"] = "B" },
            ["fr"] = new() { ["common.a"] = "À" }
        });

        Assert.Equal("À", catalog.Find("common.a", CultureInfo.GetCultureInfo("fr-CA")));
        Assert.Equal("B", catalog.Find("common.b", CultureInfo.GetCultureInfo("fr")));
        Assert.Null(catalog.Find("common.c", CultureInfo.GetCultureInfo("fr")));
        Assert.Equal("fr", catalog.Resolve(CultureInfo.GetCultureInfo("fr-BE")).Name);
        Assert.Equal("en", catalog.Resolve(CultureInfo.GetCultureInfo("de")).Name);
    }

    private static string Render(IHtmlContent content)
    {
        using var writer = new StringWriter();
        content.WriteTo(writer, System.Text.Encodings.Web.HtmlEncoder.Default);
        return writer.ToString();
    }
}
