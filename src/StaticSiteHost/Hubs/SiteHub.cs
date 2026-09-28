using Microsoft.AspNetCore.SignalR;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Realtime;

namespace StaticSiteHost.Hubs;

/// <summary>
/// The realtime hub, one for every site, at <see cref="Path"/> on each site's own domain: what
/// <c>site.realtime</c> in <c>/_host/site.js</c> connects to.
///
/// A request reaches it only through <see cref="SiteHostingMiddleware"/>, which lets it
/// pass once the site's passcode gate has, with the site in <c>HttpContext.Items</c> under
/// <see cref="SiteItemKey"/>. Program.cs maps the hub like any endpoint, so it also answers on the
/// management host; a connection that arrives without a site is closed as soon as it is made.
///
/// A connection belongs to the site it came in on, for as long as it lasts. It is registered with
/// <see cref="RealtimeRegistry"/> and put in the SignalR group of every connection to that site,
/// and it leaves both when it ends. A page may call two methods, <see cref="Join"/> and
/// <see cref="Leave"/>, and nothing else: publishing, and putting other connections in groups, is
/// for the site's functions (<see cref="SiteRealtime"/>) and the API. A refusal of either is a
/// <see cref="HubException"/> with a sentence the page can show.
///
/// The site's functions decide who may connect and who a connection belongs to, with a
/// <c>[RealtimeConnect]</c> hook, and which groups a page may join, with a <c>[RealtimeJoin]</c>
/// hook (<see cref="FunctionHost.InvokeRealtimeConnectAsync"/>). A site without them lets every
/// page connect, with no user, and join any group.
/// </summary>
public sealed class SiteHub : Hub
{
    /// <summary>Where the hub is mapped. Negotiation is under it, at <c>/_host/realtime/negotiate</c>.</summary>
    public const string Path = "/_host/realtime";

    /// <summary>The <c>HttpContext.Items</c> key the middleware leaves the connecting request's <see cref="SiteRecord"/> under.</summary>
    public const string SiteItemKey = "StaticSiteHost.SiteRecord";

    /// <summary>The connection's <see cref="HubCallerContext.Items"/> key for the domain it was registered under.</summary>
    private const string DomainKey = "StaticSiteHost.Domain";

    private readonly RealtimeRegistry _registry;
    private readonly SiteStore _sites;
    private readonly SitePasscodeGate _passcodes;
    private readonly FunctionHost _functions;
    private readonly ILogger<SiteHub> _logger;

    public SiteHub(
        RealtimeRegistry registry, SiteStore sites, SitePasscodeGate passcodes, FunctionHost functions, ILogger<SiteHub> logger)
    {
        _registry = registry;
        _sites = sites;
        _passcodes = passcodes;
        _functions = functions;
        _logger = logger;
    }

