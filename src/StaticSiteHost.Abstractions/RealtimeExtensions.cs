using System.Text.Json;

namespace StaticSiteHost.Functions;

/// <summary>
/// Publishing an object of your own rather than a <see cref="JsonElement"/>: each method serialises
/// the payload with the options you pass, then sends it as <see cref="IRealtime"/>'s own method does.
/// </summary>
/// <remarks>
/// Pass options held in a static field of your own file, the same rule as <c>Results.Json</c>:
/// <code>
/// private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
///
/// await realtime.PublishAsync("post.published", new { post.Slug, post.Title }, Json);
/// </code>
/// Options cache what they learn about every type they serialise. Options owned by the server
/// would learn your types and keep every version of your code in memory after each redeploy;
/// options of your own go when your code does.
/// </remarks>
public static class RealtimeExtensions
{
    /// <summary>Serialises <paramref name="payload"/> and sends it to every connection to the site.</summary>
    /// <exception cref="ArgumentException">The name is not an event name, or the payload is over 256 KB.</exception>
    public static Task PublishAsync<T>(
        this IRealtime realtime, string eventName, T payload, JsonSerializerOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(realtime);
        return realtime.PublishAsync(eventName, Serialize(payload, options), ct);
    }

    /// <summary>Serialises <paramref name="payload"/> and sends it to the connections in <paramref name="group"/>.</summary>
    /// <exception cref="ArgumentException">A name is not valid, or the payload is over 256 KB.</exception>
    public static Task PublishToGroupAsync<T>(
        this IRealtime realtime, string group, string eventName, T payload, JsonSerializerOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(realtime);
        return realtime.PublishToGroupAsync(group, eventName, Serialize(payload, options), ct);
    }

    /// <summary>Serialises <paramref name="payload"/> and sends it to every connection of <paramref name="user"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="user"/> is empty, the name is not valid, or the payload is over 256 KB.</exception>
    public static Task PublishToUserAsync<T>(
        this IRealtime realtime, string user, string eventName, T payload, JsonSerializerOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(realtime);
        return realtime.PublishToUserAsync(user, eventName, Serialize(payload, options), ct);
    }

    /// <summary>Serialises <paramref name="payload"/> and sends it to one connection.</summary>
    /// <exception cref="ArgumentException"><paramref name="connectionId"/> is empty, the name is not valid, or the payload is over 256 KB.</exception>
    public static Task PublishToConnectionAsync<T>(
        this IRealtime realtime, string connectionId, string eventName, T payload, JsonSerializerOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(realtime);
        return realtime.PublishToConnectionAsync(connectionId, eventName, Serialize(payload, options), ct);
    }

    private static JsonElement Serialize<T>(T payload, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return JsonSerializer.SerializeToElement(payload, options);
    }
}
