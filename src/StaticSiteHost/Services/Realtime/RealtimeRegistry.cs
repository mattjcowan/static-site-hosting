using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Functions;

namespace StaticSiteHost.Services.Realtime;

/// <summary>
/// Who is connected to each site's realtime hub: every connection with its user, when it
/// connected and its groups, and every group with its members. SignalR keeps groups of its own,
/// but it cannot list them or say which site a connection came in on. This is what lets a site's
/// functions see their connections, reach one user's, and never another site's, and what the
/// limits in <see cref="SiteHostingOptions"/> are counted against.
///
/// Each connection's <see cref="HubCallerContext"/> is kept, which is how one is closed from
/// outside the hub: by a function (<see cref="IRealtime.DisconnectAsync"/>), or by the host when
/// the site is deleted, renamed or given a new passcode (<see cref="DisconnectSite"/>). An entry
/// goes when its connection ends, so nothing here outlives the connection it describes, and it
/// holds only framework types and strings: nothing from a function bundle.
///
/// Group names are the site's own, without the prefix SignalR knows them by
/// (<see cref="RealtimeNames"/>). Each site has a lock of its own, and its entry is dropped once
/// its last connection goes, so a site nobody has open costs nothing here. Singleton; every
/// member is thread-safe, and the lists it returns are snapshots.
///
/// Connections are also counted per client address (<see cref="ClientAddress"/>), so that one
/// client cannot take every place a site has: <see cref="MaxConnectionsPerAddress"/>.
/// </summary>
public sealed class RealtimeRegistry
{
    /// <summary>What <see cref="TryAdd"/> did.</summary>
    public enum AddResult
    {
        /// <summary>The connection is registered.</summary>
        Added,

        /// <summary>The site has <see cref="MaxConnectionsPerSite"/> already; nothing registered.</summary>
        SiteFull,

        /// <summary>The address has <see cref="MaxConnectionsPerAddress"/> connections to the site already; nothing registered.</summary>
        AddressFull
    }

    /// <summary>What <see cref="Join"/> did.</summary>
    public enum JoinResult
    {
        /// <summary>The connection is now in the group. Add it to the SignalR group too.</summary>
        Joined,

        /// <summary>It already was; nothing changed.</summary>
        AlreadyMember,

        /// <summary>No such connection to this site, now or ever; nothing changed.</summary>
        NotConnected,

        /// <summary>The connection is in <see cref="MaxGroupsPerConnection"/> groups already.</summary>
        ConnectionAtLimit,

        /// <summary>The group is new, and the site has <see cref="MaxGroupsPerSite"/> groups already.</summary>
        SiteAtLimit
    }

    private readonly ConcurrentDictionary<string, SiteConnections> _sites = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One per site that has ever changed a group; see <see cref="GuardGroupsAsync"/>. As many as there are sites.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _groupGuards = new(StringComparer.OrdinalIgnoreCase);

    public RealtimeRegistry(IOptions<SiteHostingOptions> options)
    {
        var settings = options.Value;
        MaxConnectionsPerSite = Math.Max(0, settings.RealtimeMaxConnectionsPerSite);
        MaxConnectionsPerAddress = Math.Max(0, settings.RealtimeMaxConnectionsPerAddress);
        MaxGroupsPerSite = Math.Max(0, settings.RealtimeMaxGroupsPerSite);
        MaxGroupsPerConnection = Math.Max(0, settings.RealtimeMaxGroupsPerConnection);
    }

    /// <summary><see cref="SiteHostingOptions.RealtimeMaxConnectionsPerSite"/>; 0 refuses every connection.</summary>
    public int MaxConnectionsPerSite { get; }

    /// <summary><see cref="SiteHostingOptions.RealtimeMaxConnectionsPerAddress"/>; 0 sets no limit.</summary>
    public int MaxConnectionsPerAddress { get; }

    /// <summary><see cref="SiteHostingOptions.RealtimeMaxGroupsPerSite"/>.</summary>
    public int MaxGroupsPerSite { get; }

    /// <summary><see cref="SiteHostingOptions.RealtimeMaxGroupsPerConnection"/>.</summary>
    public int MaxGroupsPerConnection { get; }

    // ---- connections --------------------------------------------------------

