using System.Globalization;
using Microsoft.Extensions.Localization;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages.Shared;

/// <summary>
/// A starting point for a branding: every colour, both fonts and every size. Applying one fills in
/// the form; it is not stored, and nothing changes until the form is saved.
/// </summary>
public sealed record BrandingStarter(
    string Id, string NameKey,
    IReadOnlyDictionary<string, string> Light, IReadOnlyDictionary<string, string> Dark,
    string HeadingFont, string BodyFont, int TextSize, int HeadingWeight, int Radius, double Density)
{
    private static Dictionary<string, string> Colors(params string[] values) =>
        BrandColors.Tokens.Zip(values).ToDictionary(p => p.First, p => p.Second);

    /// <summary>Each starter's colours are in token order: bg, surface, surface-2, border, text, muted, accent, accent-text, danger, success, warning.</summary>
    public static readonly IReadOnlyList<BrandingStarter> All =
    [
        new("default", "branding.starter.default",
            BrandingTheme.DefaultColors.ToDictionary(p => p.Key, p => p.Value.Light),
            BrandingTheme.DefaultColors.ToDictionary(p => p.Key, p => p.Value.Dark),
            BrandFonts.SystemSans, BrandFonts.SystemSans, BrandingTheme.DefaultTextSize, BrandingTheme.DefaultHeadingWeight, BrandingTheme.DefaultRadius, 1),
        new("graphite", "branding.starter.graphite",
            Colors("#f5f5f4", "#ffffff", "#f0efed", "#e2e0dc", "#1c1b19", "#66625c", "#2b2a27", "#ffffff", "#c0362c", "#2f7a3f", "#946200"),
            Colors("#111110", "#1a1918", "#22211f", "#302e2b", "#ecebe8", "#a19d96", "#ecebe8", "#111110", "#ff8578", "#6fcf88", "#e2b552"),
            BrandFonts.SystemSans, BrandFonts.SystemSans, 14, 600, 6, 1),
        new("ocean", "branding.starter.ocean",
            Colors("#f3f7f9", "#ffffff", "#eaf1f4", "#d6e2e8", "#0f2530", "#4e6773", "#0b7285", "#ffffff", "#c92a2a", "#2b8a3e", "#a35d00"),
            Colors("#0a1418", "#0f1d23", "#15272e", "#1f363f", "#e3eef2", "#8fa8b2", "#3bc9db", "#06171c", "#ff8787", "#69db7c", "#ffc078"),
            BrandFonts.SystemSans, BrandFonts.SystemSans, 14, 650, 10, 1),
        new("forest", "branding.starter.forest",
            Colors("#f5f7f2", "#ffffff", "#edf1e8", "#dce3d3", "#1a2416", "#5b6852", "#2f7d32", "#ffffff", "#c0392b", "#2f7d32", "#9c6a00"),
            Colors("#0e130c", "#151c12", "#1c2618", "#2a3724", "#e6ede1", "#9aab90", "#7bc96f", "#0e130c", "#ff8a7a", "#7bc96f", "#e5b84f"),
            BrandFonts.SystemSerif, BrandFonts.SystemSans, 14, 650, 8, 1),
        new("sunset", "branding.starter.sunset",
            Colors("#fbf7f4", "#ffffff", "#f6efea", "#ebdfd6", "#2a1d17", "#74625a", "#c2410c", "#ffffff", "#c92a2a", "#2b8a3e", "#a35d00"),
            Colors("#161009", "#1f170f", "#281e15", "#3a2c20", "#f3e9e1", "#b39f91", "#ff8a4c", "#1a0d05", "#ff8787", "#69db7c", "#ffc078"),
            BrandFonts.SystemRounded, BrandFonts.SystemRounded, 14, 700, 12, 1),
        new("plum", "branding.starter.plum",
            Colors("#f8f6fb", "#ffffff", "#f1edf7", "#e2dbee", "#1f1830", "#645a78", "#7048e8", "#ffffff", "#c92a2a", "#2b8a3e", "#a35d00"),
            Colors("#110d18", "#191322", "#211a2d", "#31283f", "#ece6f5", "#a99cbd", "#b197fc", "#140c24", "#ff8787", "#69db7c", "#ffc078"),
            BrandFonts.SystemSans, BrandFonts.SystemSans, 14, 650, 8, 1),
        new("paper", "branding.starter.paper",
            Colors("#faf8f3", "#fffdf8", "#f3efe6", "#e4ddcf", "#1d1a14", "#6b6455", "#9b2c2c", "#ffffff", "#b42318", "#3f7d20", "#8f5b00"),
            Colors("#14120e", "#1c1a15", "#24211b", "#353128", "#ede8dc", "#ada593", "#f08c8c", "#1a0f0f", "#ff8b7e", "#8fce6b", "#e3b55a"),
            BrandFonts.SystemSerif, BrandFonts.SystemSerif, 15, 600, 4, 1.1),
        new("contrast", "branding.starter.contrast",
            Colors("#ffffff", "#ffffff", "#f2f2f2", "#6b6b6b", "#000000", "#3d3d3d", "#0037c1", "#ffffff", "#a4000f", "#005c1a", "#6b4100"),
            Colors("#000000", "#0a0a0a", "#1a1a1a", "#9a9a9a", "#ffffff", "#d6d6d6", "#8ab4ff", "#000000", "#ff9a9a", "#7ee2a0", "#ffd27a"),
            BrandFonts.SystemSans, BrandFonts.SystemSans, 15, 700, 4, 1.05)
    ];

    public static BrandingStarter Default => All[0];
}

