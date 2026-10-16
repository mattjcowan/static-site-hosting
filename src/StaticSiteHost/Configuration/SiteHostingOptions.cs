namespace StaticSiteHost.Configuration;

/// <summary>
/// Everything that controls where content lives and how it is served.
/// Bound from the "SiteHosting" configuration section.
/// </summary>
public sealed class SiteHostingOptions
{
    public const string SectionName = "SiteHosting";

    /// <summary>Root of the data volume. Holds config/, sites/ and tmp/.</summary>
    public string DataRoot { get; set; } = "/data";

    /// <summary>
    /// Hostnames that serve the management UI/API instead of a hosted site.
    /// When empty, the management app answers any host that has no site.
    /// </summary>
    public string[] ManagementHosts { get; set; } = [];

    /// <summary>Largest accepted upload (compressed archive) in bytes.</summary>
    public long MaxUploadBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Largest accepted total size after extraction — zip-bomb guard.</summary>
    public long MaxExtractedBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Largest accepted single file after extraction.</summary>
    public long MaxEntryBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Largest accepted number of entries in an archive.</summary>
    public int MaxEntries { get; set; } = 50_000;

    /// <summary>How many previous releases to keep on disk for rollback.</summary>
    public int ReleasesToKeep { get; set; } = 3;

    /// <summary>max-age applied to non-HTML assets served from a site.</summary>
    public int AssetCacheSeconds { get; set; } = 3600;

    /// <summary>
    /// When true, every unresolved request falls back to index.html — including
    /// .js/.css/.png. Off by default so missing assets 404 instead of being
    /// served HTML with the wrong content type.
    /// </summary>
    public bool SpaFallbackForAllRequests { get; set; }

    /// <summary>Lifetime of an invitation / password-reset link.</summary>
    public int InviteLifetimeHours { get; set; } = 168;

    /// <summary>Minimum length enforced when a user sets a password.</summary>
    public int MinPasswordLength { get; set; } = 12;

    /// <summary>Minimum length enforced when a site is given a visitor passcode.</summary>
    public int MinPasscodeLength { get; set; } = 8;

    /// <summary>
    /// How long a visitor stays unlocked after entering a site's passcode. The cookie is
    /// per-domain, so unlocking one private site never unlocks another.
    /// </summary>
    public int PasscodeSessionHours { get; set; } = 168;

    /// <summary>
    /// Honour X-Forwarded-Host/-Proto/-For. Turn this on only when the app sits behind a
    /// trusted reverse proxy: routing is driven by the Host header, so a spoofable
    /// X-Forwarded-Host would let a caller pick which site answers.
    /// </summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>
    /// How long a call to an AI provider may take: the whole answer when it is not streamed, and
    /// the longest silence between two pieces of one that is.
    /// </summary>
    public int AiTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// How many chat requests one address may make to one site's <c>/_host/ai/chat</c> in any
    /// minute. Every one of them is billed to the provider's key. 0 turns the limit off.
    /// </summary>
    public int AiVisitorRequestsPerMinute { get; set; } = 20;

    /// <summary>Largest body <c>/_host/ai/chat</c> accepts, in bytes: the conversation a browser sends.</summary>
    public int AiMaxRequestBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// The longest answer, in tokens, that a browser's chat at <c>/_host/ai/chat</c> may get, so a
    /// visitor cannot run up the bill with one question. 0 leaves it to the provider: Anthropic's
    /// 1024, or the model's own limit for an OpenAI-compatible one. Functions calling
    /// <c>ISite.Ai</c> set their own.
    /// </summary>
    public int AiVisitorMaxTokens { get; set; } = 1024;

    /// <summary>
    /// How many pages may be connected to one site's realtime hub at once. The next is refused until
    /// one leaves. 0 refuses every connection, which turns realtime off.
    /// </summary>
    public int RealtimeMaxConnectionsPerSite { get; set; } = 1000;

    /// <summary>
    /// How many pages one client address may have connected to one site's realtime hub at once, so
    /// that one client cannot take every place a site has. An IPv6 client is counted by its /64.
    /// 0 sets no limit.
    /// </summary>
    public int RealtimeMaxConnectionsPerAddress { get; set; } = 20;

    /// <summary>
    /// How many new connections one client address may start to one site's realtime hub in any
    /// minute. Each runs the site's <c>[RealtimeConnect]</c> hook, if it has one. 0 turns the limit off.
    /// </summary>
    public int RealtimeNegotiationsPerMinute { get; set; } = 60;

    /// <summary>
    /// How many groups one site may have at once. A group exists while it has a member, so the
    /// limit is on groups in use, not on names ever used.
    /// </summary>
    public int RealtimeMaxGroupsPerSite { get; set; } = 1000;

    /// <summary>How many groups one connection, which is one open page, may be in at once.</summary>
    public int RealtimeMaxGroupsPerConnection { get; set; } = 100;

