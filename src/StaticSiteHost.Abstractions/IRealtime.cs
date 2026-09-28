using System.Text.Json;

namespace StaticSiteHost.Functions;

/// <summary>
/// The realtime side of one site, as its functions use it: the pages open on the site that are
/// connected to <c>/_host/realtime</c> (through <c>site.realtime</c> in <c>/_host/site.js</c>), the
/// groups they are in, and events sent to them.
/// </summary>
/// <remarks>
/// <para>
/// Browsers can only join groups, leave them and listen. They cannot publish, and cannot put
/// another connection in a group, so everything that changes who hears what happens here. A page
/// receives an event as <c>(payload, { event, group })</c>, where <c>group</c> is the group it was
/// sent to, or null.
/// </para>
/// <para>
/// Event names and group names are 1 to 64 characters, each a letter, a digit, <c>_</c>,
/// <c>.</c>, <c>:</c> or <c>-</c> (<c>^[A-Za-z0-9_.:-]{1,64}$</c>), and are case-sensitive. A name
/// that is not one throws <see cref="ArgumentException"/>, as does a payload whose JSON is larger
/// than 256 KB: send an id, and let the page fetch the rest.
/// </para>
/// <para>
/// Everything is private to the site. A group called <c>editors</c> here is not the group of that
/// name on any other site, and a connection id from another site is never reachable: publishing
/// to one, or adding it to a group, does nothing.
/// </para>
/// <para>
/// Connections come and go at any moment, so a connection id that is no longer connected is
/// ignored rather than an error, wherever one is taken. The lists are snapshots, taken when they
/// are read.
/// </para>
/// <para>
/// Only Static Site Host implements this interface, and <see cref="Testing.FakeRealtime"/> stands
/// in for it outside the server. Later versions may add members, so do not implement it in your
/// own code.
/// </para>
/// </remarks>
public interface IRealtime
{
    /// <summary>How many pages are connected to the site right now.</summary>
    int ConnectionCount { get; }

    /// <summary>Every connection to the site, oldest first.</summary>
    IReadOnlyList<RealtimeConnection> Connections { get; }

    /// <summary>The site's groups that have at least one member, in name order.</summary>
    IReadOnlyList<string> Groups { get; }

    /// <summary>The connections in <paramref name="group"/>, oldest first; empty when it has none.</summary>
    /// <exception cref="ArgumentException"><paramref name="group"/> is not a group name.</exception>
    IReadOnlyList<RealtimeConnection> Members(string group);

    /// <summary>Sends an event to every connection to the site.</summary>
    /// <param name="eventName">What the page listens for with <c>site.realtime.on(eventName, …)</c>.</param>
    /// <param name="payload">What the page's handler receives, or null for nothing.</param>
    /// <param name="ct">Stops the send.</param>
    /// <exception cref="ArgumentException">The name is not an event name, or the payload is over 256 KB.</exception>
    Task PublishAsync(string eventName, JsonElement? payload = null, CancellationToken ct = default);

    /// <summary>Sends an event to the connections in <paramref name="group"/>. A group with no members is not an error.</summary>
    /// <exception cref="ArgumentException">A name is not valid, or the payload is over 256 KB.</exception>
    Task PublishToGroupAsync(string group, string eventName, JsonElement? payload = null, CancellationToken ct = default);

    /// <summary>
    /// Sends an event to every connection of <paramref name="user"/>: one person, in every tab they
    /// have open. A connection's user is whatever identity the site gave it as it connected (see
    /// <see cref="RealtimeConnection.User"/>); a user with no connections is not an error.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="user"/> is empty, the name is not valid, or the payload is over 256 KB.</exception>
    Task PublishToUserAsync(string user, string eventName, JsonElement? payload = null, CancellationToken ct = default);

    /// <summary>Sends an event to one connection. One that is not connected to this site is ignored.</summary>
    /// <exception cref="ArgumentException"><paramref name="connectionId"/> is empty, the name is not valid, or the payload is over 256 KB.</exception>
    Task PublishToConnectionAsync(string connectionId, string eventName, JsonElement? payload = null, CancellationToken ct = default);

    /// <summary>
    /// Puts a connection in a group, which is created by its first member. Adding one that is
    /// already a member, or one that is not connected to this site, does nothing.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="connectionId"/> is empty, or <paramref name="group"/> is not a group name.</exception>
    /// <exception cref="InvalidOperationException">
    /// The connection is in as many groups as one may join, or the site has as many groups as it may
    /// have. The server sets both limits; the message says which was reached.
    /// </exception>
    Task AddToGroupAsync(string connectionId, string group, CancellationToken ct = default);

    /// <summary>Takes a connection out of a group. One that is not a member is ignored.</summary>
    /// <exception cref="ArgumentException"><paramref name="connectionId"/> is empty, or <paramref name="group"/> is not a group name.</exception>
    Task RemoveFromGroupAsync(string connectionId, string group, CancellationToken ct = default);

    /// <summary>Takes every connection out of <paramref name="group"/>, so it no longer exists.</summary>
    /// <exception cref="ArgumentException"><paramref name="group"/> is not a group name.</exception>
    Task RemoveGroupAsync(string group, CancellationToken ct = default);

    /// <summary>
    /// Closes a connection. The page sees its state become <c>disconnected</c>, and it does not
    /// reconnect on its own. One that is not connected to this site is ignored.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="connectionId"/> is empty.</exception>
    Task DisconnectAsync(string connectionId, CancellationToken ct = default);
}
