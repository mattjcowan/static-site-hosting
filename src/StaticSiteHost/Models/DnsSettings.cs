namespace StaticSiteHost.Models;

/// <summary>
/// The server's DNS settings, in config/dns.json: the Cloudflare API token, whether each site gets
/// a DNS record and what it points at, and the records this server made. Administrators only.
/// </summary>
public sealed class DnsSettings
{
    /// <summary>The Cloudflare API token, encrypted with Data Protection. Never shown again once saved.</summary>
    public string? CloudflareToken { get; set; }

    public DateTimeOffset? TokenSetUtc { get; set; }
    public string? TokenSetBy { get; set; }

    /// <summary>Give every site a DNS record, made when the site is and removed with it.</summary>
    public bool RecordsForSites { get; set; }

    /// <summary>The address an A record points at: this server's public IPv4 address.</summary>
    public string? Ipv4 { get; set; }

    /// <summary>The address an AAAA record points at: this server's public IPv6 address.</summary>
    public string? Ipv6 { get; set; }

    /// <summary>A host name a CNAME record points at, instead of addresses, such as a load balancer's.</summary>
    public string? Target { get; set; }

    /// <summary>Whether new records go through Cloudflare's proxy (the orange cloud). A site can differ.</summary>
    public bool Proxied { get; set; } = true;

    /// <summary>The records this server made, one entry per site. Only these are ever changed or deleted.</summary>
    public List<SiteDnsRecords> Sites { get; set; } = [];

    /// <summary>Sites whose record was removed by hand: not given one again automatically.</summary>
    public List<string> Excluded { get; set; } = [];
}

/// <summary>The DNS records this server made for one site.</summary>
public sealed class SiteDnsRecords
{
    public string Domain { get; set; } = "";
    public string ZoneId { get; set; } = "";

    /// <summary>Whether the site's records go through Cloudflare's proxy.</summary>
    public bool Proxied { get; set; } = true;

    public List<DnsRecordEntry> Records { get; set; } = [];
}

/// <summary>One DNS record this server made: its Cloudflare id, type and what it points at.</summary>
public sealed class DnsRecordEntry
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Content { get; set; } = "";
}
