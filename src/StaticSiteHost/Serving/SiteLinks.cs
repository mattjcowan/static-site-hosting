namespace StaticSiteHost.Serving;

public static class SiteLinks
{
    /// <summary>
    /// The address a site answers on, as seen from the current request. Sites are served on
    /// the same port as the management app, so the request's port carries over: nothing is
    /// added behind a proxy on 80/443, while in development a site published as
    /// <c>blog.localhost</c> links to <c>http://blog.localhost:8080/</c> rather than a port
    /// nothing is listening on.
    /// </summary>
    public static string For(HttpRequest request, string domain) =>
        request.Host.Port is { } port
            ? $"{request.Scheme}://{domain}:{port}/"
            : $"{request.Scheme}://{domain}/";
}
