using Microsoft.Extensions.Localization;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;

namespace StaticSiteHost.Pages;

/// <summary>A certificate source's name as the pages show it, in the viewer's language. The API names sources by their id.</summary>
public static class CertificateSourceNames
{
    public static string Of(IStringLocalizer strings, string? source) => source switch
    {
        CertificateSources.Automatic => strings.Text("common.certificateSource.automatic"),
        CertificateSources.LetsEncrypt => strings.Text("common.certificateSource.letsEncrypt"),
        CertificateSources.CloudflareOrigin => strings.Text("common.certificateSource.cloudflareOrigin"),
        CertificateSources.SelfSigned => strings.Text("common.certificateSource.selfSigned"),
        CertificateSources.Uploaded => strings.Text("common.certificateSource.uploaded"),
        CertificateSources.Shared => strings.Text("common.certificateSource.shared"),
        CertificateSources.None => strings.Text("common.certificateSource.none"),
        _ => source ?? ""
    };
}
