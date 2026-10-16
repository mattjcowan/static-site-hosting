using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services;

/// <summary>
/// What a <see cref="Branding"/> means as CSS: the built-in values (the same as site.css's), the
/// system font stacks, the checks a branding must pass, and the stylesheet that sets its tokens
/// (<c>/_host/brand/brand.css</c>). The stylesheet only sets the values the branding has, each colour
/// as <c>light-dark(light, dark)</c>, so whatever it leaves out stays as site.css has it.
/// </summary>
public static partial class BrandingTheme
{
    public const int MinTextSize = 12, MaxTextSize = 18, DefaultTextSize = 14;
    public const int MinHeadingWeight = 400, MaxHeadingWeight = 900, DefaultHeadingWeight = 650;
    public const int MinRadius = 0, MaxRadius = 20, DefaultRadius = 8;
    public const double MinDensity = 0.8, MaxDensity = 1.25, DefaultDensity = 1;
    public const int MaxNameLength = 60;
    public const int MaxSignInMessageLength = 200;

    /// <summary>site.css's colours, light and dark, by token.</summary>
    public static readonly IReadOnlyDictionary<string, (string Light, string Dark)> DefaultColors = new Dictionary<string, (string, string)>
    {
        ["bg"] = ("#f6f7f9", "#0d0f13"),
        ["surface"] = ("#ffffff", "#15181e"),
        ["surface-2"] = ("#f1f3f6", "#1c2028"),
        ["border"] = ("#e2e5ea", "#272c35"),
        ["text"] = ("#15181e", "#e7e9ee"),
        ["muted"] = ("#5f6776", "#9097a5"),
        ["accent"] = ("#2f5be8", "#7d9bff"),
        ["accent-text"] = ("#ffffff", "#0d0f13"),
        ["danger"] = ("#cf222e", "#ff7b82"),
        ["success"] = ("#1a7f37", "#56d38f"),
        ["warning"] = ("#9a6700", "#e6b84a")
    };

    /// <summary>Chinese, Japanese and Korean fallbacks, last in every stack so the system draws what the font lacks.</summary>
    private const string Cjk = "\"PingFang SC\", \"Hiragino Sans GB\", \"Microsoft YaHei\", \"Noto Sans CJK SC\", \"Source Han Sans SC\"";
    private const string CjkSerif = "\"Songti SC\", \"STSong\", \"SimSun\", \"Noto Serif CJK SC\", \"Source Han Serif SC\"";

    public static readonly IReadOnlyDictionary<string, string> SystemStacks = new Dictionary<string, string>
    {
        [BrandFonts.SystemSans] = $"ui-sans-serif, system-ui, -apple-system, \"Segoe UI\", Roboto, \"Helvetica Neue\", Arial, {Cjk}, sans-serif",
        [BrandFonts.SystemSerif] = $"ui-serif, \"Iowan Old Style\", \"Palatino Linotype\", Palatino, Georgia, Cambria, \"Times New Roman\", {CjkSerif}, serif",
        [BrandFonts.SystemRounded] = $"ui-rounded, \"SF Pro Rounded\", \"Hiragino Maru Gothic ProN\", Quicksand, Nunito, \"Arial Rounded MT\", {Cjk}, sans-serif",
        [BrandFonts.SystemMono] = $"ui-monospace, SFMono-Regular, \"SF Mono\", Menlo, Consolas, \"Liberation Mono\", {Cjk}, monospace"
    };

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Hex();

    public static bool IsColor(string? value) => value is not null && Hex().IsMatch(value);

