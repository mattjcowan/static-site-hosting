using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Pages;
using StaticSiteHost.Services;

namespace StaticSiteHost.Serving;

/// <summary>
/// The page every screen a visitor sees is written in: the passcode form, not found, nothing here
/// yet, a proxy route that failed, a domain with no site. One template, in the visitor's language
/// and in the branding of the site they asked for (its own over the server's), so the owner's logo,
/// colours and fonts are on all of them. Its styles are inline and set the same tokens as site.css,
/// and the branding's stylesheet sets them over it.
/// </summary>
internal static class VisitorPage
{
    /// <summary>
    /// Writes a page. <paramref name="title"/> is text; <paramref name="body"/> is HTML, already
    /// encoded where it holds values. Status, content type and cache headers are set here.
    /// </summary>
    public static async Task WriteAsync(HttpContext context, int statusCode, string title, string body)
    {
        if (context.Response.HasStarted) return;

        var services = context.RequestServices;
        var host = context.Request.Host.Host.TrimEnd('.').ToLowerInvariant();
        var options = services.GetRequiredService<IOptions<SiteHostingOptions>>().Value;
        var site = options.IsManagementHost(host) ? null : services.GetRequiredService<SiteStore>().TryGet(host);
        var brand = await services.GetRequiredService<BrandingService>().ViewAsync(site, Brand.DefaultName, context.RequestAborted);
        var language = services.GetRequiredService<StringCatalog>().Resolve(System.Globalization.CultureInfo.CurrentUICulture).Name;

        var response = context.Response;
        response.StatusCode = statusCode;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";

        var theme = ThemePreference.Of(context);
        var html = new StringBuilder()
            .Append("<!doctype html>\n<html lang=\"").Append(WebUtility.HtmlEncode(language)).Append('"')
            .Append(theme is null ? "" : $" data-theme=\"{theme}\"").Append(">\n")
            .Append("<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<meta name=\"color-scheme\" content=\"light dark\">\n<meta name=\"robots\" content=\"noindex, nofollow\">\n")
            .Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>\n");

        if (brand.Favicon is { } favicon) html.Append("<link rel=\"icon\" href=\"").Append(WebUtility.HtmlEncode(favicon)).Append("\">\n");
        html.Append("<style>").Append(Styles).Append("</style>\n")
            .Append("<link rel=\"stylesheet\" href=\"").Append(WebUtility.HtmlEncode(brand.StylesheetUrl)).Append("\">\n")
            .Append("<main>\n");

        if (Mark(brand) is { Length: > 0 } mark) html.Append("<div class=\"mark\">").Append(mark).Append("</div>\n");
        html.Append(body).Append("\n</main>\n");

        await response.WriteAsync(html.ToString(), Encoding.UTF8, context.RequestAborted);
    }

    /// <summary>A message page: the status, a heading and a sentence or two. <paramref name="detail"/> is HTML.</summary>
    public static Task WriteMessageAsync(HttpContext context, int statusCode, string pageTitle, string heading, string detail) =>
        WriteAsync(context, statusCode, pageTitle, $"""
            <div class="message">
              <p class="status">{statusCode}</p>
              <h1>{WebUtility.HtmlEncode(heading)}</h1>
              <p>{detail}</p>
            </div>
            """);

    /// <summary>The branding's logo and name, when it has them: a site with no branding shows neither.</summary>
    private static string Mark(BrandingView brand)
    {
        var mark = new StringBuilder();
        var alt = brand.LogoHasName ? WebUtility.HtmlEncode(brand.Name) : "";
        if (brand.LogoLight is { } light)
        {
            mark.Append("<img class=\"logo logo-light\" src=\"").Append(WebUtility.HtmlEncode(light)).Append("\" alt=\"").Append(alt).Append("\">");
            if (brand.LogoDark is { } dark && dark != light)
                mark.Append("<img class=\"logo logo-dark\" src=\"").Append(WebUtility.HtmlEncode(dark)).Append("\" alt=\"").Append(alt).Append("\">");
        }
        if (brand.HasOwnName && !brand.LogoHasName) mark.Append("<span>").Append(WebUtility.HtmlEncode(brand.Name)).Append("</span>");
        return mark.ToString();
    }

