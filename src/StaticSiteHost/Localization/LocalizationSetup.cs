using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Localization;

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
    /// browser's (Accept-Language), else English. Only the UI culture changes. The culture that
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
                new AcceptLanguageHeaderRequestCultureProvider()
            ]
        };

        return app.UseRequestLocalization(options);
    }
}
