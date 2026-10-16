using Microsoft.Extensions.Localization;
using StaticSiteHost.Localization;
using StaticSiteHost.Serving;

namespace StaticSiteHost.Pages;

/// <summary>Whether a server-wide route is in effect for a site, as the pages say it, in the viewer's language.</summary>
public static class ProxyStateNames
{
    public static string Of(IStringLocalizer strings, string state) => state switch
    {
        ProxySites.StateOn => strings.Text("common.proxyState.on"),
        ProxySites.StateOffEverywhere => strings.Text("common.proxyState.offEverywhere"),
        ProxySites.StateOverridden => strings.Text("common.proxyState.overridden"),
        ProxySites.StateOffHere => strings.Text("common.proxyState.offHere"),
        ProxySites.StateCloserMatch => strings.Text("common.proxyState.closerMatch"),
        _ => state
    };
}
