using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;

namespace StaticSiteHost.Tests;

/// <summary>
/// The server's own messages: in the viewer's language on a page, and in English for the API, MCP
/// and anything logged or stored, whatever language the caller's browser asks for.
/// </summary>
public class ServerTextTests
{
    [Fact]
    public void A_message_comes_in_the_current_language()
    {
        InLanguage("fr", () => Assert.Equal("Un nom d'utilisateur est obligatoire.", UserStore.ValidateUsername("")));
        InLanguage("es", () => Assert.Equal("El nombre de usuario es obligatorio.", UserStore.ValidateUsername("")));
        InLanguage("zh-Hans", () => Assert.Equal("用户名为必填项。", UserStore.ValidateUsername("")));
        InLanguage("en", () => Assert.Equal("A username is required.", UserStore.ValidateUsername("")));
    }

    [Fact]
    public void A_message_built_from_parts_keeps_each_language_s_punctuation()
    {
        var route = new ProxyRoute { Path = "/api", Target = "ftp://example.com" };

        InLanguage("fr", () => Assert.Equal(
            "Route 1 (/api) : la cible doit être une URL http:// ou https://.",
            Assert.Single(ProxyRouteRules.Validate("shop.example.com", [route]))));
        InLanguage("zh-Hans", () => Assert.Equal(
            "路由 1（/api）：目标地址必须是 http:// 或 https:// URL。",
            Assert.Single(ProxyRouteRules.Validate("shop.example.com", [route]))));
        InLanguage("en", () => Assert.Equal(
            "Route 1 (/api): the target must be an http:// or https:// URL.",
            Assert.Single(ProxyRouteRules.Validate("shop.example.com", [route]))));
    }

    [Fact]
    public void A_count_takes_the_language_s_plural()
    {
        InLanguage("en", () => Assert.Equal("Passwords must be at least 1 character.", ServerText.Plural("users.error.passwordShort", 1)));
        InLanguage("en", () => Assert.Equal("Passwords must be at least 12 characters.", ServerText.Plural("users.error.passwordShort", 12)));
        // French counts 0 as one.
        InLanguage("fr", () => Assert.Equal("Les mots de passe doivent compter au moins 0 caractère.", ServerText.Plural("users.error.passwordShort", 0)));
    }

    [Fact]
    public void Sentences_take_a_space_between_them_except_in_chinese()
    {
        InLanguage("en", () => Assert.Equal("One. Two.", ServerText.Sentences(["One.", "Two."])));
        InLanguage("fr", () => Assert.Equal("Un. Deux.", ServerText.Sentences(["Un.", "Deux."])));
        InLanguage("zh-Hans", () => Assert.Equal("一。二。", ServerText.Sentences(["一。", "二。"])));
    }

    [Fact]
    public void Text_for_a_log_line_is_english_and_the_language_comes_back_after()
    {
        InLanguage("fr", () =>
        {
            Assert.Equal("A username is required.", ServerText.English(() => UserStore.ValidateUsername("")));
            Assert.Equal("fr", CultureInfo.CurrentUICulture.Name);
        });
    }

    [Fact]
    public void A_refused_proxy_connection_logs_english_and_tells_the_page_in_its_language()
    {
        InLanguage("fr", () =>
        {
            var refused = new ProxyConnectException("proxy.connect.self", new { address = "127.0.0.1:8080" });
            Assert.Equal("127.0.0.1:8080 is this server itself, and a proxy route may not point back at it.", refused.Message);
            Assert.Equal("127.0.0.1:8080 est ce serveur lui-même, et une route proxy ne peut pas pointer vers lui.", refused.Text());
        });
    }

    [Theory]
    [InlineData("/api/v1/sites", "en")]
    [InlineData("/api", "en")]
    [InlineData("/mcp", "en")]
    [InlineData("/sites", "fr")]
    [InlineData("/apis", "fr")]
    [InlineData("/ui/ai/models", "fr")]
    [InlineData("/assistant/chat", "fr")]
    public async Task The_api_and_mcp_answer_in_english_whatever_the_browser_asks(string path, string expected)
    {
        var services = new ServiceCollection().AddLogging().AddSingleton(new StringCatalog()).BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseSiteHostLanguages();
        app.UseEnglishForApiAndMcp();
        string? chosen = null;
        app.Run(_ =>
        {
            chosen = CultureInfo.CurrentUICulture.Name;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        context.Request.Headers.AcceptLanguage = "fr-FR,fr;q=0.9";
        context.Request.Headers.Cookie = $"{LocalizationSetup.CookieName}={Uri.EscapeDataString("c=fr|uic=fr")}";
        await app.Build()(context);

        Assert.Equal(expected, chosen);
    }

    private static void InLanguage(string language, Action check)
    {
        var before = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        try
        {
            check();
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }
}
