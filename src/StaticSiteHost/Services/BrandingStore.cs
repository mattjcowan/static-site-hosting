using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services;

/// <summary>
/// Where branding lives: config/branding/branding.json for the settings, assets/ for logos and
/// icons, fonts/{id}/ for the font library's files. An asset's id is its content's hash and its
/// type, so the same file uploaded twice is stored once and its address never changes; one that no
/// branding uses any more is removed when branding is saved.
///
/// Images are checked by what they hold, not by their name: PNG, JPEG, WebP, GIF, ICO, or SVG. An
/// SVG is served with a policy that stops any script in it from running (see BrandingMiddleware).
///
/// Readers get the settings without a lock, so a change never edits what they may be reading: lists
/// and the server's branding are replaced, not changed in place. A change, and any check it depends
/// on, runs under the store's lock.
/// </summary>
public sealed partial class BrandingStore
{
    public const long MaxAssetBytes = 1024 * 1024;
    public const long MaxFontFileBytes = 4 * 1024 * 1024;

    /// <summary>A Chinese font from Google comes in about a hundred pieces per weight, each a range of characters.</summary>
    public const int MaxFontFiles = 1200;

    public const long MaxFontBytes = 100L * 1024 * 1024;
    public const int MaxFonts = 40;

    /// <summary>
    /// Changes when what BrandingTheme writes changes, so a browser that cached an old stylesheet
    /// "for good" gets the new one after an upgrade.
    /// </summary>
    private const string CssFormat = "1";

    private static readonly JsonSerializerOptions VersionJson = new(JsonSerializerDefaults.Web);

    private readonly JsonFileStore<BrandingSettings> _store;
    private readonly SiteStore _sites;
    private readonly string _assets;
    private readonly string _fonts;

    /// <summary>The server's part of every version, worked out once per change.</summary>
    private string? _serverVersion;

    /// <summary>Counts changes, so a version worked out from settings read before one is not kept after it.</summary>
    private int _generation;

    public BrandingStore(DataPaths paths, SiteStore sites)
    {
        var root = Path.Combine(paths.ConfigDir, "branding");
        _store = new JsonFileStore<BrandingSettings>(Path.Combine(root, "branding.json"));
        _assets = Path.Combine(root, "assets");
        _fonts = Path.Combine(root, "fonts");
        _sites = sites;
    }

    /// <summary>Raised after any change, so cached stylesheets are made again.</summary>
    public event Action? Changed;

    private void OnChanged()
    {
        Interlocked.Increment(ref _generation);
        _serverVersion = null;
        Changed?.Invoke();
    }

    public Task<BrandingSettings> ReadAsync(CancellationToken ct = default) => _store.ReadAsync(ct);

    /// <summary>The server's branding, with a site's own over it for that site's visitor screens.</summary>
    public async Task<Branding> ForAsync(SiteRecord? site, CancellationToken ct = default)
    {
        var settings = await ReadAsync(ct);
        return site?.Branding is { } own ? own.Over(settings.Server) : settings.Server;
    }

