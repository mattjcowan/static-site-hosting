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
}
