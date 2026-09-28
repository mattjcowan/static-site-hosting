using System.Runtime.InteropServices;
using System.Text.Json;

namespace StaticSiteHost.Functions.Testing;

/// <summary>
/// An <see cref="IRealtime"/> held in memory, for running a handler outside the server. Make the
/// connections a test needs, run the handler, then look at what it sent:
/// <code>
/// var realtime = new FakeRealtime();
/// var page = realtime.Connect("page-1", user: "ada");
/// await realtime.AddToGroupAsync(page.Id, "editors");
///
/// await Publish(realtime);   // the handler under test
///
/// var sent = realtime.Published.Single();
/// // sent.Target is "group:editors", sent.EventName "post.published", sent.Payload the JSON
/// </code>
/// </summary>
/// <remarks>
/// Names and payloads are checked as the server checks them, and throw the same exceptions. Every
/// publish is recorded in <see cref="Published"/> whether or not any connection would receive it,
/// so a test can check what a handler sent without making connections first. It has none of the
/// server's limits on how many groups there may be.
/// </remarks>
public sealed class FakeRealtime : IRealtime
{
    private const int MaxNameLength = 64;
    private const int MaxPayloadBytes = 256 * 1024;

    private readonly object _gate = new();

    /// <summary>Connections in the order they were made, each with its groups.</summary>
    private readonly List<(string Id, string? User, DateTimeOffset ConnectedUtc, SortedSet<string> Groups)> _connections = [];

    private readonly List<PublishedEvent> _published = [];

    /// <summary>
    /// Makes a connection, as a page opening would.
    /// </summary>
    /// <param name="id">The connection's id, which must not already be connected.</param>
    /// <param name="user">The identity the site's connect hook would have given it, or null for none.</param>
    /// <returns>The connection as it now is.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or already connected.</exception>
    public RealtimeConnection Connect(string id, string? user = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        lock (_gate)
        {
            if (IndexOf(id) >= 0) throw new ArgumentException($"A connection called \"{id}\" is already connected.", nameof(id));

            var connection = (id, user, DateTimeOffset.UtcNow, new SortedSet<string>(StringComparer.Ordinal));
            _connections.Add(connection);
            return Snapshot(connection);
        }
    }

    /// <summary>Ends a connection, as a page closing would. Its groups lose it.</summary>
    /// <returns>False when no connection has that id.</returns>
    public bool Disconnect(string id)
    {
        lock (_gate)
        {
            var index = IndexOf(id);
            if (index < 0) return false;

            _connections.RemoveAt(index);
            return true;
        }
    }

    /// <summary>
    /// Everything published so far, oldest first. <see cref="PublishedEvent.Target"/> says where it
    /// went: <c>all</c>, <c>group:{name}</c>, <c>user:{user}</c> or <c>connection:{id}</c>.
    /// </summary>
    public IReadOnlyList<PublishedEvent> Published
    {
        get
        {
            lock (_gate) return [.. _published];
        }
    }

