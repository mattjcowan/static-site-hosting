using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Pages.Shared;
using StaticSiteHost.Services;

namespace StaticSiteHost.Tests;

public class BrandingTests
{
    private static string Url(string font, string file) => $"/f/{font}/{file}";

    // ---------------------------------------------------------------- the stylesheet

    [Fact]
    public void A_branding_with_nothing_set_changes_nothing()
    {
        var css = BrandingTheme.Css(new Branding(), [], allowGoogle: false, Url);
        Assert.DoesNotContain(":root", css);
        Assert.DoesNotContain("@font-face", css);
    }

    [Fact]
    public void A_colour_set_for_one_scheme_keeps_the_built_in_one_for_the_other()
    {
        var css = BrandingTheme.Css(new Branding { Light = new BrandColors { Accent = "#0b7285" } }, [], false, Url);
        Assert.Contains("--accent: light-dark(#0b7285, #7d9bff);", css);
        Assert.DoesNotContain("--bg", css);
    }

    [Fact]
    public void Sizes_are_written_in_the_invariant_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var css = BrandingTheme.Css(new Branding { Density = 1.15, Radius = 12, TextSize = 15, HeadingWeight = 700 }, [], false, Url);
            Assert.Contains("--density: 1.15;", css);
            Assert.Contains("--radius: 12px;", css);
            Assert.Contains("--radius-sm: 9px;", css);
            Assert.Contains("--text-size: 15px;", css);
            Assert.Contains("--heading-weight: 700;", css);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void A_library_font_is_served_here_or_loaded_from_google_only_while_google_is_allowed()
    {
        BrandFont[] library =
        [
            new() { Id = "inter-1", Family = "Inter", Kind = BrandFont.GoogleDownloaded,
                    Files = [new() { File = "0000-400-normal.woff2", Weight = "400", UnicodeRange = "U+0000-00FF" }] },
            new() { Id = "lobster-1", Family = "Lobster", Kind = BrandFont.GoogleLinked, GoogleCssUrl = "https://fonts.googleapis.com/css2?family=Lobster&display=swap" }
        ];
        var branding = new Branding { BodyFont = "inter-1", HeadingFont = "lobster-1" };

        var allowed = BrandingTheme.Css(branding, library, allowGoogle: true, Url);
        Assert.StartsWith("/*", allowed);
        Assert.Contains("@import url(\"https://fonts.googleapis.com/css2?family=Lobster&display=swap\");", allowed);
        Assert.Contains("src: url(\"/f/inter-1/0000-400-normal.woff2\") format(\"woff2\")", allowed);
        Assert.Contains("unicode-range: U+0000-00FF", allowed);
        Assert.Contains("--font-body: \"Inter\", ui-sans-serif", allowed);
        Assert.Contains("--font-heading: \"Lobster\"", allowed);

        var refused = BrandingTheme.Css(branding, library, allowGoogle: false, Url);
        Assert.DoesNotContain("googleapis", refused);
        Assert.DoesNotContain("--font-heading", refused);
        Assert.Contains("--font-body: \"Inter\"", refused);
    }

    [Fact]
    public void A_family_name_cannot_break_out_of_its_css_string() =>
        Assert.Equal("\"Evilx\"", BrandingTheme.CssString("Evil\";}x<"));

    [Fact]
    public void Every_system_stack_ends_with_the_cjk_fonts_and_a_generic_family()
    {
        foreach (var (id, stack) in BrandingTheme.SystemStacks)
        {
            Assert.Matches("\"(PingFang SC|Songti SC)\"", stack);
            Assert.Matches("(sans-serif|serif|monospace)$", stack);
            Assert.True(BrandFonts.IsSystem(id));
        }
    }

    // ---------------------------------------------------------------- checks

