using System.Net;
using System.Net.Sockets;

namespace StaticSiteHost.Services;

/// <summary>
/// A client's address as the host's per-client limits count it: the AI chat limiter, the realtime
/// negotiation limiter and the realtime connections per address.
///
/// IPv4 is counted as it is. IPv6 is counted by its /64 network, since one household is handed a
/// whole /64 and could otherwise take a fresh address, and with it a fresh allowance, for every
/// request. Behind a proxy whose forwarded headers are not trusted every visitor has the proxy's
/// address, and so shares one allowance; set SiteHosting:TrustForwardedHeaders there.
/// </summary>
public static class ClientAddress
{
    /// <summary>The key a client is counted under.</summary>
    public static string Key(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
