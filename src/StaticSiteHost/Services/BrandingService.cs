using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services;

/// <summary>What a page shows of a branding: its name, its images' addresses and its stylesheet's.</summary>
/// <param name="Name">The name to show; the built-in one when the branding has none.</param>
/// <param name="LogoLight">The logo's address on light backgrounds, or null for the built-in mark.</param>
/// <param name="LogoDark">The logo's address on dark backgrounds; the light one's when there is no dark one.</param>
/// <param name="LogoHasName">The logo shows the name itself, so the name is not written beside it.</param>
public sealed record BrandingView(
    string Name, bool HasOwnName, string? LogoLight, string? LogoDark, bool LogoHasName, string? Favicon, string? SignInMessage, string StylesheetUrl);

/// <summary>
/// The operations the Branding page, the API and the tools share, over <see cref="BrandingStore"/>:
/// saving with an audit line, adding fonts, and what pages need to show a branding.
/// </summary>
public sealed partial class BrandingService
{
    public const string Prefix = "/_host/brand";

    private readonly BrandingStore _store;
    private readonly GoogleFonts _google;
    private readonly AuditLog _audit;
    private readonly ConcurrentDictionary<string, (string Version, string Css)> _stylesheets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<BrandingService> _logger;

    public BrandingService(BrandingStore store, GoogleFonts google, AuditLog audit, SiteStore sites, ILogger<BrandingService> logger)
    {
        _store = store;
        _google = google;
        _audit = audit;
        _logger = logger;
        _store.Changed += _stylesheets.Clear;
        sites.Changed += _stylesheets.Clear;
    }

    public BrandingStore Store => _store;

    public static string AssetUrl(string id) => $"{Prefix}/a/{id}";

    public static string FontUrl(string font, string file) => $"{Prefix}/f/{font}/{file}";