    /// <summary>Why a branding cannot be saved, as one plain sentence each; empty when it can.</summary>
    /// <param name="fonts">The library's font ids.</param>
    /// <param name="assetExists">Whether an asset id is a file the server has.</param>
    /// <param name="site">A site's branding, which has no sign-in message.</param>
    public static List<string> Validate(Branding branding, IReadOnlyCollection<string> fonts, Func<string, bool> assetExists, bool site)
    {
        var errors = new List<string>();

        if (branding.Name is { Length: > MaxNameLength })
            errors.Add($"The name can be at most {MaxNameLength} characters.");
        if (branding.Name is { } name && name.Any(char.IsControl))
            errors.Add("The name cannot contain line breaks or control characters.");
        if (site && branding.SignInMessage is not null)
            errors.Add("A site has no sign-in screen of its own, so it takes no sign-in message.");
        if (branding.SignInMessage is { Length: > MaxSignInMessageLength })
            errors.Add($"The sign-in message can be at most {MaxSignInMessageLength} characters.");

        foreach (var (label, asset) in new[] { ("light logo", branding.LogoLight), ("dark logo", branding.LogoDark), ("browser icon", branding.Favicon) })
        {
            if (asset is not null && !assetExists(asset)) errors.Add($"The {label} '{asset}' is not an uploaded image.");
        }

        foreach (var (scheme, colors) in new[] { ("light", branding.Light), ("dark", branding.Dark) })
        {
            if (colors is null) continue;
            foreach (var token in BrandColors.Tokens)
            {
                if (colors[token] is { } value && !IsColor(value))
                    errors.Add($"The {scheme} colour '{BrandColors.JsonName(token)}' must be written #rrggbb, such as #2f5be8; it is '{value}'.");
            }
        }

        foreach (var (label, font) in new[] { ("heading", branding.HeadingFont), ("body", branding.BodyFont) })
        {
            if (font is not null && !BrandFonts.IsSystem(font) && !fonts.Contains(font))
                errors.Add($"The {label} font '{font}' is neither a system font ({string.Join(", ", BrandFonts.System)}) nor one in the font library.");
        }

        if (branding.TextSize is < MinTextSize or > MaxTextSize)
            errors.Add($"The text size must be between {MinTextSize} and {MaxTextSize} pixels.");
        if (branding.HeadingWeight is < MinHeadingWeight or > MaxHeadingWeight)
            errors.Add($"The heading weight must be between {MinHeadingWeight} and {MaxHeadingWeight}.");
        if (branding.Radius is < MinRadius or > MaxRadius)
            errors.Add($"The corner radius must be between {MinRadius} and {MaxRadius} pixels.");
        if (branding.Density is { } density && (!double.IsFinite(density) || density < MinDensity || density > MaxDensity))
            errors.Add($"The density must be between {MinDensity.ToString(CultureInfo.InvariantCulture)} and {MaxDensity.ToString(CultureInfo.InvariantCulture)}.");

        return errors;
    }