    /// <summary>The page's own styles, on site.css's tokens and defaults.</summary>
    private const string Styles = """

        :root {
          color-scheme: light dark;
          --bg: light-dark(#f6f7f9, #0d0f13); --surface: light-dark(#ffffff, #15181e); --border: light-dark(#e2e5ea, #272c35);
          --text: light-dark(#15181e, #e7e9ee); --muted: light-dark(#5f6776, #9097a5);
          --accent: light-dark(#2f5be8, #7d9bff); --accent-text: light-dark(#ffffff, #0d0f13); --danger: light-dark(#cf222e, #ff7b82);
          --font-cjk: "PingFang SC", "Hiragino Sans GB", "Microsoft YaHei", "Noto Sans CJK SC", "Source Han Sans SC";
          --font-body: ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, var(--font-cjk), sans-serif;
          --font-heading: var(--font-body);
          --text-size: 14px; --heading-weight: 650; --radius: 8px; --radius-sm: 6px; --density: 1;
        }
        :root[data-theme="light"] { color-scheme: light; }
        :root[data-theme="dark"] { color-scheme: dark; }
        * { box-sizing: border-box; }
        body { margin: 0; min-height: 100vh; display: grid; place-items: center; padding: calc(24px * var(--density));
               background: var(--bg); color: var(--text); font: calc(var(--text-size) + 1px)/1.6 var(--font-body); -webkit-font-smoothing: antialiased; }
        main { width: 100%; max-width: 26rem; }
        .mark { display: flex; align-items: center; justify-content: center; gap: 10px; margin-bottom: calc(24px * var(--density));
                font-family: var(--font-heading); font-weight: var(--heading-weight); font-size: 1.1rem; }
        .logo { height: 36px; width: auto; max-width: 240px; object-fit: contain; }
        .logo-dark { display: none; }
        :root[data-theme="dark"] .logo-light:has(+ .logo-dark) { display: none; }
        :root[data-theme="dark"] .logo-dark { display: block; }
        @media (prefers-color-scheme: dark) {
          :root:not([data-theme="light"]) .logo-light:has(+ .logo-dark) { display: none; }
          :root:not([data-theme="light"]) .logo-dark { display: block; }
        }
        h1 { font-family: var(--font-heading); font-weight: var(--heading-weight); font-size: 1.35rem; line-height: 1.3;
             letter-spacing: -0.01em; margin: 0 0 6px; text-wrap: balance; }
        p { margin: 0; color: var(--muted); text-wrap: pretty; overflow-wrap: anywhere; }
        .head { text-align: center; margin-bottom: calc(16px * var(--density)); }
        .head .icon { width: 28px; height: 28px; color: var(--muted); margin-bottom: 6px; }
        .head p + p { margin-top: 8px; }
        .message { text-align: center; }
        .message .status { font-family: var(--font-heading); font-size: 3rem; font-weight: var(--heading-weight); line-height: 1;
                           letter-spacing: -0.03em; color: var(--text); margin-bottom: 12px; }
        form { background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius);
               padding: calc(20px * var(--density)); }
        label { display: block; font-weight: 600; font-size: 0.875rem; margin-bottom: 6px; color: var(--text); }
        input { width: 100%; height: 36px; padding: 0 10px; border: 1px solid var(--border); border-radius: var(--radius-sm);
                background: var(--bg); color: var(--text); font: inherit; }
        input:focus-visible, button:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
        button { width: 100%; height: 36px; margin-top: calc(14px * var(--density)); border: 1px solid var(--accent);
                 border-radius: var(--radius-sm); background: var(--accent); color: var(--accent-text); font: inherit;
                 font-weight: 600; cursor: pointer; }
        button:hover { background: color-mix(in oklab, var(--accent) 88%, var(--text)); }
        .error { margin: 0 0 14px; padding: 8px 12px; border-radius: var(--radius-sm); font-size: 0.875rem; color: var(--danger);
                 background: color-mix(in oklab, var(--danger) 10%, var(--surface));
                 border: 1px solid color-mix(in oklab, var(--danger) 40%, transparent); }

        """;
}