    [Fact]
    public void A_branding_that_cannot_be_saved_says_why()
    {
        var branding = new Branding
        {
            Name = new string('n', 61),
            Light = new BrandColors { Accent = "blue" },
            Dark = new BrandColors { Text = "#12345" },
            HeadingFont = "missing-font",
            LogoLight = "0123456789abcdef.svg",
            TextSize = 30, HeadingWeight = 100, Radius = -1, Density = 2
        };
        var errors = BrandingTheme.Validate(branding, [], _ => false, site: false);

        Assert.Contains(errors, e => e.Contains("at most 60"));
        Assert.Contains(errors, e => e.Contains("'accent'") && e.Contains("'blue'"));
        Assert.Contains(errors, e => e.Contains("'text'") && e.Contains("#12345"));
        Assert.Contains(errors, e => e.Contains("missing-font"));
        Assert.Contains(errors, e => e.Contains("light logo"));
        Assert.Contains(errors, e => e.Contains("text size"));
        Assert.Contains(errors, e => e.Contains("heading weight"));
        Assert.Contains(errors, e => e.Contains("corner radius"));
        Assert.Contains(errors, e => e.Contains("density"));
        Assert.Empty(BrandingTheme.Validate(new Branding { Name = "Acme", HeadingFont = BrandFonts.SystemSerif }, [], _ => false, site: false));
    }

    [Fact]
    public void A_site_has_no_sign_in_message() =>
        Assert.Contains(BrandingTheme.Validate(new Branding { SignInMessage = "Hi" }, [], _ => false, site: true), e => e.Contains("sign-in"));

    [Fact]
    public void Contrast_is_measured_as_wcag_does()
    {
        Assert.Equal(21, BrandingTheme.Contrast("#000000", "#ffffff"), 1);
        Assert.Equal(1, BrandingTheme.Contrast("#777777", "#777777"), 3);
        Assert.Contains(BrandingTheme.ContrastWarnings(new Branding { Light = new BrandColors { Accent = "#ffcc00" } }), w => w.Contains("'accent' on 'surface'"));
        Assert.Empty(BrandingTheme.ContrastWarnings(new Branding()));
    }

    [Fact]
    public void Every_starter_sets_every_colour_and_is_easy_to_read()
    {
        Assert.Equal(BrandingStarter.All.Count, BrandingStarter.All.Select(s => s.Id).Distinct().Count());
        foreach (var starter in BrandingStarter.All)
        {
            foreach (var token in BrandColors.Tokens)
            {
                Assert.True(BrandingTheme.IsColor(starter.Light[token]), $"{starter.Id} light {token}");
                Assert.True(BrandingTheme.IsColor(starter.Dark[token]), $"{starter.Id} dark {token}");
            }

            var branding = new Branding { Light = new BrandColors(), Dark = new BrandColors() };
            foreach (var token in BrandColors.Tokens)
            {
                branding.Light[token] = starter.Light[token];
                branding.Dark[token] = starter.Dark[token];
            }
            Assert.Empty(BrandingTheme.ContrastWarnings(branding).Select(w => $"{starter.Id}: {w}"));
            Assert.Empty(BrandingTheme.Validate(new Branding { HeadingFont = starter.HeadingFont, BodyFont = starter.BodyFont, TextSize = starter.TextSize,
                HeadingWeight = starter.HeadingWeight, Radius = starter.Radius, Density = starter.Density }, [], _ => false, site: false));
        }
    }

    // ---------------------------------------------------------------- merging

    [Fact]
    public void A_sites_branding_goes_over_the_servers_field_by_field()
    {
        var server = new Branding { Name = "Acme", LogoLight = "a.svg", LogoDark = "b.svg", Light = new BrandColors { Accent = "#111111", Bg = "#eeeeee" }, Radius = 4 };
        var site = new Branding { Name = "Shop", Light = new BrandColors { Accent = "#222222" } };

        var merged = site.Over(server);
        Assert.Equal("Shop", merged.Name);
        Assert.Equal("#222222", merged.Light!.Accent);
        Assert.Equal("#eeeeee", merged.Light.Bg);
        Assert.Equal(4, merged.Radius);
        Assert.Equal(("a.svg", "b.svg"), (merged.LogoLight, merged.LogoDark));
    }