    /// <summary>
    /// Registers a connection that has just been made to <paramref name="domain"/> from
    /// <paramref name="address"/>. Nothing is registered when the site already has
    /// <see cref="MaxConnectionsPerSite"/>, or the address already has
    /// <see cref="MaxConnectionsPerAddress"/> to it.
    /// </summary>
    /// <param name="address">The client, as <see cref="ClientAddress.Key"/> gives it.</param>
    public AddResult TryAdd(string domain, HubCallerContext context, string? user, string address)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(address);

        while (true)
        {
            var site = _sites.GetOrAdd(domain, static _ => new SiteConnections());
            lock (site.Gate)
            {
                // Dropped between the lookup and the lock, by its last connection leaving or by
                // DisconnectSite. Anything added to it now would be lost, so look again.
                if (site.Retired) continue;

                var refusal =
                    site.Connections.Count >= MaxConnectionsPerSite ? AddResult.SiteFull
                    : IsAtAddressLimit(site, address) ? AddResult.AddressFull
                    : AddResult.Added;

                if (refusal != AddResult.Added)
                {
                    DropIfEmpty(domain, site);
                    return refusal;
                }

                site.Connections[context.ConnectionId] = new Connection(context, user, address, DateTimeOffset.UtcNow);
                site.PerAddress[address] = site.PerAddress.GetValueOrDefault(address) + 1;
                return AddResult.Added;
            }
        }
    }

    /// <summary>Forgets a connection that has ended, and takes it out of its groups. False when it was not registered.</summary>
    public bool Remove(string domain, string connectionId)
    {
        if (!_sites.TryGetValue(domain, out var site)) return false;

        lock (site.Gate)
        {
            if (!site.Connections.Remove(connectionId, out var connection)) return false;

            Forget(site, connection);
            DropIfEmpty(domain, site);
            return true;
        }
    }

    /// <summary>Records who a connection belongs to. False when it is not connected to this site.</summary>
    public bool SetUser(string domain, string connectionId, string? user)
    {
        if (!_sites.TryGetValue(domain, out var site)) return false;

        lock (site.Gate)
        {
            if (!site.Connections.TryGetValue(connectionId, out var connection)) return false;

            connection.User = user;
            return true;
        }
    }

    /// <summary>
    /// Closes one connection to <paramref name="domain"/> from outside the hub, and forgets it at
    /// once, so a function that closes one and then counts sees it gone. The page is told not to
    /// reconnect. False when it is not connected to this site, which leaves every other site's
    /// connections out of reach.
    /// </summary>
    public bool Abort(string domain, string connectionId)
    {
        if (!_sites.TryGetValue(domain, out var site)) return false;

        HubCallerContext context;
        lock (site.Gate)
        {
            if (!site.Connections.Remove(connectionId, out var connection)) return false;

            Forget(site, connection);
            DropIfEmpty(domain, site);
            context = connection.Context;
        }

        // Outside the lock: SignalR ends the connection on another thread, and its end runs the
        // hub's OnDisconnectedAsync, which finds nothing left to remove.
        context.Abort();
        return true;
    }

    /// <summary>
    /// Closes every connection to <paramref name="domain"/>, for a site that is being deleted,
    /// renamed or given a new passcode, and forgets them all at once. Returns how many there were.
    /// </summary>
    /// <remarks>
    /// Call it once the change has been made, not before. <see cref="Hubs.SiteHub"/> registers a new
    /// connection and only then checks that its site is still there, so a connection racing the
    /// change is either swept up here or sees the change for itself; called first, one could slip
    /// in between the two and outlive the site.
    /// </remarks>
    public int DisconnectSite(string domain)
    {
        if (!_sites.TryRemove(domain, out var site)) return 0;

        HubCallerContext[] contexts;
        lock (site.Gate)
        {
            site.Retired = true;
            contexts = [.. site.Connections.Values.Select(connection => connection.Context)];
            site.Connections.Clear();
            site.Groups.Clear();
            site.PerAddress.Clear();
        }

        foreach (var context in contexts) context.Abort();
        return contexts.Length;
    }

    // ---- groups -------------------------------------------------------------

    /// <summary>
    /// Holds the site's group guard until the result is disposed. A change to a group is two
    /// changes, one here and one in SignalR's own groups, with an await between them; whatever makes
    /// one (the hub's <c>Join</c> and <c>Leave</c>, and <see cref="SiteRealtime"/>'s group methods)
    /// makes both under this guard, so two changes to a site's groups never interleave and leave the
    /// registry and SignalR disagreeing about who is in a group. Held only across those two changes,
    /// never across a site's hook.
    /// </summary>
    public async Task<IDisposable> GuardGroupsAsync(string domain, CancellationToken ct = default)
    {
        var guard = _groupGuards.GetOrAdd(domain, static _ => new SemaphoreSlim(1, 1));
        await guard.WaitAsync(ct);
        return new Release(guard);
    }

    /// <summary>
    /// Puts a connection in one of its site's groups, within the limits. The caller adds it to the
    /// SignalR group when this says <see cref="JoinResult.Joined"/>, and only then, both under
    /// <see cref="GuardGroupsAsync"/>.
    /// </summary>
    public JoinResult Join(string domain, string connectionId, string group)
    {
        if (!_sites.TryGetValue(domain, out var site)) return JoinResult.NotConnected;

        lock (site.Gate)
        {
            if (!site.Connections.TryGetValue(connectionId, out var connection)) return JoinResult.NotConnected;
            if (connection.Groups.Contains(group)) return JoinResult.AlreadyMember;
            if (connection.Groups.Count >= MaxGroupsPerConnection) return JoinResult.ConnectionAtLimit;

            if (!site.Groups.TryGetValue(group, out var members))
            {
                if (site.Groups.Count >= MaxGroupsPerSite) return JoinResult.SiteAtLimit;
                site.Groups[group] = members = new HashSet<string>(StringComparer.Ordinal);
            }

            members.Add(connectionId);
            connection.Groups.Add(group);
            return JoinResult.Joined;
        }
    }

    /// <summary>Takes a connection out of a group, which goes with its last member. False when it was not in it.</summary>
    public bool Leave(string domain, string connectionId, string group)
    {
        if (!_sites.TryGetValue(domain, out var site)) return false;

        lock (site.Gate)
        {
            if (!site.Connections.TryGetValue(connectionId, out var connection) || !connection.Groups.Remove(group)) return false;

            RemoveMember(site, group, connectionId);
            return true;
        }
    }

    /// <summary>Empties a group. Returns the connections that were in it, for the caller to take out of the SignalR group.</summary>
    public IReadOnlyList<string> RemoveGroup(string domain, string group)
    {
        if (!_sites.TryGetValue(domain, out var site)) return [];

        lock (site.Gate)
        {
            if (!site.Groups.Remove(group, out var members)) return [];

            foreach (var id in members)
            {
                if (site.Connections.TryGetValue(id, out var connection)) connection.Groups.Remove(group);
            }

            return [.. members];
        }
    }

    // ---- reading ------------------------------------------------------------

    public int ConnectionCount(string domain) => Read(domain, 0, static site => site.Connections.Count);

    /// <summary>True when the site cannot take another connection, so a new one is refused before it is made.</summary>
    public bool IsFull(string domain) => ConnectionCount(domain) >= MaxConnectionsPerSite;

    /// <summary>
    /// True when <paramref name="address"/> (as <see cref="ClientAddress.Key"/> gives it) has as many
    /// connections to the site as it may, so its next is refused before it is made.
    /// </summary>
    public bool IsAddressFull(string domain, string address) =>
        MaxConnectionsPerAddress > 0 && Read(domain, false, site => IsAtAddressLimit(site, address));

    /// <summary>How many connections to the site come from <paramref name="address"/>.</summary>
    public int ConnectionsFrom(string domain, string address) =>
        Read(domain, 0, site => site.PerAddress.GetValueOrDefault(address));

    /// <summary>Every connection to the site, oldest first.</summary>
    public IReadOnlyList<RealtimeConnection> Connections(string domain) =>
        Read<IReadOnlyList<RealtimeConnection>>(domain, [], static site => Oldest(site.Connections.Values));

    /// <summary>True when the connection is connected to this site. A connection to any other site is not.</summary>
    public bool IsConnected(string domain, string connectionId) =>
        Read(domain, false, site => site.Connections.ContainsKey(connectionId));

    /// <summary>One connection to the site, or null when it is not connected to this site.</summary>
    public RealtimeConnection? Find(string domain, string connectionId) =>
        Read(domain, null, site => site.Connections.TryGetValue(connectionId, out var connection) ? connection.Snapshot() : null);

    /// <summary>The site's groups, in name order.</summary>
    public IReadOnlyList<string> Groups(string domain) =>
        Read<IReadOnlyList<string>>(domain, [], static site => [.. site.Groups.Keys.Order(StringComparer.Ordinal)]);

    /// <summary>The site's groups with how many connections each has, in name order.</summary>
    public IReadOnlyList<(string Name, int Members)> GroupSizes(string domain) =>
        Read<IReadOnlyList<(string, int)>>(domain, [], static site =>
            [.. site.Groups.OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => (group.Key, group.Value.Count))]);

    /// <summary>The connections in one of the site's groups, oldest first.</summary>
    public IReadOnlyList<RealtimeConnection> Members(string domain, string group) =>
        Read<IReadOnlyList<RealtimeConnection>>(domain, [], site =>
            site.Groups.TryGetValue(group, out var members)
                ? Oldest(members.Select(id => site.Connections[id]))
                : []);

    public int MemberCount(string domain, string group) =>
        Read(domain, 0, site => site.Groups.TryGetValue(group, out var members) ? members.Count : 0);

    /// <summary>The ids of every connection to the site that belongs to <paramref name="user"/>.</summary>
    public IReadOnlyList<string> ConnectionsOf(string domain, string user) =>
        Read<IReadOnlyList<string>>(domain, [], site =>
            [.. site.Connections.Where(pair => string.Equals(pair.Value.User, user, StringComparison.Ordinal)).Select(pair => pair.Key)]);

    // ---- helpers ------------------------------------------------------------

    private T Read<T>(string domain, T none, Func<SiteConnections, T> read)
    {
        if (!_sites.TryGetValue(domain, out var site)) return none;

        lock (site.Gate) return read(site);
    }

    private static IReadOnlyList<RealtimeConnection> Oldest(IEnumerable<Connection> connections) =>
        [.. connections.OrderBy(c => c.ConnectedUtc).ThenBy(c => c.Context.ConnectionId, StringComparer.Ordinal).Select(c => c.Snapshot())];

    private static void RemoveMember(SiteConnections site, string group, string connectionId)
    {
        if (site.Groups.TryGetValue(group, out var members) && members.Remove(connectionId) && members.Count == 0)
            site.Groups.Remove(group);
    }

    private bool IsAtAddressLimit(SiteConnections site, string address) =>
        MaxConnectionsPerAddress > 0 && site.PerAddress.GetValueOrDefault(address) >= MaxConnectionsPerAddress;

    /// <summary>Takes a connection that has just been removed out of its groups and its address's count. Called with the site's lock held.</summary>
    private static void Forget(SiteConnections site, Connection connection)
    {
        foreach (var group in connection.Groups) RemoveMember(site, group, connection.Context.ConnectionId);

        var count = site.PerAddress.GetValueOrDefault(connection.Address) - 1;
        if (count > 0) site.PerAddress[connection.Address] = count;
        else site.PerAddress.Remove(connection.Address);
    }

    /// <summary>Drops a site's entry once it has no connections. Called with its lock held.</summary>
    private void DropIfEmpty(string domain, SiteConnections site)
    {
        if (site.Connections.Count > 0) return;

        site.Retired = true;
        _sites.TryRemove(new KeyValuePair<string, SiteConnections>(domain, site));
    }

    /// <summary>One site's connections and groups, guarded by <see cref="Gate"/>.</summary>
    private sealed class SiteConnections
    {
        public readonly Lock Gate = new();
        public readonly Dictionary<string, Connection> Connections = new(StringComparer.Ordinal);
        public readonly Dictionary<string, HashSet<string>> Groups = new(StringComparer.Ordinal);

        /// <summary>How many connections each client address has to the site.</summary>
        public readonly Dictionary<string, int> PerAddress = new(StringComparer.Ordinal);

        /// <summary>No longer in the registry: whoever holds it must look the site up again.</summary>
        public bool Retired;
    }

    private sealed class Release(SemaphoreSlim guard) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) guard.Release();
        }
    }

    private sealed class Connection(HubCallerContext context, string? user, string address, DateTimeOffset connectedUtc)
    {
        public HubCallerContext Context { get; } = context;
        public string? User { get; set; } = user;
        public string Address { get; } = address;
        public DateTimeOffset ConnectedUtc { get; } = connectedUtc;
        public SortedSet<string> Groups { get; } = new(StringComparer.Ordinal);

        public RealtimeConnection Snapshot() => new(Context.ConnectionId, User, ConnectedUtc, [.. Groups]);
    }
}
