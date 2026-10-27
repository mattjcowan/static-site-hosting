using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Primitives;

namespace StaticSiteHost.Localization;

public static class LocalizationSetup
{
    /// <summary>Holds the viewer's choice of language, when they made one; its name starts with ssh. so proxy routes never pass it on.</summary>
    public const string CookieName = "ssh.lang";

    /// <summary>The language of everything read by scripts, AI agents and logs, and of work no request asked for.</summary>
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo(StringCatalog.Fallback);

    /// <summary>The catalogue and the localizer, as the standard <c>IStringLocalizer</c> services.</summary>
    public static IServiceCollection AddSiteHostStrings(this IServiceCollection services)
    {
        services.AddSingleton(_ => StringCatalog.Shared);
        services.AddSingleton<JsonStringLocalizer>();
        services.AddSingleton<IStringLocalizer<Strings>>(sp => sp.GetRequiredService<JsonStringLocalizer>());
        services.AddSingleton<IStringLocalizer>(sp => sp.GetRequiredService<JsonStringLocalizer>());
        services.AddSingleton<IStringLocalizerFactory, JsonStringLocalizerFactory>();
        return services;
    }

    /// <summary>
    /// Picks each request's language: the viewer's choice (the <c>ssh.lang</c> cookie), else the
    /// first of the browser's languages (Accept-Language) that has a translation, a regional variant
    /// such as <c>fr-CA</c> counting as its language, else English. Only the UI culture changes. The culture that
    /// formats and parses numbers and dates stays the server's own, so no code that reads or writes a
    /// value behaves differently by language.
    /// </summary>
    public static IApplicationBuilder UseSiteHostLanguages(this IApplicationBuilder app)
    {
        var catalog = app.ApplicationServices.GetRequiredService<StringCatalog>();
        var serverCulture = CultureInfo.CurrentCulture;

        var options = new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture(serverCulture, CultureInfo.GetCultureInfo(StringCatalog.Fallback)),
            SupportedCultures = [serverCulture],
            SupportedUICultures = [.. catalog.Languages],
            FallBackToParentUICultures = true,
            RequestCultureProviders =
            [
                new CookieRequestCultureProvider { CookieName = CookieName },
                new BrowserLanguageProvider(catalog)
            ]
        };

        return app.UseRequestLocalization(options);
    }

    /// <summary>
    /// English for the API (<c>/api</c>) and MCP (<c>/mcp</c>), whatever the caller's browser or
    /// cookie asks for: their answers are read by scripts and AI agents, and a contract that changed
    /// language would break them. On the management hosts only: a site's own paths keep the
    /// visitor's language.
    /// </summary>
    public static IApplicationBuilder UseEnglishForApiAndMcp(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/mcp"))
                CultureInfo.CurrentUICulture = English;
            return next();
        });

    /// <summary>
    /// The browser's languages (Accept-Language), in its order of preference, with two changes to
    /// the standard reading: every language listed is tried, not only the first three, so a fourth
    /// choice that has a translation beats English; and any Chinese this build has no translation
    /// for (<c>zh</c>, and Traditional Chinese such as <c>zh-TW</c> and <c>zh-HK</c> while there is
    /// no <c>zh-Hant</c>) counts as Simplified Chinese, which its readers read far better than English.
    /// </summary>
    private sealed class BrowserLanguageProvider : AcceptLanguageHeaderRequestCultureProvider
    {
        private readonly StringCatalog _catalog;

        public BrowserLanguageProvider(StringCatalog catalog)
        {
            _catalog = catalog;
            MaximumAcceptLanguageHeaderValuesToTry = 20;
        }

        public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
        {
            var result = await base.DetermineProviderCultureResult(httpContext);
            if (result is null) return null;

            IList<StringSegment> Read(IList<StringSegment> names) =>
                [.. names.Select(n => IsChinese(n) && !Speaks(n) ? new StringSegment("zh-Hans") : n)];

            return new ProviderCultureResult(Read(result.Cultures), Read(result.UICultures));
        }

        private static bool IsChinese(StringSegment name) =>
            name.Equals("zh", StringComparison.OrdinalIgnoreCase) || name.StartsWith("zh-", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether this build has a translation for the language, itself or a parent (<c>zh-TW</c> → <c>zh-Hant</c>).</summary>
        private bool Speaks(StringSegment name)
        {
            try
            {
                return _catalog.Resolve(CultureInfo.GetCultureInfo(name.Value!)).Name != StringCatalog.Fallback;
            }
            catch (CultureNotFoundException)
            {
                return false;
            }
        }
    }
}
