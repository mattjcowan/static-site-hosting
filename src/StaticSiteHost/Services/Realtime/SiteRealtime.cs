using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using StaticSiteHost.Functions;
using StaticSiteHost.Hubs;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services.Realtime;

/// <summary>
/// Makes the <see cref="IRealtime"/> for a site. Singleton; what it makes is cheap and holds
/// nothing but the domain and two singletons.
/// </summary>
public sealed class SiteRealtimeFactory
{
    private readonly IHubContext<SiteHub> _hub;
    private readonly RealtimeRegistry _registry;

    public SiteRealtimeFactory(IHubContext<SiteHub> hub, RealtimeRegistry registry)
    {
        _hub = hub;
        _registry = registry;
    }

    /// <summary>
    /// The realtime side of <paramref name="site"/>, as its functions use it. The function pipeline
    /// hands this to functions as <c>ISite.Realtime</c>, to parameters of type <see cref="IRealtime"/>,
    /// and to the functions' services, and the API publishes through it too, so all of them reach
    /// the same connections under the same rules.
    /// </summary>
    /// <remarks>
    /// It is bound to the domain the site has now. After a rename it still names the old one, whose
    /// connections the rename closed, so it reaches nobody: make a new one for the new domain, as
    /// each request does. It holds no reference to the site record, and nothing from a function
    /// bundle, so one kept for as long as the functions live keeps nothing else alive.
    /// </remarks>
    public SiteRealtime Create(SiteRecord site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return Create(site.Domain);
    }

    /// <summary>The realtime side of the site at <paramref name="domain"/>, for code that has the domain rather than the record.</summary>
    /// <param name="check">
    /// Run before every use, and throws when the caller should no longer reach the site: a set's own
    /// context passes its retirement check, so a service that kept this cannot outlive its functions
    /// (see <see cref="Services.SiteContext"/>).
    /// </param>
    public SiteRealtime Create(string domain, Action? check = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(domain);
        return new SiteRealtime(domain, _hub, _registry, check);
    }
}

/// <summary>
/// A site's <see cref="IRealtime"/>: the site's connections as <see cref="RealtimeRegistry"/> knows
/// them, and events sent through SignalR to the groups <see cref="RealtimeNames"/> keeps for the site.
/// Made by <see cref="SiteRealtimeFactory"/>.
/// </summary>
/// <remarks>
/// Every event reaches a page as the client method <see cref="RealtimeNames.ClientMethod"/> with
/// <c>(eventName, payload, group)</c>, where group is the site's own name for the group it was sent
/// to, or null. Anything that names a connection is checked against the registry first, so a
/// connection of another site is never reachable, whatever id a function passes.
/// </remarks>
public sealed class SiteRealtime : IRealtime
{
    private readonly string _domain;
    private readonly IHubContext<SiteHub> _hub;
    private readonly RealtimeRegistry _registry;
    private readonly Action? _check;

    internal SiteRealtime(string domain, IHubContext<SiteHub> hub, RealtimeRegistry registry, Action? check = null)
    {
        _domain = domain;
        _hub = hub;
        _registry = registry;
        _check = check;
    }

    /// <summary>The site this reaches.</summary>
    public string Domain => _domain;

    /// <summary>The domain, once the caller's check says it may still be reached.</summary>
    private string Site
    {
        get
        {
            _check?.Invoke();
            return _domain;
        }
    }

    public int ConnectionCount => _registry.ConnectionCount(Site);

    public IReadOnlyList<RealtimeConnection> Connections => _registry.Connections(Site);

    public IReadOnlyList<string> Groups => _registry.Groups(Site);

    public IReadOnlyList<RealtimeConnection> Members(string group)
    {
        RequireGroup(group);
        return _registry.Members(Site, group);
    }

    public Task PublishAsync(string eventName, JsonElement? payload = null, CancellationToken ct = default)
    {
        var body = Prepare(eventName, payload);
        return _hub.Clients.Group(RealtimeNames.AllGroup(Site)).SendAsync(RealtimeNames.ClientMethod, eventName, body, null, ct);
    }

    public Task PublishToGroupAsync(string group, string eventName, JsonElement? payload = null, CancellationToken ct = default)
    {
        RequireGroup(group);
        var body = Prepare(eventName, payload);
        return _hub.Clients.Group(RealtimeNames.Group(Site, group)).SendAsync(RealtimeNames.ClientMethod, eventName, body, group, ct);
    }

