using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Localization;
using Yarp.ReverseProxy.Forwarder;

namespace StaticSiteHost.Serving;

/// <summary>
/// The two clients every proxy route forwards through, for the life of the server: one checks
/// the target's certificate, the other accepts any (a route's "Accept any certificate"). Routes
/// differ only in what <see cref="ForwarderRequestConfig"/> carries, so two connection pools serve
/// them all.
///
/// Every connection is made by <see cref="ConnectAsync"/>, which refuses two kinds of address:
///
///   * one in <see cref="SiteHostingOptions.ProxyBlockedNetworks"/> (by default link-local, which
///     is where cloud metadata services answer);
///   * this server itself: a port the server listens on, at any address of this machine. That is
///     loopback, the unspecified address, or one of the machine's own interfaces, such as a
///     container's address on its Docker network. Other ports on this machine are allowed, so a
///     route can reach a service beside the server on the same host.
///
/// The check is on the address a name actually resolved to, at the moment of connecting, so a name
/// that resolves somewhere harmless when a route is saved and somewhere blocked later still cannot
/// reach it. A loop through a public name, which comes back in from outside, is
/// <see cref="ProxyTransformer"/>'s to stop, with a hop count.
/// </summary>
public sealed partial class ProxyInvokers : IDisposable
{
    /// <summary>How long the list of this machine's own addresses is trusted before it is read again.</summary>
    private static readonly TimeSpan OwnAddressLifetime = TimeSpan.FromMinutes(5);

    private readonly IPNetwork[] _blocked;
    private readonly IServer? _server;
    private int[] _ownPorts = [];
    private (HashSet<IPAddress> Addresses, DateTime ReadUtc) _ownAddresses = ([], DateTime.MinValue);

    /// <param name="server">The server this runs in, for the ports it listens on. Null in tests that need no server.</param>
    public ProxyInvokers(IOptions<SiteHostingOptions> options, IServer? server = null)
    {
        _server = server;
        _blocked = ParseNetworks(options.Value.ProxyBlockedNetworks);
        var connectTimeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.ProxyConnectTimeoutSeconds));

        Strict = Create(connectTimeout, acceptAnyCertificate: false);
        Lenient = Create(connectTimeout, acceptAnyCertificate: true);
    }

    /// <summary>Checks the target's certificate as any client would.</summary>
    public HttpMessageInvoker Strict { get; }

    /// <summary>Accepts any certificate, a self-signed one included.</summary>
    public HttpMessageInvoker Lenient { get; }

    public HttpMessageInvoker For(bool acceptAnyCertificate) => acceptAnyCertificate ? Lenient : Strict;

    /// <summary>
    /// Why a proxy route may not connect to an address and port, as the key of its message
    /// (<c>proxy.connect.blocked</c> or <c>proxy.connect.self</c>), or null when it may.
    /// </summary>
    public string? Refusal(IPAddress address, int port)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (_blocked.Any(network => network.Contains(address)))
            return "proxy.connect.blocked";

        if (OwnPorts().Contains(port) && IsThisMachine(address))
            return "proxy.connect.self";

        return null;
    }

    /// <summary>The ports the server listens on, read once it has started listening.</summary>
    private int[] OwnPorts()
    {
        if (_ownPorts.Length > 0 || _server?.Features.Get<IServerAddressesFeature>() is not { Addresses.Count: > 0 } feature)
            return _ownPorts;

        // "http://127.0.0.1:8080", "http://[::]:8080", "http://+:8080": the port is what follows the last colon.
        return _ownPorts = feature.Addresses
            .Select(address => ListenPort().Match(address))
            .Where(match => match.Success)
            .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Distinct()
            .ToArray();
    }

    [GeneratedRegex(@":(\d+)/?$")]
    private static partial Regex ListenPort();

    /// <summary>Loopback, 0.0.0.0/8 and :: (which reach this machine on Linux), or one of its own interface addresses.</summary>
    private bool IsThisMachine(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any) ||
            (address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] == 0))
            return true;

        var (addresses, readUtc) = _ownAddresses;
        if (DateTime.UtcNow - readUtc > OwnAddressLifetime)
        {
            addresses = ReadOwnAddresses();
            _ownAddresses = (addresses, DateTime.UtcNow);
        }

        return addresses.Contains(address);
    }

    private static HashSet<IPAddress> ReadOwnAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address.IsIPv4MappedToIPv6 ? unicast.Address.MapToIPv4() : unicast.Address)
                .ToHashSet();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads the comma-separated list. An entry that is not a network stops the server from
    /// starting: quietly blocking less than the administrator wrote would be the worse failure.
    /// </summary>
    public static IPNetwork[] ParseNetworks(string? text) =>
        (text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => IPNetwork.TryParse(entry, out var network)
                ? network
                : throw new InvalidOperationException(
                    $"SiteHosting:ProxyBlockedNetworks: '{entry}' is not a network. Write each one as an address and prefix length, such as 169.254.0.0/16."))
            .ToArray();

    private HttpMessageInvoker Create(TimeSpan connectTimeout, bool acceptAnyCertificate)
    {
        // What YARP's own client factory sets, plus the connection check. The handler never
        // expires, as the AI client's does not; PooledConnectionLifetime is what picks up a
        // container that came back on a new address.
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            EnableMultipleHttp2Connections = true,
            ActivityHeadersPropagator = new ReverseProxyPropagator(DistributedContextPropagator.Current),
            ConnectTimeout = connectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectAsync
        };

        if (acceptAnyCertificate)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        return new HttpMessageInvoker(handler, disposeHandler: true);
    }

    /// <summary>
    /// Connects to the first address the target's name resolves to that is not blocked, trying the
    /// next on failure.
    /// </summary>
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct);

        string? refusal = null;
        ExceptionDispatchInfo? lastFailure = null;

        foreach (var address in addresses)
        {
            if (Refusal(address, port) is { } reason)
            {
                refusal = reason;
                continue;
            }

            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                if (ct.IsCancellationRequested) throw;
                lastFailure = ExceptionDispatchInfo.Capture(ex);
            }
        }

        lastFailure?.Throw();

        throw refusal is not null
            ? new ProxyConnectException(refusal, new { address = $"{host}:{port}" })
            : new ProxyConnectException("proxy.connect.noAddress", new { host });
    }

    public void Dispose()
    {
        Strict.Dispose();
        Lenient.Dispose();
    }
}

/// <summary>
/// A connection a proxy route may not make, or a target with no address. Its message is English,
/// for the log; <see cref="Text"/> is in the current language, for the page that tested the route.
/// </summary>
public sealed class ProxyConnectException(string key, object values)
    : HttpRequestException(ServerText.English(() => ServerText.Get(key, values)))
{
    public string Text() => ServerText.Get(key, values);
}
