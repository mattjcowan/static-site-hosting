using System.IO.Compression;
using System.Text;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;

namespace StaticSiteHost.Services;

/// <summary>One file of a release, as listed.</summary>
/// <param name="Path">Where it is in the site, with forward slashes.</param>
/// <param name="Configuration">
/// <c>_headers</c>, <c>_redirects</c> or <c>_variables.json</c>: never served, and kept as the
/// release's rules and declarations rather than as a file. Listed and downloaded as the file they
/// came from, so a download deploys back as it was.
/// </param>
public sealed record SiteFileInfo(string Path, long Size, bool Configuration);

/// <summary>
/// Reading a release's files: listing them, reading one, and the whole release as a zip. The zip
/// holds what the release serves plus its configuration files, written back out, so deploying it
/// again gives the same site. Left out: a secret's default value (see
/// <see cref="VariableFileText.Format"/>) and the functions, which download from the Functions card.
/// </summary>
public sealed class SiteFiles
{
    /// <summary>The largest file <see cref="ReadAsync"/> returns, for the tools that show it to a model.</summary>
    public const int MaxReadBytes = 512 * 1024;

    private readonly DataPaths _paths;

    public SiteFiles(DataPaths paths) => _paths = paths;

    /// <summary>A site's release by id; "current", empty or null for the live one. Null when there is none.</summary>
    public static ReleaseRecord? ReleaseOf(SiteRecord site, string? releaseId) =>
        string.IsNullOrWhiteSpace(releaseId) || releaseId.Equals("current", StringComparison.OrdinalIgnoreCase)
            ? site.Current
            : site.Releases.FirstOrDefault(r => r.Id == releaseId);

    public IReadOnlyList<SiteFileInfo> List(SiteRecord site, ReleaseRecord release)
    {
        var dir = _paths.ReleaseDir(site.Domain, release.Id);
        var files = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => new SiteFileInfo(Path.GetRelativePath(dir, f).Replace(Path.DirectorySeparatorChar, '/'), new FileInfo(f).Length, false))
            : [];

        var configuration = Configuration(release)
            .Select(c => new SiteFileInfo(c.Name, Encoding.UTF8.GetByteCount(c.Text), true));

        return [.. configuration, .. files.OrderBy(f => f.Path, StringComparer.Ordinal)];
    }

    /// <summary>One file's bytes, or null when the release has no such file.</summary>
    public async Task<byte[]?> ReadAsync(SiteRecord site, ReleaseRecord release, string path, CancellationToken ct)
    {
        var relative = (path ?? "").Trim().TrimStart('/');
        if (Configuration(release).Where(c => c.Name == relative).Select(c => c.Text).FirstOrDefault() is { } text)
            return Encoding.UTF8.GetBytes(text);

        var dir = _paths.ReleaseDir(site.Domain, release.Id);
        var full = Path.GetFullPath(Path.Combine(dir, relative));
        if (!PathHelpers.IsInside(dir, full) || !File.Exists(full)) return null;

        return await File.ReadAllBytesAsync(full, ct);
    }

    /// <summary>
    /// One file to read as a stream, or null when the release has no such file. A configuration
    /// file is its text, as <see cref="ReadAsync"/> gives it; any other file is opened where it is.
    /// </summary>
    public Stream? Open(SiteRecord site, ReleaseRecord release, string path)
    {
        var relative = (path ?? "").Trim().TrimStart('/');
        if (Configuration(release).Where(c => c.Name == relative).Select(c => c.Text).FirstOrDefault() is { } text)
            return new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false);

        var dir = _paths.ReleaseDir(site.Domain, release.Id);
        var full = Path.GetFullPath(Path.Combine(dir, relative));
        if (!PathHelpers.IsInside(dir, full) || !File.Exists(full)) return null;

        return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    /// <summary>The release as a zip, written to <paramref name="output"/> as it is built.</summary>
    public async Task WriteZipAsync(SiteRecord site, ReleaseRecord release, Stream output, CancellationToken ct)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var (name, text) in Configuration(release))
        {
            await using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            await entry.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        }

        var dir = _paths.ReleaseDir(site.Domain, release.Id);
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetRelativePath(dir, file).Replace(Path.DirectorySeparatorChar, '/');
            await using var source = File.OpenRead(file);
            await using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            await source.CopyToAsync(entry, ct);
        }
    }

    /// <summary>The release's configuration files, as text, the ones it has.</summary>
    private static IEnumerable<(string Name, string Text)> Configuration(ReleaseRecord release)
    {
        if (release.Headers.Count > 0) yield return (HeaderRuleText.FileName, HeaderRuleText.Format(release.Headers) + "\n");
        if (release.Redirects.Count > 0) yield return (RedirectRuleText.FileName, RedirectRuleText.Format(release.Redirects) + "\n");
        if (release.Variables.Count > 0) yield return (VariableFileText.FileName, VariableFileText.Format(release.Variables));
    }

    /// <summary>
    /// The release as a zip in a temporary file, positioned at its start, which deletes itself when
    /// closed. A zip writes its directory synchronously as it closes, which a response body does not
    /// allow, so it is built here and then sent.
    /// </summary>
    public async Task<FileStream> CreateZipAsync(SiteRecord site, ReleaseRecord release, CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.TempDir);
        var file = new FileStream(Path.Combine(_paths.TempDir, $"download-{Guid.NewGuid():n}.zip"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await WriteZipAsync(site, release, file, ct);
            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    /// <summary>The file name a download gets: the domain and the release.</summary>
    public static string ZipName(SiteRecord site, ReleaseRecord release) => $"{site.Domain}-{release.Id}.zip";
}