    /// <inheritdoc />
    public int ConnectionCount
    {
        get
        {
            lock (_gate) return _connections.Count;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RealtimeConnection> Connections
    {
        get
        {
            lock (_gate) return [.. _connections.Select(Snapshot)];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Groups
    {
        get
        {
            lock (_gate) return [.. _connections.SelectMany(c => c.Groups).Distinct().Order(StringComparer.Ordinal)];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RealtimeConnection> Members(string group)
    {
        RequireName(group, "group", nameof(group));

        lock (_gate) return [.. _connections.Where(c => c.Groups.Contains(group)).Select(Snapshot)];
    }

    /// <inheritdoc />
    public Task PublishAsync(string eventName, JsonElement? payload = null, CancellationToken ct = default) =>
        Record("all", eventName, payload, ct);

    /// <inheritdoc />
    public Task PublishToGroupAsync(string group, string eventName, JsonElement? payload = null, CancellationToken ct = default)
    {
        RequireName(group, "group", nameof(group));
        return Record($"group:{group}", eventName, payload, ct);
    }

    /// <inheritdoc />
    public Task PublishToUserAsync(string user, string eventName, JsonElement? payload = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(user);
        return Record($"user:{user}", eventName, payload, ct);
    }

    /// <inheritdoc />
    public Task PublishToConnectionAsync(string connectionId, string eventName, JsonElement? payload = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        return Record($"connection:{connectionId}", eventName, payload, ct);
    }

    /// <inheritdoc />
    public Task AddToGroupAsync(string connectionId, string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        RequireName(group, "group", nameof(group));
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var index = IndexOf(connectionId);
            if (index >= 0) _connections[index].Groups.Add(group);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveFromGroupAsync(string connectionId, string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        RequireName(group, "group", nameof(group));
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var index = IndexOf(connectionId);
            if (index >= 0) _connections[index].Groups.Remove(group);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveGroupAsync(string group, CancellationToken ct = default)
    {
        RequireName(group, "group", nameof(group));
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            foreach (var connection in _connections) connection.Groups.Remove(group);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>The same as <see cref="Disconnect"/>.</remarks>
    public Task DisconnectAsync(string connectionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        ct.ThrowIfCancellationRequested();

        Disconnect(connectionId);
        return Task.CompletedTask;
    }

    private Task Record(string target, string eventName, JsonElement? payload, CancellationToken ct)
    {
        RequireName(eventName, "event", nameof(eventName));
        ct.ThrowIfCancellationRequested();

        // Undefined is what a default JsonElement holds: no payload at all, the same as null.
        JsonElement? copy = null;
        if (payload is { ValueKind: not JsonValueKind.Undefined } value)
        {
            var bytes = JsonMarshal.GetRawUtf8Value(value).Length;
            if (bytes > MaxPayloadBytes)
            {
                throw new ArgumentException(
                    $"The payload is {bytes / 1024.0:0.#} KB of JSON, and an event can carry at most {MaxPayloadBytes / 1024} KB. " +
                    "Send an id, and let the page fetch the rest.", nameof(payload));
            }

            // A copy, so a payload read from a JsonDocument the handler then disposed can still be looked at.
            copy = value.Clone();
        }

        lock (_gate) _published.Add(new PublishedEvent(target, eventName, copy));
        return Task.CompletedTask;
    }

    /// <summary>The server's rule for event and group names: <c>^[A-Za-z0-9_.:-]{1,64}$</c>.</summary>
    private static void RequireName(string? name, string kind, string parameter)
    {
        if (name is { Length: > 0 and <= MaxNameLength } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '-'))
            return;

        var what = kind == "event" ? "an event" : "a group";
        var problem = string.IsNullOrEmpty(name)
            ? $"{char.ToUpperInvariant(what[0])}{what[1..]} name is required"
            : $"{JsonSerializer.Serialize(name)} cannot be {what} name";

        throw new ArgumentException(
            $"{problem}: names are 1 to {MaxNameLength} characters, each a letter, a digit, '_', '.', ':' or '-'.", parameter);
    }

    private int IndexOf(string id) => _connections.FindIndex(c => c.Id == id);

    private static RealtimeConnection Snapshot((string Id, string? User, DateTimeOffset ConnectedUtc, SortedSet<string> Groups) connection) =>
        new(connection.Id, connection.User, connection.ConnectedUtc, [.. connection.Groups]);
}

/// <summary>One event a handler published through <see cref="FakeRealtime"/>.</summary>
/// <param name="Target"><c>all</c>, <c>group:{name}</c>, <c>user:{user}</c> or <c>connection:{id}</c>.</param>
/// <param name="EventName">The name the page listens for.</param>
/// <param name="Payload">What the page's handler would receive, or null for nothing.</param>
public sealed record PublishedEvent(string Target, string EventName, JsonElement? Payload);