/// <summary>The string keys for a colour token's and a weight's names on the form.</summary>
public static class BrandingLabels
{
    public static string Color(string token) => token switch
    {
        "bg" => "branding.color.bg", "surface" => "branding.color.surface", "surface-2" => "branding.color.surface2",
        "border" => "branding.color.border", "text" => "branding.color.text", "muted" => "branding.color.muted",
        "accent" => "branding.color.accent", "accent-text" => "branding.color.accentText", "danger" => "branding.color.danger",
        "success" => "branding.color.success", _ => "branding.color.warning"
    };

    public static readonly (int Weight, string Key)[] Weights =
    [
        (400, "branding.type.weight.w400"), (500, "branding.type.weight.w500"), (600, "branding.type.weight.w600"),
        (650, "branding.type.weight.w650"), (700, "branding.type.weight.w700"), (800, "branding.type.weight.w800")
    ];

    public static readonly (string Font, string Key)[] SystemFonts =
    [
        (BrandFonts.SystemSans, "branding.type.systemSans"), (BrandFonts.SystemSerif, "branding.type.systemSerif"),
        (BrandFonts.SystemRounded, "branding.type.systemRounded"), (BrandFonts.SystemMono, "branding.type.systemMono")
    ];
}

/// <summary>What the branding editor shows: the server's page, or a site's override of its visitor screens.</summary>
/// <param name="Values">What the form is filled with: the branding as saved, with the built-in values where it has none.</param>
/// <param name="Saved">The branding as saved, for which logos exist.</param>
/// <param name="Fonts">The library, for the font choices.</param>
/// <param name="Site">The site whose visitor screens this brands, or null for the server.</param>
/// <param name="Inherited">For a site: the server's branding it would otherwise use.</param>
public sealed record BrandingEditor(
    Branding Values, Branding Saved, IReadOnlyList<BrandFont> Fonts, bool AllowGoogleFonts, string? Site, Branding? Inherited,
    IReadOnlyList<string> Errors, string Action)
{
    public bool IsSite => Site is not null;

    /// <summary>The form's values: the branding's own, then the inherited ones, then the built-in look.</summary>
    public static Branding Filled(Branding? own, Branding? inherited)
    {
        var start = BrandingStarter.Default;
        var merged = (own ?? new()).Over(inherited);
        var light = new BrandColors();
        var dark = new BrandColors();
        foreach (var token in BrandColors.Tokens)
        {
            light[token] = merged.Light?[token] ?? start.Light[token];
            dark[token] = merged.Dark?[token] ?? start.Dark[token];
        }

        return new Branding
        {
            Name = own?.Name,
            SignInMessage = own?.SignInMessage,
            LogoLight = own?.LogoLight,
            LogoDark = own?.LogoDark,
            LogoHasName = own?.LogoHasName,
            Favicon = own?.Favicon,
            Light = light,
            Dark = dark,
            HeadingFont = merged.HeadingFont ?? start.HeadingFont,
            BodyFont = merged.BodyFont ?? start.BodyFont,
            TextSize = merged.TextSize ?? start.TextSize,
            HeadingWeight = merged.HeadingWeight ?? start.HeadingWeight,
            Radius = merged.Radius ?? start.Radius,
            Density = merged.Density ?? start.Density
        };
    }

    /// <summary>Each font choice's CSS stack, for the preview.</summary>
    public IReadOnlyDictionary<string, string> Stacks() =>
        BrandingTheme.SystemStacks.Concat(Fonts
                .Select(f => KeyValuePair.Create(f.Id, BrandingTheme.Stack(f.Id, Fonts, AllowGoogleFonts)))
                .Where(p => p.Value is not null)
                .Select(p => KeyValuePair.Create(p.Key, p.Value!)))
            .ToDictionary();

    /// <summary>The starters, with their names in the viewer's language, for the page's script.</summary>
    public object StartersFor(IStringLocalizer strings) => BrandingStarter.All.Select(s => new
    {
        id = s.Id,
        name = strings.Text(s.NameKey),
        light = s.Light,
        dark = s.Dark,
        headingFont = s.HeadingFont,
        bodyFont = s.BodyFont,
        textSize = s.TextSize,
        headingWeight = s.HeadingWeight,
        radius = s.Radius,
        density = s.Density
    });
}

