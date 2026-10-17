using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Primitives;

namespace StaticSiteHost.Localization;

public static class LocalizationSetup
{
    /// <summary>Holds the viewer's choice of language, when they made one; its name starts with ssh. so proxy routes never pass it on.</summary>
    public const string CookieName = "ssh.lang";

    /// <summary>The catalogue and the localizer, as the standard <c>IStringLocalizer</c> services.</summary>
    public static IServiceCollection AddSiteHostStrings(this IServiceCollection services)
    {
        services.AddSingleton<StringCatalog>();
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
                new BrowserLanguageProvider()
            ]
        };

        return app.UseRequestLocalization(options);
    }

    /// <summary>
    /// The browser's languages (Accept-Language), in its order of preference, with two changes to
    /// the standard reading: every language listed is tried, not only the first three, so a fourth
    /// choice that has a translation beats English; and a bare <c>zh</c> counts as Simplified
    /// Chinese, which is what browsers mean by it. <c>zh-TW</c> and <c>zh-HK</c> (Traditional) do not.
    /// </summary>
    private sealed class BrowserLanguageProvider : AcceptLanguageHeaderRequestCultureProvider
    {
        public BrowserLanguageProvider() => MaximumAcceptLanguageHeaderValuesToTry = 20;

        public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
        {
            var result = await base.DetermineProviderCultureResult(httpContext);
            if (result is null) return null;

            static IList<StringSegment> Read(IList<StringSegment> names) =>
                [.. names.Select(n => n.Equals("zh", StringComparison.OrdinalIgnoreCase) ? new StringSegment("zh-Hans") : n)];

            return new ProviderCultureResult(Read(result.Cultures), Read(result.UICultures));
        }
    }
}
