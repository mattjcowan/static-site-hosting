<Query Kind="Program">
  <NuGetReference>StaticSiteHost.Abstractions</NuGetReference>
  <Namespace>Microsoft.AspNetCore.Http</Namespace>
  <Namespace>Microsoft.AspNetCore.Mvc</Namespace>
  <Namespace>StaticSiteHost.Functions</Namespace>
  <Namespace>StaticSiteHost.Functions.Testing</Namespace>
  <Namespace>System.Text.Json</Namespace>
  <IncludeAspNet>true</IncludeAspNet>
  <DisableMyExtensions>true</DisableMyExtensions>
</Query>

// The same handler as SiteFunctions.cs, reading the site through StaticSiteHost.Abstractions.
// The server compiles against its own copy of the package, so whichever version LINQPad
// restores only matters here; any version deploys.

#if LINQPAD
void Main()
{
    var site = new FakeSite { Domain = "demo.localhost" };
    site.Variables.Set("GREETING", "Hello", isPublic: true).Set("API_KEY", "not-a-real-key");

    var context = new DefaultHttpContext();
    context.UseSite(site);

    SiteHandlers.SiteInfo(context).Dump();

    // Shows a JsonHttpResult whose Value is { domain = demo.localhost, variables = [ API_KEY, GREETING ] }
}
#endif

public static class SiteHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>GET /site-info: the site this request is for, and the names of its variables.</summary>
    [HttpGet("/site-info")]
    public static IResult SiteInfo(HttpContext context)
    {
        var site = context.Site();
        var response = new
        {
            domain = site.Domain,
            // Names only: values include secrets, and this goes back to a browser.
            variables = site.Variables.All.Keys.Order(StringComparer.Ordinal).ToArray(),
        };
        return Results.Json(response, JsonOptions);
    }
}