    /// <summary>
    /// What the management app (no site) or a site's visitor screens show. When the branding cannot
    /// be read (a damaged branding.json, say) it is the built-in look, and the error is logged: no
    /// page, and no visitor screen, fails because of its branding.
    /// </summary>
    public async Task<BrandingView> ViewAsync(SiteRecord? site, string defaultName, CancellationToken ct = default)
    {
        try
        {
            return await ReadViewAsync(site, defaultName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The branding could not be read, so pages use the built-in look until it is fixed");
            return new BrandingView(defaultName, false, null, null, false, null, null, $"{Prefix}/brand.css?v=built-in");
        }
    }

    private async Task<BrandingView> ReadViewAsync(SiteRecord? site, string defaultName, CancellationToken ct)
    {
        var branding = await _store.ForAsync(site, ct);
        var (version, _) = await StylesheetAsync(site, ct);
        var light = branding.LogoLight ?? branding.LogoDark;
        var dark = branding.LogoDark ?? branding.LogoLight;
        return new BrandingView(
            branding.Name ?? defaultName,
            branding.Name is not null,
            light is null ? null : AssetUrl(light),
            dark is null ? null : AssetUrl(dark),
            light is not null && branding.LogoHasName == true,
            branding.Favicon is null ? null : AssetUrl(branding.Favicon),
            site is null ? branding.SignInMessage : null,
            $"{Prefix}/brand.css?v={version}");
    }

    /// <summary>The stylesheet for the management app (no site) or a site's visitor screens, with its version.</summary>
    public async Task<(string Version, string Css)> StylesheetAsync(SiteRecord? site, CancellationToken ct = default)
    {
        try
        {
            return await ReadStylesheetAsync(site, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The branding could not be read, so its stylesheet is empty until it is fixed");
            return ("built-in", "/* The branding could not be read; the built-in look is used. */\n");
        }
    }

    private async Task<(string Version, string Css)> ReadStylesheetAsync(SiteRecord? site, CancellationToken ct)
    {
        var key = site?.Domain ?? "";
        var version = await _store.VersionAsync(site, ct);
        if (_stylesheets.TryGetValue(key, out var cached) && cached.Version == version) return cached;

        var settings = await _store.ReadAsync(ct);
        var branding = await _store.ForAsync(site, ct);
        var css = BrandingTheme.Css(branding, settings.Fonts, settings.AllowGoogleFonts, FontUrl);
        _stylesheets[key] = (version, css);
        return (version, css);
    }

    // ---------------------------------------------------------------- saving

    public async Task<List<string>> SetServerAsync(Branding branding, string actor, CancellationToken ct = default)
    {
        var errors = await _store.SetServerAsync(Normalize(branding), ct);
        if (errors.Count == 0) await _audit.WriteAsync("branding.set", actor);
        return errors;
    }

    public async Task<List<string>> SetSiteAsync(SiteRecord site, Branding? branding, string actor, CancellationToken ct = default)
    {
        var errors = await _store.SetSiteAsync(site, branding is null ? null : Normalize(branding), ct);
        if (errors.Count == 0) await _audit.WriteAsync(branding is null ? "site.branding.remove" : "site.branding.set", actor, new { domain = site.Domain });
        return errors;
    }

    public async Task SetAllowGoogleFontsAsync(bool allow, string actor, CancellationToken ct = default)
    {
        await _store.SetAllowGoogleFontsAsync(allow, ct);
        await _audit.WriteAsync(allow ? "branding.google-fonts.allow" : "branding.google-fonts.disallow", actor);
    }

    /// <summary>Text trimmed, empty text and colours made null, hex colours in lower case.</summary>
    public static Branding Normalize(Branding branding)
    {
        static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        static BrandColors? Colors(BrandColors? colors)
        {
            if (colors is null) return null;
            var normal = new BrandColors();
            foreach (var token in BrandColors.Tokens) normal[token] = Text(colors[token])?.ToLowerInvariant();
            return normal.IsEmpty ? null : normal;
        }

        return new Branding
        {
            Name = Text(branding.Name),
            SignInMessage = Text(branding.SignInMessage),
            LogoLight = Text(branding.LogoLight),
            LogoDark = Text(branding.LogoDark),
            LogoHasName = branding.LogoHasName == true ? true : null,
            Favicon = Text(branding.Favicon),
            Light = Colors(branding.Light),
            Dark = Colors(branding.Dark),
            HeadingFont = Text(branding.HeadingFont),
            BodyFont = Text(branding.BodyFont),
            TextSize = branding.TextSize,
            HeadingWeight = branding.HeadingWeight,
            Radius = branding.Radius,
            Density = branding.Density is { } d ? Math.Round(d, 2) : null
        };
    }

    // ---------------------------------------------------------------- fonts

    /// <summary>Adds a font from Google Fonts, downloaded here or loaded from Google, if Google Fonts are allowed.</summary>
    public async Task<(BrandFont? Font, string? Error)> AddGoogleFontAsync(string family, bool download, string actor, CancellationToken ct = default)
    {
        var settings = await _store.ReadAsync(ct);
        if (!settings.AllowGoogleFonts)
            return (null, ServerText.Get("branding.google.error.notAllowed"));

        var (url, css, error) = await _google.StylesheetAsync(family, ct);
        if (url is null || css is null) return (null, error);

        var files = new List<(byte[] Data, string Weight, string Style, string? UnicodeRange)>();
        if (download)
        {
            var (downloaded, downloadError) = await _google.DownloadAsync(css, ct);
            if (downloaded is null) return (null, downloadError);
            files = downloaded;
        }

        var result = await _store.AddFontAsync(family, download ? BrandFont.GoogleDownloaded : BrandFont.GoogleLinked, files,
            download ? null : url, actor, ct);
        if (result.Font is not null)
            await _audit.WriteAsync("branding.font.add", actor, new { family = result.Font.Family, kind = result.Font.Kind, files = result.Font.Files.Count });
        return result;
    }

    /// <summary>Adds uploaded font files as one family, each file's weight and style read from its name ("Inter-SemiBoldItalic.woff2").</summary>
    public async Task<(BrandFont? Font, string? Error)> AddUploadedFontAsync(
        string family, IReadOnlyList<(string FileName, byte[] Data)> uploads, string actor, CancellationToken ct = default)
    {
        var files = uploads.Select(u => (u.Data, WeightOf(u.FileName), StyleOf(u.FileName), (string?)null)).ToList();
        var result = await _store.AddFontAsync(family, BrandFont.Uploaded, files, null, actor, ct);
        if (result.Font is not null)
            await _audit.WriteAsync("branding.font.add", actor, new { family = result.Font.Family, kind = result.Font.Kind, files = result.Font.Files.Count });
        return result;
    }

    public async Task<bool> RemoveFontAsync(string id, string actor, CancellationToken ct = default)
    {
        var removed = await _store.RemoveFontAsync(id, ct);
        if (removed) await _audit.WriteAsync("branding.font.remove", actor, new { id });
        return removed;
    }

    [GeneratedRegex(@"(?<![0-9])([1-9]00)(?![0-9])")]
    private static partial Regex NumericWeight();

    /// <summary>A weight from a font file's name: a number such as 600, or a word such as SemiBold; regular otherwise.</summary>
    public static string WeightOf(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
        if (NumericWeight().Match(name) is { Success: true } number) return number.Value;
        (string Word, string Weight)[] words =
        [
            ("extrablack", "950"), ("ultrablack", "950"), ("black", "900"), ("heavy", "900"),
            ("extrabold", "800"), ("ultrabold", "800"), ("semibold", "600"), ("demibold", "600"), ("bold", "700"),
            ("medium", "500"), ("extralight", "200"), ("ultralight", "200"), ("thin", "100"), ("hairline", "100"), ("light", "300")
        ];
        foreach (var (word, weight) in words)
        {
            if (name.Contains(word, StringComparison.Ordinal)) return weight;
        }
        return "400";
    }

    public static string StyleOf(string fileName) =>
        Path.GetFileNameWithoutExtension(fileName).Contains("italic", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileNameWithoutExtension(fileName).Contains("oblique", StringComparison.OrdinalIgnoreCase)
            ? "italic"
            : "normal";
}
