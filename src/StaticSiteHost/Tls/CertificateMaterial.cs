using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using StaticSiteHost.Localization;

namespace StaticSiteHost.Tls;

/// <summary>
/// A certificate ready to serve: the leaf with its key, the chain sent with it, and what the pages
/// show about it. Built once and kept, since building the TLS context is not free.
/// </summary>
public sealed class ServedCertificate
{
    public required X509Certificate2 Leaf { get; init; }
    public required SslStreamCertificateContext Context { get; init; }
    public required IReadOnlyList<string> Names { get; init; }

    public DateTimeOffset NotBefore => Leaf.NotBefore.ToUniversalTime();
    public DateTimeOffset NotAfter => Leaf.NotAfter.ToUniversalTime();
    public string Issuer => Leaf.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
    public string Thumbprint => Leaf.Thumbprint;
    public bool SelfSigned => Leaf.SubjectName.RawData.AsSpan().SequenceEqual(Leaf.IssuerName.RawData);

    /// <summary>Whether this certificate names <paramref name="host"/>, directly or by a wildcard one level up.</summary>
    public bool Covers(string host) => Names.Any(name => CertificateMaterial.Matches(name, host));
}

/// <summary>Turning PEM into something Kestrel can serve, checking uploads, and making self-signed certificates.</summary>
public static class CertificateMaterial
{
    /// <summary>
    /// The certificate chain and key, as PEM, made servable. The first certificate is the leaf; any
    /// others are sent with it, so browsers can build the chain to a root they trust.
    /// </summary>
    public static ServedCertificate Load(string chainPem, string keyPem)
    {
        var all = new X509Certificate2Collection();
        all.ImportFromPem(chainPem);
        if (all.Count == 0) throw new CryptographicException(ServerText.Get("https.upload.error.noCertificate"));

        using var withKey = X509Certificate2.CreateFromPem(Pem(all[0]), keyPem);
        // Exported and loaded again, so the key is one SslStream can use on every platform,
        // not only on Linux where an ephemeral key works.
        var leaf = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), password: null);

        var intermediates = new X509Certificate2Collection();
        for (var i = 1; i < all.Count; i++) intermediates.Add(all[i]);

        return new ServedCertificate
        {
            Leaf = leaf,
            Context = SslStreamCertificateContext.Create(leaf, intermediates, offline: true),
            Names = NamesOf(leaf)
        };
    }

    /// <summary>
    /// An uploaded certificate and key, checked: both PEM, the key the certificate's own, still
    /// valid, and (when <paramref name="host"/> is given) naming that host. Returns the reason when not.
    /// </summary>
    public static (ServedCertificate? Certificate, string? Error) Check(string? chainPem, string? keyPem, string? host)
    {
        if (string.IsNullOrWhiteSpace(chainPem) || !chainPem.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            return (null, ServerText.Get("https.upload.error.certificatePem"));
        if (string.IsNullOrWhiteSpace(keyPem) || !keyPem.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
            return (null, ServerText.Get("https.upload.error.keyPem"));

        ServedCertificate certificate;
        try
        {
            certificate = Load(chainPem, keyPem);
        }
        catch (CryptographicException ex)
        {
            return (null, ServerText.Get("https.upload.error.unreadableTogether", new { problem = ex.Message }));
        }
        catch (ArgumentException ex)
        {
            return (null, ex.Message.Contains("does not match", StringComparison.OrdinalIgnoreCase)
                ? ServerText.Get("https.upload.error.keyMismatch")
                : ServerText.Get("https.upload.error.unreadable", new { problem = ex.Message.Split(" (Parameter")[0] }));
        }

        if (certificate.NotAfter < DateTimeOffset.UtcNow)
            return (null, ServerText.Get("https.upload.error.expired", new { date = certificate.NotAfter.ToString("yyyy-MM-dd") }));
        if (certificate.Names.Count == 0)
            return (null, ServerText.Get("https.upload.error.noNames"));
        if (host is not null && !certificate.Covers(host))
            return (null, ServerText.Get("https.upload.error.otherHost", new { names = string.Join(", ", certificate.Names), host }));

        return (certificate, null);
    }

    /// <summary>A self-signed certificate for <paramref name="host"/>, valid for a year, with its key, as PEM.</summary>
    public static (string ChainPem, string KeyPem) CreateSelfSigned(string host, TimeSpan? lifetime = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // A name no other certificate has: one the system trusts with the same name (such as the
        // .NET developer certificate, CN=localhost) would be taken for its issuer, and the chain
        // would fail to build.
        var subject = new X500DistinguishedName($"CN={host}, O=Self-signed {Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}");
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        if (System.Net.IPAddress.TryParse(host, out var address)) names.AddIpAddress(address);
        else names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now + (lifetime ?? TimeSpan.FromDays(365)));
        return (Pem(certificate), key.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>The DNS names a certificate is for: its SANs, or its common name when it has none.</summary>
    public static IReadOnlyList<string> NamesOf(X509Certificate2 certificate)
    {
        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(e => e.EnumerateDnsNames().Concat(e.EnumerateIPAddresses().Select(a => a.ToString())))
            .Select(n => n.ToLowerInvariant())
            .Distinct()
            .ToList();
        if (names.Count > 0) return names;

        var common = certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false);
        return string.IsNullOrEmpty(common) ? [] : [common.ToLowerInvariant()];
    }

    /// <summary>
    /// Whether a certificate name covers a host: the same name, or <c>*.example.com</c> for a host
    /// exactly one label under example.com, as browsers match it.
    /// </summary>
    public static bool Matches(string name, string host)
    {
        if (name.Equals(host, StringComparison.OrdinalIgnoreCase)) return true;
        if (!name.StartsWith("*.", StringComparison.Ordinal)) return false;

        var dot = host.IndexOf('.');
        return dot > 0 && host[(dot + 1)..].Equals(name[2..], StringComparison.OrdinalIgnoreCase);
    }

    public static string Pem(X509Certificate2 certificate) => certificate.ExportCertificatePem() + "\n";
}
