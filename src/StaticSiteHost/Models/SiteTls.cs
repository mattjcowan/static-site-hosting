namespace StaticSiteHost.Models;

/// <summary>Where a host's certificate comes from. See <c>Tls/CertificateManager.cs</c>.</summary>
public static class CertificateSources
{
    /// <summary>The server's default (<c>SiteHosting:Tls:DefaultSource</c>), after any shared certificate that covers the host.</summary>
    public const string Automatic = "automatic";

    /// <summary>Let's Encrypt (or the configured ACME server), checked over HTTP on port 80, renewed by itself.</summary>
    public const string LetsEncrypt = "letsencrypt";

    /// <summary>Made by this server, valid for a year and renewed by itself. Browsers warn about it.</summary>
    public const string SelfSigned = "selfsigned";

    /// <summary>A certificate and key uploaded for this site. Not renewed here.</summary>
    public const string Uploaded = "uploaded";

    /// <summary>A certificate uploaded for the whole server, such as a wildcard, that covers this host.</summary>
    public const string Shared = "shared";

    /// <summary>No HTTPS: plain HTTP only.</summary>
    public const string None = "none";

    /// <summary>What a site may choose.</summary>
    public static readonly string[] ForSites = [Automatic, LetsEncrypt, SelfSigned, Uploaded, Shared, None];

    /// <summary>What <c>SiteHosting:Tls:DefaultSource</c> may be.</summary>
    public static readonly string[] Defaults = [LetsEncrypt, SelfSigned, None];

    public static string Describe(string source) => source switch
    {
        Automatic => "automatic",
        LetsEncrypt => "Let's Encrypt",
        SelfSigned => "self-signed",
        Uploaded => "uploaded",
        Shared => "shared certificate",
        None => "no HTTPS",
        _ => source
    };
}

/// <summary>
/// A site's HTTPS settings, kept in <see cref="SiteRecord.Tls"/>. Administrators only. Replaced as
/// a whole on every change.
/// </summary>
public sealed class SiteTlsSettings
{
    /// <summary>One of <see cref="CertificateSources.ForSites"/>.</summary>
    public string Source { get; set; } = CertificateSources.Automatic;

    /// <summary>Send HTTP requests to HTTPS once the site has a certificate. On by default.</summary>
    public bool RedirectToHttps { get; set; } = true;

    /// <summary>
    /// Send <c>Strict-Transport-Security</c>, so browsers use HTTPS only for a year. Off by default:
    /// once a browser has seen it, turning it off here does not help that browser until it expires.
    /// </summary>
    public bool Hsts { get; set; }
}
