using System.Text.Json.Serialization;

namespace StaticSiteHost.Models;

/// <summary>
/// How the management app and the visitor screens look: a name, logos, colours, fonts and sizes.
/// Every field is optional; one left out keeps the built-in look (<see cref="Services.BrandingTheme"/>).
/// The server has one (<see cref="BrandingSettings.Server"/>), used by the management app and by every
/// site's visitor screens (passcode, not found, errors); a site may have its own
/// (<see cref="SiteRecord.Branding"/>), merged over the server's, for its visitor screens only.
/// Only administrators change either.
/// A field the JSON does not have is refused rather than ignored, so a misspelt one is not quietly
/// lost.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class Branding
{
    /// <summary>The name in the menu, the browser tab and the sign-in screen; on a site, the name its visitor screens show.</summary>
    public string? Name { get; set; }

    /// <summary>A line on the sign-in screen, under its heading. The server's only.</summary>
    public string? SignInMessage { get; set; }

    /// <summary>The logo on light backgrounds: an asset id (<see cref="Services.BrandingStore"/>).</summary>
    public string? LogoLight { get; set; }

    /// <summary>The logo on dark backgrounds; the light one is used when there is none.</summary>
    public string? LogoDark { get; set; }

    /// <summary>The logo already shows the name, so it is shown alone, without the name beside it.</summary>
    public bool? LogoHasName { get; set; }

    /// <summary>The browser tab's icon: an asset id.</summary>
    public string? Favicon { get; set; }

    public BrandColors? Light { get; set; }

    public BrandColors? Dark { get; set; }

    /// <summary>A system stack (<see cref="BrandFonts"/>) or a font id from the library.</summary>
    public string? HeadingFont { get; set; }

    public string? BodyFont { get; set; }

    /// <summary>The base text size in pixels, 12 to 18.</summary>
    public int? TextSize { get; set; }

    /// <summary>Heading weight, 400 to 900.</summary>
    public int? HeadingWeight { get; set; }

    /// <summary>Corner radius in pixels, 0 to 20.</summary>
    public int? Radius { get; set; }

    /// <summary>Spacing: 1 is the default, 0.8 tighter, 1.25 roomier.</summary>
    public double? Density { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        Name is null && SignInMessage is null && LogoLight is null && LogoDark is null && LogoHasName is null && Favicon is null &&
        (Light?.IsEmpty ?? true) && (Dark?.IsEmpty ?? true) && HeadingFont is null && BodyFont is null &&
        TextSize is null && HeadingWeight is null && Radius is null && Density is null;

    /// <summary>This one's fields where it has them, <paramref name="under"/>'s where it does not.</summary>
    public Branding Over(Branding? under) => under is null ? this : new Branding
    {
        Name = Name ?? under.Name,
        SignInMessage = SignInMessage ?? under.SignInMessage,
        LogoLight = LogoLight ?? (LogoDark is null ? under.LogoLight : null),
        LogoDark = LogoDark ?? (LogoLight is null ? under.LogoDark : null),
        // Whether the logo shows the name belongs with the logo. A site that keeps the server's logo
        // but has a name of its own shows that name, so the logo cannot stand for it.
        LogoHasName = LogoLight is not null || LogoDark is not null ? LogoHasName : Name is not null ? null : under.LogoHasName,
        Favicon = Favicon ?? under.Favicon,
        Light = (Light ?? new()).Over(under.Light),
        Dark = (Dark ?? new()).Over(under.Dark),
        HeadingFont = HeadingFont ?? under.HeadingFont,
        BodyFont = BodyFont ?? under.BodyFont,
        TextSize = TextSize ?? under.TextSize,
        HeadingWeight = HeadingWeight ?? under.HeadingWeight,
        Radius = Radius ?? under.Radius,
        Density = Density ?? under.Density
    };

    /// <summary>The asset ids this branding points at, so unused files can be removed.</summary>
    public IEnumerable<string> Assets() => new[] { LogoLight, LogoDark, Favicon }.OfType<string>();

    /// <summary>The font ids it points at, leaving out the system stacks.</summary>
    public IEnumerable<string> Fonts() => new[] { HeadingFont, BodyFont }.OfType<string>().Where(f => !BrandFonts.IsSystem(f));
}

/// <summary>One colour scheme's colours, each <c>#rrggbb</c>. The tints (soft backgrounds) are mixed from these.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class BrandColors
{
    public string? Bg { get; set; }
    public string? Surface { get; set; }
    public string? Surface2 { get; set; }
    public string? Border { get; set; }
    public string? Text { get; set; }
    public string? Muted { get; set; }
    public string? Accent { get; set; }
    public string? AccentText { get; set; }
    public string? Danger { get; set; }
    public string? Success { get; set; }
    public string? Warning { get; set; }

    /// <summary>The token each colour sets, in the order the editor shows them.</summary>
    public static readonly string[] Tokens = ["bg", "surface", "surface-2", "border", "text", "muted", "accent", "accent-text", "danger", "success", "warning"];

    public string? this[string token]
    {
        get => token switch
        {
            "bg" => Bg, "surface" => Surface, "surface-2" => Surface2, "border" => Border, "text" => Text, "muted" => Muted,
            "accent" => Accent, "accent-text" => AccentText, "danger" => Danger, "success" => Success, "warning" => Warning,
            _ => null
        };
        set
        {
            switch (token)
            {
                case "bg": Bg = value; break;
                case "surface": Surface = value; break;
                case "surface-2": Surface2 = value; break;
                case "border": Border = value; break;
                case "text": Text = value; break;
                case "muted": Muted = value; break;
                case "accent": Accent = value; break;
                case "accent-text": AccentText = value; break;
                case "danger": Danger = value; break;
                case "success": Success = value; break;
                case "warning": Warning = value; break;
            }
        }
    }

    [JsonIgnore]
    public bool IsEmpty => Tokens.All(t => this[t] is null);

    /// <summary>A token's name in the JSON: <c>surface-2</c> is <c>surface2</c>, <c>accent-text</c> is <c>accentText</c>.</summary>
    public static string JsonName(string token) => token switch
    {
        "surface-2" => "surface2",
        "accent-text" => "accentText",
        _ => token
    };

    public BrandColors Over(BrandColors? under)
    {
        var merged = new BrandColors();
        foreach (var token in Tokens) merged[token] = this[token] ?? under?[token];
        return merged;
    }
}

/// <summary>The font stacks every device already has, offered beside the library's fonts.</summary>
public static class BrandFonts
{
    public const string SystemSans = "system-sans";
    public const string SystemSerif = "system-serif";
    public const string SystemRounded = "system-rounded";
    public const string SystemMono = "system-mono";

    public static readonly string[] System = [SystemSans, SystemSerif, SystemRounded, SystemMono];

    public static bool IsSystem(string id) => System.Contains(id);
}

/// <summary>Everything branding keeps, in config/branding/branding.json.</summary>
public sealed class BrandingSettings
{
    public Branding Server { get; set; } = new();

    /// <summary>Off by default. On, administrators may add fonts from Google Fonts.</summary>
    public bool AllowGoogleFonts { get; set; }

    public List<BrandFont> Fonts { get; set; } = [];
}

/// <summary>A font in the library: uploaded, or from Google Fonts (downloaded here, or loaded from Google).</summary>
public sealed class BrandFont
{
    public const string Uploaded = "uploaded";
    public const string GoogleDownloaded = "google-downloaded";
    public const string GoogleLinked = "google-linked";

    public string Id { get; set; } = "";

    /// <summary>The family name pages ask for, such as "Inter".</summary>
    public string Family { get; set; } = "";

    /// <summary><see cref="Uploaded"/>, <see cref="GoogleDownloaded"/> or <see cref="GoogleLinked"/>.</summary>
    public string Kind { get; set; } = Uploaded;

    /// <summary>The files served from this server, for uploaded and downloaded fonts.</summary>
    public List<BrandFontFile> Files { get; set; } = [];

    /// <summary>Google's stylesheet address, for a font loaded from Google.</summary>
    public string? GoogleCssUrl { get; set; }

    public string? AddedBy { get; set; }

    public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One font file and what it holds.</summary>
public sealed class BrandFontFile
{
    public string File { get; set; } = "";
    public string Weight { get; set; } = "400";
    public string Style { get; set; } = "normal";

    /// <summary>The characters it covers (Google splits a font into ranges), or null for all of them.</summary>
    public string? UnicodeRange { get; set; }
}
