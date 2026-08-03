using System.IO.Compression;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;

namespace StaticSiteHost.Services;

public sealed record DeployResult(
    bool Ok,
    string? Error,
    string? Domain = null,
    ReleaseRecord? Release = null,
    IReadOnlyList<string>? Warnings = null)
{
    public static DeployResult Failed(string error) => new(false, error);
}

/// <summary>
/// Turns an uploaded zip into a new immutable release directory, then flips the
/// site's current-release pointer. Nothing is served from a half-written directory,
/// and the previous release stays on disk for rollback.
/// </summary>
public sealed class ZipDeploymentService
{
    private const int CopyBufferSize = 81_920;

    private static readonly string[] ExcludedNames =
        ["__MACOSX", ".DS_Store", "Thumbs.db", ".git", ".svn", ".hg", ".bzr", ".htpasswd"];

    private static readonly char[] RejectedPathChars = [':', '*', '?', '"', '<', '>', '|'];

    private readonly DataPaths _paths;
    private readonly SiteStore _sites;
    private readonly SiteContentServer _content;
    private readonly AuditLog _audit;
    private readonly SiteHostingOptions _options;
    private readonly ILogger<ZipDeploymentService> _logger;

    // Deploys are serialised: extraction is I/O bound and two uploads to the same domain
    // must not interleave releases. Uploads themselves stream in parallel.
    private readonly SemaphoreSlim _deployGate = new(1, 1);

    public ZipDeploymentService(
        DataPaths paths,
        SiteStore sites,
        SiteContentServer content,
        AuditLog audit,
        IOptions<SiteHostingOptions> options,
        ILogger<ZipDeploymentService> logger)
    {
        _paths = paths;
        _sites = sites;
        _content = content;
        _audit = audit;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DeployResult> DeployAsync(
        string? domainInput,
        Stream archive,
        string? archiveName,
        string actor,
        string source,
        CancellationToken ct = default)
    {
        var (domain, domainError) = SiteStore.NormalizeDomain(domainInput);
        if (domain is null) return DeployResult.Failed(domainError!);
        if (_options.IsManagementHost(domain))
            return DeployResult.Failed($"'{domain}' is reserved for the management interface.");

        string? scratchFile = null;
        await _deployGate.WaitAsync(ct);
        try
        {
            // ZipArchive needs to seek to read the central directory; raw request bodies do not seek.
            if (!archive.CanSeek)
            {
                scratchFile = Path.Combine(_paths.TempDir, $"upload-{Guid.NewGuid():n}.zip");
                await using (var scratch = File.Create(scratchFile))
                {
                    await archive.CopyToAsync(scratch, ct);
                    await scratch.FlushAsync(ct);
                }

                archive = File.OpenRead(scratchFile);
            }
            else
            {
                archive.Seek(0, SeekOrigin.Begin);
            }

            if (archive.Length == 0) return DeployResult.Failed("The uploaded file is empty.");
            if (archive.Length > _options.MaxUploadBytes)
                return DeployResult.Failed($"The archive is larger than the {Format.Bytes(_options.MaxUploadBytes)} upload limit.");

            return await ExtractAndPublishAsync(domain, archive, archiveName, actor, source, ct);
        }
        catch (InvalidDataException)
        {
            return DeployResult.Failed("That file is not a readable zip archive.");
        }
        catch (ExtractionLimitException ex)
        {
            return DeployResult.Failed(ex.Message);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Deploy to {Domain} failed while writing to disk", domain);
            return DeployResult.Failed("The archive could not be written to disk. Check the server logs.");
        }
        finally
        {
            _deployGate.Release();
            if (scratchFile is not null)
            {
                archive.Dispose();
                TryDelete(scratchFile);
            }
        }
    }

    private async Task<DeployResult> ExtractAndPublishAsync(
        string domain, Stream archive, string? archiveName, string actor, string source, CancellationToken ct)
    {
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);

        if (zip.Entries.Count > _options.MaxEntries)
            return DeployResult.Failed($"The archive holds more than the {_options.MaxEntries:N0} allowed entries.");

        var warnings = new List<string>();
        var planned = new List<(ZipArchiveEntry Entry, string[] Segments)>();
        var skipped = 0;

        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue; // directory entry

            var segments = SplitEntryPath(entry.FullName);
            if (segments is null)
            {
                skipped++;
                continue;
            }

            if (entry.Length > _options.MaxEntryBytes)
                return DeployResult.Failed(
                    $"'{entry.FullName}' is larger than the {Format.Bytes(_options.MaxEntryBytes)} per-file limit.");

            planned.Add((entry, segments));
        }