    /// <summary>
    /// The stylesheet for a branding: Google's stylesheets for fonts loaded from Google (only while
    /// Google Fonts are allowed), then <c>@font-face</c> for the fonts served here, then the tokens.
    /// </summary>
    /// <param name="fontUrl">The address a font file is served at, from its font id and file name.</param>
    public static string Css(Branding branding, IReadOnlyList<BrandFont> library, bool allowGoogle, Func<string, string, string> fontUrl)
    {
        var css = new StringBuilder("/* The server's branding: set under Branding. */\n");
        var used = branding.Fonts().Distinct().Select(id => library.FirstOrDefault(f => f.Id == id)).OfType<BrandFont>().ToList();

        foreach (var font in used.Where(f => f.Kind == BrandFont.GoogleLinked && allowGoogle && f.GoogleCssUrl is not null))
            css.Append("@import url(\"").Append(font.GoogleCssUrl).Append("\");\n");

        foreach (var font in used.Where(f => f.Kind != BrandFont.GoogleLinked))
            css.Append(FontFaces(font, fontUrl));

        var tokens = new List<string>();
        foreach (var token in BrandColors.Tokens)
        {
            var light = branding.Light?[token];
            var dark = branding.Dark?[token];
            if (light is null && dark is null) continue;
            var (defaultLight, defaultDark) = DefaultColors[token];
            tokens.Add($"--{token}: light-dark({light ?? defaultLight}, {dark ?? defaultDark});");
        }

        if (branding.BodyFont is { } body && Stack(body, library, allowGoogle) is { } bodyStack) tokens.Add($"--font-body: {bodyStack};");
        if (branding.HeadingFont is { } heading && Stack(heading, library, allowGoogle) is { } headingStack) tokens.Add($"--font-heading: {headingStack};");
        if (branding.TextSize is { } size) tokens.Add($"--text-size: {size}px;");
        if (branding.HeadingWeight is { } weight) tokens.Add($"--heading-weight: {weight};");
        if (branding.Radius is { } radius)
        {
            tokens.Add($"--radius: {radius}px;");
            tokens.Add($"--radius-sm: {Math.Max(0, (int)Math.Round(radius * 0.75))}px;");
        }
        if (branding.Density is { } density) tokens.Add($"--density: {density.ToString("0.##", CultureInfo.InvariantCulture)};");

        if (tokens.Count > 0) css.Append(":root {\n  ").Append(string.Join("\n  ", tokens)).Append("\n}\n");
        return css.ToString();
    }

    /// <summary>
    /// The CSS font stack for a font choice: a system stack, or the library font's family in front of
    /// the system sans stack. Null for a font the library no longer has, or one loaded from Google
    /// while Google Fonts are not allowed, so the built-in stack stays.
    /// </summary>
    public static string? Stack(string font, IReadOnlyList<BrandFont> library, bool allowGoogle)
    {
        if (SystemStacks.TryGetValue(font, out var stack)) return stack;
        if (library.FirstOrDefault(f => f.Id == font) is not { } entry) return null;
        if (entry.Kind == BrandFont.GoogleLinked && !allowGoogle) return null;
        return $"{CssString(entry.Family)}, {SystemStacks[BrandFonts.SystemSans]}";
    }

    /// <summary>The <c>@font-face</c> rules for a font served from this server.</summary>
    public static string FontFaces(BrandFont font, Func<string, string, string> fontUrl)
    {
        var css = new StringBuilder();
        foreach (var file in font.Files)
        {
            css.Append("@font-face { font-family: ").Append(CssString(font.Family))
               .Append("; src: url(\"").Append(fontUrl(font.Id, file.File)).Append("\") format(\"").Append(Format(file.File)).Append("\")")
               .Append("; font-weight: ").Append(file.Weight)
               .Append("; font-style: ").Append(file.Style == "italic" ? "italic" : "normal")
               .Append("; font-display: swap");
            if (file.UnicodeRange is { Length: > 0 } range) css.Append("; unicode-range: ").Append(range);
            css.Append("; }\n");
        }
        return css.ToString();
    }

    private static string Format(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".woff2" => "woff2",
        ".woff" => "woff",
        ".otf" => "opentype",
        _ => "truetype"
    };

    /// <summary>A family name as a CSS string: quoted, with nothing that could end it.</summary>
    public static string CssString(string value) =>
        "\"" + new string(value.Where(c => c is not ('"' or '\\' or '<' or '>' or ';' or '{' or '}') && !char.IsControl(c)).ToArray()) + "\"";

    // ---------------------------------------------------------------- contrast

    /// <summary>
    /// The WCAG contrast ratio of two colours, from 1 (the same) to 21 (black on white). Body text
    /// needs 4.5; large text and controls need 3.
    /// </summary>
    public static double Contrast(string foreground, string background)
    {
        var (a, b) = (Luminance(foreground), Luminance(background));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int at)
        {
            var c = int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }

    /// <summary>The pairs worth checking, and the ratio each needs.</summary>
    public static readonly (string Foreground, string Background, double Needs)[] ContrastPairs =
    [
        ("text", "bg", 4.5), ("text", "surface", 4.5), ("muted", "surface", 4.5),
        ("accent", "surface", 3), ("accent-text", "accent", 4.5), ("danger", "surface", 4.5)
    ];

    /// <summary>Colour pairs in a branding that are hard to read, as plain sentences, for the API and the tools.</summary>
    public static List<string> ContrastWarnings(Branding branding)
    {
        var warnings = new List<string>();
        foreach (var (scheme, colors, dark) in new[] { ("light", branding.Light, false), ("dark", branding.Dark, true) })
        {
            string Color(string token) => colors?[token] is { } set && IsColor(set) ? set : dark ? DefaultColors[token].Dark : DefaultColors[token].Light;
            foreach (var (fg, bg, needs) in ContrastPairs)
            {
                var ratio = Contrast(Color(fg), Color(bg));
                if (ratio < needs)
                    warnings.Add($"In the {scheme} colours, '{BrandColors.JsonName(fg)}' on '{BrandColors.JsonName(bg)}' has a contrast of {ratio.ToString("0.0", CultureInfo.InvariantCulture)}:1; it needs {needs.ToString("0.#", CultureInfo.InvariantCulture)}:1 to be easy to read.");
            }
        }
        return warnings;
    }
}
