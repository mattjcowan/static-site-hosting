using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using StaticSiteHost.Services;

namespace StaticSiteHost.Tls;

/// <summary>How getting a host's certificate has gone: the last attempt, and when to try again after a failure.</summary>
public sealed class HostCertificateState
{
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public string? LastError { get; set; }

    /// <summary>Failed attempts in a row; reset by a success.</summary>
    public int Failures { get; set; }

    /// <summary>No new attempt before this, after a failure, so Let's Encrypt's limits are never hit.</summary>
    public DateTimeOffset? NextAttemptUtc { get; set; }
}

/// <summary>A certificate uploaded for the whole server, used for every host it covers.</summary>
public sealed class SharedCertificateInfo
{
    public string Id { get; set; } = "";
    public List<string> Names { get; set; } = [];
    public DateTimeOffset NotAfterUtc { get; set; }
    public string Issuer { get; set; } = "";
    public string? UploadedBy { get; set; }
    public DateTimeOffset UploadedUtc { get; set; }
}

/// <summary>
/// Certificates on disk, under config/certs/:
///
///   hosts/&lt;host&gt;/&lt;source&gt;/cert.pem   the chain, leaf first, one folder per source
///   hosts/&lt;host&gt;/&lt;source&gt;/key.bin    its private key, encrypted with Data Protection
///   hosts/&lt;host&gt;/&lt;source&gt;/origin.txt for Let's Encrypt, the ACME directory it came from
///   hosts/&lt;host&gt;/state.json          how getting one has gone
///
/// One folder per source, so switching a site from Let's Encrypt to self-signed and back does not
/// throw the Let's Encrypt certificate away and ask for the same one again.
///   shared/&lt;id&gt;/…            certificates for many hosts, such as a wildcard, the same way
///   acme/&lt;directory&gt;/…        the ACME account: its key (encrypted) and URL
///
/// Keys are encrypted like the AI providers' keys, with the key ring in config/keys, so a copy of
/// the certificates folder alone is no use to anyone.
/// </summary>
public sealed class CertificateStore
{
    private const string ProtectorPurpose = "StaticSiteHost.Certificates";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _root;
    private readonly IDataProtector _protector;

    public CertificateStore(DataPaths paths, IDataProtectionProvider protection)
    {
        _root = Path.Combine(paths.ConfigDir, "certs");
        _protector = protection.CreateProtector(ProtectorPurpose);
    }

    // ---------------------------------------------------------------- one host

    /// <summary>
    /// The host's certificate from one source, ready to serve, or null when it has none or it cannot
    /// be read. With <paramref name="origin"/>, only one that came from there (an ACME directory).
    /// </summary>
    public ServedCertificate? LoadHost(string host, string source, string? origin = null)
    {
        if (SourceDir(host, source) is not { } dir) return null;
        if (origin is not null && ReadOrigin(dir) != origin) return null;
        return LoadFrom(dir);
    }

    /// <summary>Saves the host's certificate from one source, replacing the one from that source only.</summary>
    public async Task SaveHostAsync(string host, string source, string chainPem, string keyPem, string? origin = null)
    {
        var dir = SourceDir(host, source) ?? throw new ArgumentException($"'{host}' is not a host name.", nameof(host));
        await WriteAsync(dir, chainPem, keyPem);
        var originPath = Path.Combine(dir, "origin.txt");
        if (origin is null) File.Delete(originPath);
        else await WriteFileAsync(originPath, Encoding.UTF8.GetBytes(origin));
    }

    /// <summary>Whether the host has a certificate from that source on disk.</summary>
    public bool HasHost(string host, string source) =>
        SourceDir(host, source) is { } dir && File.Exists(Path.Combine(dir, "cert.pem")) && File.Exists(Path.Combine(dir, "key.bin"));

