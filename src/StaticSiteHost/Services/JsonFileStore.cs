using System.Text.Json;

namespace StaticSiteHost.Services;

/// <summary>
/// A JSON document on disk, guarded by an async lock and written atomically
/// (temp file + rename). Assumes a single process owns the file — which is the
/// deployment model for this app.
/// </summary>
public sealed class JsonFileStore<T> where T : new()
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private T? _cache;

    public JsonFileStore(string path) => _path = path;

    public async Task<T> ReadAsync(CancellationToken ct = default)
    {
        if (_cache is not null) return _cache;

        await _gate.WaitAsync(ct);
        try
        {
            return _cache ??= await LoadAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> under the file lock and persists the document
    /// afterwards unless the action returns false via <paramref name="shouldSave"/>.
    /// </summary>
    public async Task<TResult> UpdateAsync<TResult>(
        Func<T, Task<(TResult Result, bool Save)>> action,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var data = _cache ??= await LoadAsync(ct);
            var (result, save) = await action(data);
            if (save) await SaveAsync(data, ct);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<TResult> UpdateAsync<TResult>(Func<T, (TResult Result, bool Save)> action, CancellationToken ct = default) =>
        UpdateAsync(data => Task.FromResult(action(data)), ct);

    private async Task<T> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new T();

        await using var stream = File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length == 0) return new T();

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, ct) ?? new T();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"'{_path}' is not valid JSON and cannot be loaded.", ex);
        }
    }

    private async Task SaveAsync(T data, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";

        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, data, SerializerOptions, ct);
            await stream.FlushAsync(ct);
        }

        File.Move(temp, _path, overwrite: true);
    }
}
