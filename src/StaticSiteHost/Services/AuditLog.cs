using System.Text;
using System.Text.Json;

namespace StaticSiteHost.Services;

/// <summary>Append-only JSON-lines log of anything that changes users, keys or sites.</summary>
public sealed class AuditLog
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<AuditLog> _logger;

    public AuditLog(DataPaths paths, ILogger<AuditLog> logger)
    {
        _path = paths.AuditFile;
        _logger = logger;
    }

    public async Task WriteAsync(string action, string? actor, object? details = null)
    {
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow,
            action,
            actor,
            details
        }, SerializerOptions);

        await _gate.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(_path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not append to the audit log at {Path}", _path);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The most entries <see cref="ReadAsync"/> returns at once.</summary>
    public const int MaxRead = 1000;

    /// <summary>
    /// The newest entries first, at most <paramref name="limit"/>. <paramref name="action"/> keeps
    /// entries whose action is it or starts with it followed by a dot (<c>site</c> keeps
    /// <c>site.deploy</c>); <paramref name="domain"/> keeps entries about that site, under its old
    /// or new name for a rename; <paramref name="since"/> keeps entries at or after that time.
    ///
    /// Reads the file backwards from its end and stops once it has enough, or has passed
    /// <paramref name="since"/>, so a long log costs no more than the entries asked for. It does not
    /// take the write lock: writes only ever add whole lines at the end, and a line still being
    /// written does not parse and is skipped.
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> ReadAsync(int limit, string? action = null, string? domain = null, DateTimeOffset? since = null)
    {
        limit = Math.Clamp(limit, 1, MaxRead);
        var found = new List<JsonElement>(Math.Min(limit, 64));
        if (!File.Exists(_path)) return found;

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.Asynchronous);

        await foreach (var raw in LinesFromEndAsync(stream))
        {
            var line = raw.TrimStart('\uFEFF').Trim();
            if (line.Length == 0) continue;

            JsonElement entry;
            try
            {
                entry = JsonSerializer.Deserialize<JsonElement>(line);
            }
            catch (JsonException)
            {
                continue;
            }

            // Entries are written in time order, so the first one older than since ends the search.
            if (since is not null && entry.TryGetProperty("ts", out var ts) && ts.TryGetDateTimeOffset(out var when) && when < since)
                break;

            if (!Matches(entry, action, domain, since)) continue;

            found.Add(entry);
            if (found.Count == limit) break;
        }

        return found;
    }

    /// <summary>
    /// The file's lines, last first, read in blocks from the end. A newline byte never occurs
    /// inside a multi-byte UTF-8 character, so splitting the bytes on it is safe.
    /// </summary>
    internal static async IAsyncEnumerable<string> LinesFromEndAsync(Stream stream)
    {
        const int BlockSize = 64 * 1024;
        var position = stream.Length;
        var tail = Array.Empty<byte>(); // the start of a line whose beginning is in an earlier block

        while (position > 0)
        {
            var size = (int)Math.Min(BlockSize, position);
            position -= size;

            var block = new byte[size + tail.Length];
            stream.Seek(position, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(block.AsMemory(0, size));
            tail.CopyTo(block, size);

            var end = block.Length;
            for (var i = block.Length - 1; i >= 0; i--)
            {
                if (block[i] != (byte)'\n') continue;
                if (end - i - 1 > 0) yield return Encoding.UTF8.GetString(block, i + 1, end - i - 1);
                end = i;
            }

            tail = block[..end];
        }

        if (tail.Length > 0) yield return Encoding.UTF8.GetString(tail);
    }

    private static bool Matches(JsonElement entry, string? action, string? domain, DateTimeOffset? since)
    {
        if (!string.IsNullOrEmpty(action))
        {
            var name = entry.TryGetProperty("action", out var value) ? value.GetString() ?? "" : "";
            if (!name.Equals(action, StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith(action + ".", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (since is not null &&
            (!entry.TryGetProperty("ts", out var ts) || !ts.TryGetDateTimeOffset(out var when) || when < since))
            return false;

        if (!string.IsNullOrEmpty(domain))
        {
            if (!entry.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Object) return false;

            var about = new[] { "domain", "from", "to" }
                .Any(key => details.TryGetProperty(key, out var named) && named.ValueKind == JsonValueKind.String &&
                            string.Equals(named.GetString(), domain, StringComparison.OrdinalIgnoreCase));
            if (!about) return false;
        }

        return true;
    }
}