    /// <summary>
    /// A short hash of everything a stylesheet or a logo depends on, for cache-busting addresses:
    /// the server's settings (hashed once per change), the site's own branding, the stylesheet's
    /// format and the server's version.
    /// </summary>
    public async Task<string> VersionAsync(SiteRecord? site, CancellationToken ct = default)
    {
        var server = _serverVersion;
        if (server is null)
        {
            var generation = Volatile.Read(ref _generation);
            var settings = await ReadAsync(ct);
            server = Hash(JsonSerializer.Serialize(settings, VersionJson));
            if (Volatile.Read(ref _generation) == generation) _serverVersion = server;
        }

        if (site?.Branding is null) return Hash($"{CssFormat}|{Tools.SiteHostTools.ServerVersion}|{server}");
        return Hash($"{CssFormat}|{Tools.SiteHostTools.ServerVersion}|{server}|{JsonSerializer.Serialize(site.Branding, VersionJson)}");
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    // ---------------------------------------------------------------- settings

    /// <summary>Saves the server's branding. Returns why not; empty when it was saved.</summary>
    public async Task<List<string>> SetServerAsync(Branding branding, CancellationToken ct = default)
    {
        var errors = await _store.UpdateAsync(settings =>
        {
            var problems = BrandingTheme.Validate(branding, settings.Fonts.Select(f => f.Id).ToList(), AssetExists, site: false);
            if (problems.Count > 0) return (problems, false);
            settings.Server = branding;
            return (problems, true);
        }, ct);

        if (errors.Count == 0) await CollectAsync(ct);
        return errors;
    }

    /// <summary>
    /// Saves a site's own branding for its visitor screens, or removes it with null. Checked and
    /// saved under the store's lock, so a font removed at the same moment cannot be left named.
    /// </summary>
    public async Task<List<string>> SetSiteAsync(SiteRecord site, Branding? branding, CancellationToken ct = default)
    {
        var errors = await _store.UpdateAsync(async settings =>
        {
            if (branding is not null)
            {
                var problems = BrandingTheme.Validate(branding, settings.Fonts.Select(f => f.Id).ToList(), AssetExists, site: true);
                if (problems.Count > 0) return (problems, false);
            }

            site.Branding = branding is null || branding.IsEmpty ? null : branding;
            await _sites.SaveAsync(site);
            return (new List<string>(), false);
        }, ct);

        if (errors.Count == 0) await CollectAsync(ct);
        return errors;
    }

    public async Task SetAllowGoogleFontsAsync(bool allow, CancellationToken ct = default)
    {
        await _store.UpdateAsync(settings =>
        {
            settings.AllowGoogleFonts = allow;
            return (true, true);
        }, ct);
        OnChanged();
    }

    // ---------------------------------------------------------------- assets

    /// <summary>The image types an asset may be, by what its first bytes say.</summary>
    public static string? ImageType(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G'])) return "png";
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return "jpg";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)) return "webp";
        if (data.StartsWith("GIF8"u8)) return "gif";
        if (data.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x00])) return "ico";
        return IsSvg(Encoding.UTF8.GetString(data[..Math.Min(data.Length, 4096)])) ? "svg" : null;
    }

    /// <summary>
    /// Text whose first element is <c>&lt;svg&gt;</c>: only a byte order mark, white space, an XML
    /// declaration, comments and a doctype may come before it, so an HTML page with an SVG inside it
    /// is not one. Read in one pass, so no input can make it slow.
    /// </summary>
    private static bool IsSvg(string text)
    {
        var at = 0;
        while (true)
        {
            while (at < text.Length && (char.IsWhiteSpace(text[at]) || text[at] == '﻿')) at++;
            if (Skip(text, ref at, "<?xml", "?>") || Skip(text, ref at, "<!--", "-->") || Skip(text, ref at, "<!doctype", ">")) continue;
            break;
        }

        return string.Compare(text, at, "<svg", 0, 4, StringComparison.OrdinalIgnoreCase) == 0 &&
               at + 4 < text.Length && (char.IsWhiteSpace(text[at + 4]) || text[at + 4] is '>' or '/');

        static bool Skip(string text, ref int at, string open, string close)
        {
            if (string.Compare(text, at, open, 0, open.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            var end = text.IndexOf(close, at + open.Length, StringComparison.Ordinal);
            if (end < 0) return false;
            at = end + close.Length;
            return true;
        }
    }

    [GeneratedRegex("^[0-9a-f]{16}\\.(png|jpg|webp|gif|ico|svg)$")]
    private static partial Regex AssetId();

    /// <summary>Stores an image and returns its id, or why it was refused.</summary>
    public async Task<(string? Id, string? Error)> SaveAssetAsync(byte[] data, CancellationToken ct = default)
    {
        if (data.Length == 0) return (null, "The file is empty.");
        if (data.Length > MaxAssetBytes) return (null, $"An image can be at most {MaxAssetBytes / 1024} KB.");
        if (ImageType(data) is not { } type) return (null, "Upload an image: SVG, PNG, WebP, JPEG, GIF or ICO.");

        var id = Convert.ToHexStringLower(SHA256.HashData(data))[..16] + "." + type;
        Directory.CreateDirectory(_assets);
        var path = Path.Combine(_assets, id);
        if (!File.Exists(path))
        {
            var temp = path + "." + RandomNumberGenerator.GetHexString(8, lowercase: true) + ".tmp";
            await File.WriteAllBytesAsync(temp, data, ct);
            File.Move(temp, path, overwrite: true);
        }
        else
        {
            // Uploaded again to be used: the clock that removes unused images starts over.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        return (id, null);
    }

    public bool AssetExists(string id) => AssetId().IsMatch(id) && File.Exists(Path.Combine(_assets, id));

    /// <summary>The file of an asset, or null for an id that is not one.</summary>
    public string? AssetPath(string id) => AssetExists(id) ? Path.Combine(_assets, id) : null;

    // ---------------------------------------------------------------- fonts

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 \\-]{0,59}$")]
    private static partial Regex Family();

    public static bool IsFamilyName(string? family) => family is not null && Family().IsMatch(family.Trim());

    [GeneratedRegex("^[a-z0-9][a-z0-9_.\\-]{0,80}\\.(woff2|woff|ttf|otf)$")]
    private static partial Regex FontFileName();

    /// <summary>A font file's type, by its first bytes: WOFF2, WOFF, TrueType or OpenType.</summary>
    public static string? FontType(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith("wOF2"u8)) return "woff2";
        if (data.StartsWith("wOFF"u8)) return "woff";
        if (data.StartsWith("OTTO"u8)) return "otf";
        if (data.StartsWith((ReadOnlySpan<byte>)[0x00, 0x01, 0x00, 0x00]) || data.StartsWith("true"u8)) return "ttf";
        return null;
    }

    /// <summary>
    /// Adds a font to the library from files given here or downloaded from Google. Every file is
    /// checked before any is written, and identical files (a variable font Google names once per
    /// weight) are stored once. Returns the font, or why not.
    /// </summary>
    public async Task<(BrandFont? Font, string? Error)> AddFontAsync(
        string family, string kind, IReadOnlyList<(byte[] Data, string Weight, string Style, string? UnicodeRange)> files,
        string? googleCssUrl, string? actor, CancellationToken ct = default)
    {
        family = family.Trim();
        if (!IsFamilyName(family)) return (null, "A font's name can have letters, digits, spaces and hyphens, at most 60 of them, such as Inter or Source Serif 4.");
        if (kind != BrandFont.GoogleLinked && files.Count == 0) return (null, "Add at least one font file: WOFF2, WOFF, TTF or OTF.");
        if (files.Count > MaxFontFiles) return (null, $"A font can have at most {MaxFontFiles} files.");
        if (files.Sum(f => (long)f.Data.Length) > MaxFontBytes) return (null, $"A font's files can be at most {MaxFontBytes / (1024 * 1024)} MB together.");

        var types = new string[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            if (files[i].Data.Length > MaxFontFileBytes) return (null, $"A font file can be at most {MaxFontFileBytes / (1024 * 1024)} MB.");
            if (FontType(files[i].Data) is not { } type) return (null, "One of the files is not a font. Use WOFF2, WOFF, TTF or OTF files.");
            types[i] = type;
        }

        var id = $"{Slug(family)}-{RandomNumberGenerator.GetHexString(6, lowercase: true)}";
        var font = new BrandFont { Id = id, Family = family, Kind = kind, GoogleCssUrl = googleCssUrl, AddedBy = actor };
        var dir = Path.Combine(_fonts, id);
        var written = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var (data, weight, style, range) = files[i];
                var hash = Convert.ToHexStringLower(SHA256.HashData(data));
                if (!written.TryGetValue(hash, out var name))
                {
                    Directory.CreateDirectory(dir);
                    name = $"{written.Count:0000}-{weight.Replace(' ', '-')}-{style}.{types[i]}";
                    await File.WriteAllBytesAsync(Path.Combine(dir, name), data, ct);
                    written[hash] = name;
                }
                font.Files.Add(new BrandFontFile { File = name, Weight = weight, Style = style, UnicodeRange = range });
            }
        }
        catch
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            throw;
        }

        var added = await _store.UpdateAsync(settings =>
        {
            if (settings.Fonts.Count >= MaxFonts) return (false, false);
            settings.Fonts = [.. settings.Fonts, font];
            return (true, true);
        }, ct);

        if (!added)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return (null, $"The library can hold at most {MaxFonts} fonts. Remove one first.");
        }

        OnChanged();
        return (font, null);
    }

    /// <summary>Removes a font. Branding that used it, the server's and every site's, falls back to the built-in font.</summary>
    public async Task<bool> RemoveFontAsync(string id, CancellationToken ct = default)
    {
        var removed = await _store.UpdateAsync(async settings =>
        {
            if (settings.Fonts.All(f => f.Id != id)) return (false, false);
            settings.Fonts = settings.Fonts.Where(f => f.Id != id).ToList();
            if (settings.Server.HeadingFont == id || settings.Server.BodyFont == id)
                settings.Server = WithoutFont(settings.Server, id);

            foreach (var site in _sites.List().Where(s => s.Branding is { } b && (b.HeadingFont == id || b.BodyFont == id)))
            {
                var branding = WithoutFont(site.Branding!, id);
                site.Branding = branding.IsEmpty ? null : branding;
                await _sites.SaveAsync(site);
            }
            return (true, true);
        }, ct);

        if (!removed) return false;

        var dir = Path.Combine(_fonts, id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        OnChanged();
        return true;
    }

    /// <summary>A copy of a branding without a font, so no reader sees the branding change under it.</summary>
    private static Branding WithoutFont(Branding branding, string id)
    {
        var copy = JsonSerializer.Deserialize<Branding>(JsonSerializer.Serialize(branding, VersionJson), VersionJson)!;
        if (copy.HeadingFont == id) copy.HeadingFont = null;
        if (copy.BodyFont == id) copy.BodyFont = null;
        return copy;
    }

    /// <summary>A font file's path, or null for a name that is not one of the font's.</summary>
    public async Task<string?> FontFilePathAsync(string id, string file, CancellationToken ct = default)
    {
        if (!FontFileName().IsMatch(file)) return null;
        var settings = await ReadAsync(ct);
        if (settings.Fonts.FirstOrDefault(f => f.Id == id) is not { } font || font.Files.All(f => f.File != file)) return null;
        var path = Path.Combine(_fonts, id, file);
        return File.Exists(path) ? path : null;
    }

    private static string Slug(string family) =>
        new string(family.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    // ---------------------------------------------------------------- tidying

    /// <summary>Removes the assets that no branding uses any more, under the store's lock, then says it changed.</summary>
    private async Task CollectAsync(CancellationToken ct)
    {
        await _store.UpdateAsync(settings =>
        {
            var used = settings.Server.Assets()
                .Concat(_sites.List().SelectMany(s => s.Branding?.Assets() ?? []))
                .ToHashSet(StringComparer.Ordinal);

            if (Directory.Exists(_assets))
            {
                foreach (var file in Directory.GetFiles(_assets))
                {
                    var name = Path.GetFileName(file);
                    // An upload is saved before the branding that names it, so a fresh one is kept for a while.
                    if (!used.Contains(name) && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1)) File.Delete(file);
                }
            }
            return (true, false);
        }, ct);

        OnChanged();
    }
}