    /// <summary>True for the hub's own path and anything under it, without regard to case, as routing matches it.</summary>
    public static bool Handles(PathString path) => path.StartsWithSegments(Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the request that starts a connection, before any transport is chosen.</summary>
    public static bool IsNegotiation(PathString path) =>
        path.StartsWithSegments(Path, StringComparison.OrdinalIgnoreCase, out var rest) &&
        rest.Equals("/negotiate", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a request that would make a new connection: the negotiation, and a request to the
    /// hub's own path without a connection id, which is how a client that skips negotiating opens a
    /// WebSocket. Every other request under the hub belongs to a connection already made.
    /// </summary>
    public static bool StartsConnection(HttpRequest request)
    {
        if (IsNegotiation(request.Path)) return true;

        return request.Path.StartsWithSegments(Path, StringComparison.OrdinalIgnoreCase, out var rest) &&
               (!rest.HasValue || rest.Value == "/") &&
               !request.Query.ContainsKey("id");
    }

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        if (http is null || !http.Items.TryGetValue(SiteItemKey, out var item) || item is not SiteRecord site)
        {
            _logger.LogDebug(
                "Closed realtime connection {ConnectionId}: it came in on {Host}, which is not a site's domain",
                Context.ConnectionId, http?.Request.Host.Value);
            Context.Abort();
            return;
        }

        var domain = site.Domain;

        // The site's [RealtimeConnect] hook, when its functions declare one, says whether this page
        // may connect and who it belongs to. It fails closed, and FunctionHost logs why when it does.
        var hook = await _functions.InvokeRealtimeConnectAsync(site, http);
        if (!hook.Allowed)
        {
            _logger.LogDebug(
                "Closed realtime connection {ConnectionId} to {Domain}: the site's [RealtimeConnect] hook {Reason}",
                Context.ConnectionId, domain, hook.Error is null ? "refused it" : "failed");
            Context.Abort();
            return;
        }

        var address = ClientAddress.Key(http.Connection.RemoteIpAddress);
        switch (_registry.TryAdd(domain, Context, hook.User, address))
        {
            case RealtimeRegistry.AddResult.SiteFull:
                _logger.LogDebug(
                    "Closed realtime connection {ConnectionId} to {Domain}: the site has {Max} already",
                    Context.ConnectionId, domain, _registry.MaxConnectionsPerSite);
                Context.Abort();
                return;

            case RealtimeRegistry.AddResult.AddressFull:
                _logger.LogDebug(
                    "Closed realtime connection {ConnectionId} to {Domain}: {Address} has {Max} to it already",
                    Context.ConnectionId, domain, address, _registry.MaxConnectionsPerAddress);
                Context.Abort();
                return;
        }

        Context.Items[DomainKey] = domain;

        try
        {
            // Deleted, renamed or given a new passcode after the middleware let this request
            // through. Each of those closes the domain's connections once it is done, and this
            // check comes after the registration, so whichever order the two run in, this
            // connection goes.
            if (!IsStillServed(http, site, domain))
            {
                _logger.LogDebug("Closed realtime connection {ConnectionId}: {Domain} changed while it was being made", Context.ConnectionId, domain);
                _registry.Remove(domain, Context.ConnectionId);
                Context.Abort();
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeNames.AllGroup(domain), Context.ConnectionAborted);
        }
        catch
        {
            // SignalR does not call OnDisconnectedAsync when this method throws.
            _registry.Remove(domain, Context.ConnectionId);
            throw;
        }
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // SignalR takes the connection out of its groups itself; this is the registry's copy.
        if (DomainOf(Context) is { } domain) _registry.Remove(domain, Context.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Puts this connection in one of its site's groups, so it receives what is published to it:
    /// <c>site.realtime.join(group)</c>. Joining a group it is already in does nothing.
    /// </summary>
    public async Task Join(string group)
    {
        var domain = RequireDomain();
        if (RealtimeNames.GroupError(group) is { } error) throw new HubException(error);

        // The site's [RealtimeJoin] hook, when its functions declare one, decides; without one every
        // join is allowed. It runs with the request that made this connection, cookies included.
        if (_sites.TryGet(domain) is not { } site || Context.GetHttpContext() is not { } http)
            throw new HubException("This connection has been closed. Connect again.");

        var hook = await _functions.InvokeRealtimeJoinAsync(site, http, group);
        if (!hook.Allowed)
        {
            throw new HubException(hook.Error is null
                ? $"This site did not let this page join \"{group}\"."
                : $"This site did not let this page join \"{group}\": its check failed. The details are in the server log.");
        }

        // The registry and SignalR change together, under the site's group guard, so a function
        // emptying the group cannot land between the two (see RealtimeRegistry.GuardGroupsAsync).
        using var guard = await _registry.GuardGroupsAsync(domain, Context.ConnectionAborted);

        switch (_registry.Join(domain, Context.ConnectionId, group))
        {
            case RealtimeRegistry.JoinResult.Joined:
                try
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeNames.Group(domain, group), Context.ConnectionAborted);
                }
                catch
                {
                    _registry.Leave(domain, Context.ConnectionId, group);
                    throw;
                }

                return;

            case RealtimeRegistry.JoinResult.AlreadyMember:
                return;

            case RealtimeRegistry.JoinResult.ConnectionAtLimit:
                throw new HubException(
                    $"This page is already in {_registry.MaxGroupsPerConnection} groups, the most one connection may join. Leave one first.");

            case RealtimeRegistry.JoinResult.SiteAtLimit:
                throw new HubException(
                    $"This site already has {_registry.MaxGroupsPerSite} groups, the most it may have, so a new one cannot start. " +
                    "Join one that has members, or try again once some have emptied.");

            default:
                throw new HubException("This connection has been closed. Connect again.");
        }
    }

    /// <summary>
    /// Takes this connection out of a group: <c>site.realtime.leave(group)</c>. Leaving a group it
    /// is not in does nothing.
    /// </summary>
    public async Task Leave(string group)
    {
        var domain = RequireDomain();
        if (RealtimeNames.GroupError(group) is { } error) throw new HubException(error);

        using var guard = await _registry.GuardGroupsAsync(domain, Context.ConnectionAborted);

        if (_registry.Leave(domain, Context.ConnectionId, group))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeNames.Group(domain, group), Context.ConnectionAborted);
    }

    /// <summary>
    /// False when the site has changed under the request making this connection since it was let
    /// in: the store no longer has this record under this domain (deleted), the record has a domain
    /// other than the one the request came in on (renamed), or the request's passcode cookie no
    /// longer opens it (a new passcode).
    /// </summary>
    private bool IsStillServed(HttpContext http, SiteRecord site, string domain) =>
        ReferenceEquals(_sites.TryGet(domain), site) &&
        string.Equals(http.Request.Host.Host.TrimEnd('.'), domain, StringComparison.OrdinalIgnoreCase) &&
        _passcodes.IsUnlocked(http, site);

    private static string? DomainOf(HubCallerContext context) =>
        context.Items.TryGetValue(DomainKey, out var domain) ? domain as string : null;

    /// <summary>The domain this connection belongs to. Only a connection that was let in has one, and only those stay open.</summary>
    private string RequireDomain() =>
        DomainOf(Context) ?? throw new HubException("This connection has been closed. Connect again.");
}