        if (planned.Count == 0)
            return DeployResult.Failed("The archive contains no usable files.");

        if (skipped > 0)
            warnings.Add($"{skipped} entr{(skipped == 1 ? "y was" : "ies were")} skipped (unsafe path or excluded name).");

        var declaredTotal = planned.Sum(p => p.Entry.Length);
        if (declaredTotal > _options.MaxExtractedBytes)
            return DeployResult.Failed(
                $"The archive expands to {Format.Bytes(declaredTotal)}, over the {Format.Bytes(_options.MaxExtractedBytes)} limit.");

        // "If the zip has a single folder in it, the files are inside that folder."
        var stripRoot = planned.All(p => p.Segments.Length >= 2)
                        && planned.Select(p => p.Segments[0]).Distinct(StringComparer.Ordinal).Count() == 1;

        var releaseId = NewReleaseId();
        var stagingDir = Path.Combine(_paths.StagingDir(domain), releaseId);
        Directory.CreateDirectory(stagingDir);

        long totalBytes = 0;
        var fileCount = 0;

        try
        {
            foreach (var (entry, segments) in planned)
            {
                ct.ThrowIfCancellationRequested();

                var relative = stripRoot ? segments[1..] : segments;
                if (relative.Length == 0) continue;

                var destination = Path.GetFullPath(Path.Combine([stagingDir, .. relative]));
                if (!PathHelpers.IsInside(stagingDir, destination))
                {
                    skipped++;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                await using var input = entry.Open();
                await using var output = File.Create(destination);
                var written = await CopyLimitedAsync(input, output, _options.MaxExtractedBytes - totalBytes, ct);

                totalBytes += written;
                fileCount++;
            }

            if (fileCount == 0) return DeployResult.Failed("The archive contains no usable files.");

            var hasRootIndex = File.Exists(Path.Combine(stagingDir, "index.html"));
            if (!hasRootIndex)
                warnings.Add("No index.html was found at the root of the site — requests will fall back to a 404.");

            var releaseDir = _paths.ReleaseDir(domain, releaseId);
            Directory.CreateDirectory(_paths.ReleasesDir(domain));
            Directory.Move(stagingDir, releaseDir);

            var release = new ReleaseRecord
            {
                Id = releaseId,
                DeployedBy = actor,
                Source = source,
                FileCount = fileCount,
                TotalBytes = totalBytes,
                ArchiveName = archiveName,
                StrippedRootFolder = stripRoot,
                HasRootIndex = hasRootIndex
            };

            var site = _sites.TryGet(domain) ?? new SiteRecord { Domain = domain, CreatedBy = actor };
            site.Releases.Insert(0, release);
            site.CurrentRelease = releaseId;
            site.LastDeployedBy = actor;

            PruneReleases(site);
            await _sites.SaveAsync(site);
            _content.Evict(domain);

            await _audit.WriteAsync("site.deploy", actor, new { domain, release = releaseId, fileCount, totalBytes, source });
            _logger.LogInformation("Deployed {Files} file(s) ({Bytes}) to {Domain} as release {Release}",
                fileCount, Format.Bytes(totalBytes), domain, releaseId);

            return new DeployResult(true, null, domain, release, warnings);
        }
        catch (Exception)
        {
            TryDeleteDirectory(stagingDir);
            throw;
        }
    }