    /// <summary>
    /// Networks a proxy route may never connect to, comma-separated, checked against the address a
    /// target's name resolves to when the connection is made. The default is link-local addresses,
    /// which is where cloud metadata services answer. Private networks and other ports on this
    /// machine are allowed: Docker networks and services beside the server are what proxy routes are
    /// for. This server's own ports are refused whatever this says (see <c>ProxyInvokers</c>). Empty
    /// blocks nothing more. A string rather than a list, so that setting it replaces the default
    /// instead of being merged into it.
    /// </summary>
    public string ProxyBlockedNetworks { get; set; } = "169.254.0.0/16, fe80::/10";

    /// <summary>How long a proxy route waits for a connection to its target.</summary>
    public int ProxyConnectTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// A second plain-HTTP port that speaks HTTP/2 only (h2c, with prior knowledge), for a proxy in
    /// front that ends HTTPS and passes gRPC on: Caddy's <c>h2c://</c>, nginx's <c>grpc_pass</c>.
    /// Kestrel cannot offer HTTP/1.1 and h2c on one plain port, so the usual port stays HTTP/1.1 and
    /// this one is added beside it. 0, the default, leaves it off.
    /// </summary>
    public int H2cPort { get; set; }

    /// <summary>HTTPS in the server itself: off unless turned on. See <see cref="TlsOptions"/>.</summary>
    public TlsOptions Tls { get; set; } = new();

    private HashSet<string>? _managementHostSet;

    public bool IsManagementHost(string host)
    {
        _managementHostSet ??= new HashSet<string>(
            ManagementHosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
        return _managementHostSet.Contains(host);
    }

    public bool HasManagementHosts => ManagementHosts.Any(h => !string.IsNullOrWhiteSpace(h));
}

/// <summary>
/// HTTPS ended in the server, with a certificate per host name: from Let's Encrypt, made here
/// (self-signed), or uploaded. Bound from SiteHosting:Tls. Off by default, so a server behind a
/// proxy that ends TLS itself (Caddy, Railway, a Cloudflare Tunnel) is not affected.
/// </summary>
public sealed class TlsOptions
{
    /// <summary>Let's Encrypt's production directory.</summary>
    public const string LetsEncrypt = "https://acme-v02.api.letsencrypt.org/directory";

    /// <summary>Let's Encrypt's staging directory: certificates no browser trusts, and generous rate limits.</summary>
    public const string LetsEncryptStaging = "https://acme-staging-v02.api.letsencrypt.org/directory";

    /// <summary>Listen for HTTPS, and answer Let's Encrypt's checks on the HTTP port.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The port HTTP is served on, with Let's Encrypt's checks and the redirects to HTTPS. In the
    /// container, 8080, published as 80: Let's Encrypt only ever checks port 80.
    /// </summary>
    public int HttpPort { get; set; } = 8080;

    /// <summary>The port HTTPS is served on. In the container, 8443, published as 443.</summary>
    public int HttpsPort { get; set; } = 8443;

    /// <summary>
    /// The port visitors reach HTTPS on, for redirects from HTTP: 443 when the container's 8443 is
    /// published as 443, which is the usual case.
    /// </summary>
    public int PublicHttpsPort { get; set; } = 443;

    /// <summary>
    /// Where a host's certificate comes from when its site has not chosen: <c>letsencrypt</c>,
    /// <c>selfsigned</c> or <c>none</c>. Either way a shared certificate that covers the host (such
    /// as a Cloudflare Origin certificate for <c>*.example.com</c>) is used first.
    /// </summary>
    public string DefaultSource { get; set; } = "letsencrypt";

    /// <summary>
    /// A contact address for the ACME account. Optional: Let's Encrypt stopped sending expiry
    /// e-mails in 2025, and other ACME servers may use it.
    /// </summary>
    public string? AcmeEmail { get; set; }

    /// <summary>
    /// The ACME directory: <c>production</c> or <c>staging</c> for Let's Encrypt's, or the URL of
    /// any other ACME server (step-ca, ZeroSSL, Pebble).
    /// </summary>
    public string AcmeDirectory { get; set; } = "production";

    /// <summary>
    /// A PEM file of the certificate authority an ACME server's own HTTPS uses, for a private one
    /// such as step-ca or Pebble. Leave empty for Let's Encrypt.
    /// </summary>
    public string? AcmeCaFile { get; set; }

    /// <summary>Renew a certificate this many days before it expires.</summary>
    public int RenewDaysBefore { get; set; } = 30;

    /// <summary>The directory URL <see cref="AcmeDirectory"/> names.</summary>
    public string AcmeDirectoryUrl => AcmeDirectory.Trim().ToLowerInvariant() switch
    {
        "" or "production" or "letsencrypt" => LetsEncrypt,
        "staging" or "letsencrypt-staging" => LetsEncryptStaging,
        _ => AcmeDirectory.Trim()
    };

    /// <summary>Whether <see cref="AcmeDirectoryUrl"/> is Let's Encrypt's staging directory.</summary>
    public bool IsStaging => AcmeDirectoryUrl == LetsEncryptStaging;
}

