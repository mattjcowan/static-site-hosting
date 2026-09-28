using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;

namespace StaticSiteHost.Functions;

/// <summary>
/// Reaches the request's <see cref="ISite"/> from anywhere the <see cref="HttpContext"/> is,
/// such as helper code a handler calls, and lets a test supply one.
/// </summary>
public static class SiteHttpContextExtensions
{
    /// <summary>
    /// The <see cref="HttpContext.Items"/> key the server stores the request's
    /// <see cref="ISite"/> under. Use the methods below rather than the key.
    /// </summary>
    public const string ItemKey = "StaticSiteHost.Site";

    /// <summary>The site the request is for.</summary>
    /// <exception cref="InvalidOperationException">
    /// The request is not running inside Static Site Host, and no test supplied a site with
    /// <see cref="UseSite(HttpContext, ISite)"/>.
    /// </exception>
    public static ISite Site(this HttpContext context) =>
        context.TryGetSite(out var site)
            ? site
            : throw new InvalidOperationException(
                "This request is not running inside Static Site Host, so it has no site. To run a handler " +
                "anywhere else, such as in a test, call context.UseSite(new FakeSite()) first; FakeSite is " +
                "in StaticSiteHost.Functions.Testing.");

    /// <summary>The site the request is for, when there is one.</summary>
    /// <returns>False when the request is not running inside Static Site Host and no test supplied a site.</returns>
    public static bool TryGetSite(this HttpContext context, [NotNullWhen(true)] out ISite? site)
    {
        ArgumentNullException.ThrowIfNull(context);

        site = context.Items.TryGetValue(ItemKey, out var value) ? value as ISite : null;
        return site is not null;
    }

    /// <summary>
    /// Makes <paramref name="site"/> the site this request is for, so a handler can run outside
    /// the server: <c>context.UseSite(new FakeSite { Domain = "demo.localhost" })</c>. The
    /// server does the same for every request it hands to a function.
    /// </summary>
    public static void UseSite(this HttpContext context, ISite site)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(site);

        context.Items[ItemKey] = site;
    }
}