    /// <summary>Points a site back at an earlier release that is still on disk.</summary>
    public async Task<(bool Ok, string? Error)> RollbackAsync(string domain, string releaseId, string actor)
    {
        // Same gate as a deploy: both mutate the site's release list and current pointer.
        await _deployGate.WaitAsync();
        try
        {
            var site = _sites.TryGet(domain);
            if (site is null) return (false, "No such site.");
            if (site.Releases.All(r => r.Id != releaseId)) return (false, "No such release.");
            if (!Directory.Exists(_paths.ReleaseDir(domain, releaseId)))
                return (false, "That release is no longer on disk.");

            site.CurrentRelease = releaseId;
            await _sites.SaveAsync(site);
            _content.Evict(domain);
            await _audit.WriteAsync("site.rollback", actor, new { domain, release = releaseId });
            return (true, null);
        }
        finally
        {
            _deployGate.Release();
        }
    }

    /// <summary>Removes leftover staging directories from a deploy interrupted by a restart.</summary>
    public void CleanupStaging()
    {
        if (!Directory.Exists(_paths.SitesDir)) return;

        foreach (var siteDir in Directory.EnumerateDirectories(_paths.SitesDir))
        {
            var staging = Path.Combine(siteDir, ".staging");
            if (Directory.Exists(staging)) TryDeleteDirectory(staging);
        }

        foreach (var file in Directory.EnumerateFiles(_paths.TempDir))
        {
            TryDelete(file);
        }
    }

    private void PruneReleases(SiteRecord site)
    {
        var keep = Math.Max(1, _options.ReleasesToKeep);
        if (site.Releases.Count <= keep) return;

        var retained = site.Releases
            .OrderByDescending(r => r.Id == site.CurrentRelease)
            .ThenByDescending(r => r.CreatedUtc)
            .Take(keep)
            .ToHashSet();

        foreach (var release in site.Releases.Where(r => !retained.Contains(r)).ToList())
        {
            TryDeleteDirectory(_paths.ReleaseDir(site.Domain, release.Id));
        }

        site.Releases.RemoveAll(r => !retained.Contains(r));
    }

    /// <summary>
    /// Normalises a zip entry name to safe path segments, or null when the entry
    /// must be skipped (traversal, absolute path, excluded or unportable name).
    /// </summary>
    private static string[]? SplitEntryPath(string fullName)
    {
        var segments = new List<string>();

        foreach (var raw in fullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Trim();
            if (segment is "." ) continue;
            if (segment is "..") return null;
            if (segment.Length == 0 || segment.Length > 255) return null;
            if (segment.IndexOfAny(RejectedPathChars) >= 0) return null;
            if (segment.Any(char.IsControl)) return null;
            if (ExcludedNames.Contains(segment, StringComparer.OrdinalIgnoreCase)) return null;
            if (segment.StartsWith(".env", StringComparison.OrdinalIgnoreCase) &&
                (segment.Length == 4 || segment[4] == '.')) return null;

            segments.Add(segment);
        }

        // Anything nested deeper than the resolver will look could never be served anyway.
        if (segments.Count > SitePathResolver.MaxPathSegments) return null;

        return segments.Count == 0 ? null : segments.ToArray();
    }

    private static async Task<long> CopyLimitedAsync(Stream source, Stream destination, long remaining, CancellationToken ct)
    {
        var buffer = new byte[CopyBufferSize];
        long written = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0) break;

            written += read;
            if (written > remaining)
                throw new ExtractionLimitException("The archive expands to more than the configured extraction limit.");

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return written;
    }

    private static string NewReleaseId() =>
        $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x1000, 0xffff):x4}";

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }
}

/// <summary>Raised when an archive's real contents exceed what its headers claimed.</summary>
public sealed class ExtractionLimitException(string message) : Exception(message);

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long value)
    {
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} B" : $"{size:0.#} {Units[unit]}";
    }
}
