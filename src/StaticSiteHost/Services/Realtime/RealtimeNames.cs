using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace StaticSiteHost.Services.Realtime;

/// <summary>
/// The rules for what travels over a site's realtime hub, in one place for the hub, the
/// <see cref="SiteRealtime"/> functions use and the API: which names are allowed, how large a
/// payload may be, and how a site's names become SignalR's.
///
/// SignalR has one set of group names for the whole server, so every group a site uses is kept
/// there under the site's domain: <c>site:{domain}:all</c> holds every connection to the site and
/// <c>site:{domain}:g:{group}</c> one of its groups. A domain cannot contain a colon, so no name one
/// site can choose is ever another site's. The registry, the API and the site's own code only ever
/// see the plain names.
/// </summary>
public static class RealtimeNames
{
    /// <summary>The longest event or group name.</summary>
    public const int MaxLength = 64;

    /// <summary>The largest payload, as JSON.</summary>
    public const int MaxPayloadBytes = 256 * 1024;

    /// <summary>The method a page's connection receives every event on: <c>(eventName, payload, group)</c>.</summary>
    public const string ClientMethod = "event";

    /// <summary>
    /// True for a name that matches <c>^[A-Za-z0-9_.:-]{1,64}$</c>. Checked by hand rather than with
    /// that expression, whose <c>$</c> would also let a name end in a line break.
    /// </summary>
    public static bool IsValid([NotNullWhen(true)] string? name) =>
        name is { Length: > 0 and <= MaxLength } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '-');

    /// <summary>Why <paramref name="name"/> cannot be an event name, or null when it can.</summary>
    public static string? EventError(string? name) => IsValid(name) ? null : Refusal(name, "an event");

    /// <summary>Why <paramref name="name"/> cannot be a group name, or null when it can.</summary>
    public static string? GroupError(string? name) => IsValid(name) ? null : Refusal(name, "a group");

    /// <summary>Why <paramref name="payload"/> cannot be sent, or null when it can.</summary>
    public static string? PayloadError(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Undefined) return null;

        var bytes = JsonMarshal.GetRawUtf8Value(payload).Length;
        return bytes <= MaxPayloadBytes
            ? null
            : $"The payload is {bytes / 1024.0:0.#} KB of JSON, and an event can carry at most {MaxPayloadBytes / 1024} KB. " +
              "Send an id, and let the page fetch the rest.";
    }

    /// <summary>The SignalR group every connection to <paramref name="domain"/> is in.</summary>
    public static string AllGroup(string domain) => $"site:{domain}:all";

    /// <summary>The SignalR group behind the site's group <paramref name="group"/>.</summary>
    public static string Group(string domain, string group) => $"site:{domain}:g:{group}";

    /// <param name="what">"an event" or "a group".</param>
    private static string Refusal(string? name, string what) =>
        (string.IsNullOrEmpty(name) ? $"{char.ToUpperInvariant(what[0])}{what[1..]} name is required" : $"{Quote(name)} cannot be {what} name") +
        $": names are 1 to {MaxLength} characters, each a letter, a digit, '_', '.', ':' or '-'.";

    /// <summary>A name as a message can show it: quoted, escaped, and cut short when it is long.</summary>
    private static string Quote(string name) =>
        JsonSerializer.Serialize(name.Length > MaxLength + 8 ? name[..MaxLength] + "…" : name);
}