    public Task PublishToUserAsync(string user, string eventName, JsonElement? payload = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(user);
        var body = Prepare(eventName, payload);

        var connections = _registry.ConnectionsOf(Site, user);
        return connections.Count == 0
            ? Task.CompletedTask
            : _hub.Clients.Clients(connections).SendAsync(RealtimeNames.ClientMethod, eventName, body, null, ct);
    }

    public Task PublishToConnectionAsync(string connectionId, string eventName, JsonElement? payload = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        var body = Prepare(eventName, payload);

        return !_registry.IsConnected(Site, connectionId)
            ? Task.CompletedTask
            : _hub.Clients.Client(connectionId).SendAsync(RealtimeNames.ClientMethod, eventName, body, null, ct);
    }

    public async Task AddToGroupAsync(string connectionId, string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        RequireGroup(group);
        var domain = Site;

        // Under the site's group guard, so a removal of the group cannot land between the two changes (see RealtimeRegistry.GuardGroupsAsync).
        using var guard = await _registry.GuardGroupsAsync(domain, ct);

        switch (_registry.Join(domain, connectionId, group))
        {
            case RealtimeRegistry.JoinResult.Joined:
                await AddToSignalRGroupAsync(domain, connectionId, group, ct);
                return;

            case RealtimeRegistry.JoinResult.ConnectionAtLimit:
                throw new InvalidOperationException(
                    $"Connection {connectionId} is already in {_registry.MaxGroupsPerConnection} groups, the most one connection may " +
                    "join on this server (SiteHosting:RealtimeMaxGroupsPerConnection). Take it out of one first.");

            case RealtimeRegistry.JoinResult.SiteAtLimit:
                throw new InvalidOperationException(
                    $"{domain} already has {_registry.MaxGroupsPerSite} groups, the most a site may have on this server " +
                    "(SiteHosting:RealtimeMaxGroupsPerSite). Remove one with RemoveGroupAsync first.");

            default:
                return; // Already a member, or not connected to this site: nothing to do.
        }
    }

    public async Task RemoveFromGroupAsync(string connectionId, string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        RequireGroup(group);
        var domain = Site;

        using var guard = await _registry.GuardGroupsAsync(domain, ct);

        if (_registry.Leave(domain, connectionId, group))
            await _hub.Groups.RemoveFromGroupAsync(connectionId, RealtimeNames.Group(domain, group), ct);
    }

    /// <summary>
    /// Empties a group, in the registry and in SignalR, under the site's group guard: a page joining
    /// the group again must come before both changes or after both, never between them, where it
    /// would be left in the registry's copy of the group and out of SignalR's, hearing nothing.
    /// </summary>
    public async Task RemoveGroupAsync(string group, CancellationToken ct = default)
    {
        RequireGroup(group);
        var domain = Site;

        using var guard = await _registry.GuardGroupsAsync(domain, ct);

        var name = RealtimeNames.Group(domain, group);
        foreach (var connectionId in _registry.RemoveGroup(domain, group))
            await _hub.Groups.RemoveFromGroupAsync(connectionId, name, ct);
    }

    public Task DisconnectAsync(string connectionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);

        _registry.Abort(Site, connectionId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds a connection the registry has just put in a group to the SignalR group as well, and
    /// takes it back out of the registry if that fails, so the two never disagree for long.
    /// </summary>
    private async Task AddToSignalRGroupAsync(string domain, string connectionId, string group, CancellationToken ct)
    {
        try
        {
            await _hub.Groups.AddToGroupAsync(connectionId, RealtimeNames.Group(domain, group), ct);
        }
        catch
        {
            _registry.Leave(domain, connectionId, group);
            throw;
        }
    }

    /// <summary>
    /// Checks an event and its payload, and returns the payload to send: null for none, otherwise a
    /// copy, so a caller that disposes the JsonDocument the element came from before the send has
    /// finished cannot break it for every connection it was going to.
    /// </summary>
    private static JsonElement? Prepare(string eventName, JsonElement? payload)
    {
        if (RealtimeNames.EventError(eventName) is { } error) throw new ArgumentException(error, nameof(eventName));

        // Undefined is what a default JsonElement holds: no payload at all, the same as null.
        if (payload is not { ValueKind: not JsonValueKind.Undefined } value) return null;
        if (RealtimeNames.PayloadError(value) is { } tooLarge) throw new ArgumentException(tooLarge, nameof(payload));

        return value.Clone();
    }

    private static void RequireGroup(string group)
    {
        if (RealtimeNames.GroupError(group) is { } error) throw new ArgumentException(error, nameof(group));
    }
}