/// <summary>
/// What the branding form posts. Colours arrive as <c>Light[bg]</c> and the like; images as files,
/// with the id of the one in use beside each (so a new upload survives a form that is shown again
/// after an error) and a box to remove it. Numbers are read with the invariant culture, digits and
/// a decimal point only, as a range or number field sends them.
/// </summary>
public sealed class BrandingInput
{
    public string? Name { get; set; }
    public string? SignInMessage { get; set; }
    public bool LogoHasName { get; set; }
    public Dictionary<string, string>? Light { get; set; }
    public Dictionary<string, string>? Dark { get; set; }
    public string? HeadingFont { get; set; }
    public string? BodyFont { get; set; }
    public string? TextSize { get; set; }
    public string? HeadingWeight { get; set; }
    public string? Radius { get; set; }
    public string? Density { get; set; }

    public IFormFile? LogoLightFile { get; set; }
    public IFormFile? LogoDarkFile { get; set; }
    public IFormFile? FaviconFile { get; set; }

    /// <summary>The image in use for each, as the form was drawn: the saved one, or one uploaded before an error.</summary>
    public string? LogoLightId { get; set; }
    public string? LogoDarkId { get; set; }
    public string? FaviconId { get; set; }

    public bool RemoveLogoLight { get; set; }
    public bool RemoveLogoDark { get; set; }
    public bool RemoveFavicon { get; set; }

    /// <summary>
    /// The branding the form describes, with uploads stored and removals applied, and why it cannot
    /// be saved. Values equal to what the site would get anyway (the built-in look for the server,
    /// the server's for a site) are left out, so a later change underneath still shows through. The
    /// branding comes back even with errors, as far as it could be read, so the form is shown again
    /// as it was posted; save it only when there are none.
    /// </summary>
    public async Task<(Branding Branding, List<string> Errors)> ToBrandingAsync(
        Branding saved, Branding? inherited, BrandingStore store, IStringLocalizer strings, CancellationToken ct)
    {
        var errors = new List<string>();

        async Task<string?> Image(IFormFile? file, bool remove, string? posted, string? current, string label)
        {
            // The id the form carried, when it is an image this server has; else what is saved.
            var inUse = posted is not null && store.AssetExists(posted) ? posted : current;
            if (file is { Length: > 0 })
            {
                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, ct);
                var (id, error) = await store.SaveAssetAsync(buffer.ToArray(), ct);
                if (error is not null) errors.Add($"{label}: {error}");
                return id ?? inUse;
            }
            return remove ? null : inUse;
        }

        var logoLight = await Image(LogoLightFile, RemoveLogoLight, LogoLightId, saved.LogoLight, strings.Text("branding.identity.logoLight"));
        var logoDark = await Image(LogoDarkFile, RemoveLogoDark, LogoDarkId, saved.LogoDark, strings.Text("branding.identity.logoDark"));
        var favicon = await Image(FaviconFile, RemoveFavicon, FaviconId, saved.Favicon, strings.Text("branding.identity.favicon"));

        var baseline = BrandingEditor.Filled(null, inherited);
        BrandColors? Colors(Dictionary<string, string>? posted, BrandColors? under)
        {
            if (posted is null) return null;
            var colors = new BrandColors();
            foreach (var token in BrandColors.Tokens)
            {
                if (!posted.TryGetValue(token, out var value) || string.IsNullOrWhiteSpace(value)) continue;
                value = value.Trim().ToLowerInvariant();
                if (value != under?[token]) colors[token] = value;
            }
            return colors.IsEmpty ? null : colors;
        }

        int? Whole(string? text, int? under)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                errors.Add(strings.Text("branding.form.notANumber", new { value = text }));
                return null;
            }
            return value == under ? null : value;
        }

        double? density = null;
        if (!string.IsNullOrWhiteSpace(Density))
        {
            if (double.TryParse(Density, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
                density = Math.Abs(d - (baseline.Density ?? 1)) < 0.001 ? null : Math.Round(d, 2);
            else
                errors.Add(strings.Text("branding.form.notANumber", new { value = Density }));
        }

        var branding = new Branding
        {
            Name = Name,
            SignInMessage = SignInMessage,
            LogoLight = logoLight,
            LogoDark = logoDark,
            LogoHasName = logoLight is not null || logoDark is not null ? LogoHasName : null,
            Favicon = favicon,
            Light = Colors(Light, baseline.Light),
            Dark = Colors(Dark, baseline.Dark),
            HeadingFont = HeadingFont == baseline.HeadingFont ? null : HeadingFont,
            BodyFont = BodyFont == baseline.BodyFont ? null : BodyFont,
            TextSize = Whole(TextSize, baseline.TextSize),
            HeadingWeight = Whole(HeadingWeight, baseline.HeadingWeight),
            Radius = Whole(Radius, baseline.Radius),
            Density = density
        };
        return (branding, errors);
    }
}
