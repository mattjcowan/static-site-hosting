using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;

namespace StaticSiteHost.Serving;

/// <summary>
/// The response compression the whole server uses, except for a request through a streaming proxy
/// route (<see cref="SiteProxy.NoCompressionKey"/>). A compressor holds bytes back until it has
/// enough to work with, which turns a stream of small events into long silences. The framework's
/// own switch for this, <c>IHttpsCompressionFeature</c>, only applies to HTTPS requests, and behind
/// a proxy that ends TLS the requests here are plain HTTP.
/// </summary>
public sealed class SiteResponseCompressionProvider : ResponseCompressionProvider
{
    public SiteResponseCompressionProvider(IServiceProvider services, IOptions<ResponseCompressionOptions> options)
        : base(services, options)
    {
    }

    public override bool ShouldCompressResponse(HttpContext context) =>
        !context.Items.ContainsKey(SiteProxy.NoCompressionKey) && base.ShouldCompressResponse(context);
}