    /// <summary>Removes the host's certificate from one source.</summary>
    public void DeleteHost(string host, string source)
    {
        if (SourceDir(host, source) is { } dir && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static string? ReadOrigin(string dir)
    {
        var path = Path.Combine(dir, "origin.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    public HostCertificateState ReadState(string host)
    {
        var path = HostDir(host) is { } dir ? Path.Combine(dir, "state.json") : null;
        if (path is null || !File.Exists(path)) return new HostCertificateState();

        try
        {
            return JsonSerializer.Deserialize<HostCertificateState>(File.ReadAllText(path), Json) ?? new HostCertificateState();
        }
        catch (JsonException)
        {
            return new HostCertificateState();
        }
    }

    public async Task SaveStateAsync(string host, HostCertificateState state)
    {
        var dir = HostDir(host) ?? throw new ArgumentException($"'{host}' is not a host name.", nameof(host));
        Directory.CreateDirectory(dir);
        await WriteFileAsync(Path.Combine(dir, "state.json"), JsonSerializer.SerializeToUtf8Bytes(state, Json));
    }

    /// <summary>Removes everything kept for a host: its certificate, key and state.</summary>
    public void DeleteHost(string host)
    {
        if (HostDir(host) is { } dir && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>Every host with something kept for it.</summary>
    public IReadOnlyList<string> StoredHosts()
    {
        var dir = Path.Combine(_root, "hosts");
        return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).Select(Path.GetFileName).OfType<string>().ToList() : [];
    }

    // ---------------------------------------------------------------- shared

    public IReadOnlyList<(SharedCertificateInfo Info, ServedCertificate Certificate)> LoadShared()
    {
        var dir = Path.Combine(_root, "shared");
        if (!Directory.Exists(dir)) return [];

        var shared = new List<(SharedCertificateInfo, ServedCertificate)>();
        foreach (var each in Directory.EnumerateDirectories(dir))
        {
            var infoPath = Path.Combine(each, "info.json");
            if (!File.Exists(infoPath) || LoadFrom(each) is not { } certificate) continue;
            try
            {
                if (JsonSerializer.Deserialize<SharedCertificateInfo>(File.ReadAllText(infoPath), Json) is { } info)
                    shared.Add((info, certificate));
            }
            catch (JsonException)
            {
                // A damaged one is skipped; the rest still serve.
            }
        }

        return shared;
    }

    public async Task<SharedCertificateInfo> AddSharedAsync(string chainPem, string keyPem, ServedCertificate certificate, string actor)
    {
        var info = new SharedCertificateInfo
        {
            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)),
            Names = [.. certificate.Names],
            NotAfterUtc = certificate.NotAfter,
            Issuer = certificate.Issuer,
            UploadedBy = actor,
            UploadedUtc = DateTimeOffset.UtcNow
        };

        var dir = Path.Combine(_root, "shared", info.Id);
        await WriteAsync(dir, chainPem, keyPem);
        await WriteFileAsync(Path.Combine(dir, "info.json"), JsonSerializer.SerializeToUtf8Bytes(info, Json));
        return info;
    }

    public bool RemoveShared(string id)
    {
        if (id.Length != 12 || !id.All(char.IsAsciiHexDigit)) return false;
        var dir = Path.Combine(_root, "shared", id);
        if (!Directory.Exists(dir)) return false;
        Directory.Delete(dir, recursive: true);
        return true;
    }

    // ---------------------------------------------------------------- the ACME account

    /// <summary>The account key for an ACME directory, made the first time, and the account's URL once known.</summary>
    public (ECDsa Key, string? Url) Account(string directoryUrl)
    {
        var dir = AccountDir(directoryUrl);
        var keyPath = Path.Combine(dir, "account.bin");
        var urlPath = Path.Combine(dir, "account-url.txt");

        if (File.Exists(keyPath))
        {
            var existing = ECDsa.Create();
            try
            {
                existing.ImportPkcs8PrivateKey(_protector.Unprotect(File.ReadAllBytes(keyPath)), out _);
                var url = File.Exists(urlPath) ? File.ReadAllText(urlPath).Trim() : "";
                return (existing, url.Length > 0 ? url : null);
            }
            catch (CryptographicException)
            {
                // Encrypted under a key ring that is gone, or damaged: a new account does as well,
                // since nothing issued depends on which account asked for it.
                existing.Dispose();
                File.Delete(keyPath);
                File.Delete(urlPath);
            }
        }

        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(keyPath, _protector.Protect(key.ExportPkcs8PrivateKey()));
        return (key, null);
    }

    public void SaveAccountUrl(string directoryUrl, string url) =>
        File.WriteAllText(Path.Combine(AccountDir(directoryUrl), "account-url.txt"), url);

    private string AccountDir(string directoryUrl) =>
        Path.Combine(_root, "acme", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(directoryUrl)))[..16]);

    // ---------------------------------------------------------------- files

    private ServedCertificate? LoadFrom(string dir)
    {
        var certPath = Path.Combine(dir, "cert.pem");
        var keyPath = Path.Combine(dir, "key.bin");
        if (!File.Exists(certPath) || !File.Exists(keyPath)) return null;

        try
        {
            var keyPem = Encoding.UTF8.GetString(_protector.Unprotect(File.ReadAllBytes(keyPath)));
            return CertificateMaterial.Load(File.ReadAllText(certPath), keyPem);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or IOException)
        {
            // Damaged, encrypted under a key ring that is gone, or a certificate and key that do
            // not belong together (a write cut short): treated as missing, so a certificate that
            // renews itself is simply issued again.
            return null;
        }
    }

    private async Task WriteAsync(string dir, string chainPem, string keyPem)
    {
        Directory.CreateDirectory(dir);
        await WriteFileAsync(Path.Combine(dir, "key.bin"), _protector.Protect(Encoding.UTF8.GetBytes(keyPem)));
        await WriteFileAsync(Path.Combine(dir, "cert.pem"), Encoding.UTF8.GetBytes(chainPem));
    }

    private static async Task WriteFileAsync(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private string? SourceDir(string host, string source) =>
        HostDir(host) is { } dir && source is Models.CertificateSources.LetsEncrypt or Models.CertificateSources.SelfSigned or Models.CertificateSources.Uploaded
            ? Path.Combine(dir, source)
            : null;

    /// <summary>The host's folder, or null for anything that is not a plain lowercase host name, so a name can never reach outside.</summary>
    private string? HostDir(string host) => IsHostName(host) ? Path.Combine(_root, "hosts", host) : null;

    /// <summary>A plain lowercase host name or IPv4 address: letters, digits, dots and hyphens, no empty label at the start.</summary>
    public static bool IsHostName(string host) =>
        host is { Length: > 0 and <= 253 } && host[0] != '.' && !host.Contains("..", StringComparison.Ordinal) &&
        host.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-');
}
