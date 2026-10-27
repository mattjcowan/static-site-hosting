using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using StaticSiteHost.Localization;

namespace StaticSiteHost.Tests;

/// <summary>
/// Which language a request is shown in: the picker's choice (the ssh.lang cookie), else the
/// browser's languages in its order of preference, a regional variant counting as its language,
/// else English.
/// </summary>
public class LanguageTests
{
    [Theory]
    [InlineData(null, "en")]
    [InlineData("en-US,en;q=0.9", "en")]
    [InlineData("fr-FR,fr;q=0.9,en;q=0.8", "fr")]
    [InlineData("fr-CA", "fr")]
    [InlineData("fr-BE,fr;q=0.9", "fr")]
    [InlineData("es-ES,es;q=0.9", "es")]
    [InlineData("es-MX,es;q=0.9,en;q=0.8", "es")]
    [InlineData("es-419,es;q=0.9", "es")]
    [InlineData("zh-CN,zh;q=0.9", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    [InlineData("de-DE,de;q=0.9", "en")]
    [InlineData("pt-BR,pt;q=0.9,en;q=0.8", "en")]
    [InlineData("de-DE,es;q=0.8", "es")]
    [InlineData("en-GB,fr;q=0.8", "en")]
    [InlineData("zh", "zh-Hans")]
    [InlineData("zh-TW,zh;q=0.9", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hans")]
    [InlineData("zh-HK,en;q=0.9", "zh-Hans")]
    [InlineData("zh-MO", "zh-Hans")]
    [InlineData("zh-Hant-TW", "zh-Hans")]
    [InlineData("en-US,zh-TW;q=0.9", "en")]
    [InlineData("de,it,nl,fr", "fr")]
    public async Task The_browser_language_is_used_when_there_is_a_translation(string? acceptLanguage, string expected) =>
        Assert.Equal(expected, await LanguageFor(acceptLanguage));

    [Fact]
    public async Task The_language_picked_in_the_menu_wins_over_the_browser() =>
        Assert.Equal("es", await LanguageFor("fr-FR,fr;q=0.9", cookie: "c=es|uic=es"));

    [Theory]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK,en;q=0.9", "zh-Hant")]
    [InlineData("zh-CN", "zh-Hans")]
    public async Task Traditional_Chinese_gets_its_own_translation_once_there_is_one(string acceptLanguage, string expected)
    {
        var catalog = new StringCatalog(new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = new() { ["common.save"] = "Save" },
            ["zh-Hans"] = new() { ["common.save"] = "保存" },
            ["zh-Hant"] = new() { ["common.save"] = "儲存" }
        });
        Assert.Equal(expected, await LanguageFor(acceptLanguage, catalog: catalog));
    }

    /// <summary>Runs one request through the app's language step and returns the language it chose.</summary>
    internal static async Task<string> LanguageFor(string? acceptLanguage, string? cookie = null, StringCatalog? catalog = null)
    {
        var services = new ServiceCollection().AddLogging().AddSingleton(catalog ?? new StringCatalog()).BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseSiteHostLanguages();
        string? chosen = null;
        app.Run(_ =>
        {
            chosen = CultureInfo.CurrentUICulture.Name;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext { RequestServices = services };
        if (acceptLanguage is not null) context.Request.Headers.AcceptLanguage = acceptLanguage;
        if (cookie is not null) context.Request.Headers.Cookie = $"{LocalizationSetup.CookieName}={Uri.EscapeDataString(cookie)}";
        await app.Build()(context);
        return chosen!;
    }
}
