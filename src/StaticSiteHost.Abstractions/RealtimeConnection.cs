namespace StaticSiteHost.Functions;

/// <summary>One page connected to the site's realtime hub, as it was when the list was read.</summary>
/// <param name="Id">
/// The connection's id. The page reads its own as <c>site.realtime.connectionId</c>, and can send
/// it to a function so the function can reach that page alone. A page that reconnects gets a new one.
/// </param>
/// <param name="User">
/// Who the connection belongs to: the identity the site's connect hook gave it as it connected.
/// Null when the site has no such hook, or the hook gave none.
/// </param>
/// <param name="ConnectedUtc">When the connection was made.</param>
/// <param name="Groups">The groups the connection is in, in name order.</param>
public sealed record RealtimeConnection(string Id, string? User, DateTimeOffset ConnectedUtc, IReadOnlyList<string> Groups);