    [Fact]
    public void A_sites_own_logo_never_pairs_with_the_servers_other_one()
    {
        var server = new Branding { LogoLight = "a.svg", LogoDark = "b.svg", LogoHasName = true };
        var merged = new Branding { LogoLight = "mine.svg" }.Over(server);
        Assert.Equal("mine.svg", merged.LogoLight);
        Assert.Null(merged.LogoDark);
        Assert.Null(merged.LogoHasName);
    }

    // ---------------------------------------------------------------- files

    [Theory]
    [InlineData(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10 }, "png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "jpg")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, "gif")]
    [InlineData(new byte[] { 0, 0, 1, 0, 1, 0 }, "ico")]
    public void Images_are_known_by_their_first_bytes(byte[] data, string type) => Assert.Equal(type, BrandingStore.ImageType(data));

    [Fact]
    public void Webp_and_svg_are_recognised_and_other_text_is_not()
    {
        Assert.Equal("webp", BrandingStore.ImageType("RIFF\0\0\0\0WEBPVP8 "u8.ToArray()));
        Assert.Equal("svg", BrandingStore.ImageType(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")));
        Assert.Equal("svg", BrandingStore.ImageType(Encoding.UTF8.GetBytes("﻿<?xml version=\"1.0\"?>\n<!-- logo -->\n<!DOCTYPE svg>\n<svg viewBox=\"0 0 1 1\"></svg>")));
        Assert.Null(BrandingStore.ImageType(Encoding.UTF8.GetBytes("<html><body><svg></svg></body></html>")));
        Assert.Null(BrandingStore.ImageType(Encoding.UTF8.GetBytes("just text")));
    }

    [Fact]
    public void Fonts_are_known_by_their_first_bytes()
    {
        Assert.Equal("woff2", BrandingStore.FontType("wOF2...."u8.ToArray()));
        Assert.Equal("woff", BrandingStore.FontType("wOFF...."u8.ToArray()));
        Assert.Equal("otf", BrandingStore.FontType("OTTO...."u8.ToArray()));
        Assert.Equal("ttf", BrandingStore.FontType(new byte[] { 0, 1, 0, 0, 0, 0 }));
        Assert.Null(BrandingStore.FontType("<html>"u8.ToArray()));
    }

    [Theory]
    [InlineData("Inter-Regular.woff2", "400", "normal")]
    [InlineData("Inter-Bold.woff2", "700", "normal")]
    [InlineData("Inter-SemiBoldItalic.woff2", "600", "italic")]
    [InlineData("Inter_ExtraLight.ttf", "200", "normal")]
    [InlineData("brand-sans-600-italic.woff2", "600", "italic")]
    [InlineData("Go-Medium-Italic.ttf", "500", "italic")]
    [InlineData("Lato-Black.otf", "900", "normal")]
    [InlineData("Plain.woff", "400", "normal")]
    public void A_font_files_weight_and_style_come_from_its_name(string file, string weight, string style)
    {
        Assert.Equal(weight, BrandingService.WeightOf(file));
        Assert.Equal(style, BrandingService.StyleOf(file));
    }

    // ---------------------------------------------------------------- the store and the form

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ssh-branding-" + Guid.NewGuid().ToString("n"));
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }

    private static BrandingStore Store(string root)
    {
        var paths = new DataPaths(Options.Create(new SiteHostingOptions { DataRoot = root }));
        return new BrandingStore(paths, new SiteStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<SiteStore>.Instance));
    }

    [Fact]
    public async Task An_image_is_stored_once_under_its_hash_and_anything_else_is_refused()
    {
        using var folder = new Folder();
        var store = Store(folder.Path);
        var svg = Encoding.UTF8.GetBytes("<svg viewBox=\"0 0 1 1\"></svg>");

        var (first, _) = await store.SaveAssetAsync(svg);
        var (second, _) = await store.SaveAssetAsync(svg);
        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{16}\\.svg$", first!);
        Assert.True(store.AssetExists(first!));
        Assert.False(store.AssetExists("../../etc/passwd"));

        var (none, error) = await store.SaveAssetAsync("not an image"u8.ToArray());
        Assert.Null(none);
        Assert.Contains("Upload an image", error);
        Assert.Contains("1024 KB", (await store.SaveAssetAsync(new byte[BrandingStore.MaxAssetBytes + 1])).Error);
    }

    [Fact]
    public async Task A_font_needs_a_plain_name_and_real_files_and_removing_it_clears_its_use()
    {
        using var folder = new Folder();
        var store = Store(folder.Path);

        Assert.NotNull((await store.AddFontAsync("Bad\"Name", BrandFont.Uploaded, [("wOF2"u8.ToArray(), "400", "normal", null)], null, "me")).Error);
        Assert.NotNull((await store.AddFontAsync("Brand Sans", BrandFont.Uploaded, [("<html>"u8.ToArray(), "400", "normal", null)], null, "me")).Error);

        var (font, error) = await store.AddFontAsync("Brand Sans", BrandFont.Uploaded, [("wOF2xxxx"u8.ToArray(), "700", "italic", null)], null, "me");
        Assert.Null(error);
        Assert.StartsWith("brand-sans-", font!.Id);
        Assert.Equal("0000-700-italic.woff2", font.Files[0].File);
        Assert.NotNull(await store.FontFilePathAsync(font.Id, font.Files[0].File));
        Assert.Null(await store.FontFilePathAsync(font.Id, "../../config/users.json"));

        Assert.Empty(await store.SetServerAsync(new Branding { BodyFont = font.Id }));
        Assert.True(await store.RemoveFontAsync(font.Id));
        Assert.Null((await store.ReadAsync()).Server.BodyFont);
        Assert.False(Directory.Exists(System.IO.Path.Combine(folder.Path, "config", "branding", "fonts", font.Id)));
    }

    [Fact]
    public async Task The_form_keeps_only_what_differs_from_what_the_site_would_get_anyway()
    {
        using var folder = new Folder();
        var store = Store(folder.Path);
        var strings = new JsonStringLocalizer(new StringCatalog(), Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonStringLocalizer>.Instance);
        var start = BrandingStarter.Default;

        // The built-in look, with the accent changed: only the accent is kept.
        var input = new BrandingInput
        {
            Name = "Acme",
            Light = start.Light.ToDictionary(p => p.Key, p => p.Key == "accent" ? "#0B7285" : p.Value),
            Dark = start.Dark.ToDictionary(),
            HeadingFont = start.HeadingFont, BodyFont = start.BodyFont,
            TextSize = "14", HeadingWeight = "650", Radius = "8", Density = "1"
        };
        var (branding, errors) = await input.ToBrandingAsync(new Branding(), null, store, strings, default);
        Assert.Empty(errors);
        Assert.Equal("Acme", branding.Name);
        Assert.Equal("#0b7285", branding.Light!.Accent);
        Assert.True(BrandColors.Tokens.Where(t => t != "accent").All(t => branding.Light[t] is null));
        Assert.Null(branding.Dark);
        Assert.Null(branding.TextSize);
        Assert.Null(branding.HeadingFont);

        // On a site, what equals the server's is left to the server.
        var server = new Branding { Light = new BrandColors { Accent = "#0b7285" }, Radius = 12 };
        var siteInput = new BrandingInput
        {
            Light = BrandingEditor.Filled(null, server).Light!.Tokens(),
            Radius = "12", Density = "1"
        };
        var (site, _) = await siteInput.ToBrandingAsync(new Branding(), server, store, strings, default);
        Assert.True(site.IsEmpty);

        // A number that is not one is refused, in the viewer's words.
        var (_, refused) = await new BrandingInput { Radius = "twelve" }.ToBrandingAsync(new Branding(), null, store, strings, default);
        Assert.Contains(refused, e => e.Contains("'twelve' is not a number"));
    }
}

public class BrandingReviewFixTests
{
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ssh-branding-" + Guid.NewGuid().ToString("n"));
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }

    private static (BrandingStore Store, SiteStore Sites) Stores(string root)
    {
        var paths = new DataPaths(Options.Create(new SiteHostingOptions { DataRoot = root }));
        var sites = new SiteStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<SiteStore>.Instance);
        return (new BrandingStore(paths, sites), sites);
    }

    private static JsonStringLocalizer Strings() =>
        new(new StringCatalog(), Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonStringLocalizer>.Instance);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_density_that_is_not_a_finite_number_is_refused(double density) =>
        Assert.Contains(BrandingTheme.Validate(new Branding { Density = density }, [], _ => false, site: false), e => e.Contains("density"));

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e3")]
    [InlineData("-1")]
    public async Task The_form_reads_numbers_as_plain_digits_only(string density)
    {
        using var folder = new Folder();
        var (store, _) = Stores(folder.Path);
        var (_, errors) = await new BrandingInput { Density = density }.ToBrandingAsync(new Branding(), null, store, Strings(), default);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task A_refused_save_keeps_nothing_in_memory_and_every_page_still_reads_the_branding()
    {
        using var folder = new Folder();
        var (store, _) = Stores(folder.Path);
        Assert.NotEmpty(await store.SetServerAsync(new Branding { Density = double.NaN }));
        Assert.Null((await store.ReadAsync()).Server.Density);
        Assert.Matches("^[0-9a-f]{12}$", await store.VersionAsync(null));
    }

    [Fact]
    public void The_svg_check_is_fast_on_any_input()
    {
        var evil = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<!---->", 2000)) + "x");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(BrandingStore.ImageType(evil));
        Assert.True(watch.ElapsedMilliseconds < 200, $"took {watch.ElapsedMilliseconds} ms");

        Assert.Equal("svg", BrandingStore.ImageType(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<!-- c -->", 50)) + "<svg/>")));
        Assert.Null(BrandingStore.ImageType(Encoding.UTF8.GetBytes("<svgfoo></svgfoo>")));
        Assert.Null(BrandingStore.ImageType(Encoding.UTF8.GetBytes("<!-- never closed <svg>")));
    }

    [Fact]
    public void A_sites_own_name_is_shown_beside_the_servers_wordmark()
    {
        var server = new Branding { Name = "Acme", LogoLight = "a.svg", LogoHasName = true };
        var merged = new Branding { Name = "Shop" }.Over(server);
        Assert.Equal("a.svg", merged.LogoLight);
        Assert.Null(merged.LogoHasName);
        Assert.True(new Branding().Over(server).LogoHasName);
    }

    [Fact]
    public void Colours_go_by_their_json_names_and_an_unknown_one_is_refused()
    {
        Assert.Equal("surface2", BrandColors.JsonName("surface-2"));
        Assert.Equal("accentText", BrandColors.JsonName("accent-text"));
        Assert.Contains(BrandingTheme.ContrastWarnings(new Branding { Light = new BrandColors { AccentText = "#2f5be8" } }), w => w.Contains("'accentText' on 'accent'"));

        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        Assert.Equal("#ffffff", System.Text.Json.JsonSerializer.Deserialize<Branding>("{\"light\":{\"surface2\":\"#ffffff\"}}", json)!.Light!.Surface2);
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<Branding>("{\"light\":{\"surface-2\":\"#ffffff\"}}", json));
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<Branding>("{\"colour\":\"#ffffff\"}", json));
    }

    [Fact]
    public async Task Identical_font_files_are_stored_once_and_a_refused_upload_leaves_nothing()
    {
        using var folder = new Folder();
        var (store, _) = Stores(folder.Path);
        var data = "wOF2same"u8.ToArray();
        var (font, _) = await store.AddFontAsync("Inter", BrandFont.GoogleDownloaded,
            [(data, "100 900", "normal", "U+0000-00FF"), (data, "100 900", "normal", "U+0100-024F")], null, "me");
        Assert.Equal(2, font!.Files.Count);
        Assert.Equal(font.Files[0].File, font.Files[1].File);
        Assert.Equal("0000-100-900-normal.woff2", font.Files[0].File);
        Assert.NotNull(await store.FontFilePathAsync(font.Id, font.Files[0].File));

        var fontsDir = System.IO.Path.Combine(folder.Path, "config", "branding", "fonts");
        var before = Directory.GetDirectories(fontsDir).Length;
        var (refused, error) = await store.AddFontAsync("Brand", BrandFont.Uploaded, [("wOF2ok"u8.ToArray(), "400", "normal", null), ("<html>"u8.ToArray(), "700", "normal", null)], null, "me");
        Assert.Null(refused);
        Assert.NotNull(error);
        Assert.Equal(before, Directory.GetDirectories(fontsDir).Length);
    }

    [Fact]
    public async Task Removing_the_only_thing_a_sites_branding_set_leaves_it_with_none()
    {
        using var folder = new Folder();
        var (store, sites) = Stores(folder.Path);
        var (font, _) = await store.AddFontAsync("Brand", BrandFont.Uploaded, [("wOF2xxxx"u8.ToArray(), "400", "normal", null)], null, "me");
        var site = new SiteRecord { Domain = "shop.example.com" };
        await sites.SaveAsync(site);
        Assert.Empty(await store.SetSiteAsync(site, new Branding { BodyFont = font!.Id }));

        Assert.True(await store.RemoveFontAsync(font.Id));
        Assert.Null(sites.TryGet("shop.example.com")!.Branding);
    }

    [Fact]
    public async Task An_image_uploaded_again_starts_its_clock_over()
    {
        using var folder = new Folder();
        var (store, _) = Stores(folder.Path);
        var svg = Encoding.UTF8.GetBytes("<svg viewBox=\"0 0 1 1\"></svg>");
        var (id, _) = await store.SaveAssetAsync(svg);
        File.SetLastWriteTimeUtc(store.AssetPath(id!)!, DateTime.UtcNow.AddDays(-2));
        await store.SaveAssetAsync(svg);
        Assert.True(File.GetLastWriteTimeUtc(store.AssetPath(id!)!) > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task A_form_shown_again_after_an_error_keeps_what_was_posted_and_the_image_uploaded()
    {
        using var folder = new Folder();
        var (store, _) = Stores(folder.Path);
        var (logo, _) = await store.SaveAssetAsync(Encoding.UTF8.GetBytes("<svg viewBox=\"0 0 1 1\"></svg>"));

        var input = new BrandingInput { Name = "Acme", LogoLightId = logo, Radius = "twelve", Light = new() { ["accent"] = "#0b7285" } };
        var (branding, errors) = await input.ToBrandingAsync(new Branding(), null, store, Strings(), default);
        Assert.NotEmpty(errors);
        Assert.Equal("Acme", branding.Name);
        Assert.Equal(logo, branding.LogoLight);
        Assert.Equal("#0b7285", branding.Light!.Accent);

        // An id the server does not have is not taken from the form.
        var (forged, _) = await new BrandingInput { LogoLightId = "0123456789abcdef.svg" }.ToBrandingAsync(new Branding(), null, store, Strings(), default);
        Assert.Null(forged.LogoLight);
    }

    [Fact]
    public async Task The_version_follows_every_change_and_differs_by_site()
    {
        using var folder = new Folder();
        var (store, _) = Stores(folder.Path);
        var first = await store.VersionAsync(null);
        Assert.Equal(first, await store.VersionAsync(null));
        await store.SetServerAsync(new Branding { Radius = 4 });
        var second = await store.VersionAsync(null);
        Assert.NotEqual(first, second);
        Assert.NotEqual(second, await store.VersionAsync(new SiteRecord { Domain = "a.example.com", Branding = new Branding { Name = "A" } }));
        Assert.Equal(second, await store.VersionAsync(new SiteRecord { Domain = "b.example.com" }));
    }
}

internal static class BrandColorsTestExtensions
{
    public static Dictionary<string, string> Tokens(this BrandColors colors) =>
        BrandColors.Tokens.ToDictionary(t => t, t => colors[t]!);
}
