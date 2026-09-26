using System.IO.Compression;
using System.Text;
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
    IReadOnlyList<string>? Warnings = null,
    FunctionBundle? Functions = null,
    IReadOnlyList<FunctionDiagnostic>? Diagnostics = null)
{
    public static DeployResult Failed(string error, IReadOnlyList<FunctionDiagnostic>? diagnostics = null) =>
        new(false, error, Diagnostics: diagnostics);
}

/// <summary>
/// Turns an uploaded zip into a new immutable release directory, then flips the
/// site's current-release pointer. Nothing is served from a half-written directory,
/// and the previous release stays on disk for rollback.
/// </summary>
public sealed class ZipDeploymentService
{
    private const int CopyBufferSize = 81_920;

    /// <summary>
    /// A top-level folder of this name holds the site's functions. It is compiled, never
    /// served, and only an administrator may deploy it: it is code that runs in this server.
    /// </summary>
    public const string FunctionsFolder = "_functions";

    /// <summary>Cap on the archive's rule files. A rule set is a page of text at most.</summary>
    private const long MaxRuleFileBytes = 64 * 1024;

    private static readonly string[] ExcludedNames =
        ["__MACOSX", ".DS_Store", "Thumbs.db", ".git", ".svn", ".hg", ".bzr", ".htpasswd"];

    private static readonly char[] RejectedPathChars = [':', '*', '?', '"', '<', '>', '|'];

    private readonly DataPaths _paths;
    private readonly SiteStore _sites;
    private readonly SiteContentServer _content;
    private readonly AuditLog _audit;
    private readonly FunctionHost _functions;
    private readonly FunctionBundleBuilder _functionBuilder;
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
        FunctionHost functions,
        FunctionBundleBuilder functionBuilder,
        IOptions<SiteHostingOptions> options,
        ILogger<ZipDeploymentService> logger)
    {
        _functions = functions;
        _functionBuilder = functionBuilder;
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
        bool canDeployFunctions,
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

            return await ExtractAndPublishAsync(domain, archive, archiveName, actor, source, canDeployFunctions, ct);
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
        string domain, Stream archive, string? archiveName, string actor, string source,
        bool canDeployFunctions, CancellationToken ct)
    {
        using var zip = OpenArchive(archive);

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

        // Refused before a byte is extracted: a non-administrator's zip with functions in it
        // does not deploy at all, rather than deploying without them and looking like it worked.
        var functionEntries = planned
            .Select(p => (p.Entry, Relative: stripRoot ? p.Segments[1..] : p.Segments))
            .Where(p => p.Relative.Length >= 2 && p.Relative[0].Equals(FunctionsFolder, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (functionEntries.Count > 0 && !canDeployFunctions)
        {
            return DeployResult.Failed(
                $"This archive contains a {FunctionsFolder}/ folder, which only administrators can deploy because it runs " +
                "as code on the server. Remove the folder, or ask an administrator to deploy it.");
        }

        var functionFiles = new List<FunctionFile>();
        foreach (var (entry, relative) in functionEntries)
        {
            if (relative.Length != 2 || !FunctionBundleBuilder.IsFunctionFileName(relative[1]))
            {
                warnings.Add($"{FunctionsFolder}/{string.Join('/', relative[1..])} was ignored: only .cs and .linq files " +
                             $"directly inside {FunctionsFolder}/ are compiled.");
                continue;
            }

            if (entry.Length > FunctionSourceReader.MaxSourceBytes)
                return DeployResult.Failed($"{FunctionsFolder}/{relative[1]} is over the {Format.Bytes(FunctionSourceReader.MaxSourceBytes)} limit for one function file.");

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            functionFiles.Add(new FunctionFile(relative[1], await reader.ReadToEndAsync(ct)));
        }

        if (functionEntries.Count > 0 && functionFiles.Count == 0)
            warnings.Add($"{FunctionsFolder}/ holds no .cs or .linq files, so the site keeps the functions it had.");

        var releaseId = NewReleaseId();
        var stagingDir = Path.Combine(_paths.StagingDir(domain), releaseId);
        Directory.CreateDirectory(stagingDir);

        long totalBytes = 0;
        var fileCount = 0;
        var headerRules = new List<HeaderRule>();
        FunctionBundle? bundle = null;
        IReadOnlyList<FunctionDiagnostic>? functionDiagnostics = null;
        var redirectRules = new List<RedirectRule>();

        try
        {
            foreach (var (entry, segments) in planned)
            {
                ct.ThrowIfCancellationRequested();

                var relative = stripRoot ? segments[1..] : segments;
                if (relative.Length == 0) continue;

                // Read above and compiled below; never part of what is served.
                if (relative.Length >= 2 && relative[0].Equals(FunctionsFolder, StringComparison.OrdinalIgnoreCase)) continue;

                // The rule files are configuration, not content: they are read here and never
                // written into the release, so they cannot be fetched from the site.
                if (relative.Length == 1)
                {
                    if (relative[0].Equals(HeaderRuleText.FileName, StringComparison.OrdinalIgnoreCase))
                    {
                        var text = await ReadRuleFileAsync(entry, HeaderRuleText.FileName, warnings, ct);
                        if (text is not null)
                            headerRules = ParseRules<HeaderRule>(text, HeaderRuleText.FileName, warnings, HeaderRuleText.TryParse);
                        continue;
                    }

                    if (relative[0].Equals(RedirectRuleText.FileName, StringComparison.OrdinalIgnoreCase))
                    {
                        var text = await ReadRuleFileAsync(entry, RedirectRuleText.FileName, warnings, ct);
                        if (text is not null)
                            redirectRules = ParseRules<RedirectRule>(text, RedirectRuleText.FileName, warnings, RedirectRuleText.TryParse);
                        continue;
                    }
                }

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

            // Built after the content is staged and before anything goes live, so a function
            // that does not compile fails the whole deploy: new content never goes out beside
            // old code it may have been written against.
            if (functionFiles.Count > 0)
            {
                var built = await _functionBuilder.BuildAsync(_paths.FunctionsDir(domain), functionFiles, actor, source, ct);
                if (!built.Ok)
                {
                    TryDeleteDirectory(stagingDir);
                    return DeployResult.Failed($"Nothing was deployed: the functions did not build. {built.Error}", built.Diagnostics);
                }

                bundle = built.Bundle;
                functionDiagnostics = built.Diagnostics;
            }

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
                HasRootIndex = hasRootIndex,
                Headers = headerRules,
                Redirects = redirectRules
            };

            var site = _sites.TryGet(domain) ?? new SiteRecord { Domain = domain, CreatedBy = actor };

            // Functions that came with the zip are the release's own. Otherwise new content keeps
            // the endpoints the site already had: a bundle is referenced, not copied, so that
            // costs nothing and needs no rebuild.
            if (bundle is not null) site.FunctionBundles.Add(bundle);
            release.Functions = bundle?.Id ?? site.Current?.Functions;

            site.Releases.Insert(0, release);
            site.CurrentRelease = releaseId;
            site.LastDeployedBy = actor;

            PruneReleases(site);
            await _sites.SaveAsync(site);
            _content.Evict(domain);

            await _audit.WriteAsync("site.deploy", actor, new { domain, release = releaseId, fileCount, totalBytes, source });
            _logger.LogInformation("Deployed {Files} file(s) ({Bytes}) to {Domain} as release {Release}",
                fileCount, Format.Bytes(totalBytes), domain, releaseId);

            return new DeployResult(true, null, domain, release, warnings, bundle, functionDiagnostics);
        }
        catch (Exception)
        {
            TryDeleteDirectory(stagingDir);
            if (bundle is not null) TryDeleteDirectory(_paths.FunctionBundleDir(domain, bundle.Id));
            throw;
        }
    }

    private delegate bool RuleParser<T>(string? text, out List<T> rules, out IReadOnlyList<string> errors);

    /// <summary>Reads one of the archive's rule files, or null when it is too large to be one.</summary>
    private static async Task<string?> ReadRuleFileAsync(
        ZipArchiveEntry entry, string name, List<string> warnings, CancellationToken ct)
    {
        await using var stream = entry.Open();
        using var buffer = new MemoryStream();

        try
        {
            await CopyLimitedAsync(stream, buffer, MaxRuleFileBytes, ct);
        }
        catch (ExtractionLimitException)
        {
            warnings.Add($"'{name}' is larger than {Format.Bytes(MaxRuleFileBytes)} and was ignored.");
            return null;
        }

        // TrimStart drops a byte-order mark, which would otherwise make the first line of
        // the file look like it does not start with '/'.
        return Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
    }

    /// <summary>
    /// A rule file that will not parse is a warning rather than a failed deploy — the site
    /// itself is fine, and refusing to publish it would be a surprising way to report a typo
    /// in a caching rule.
    /// </summary>
    private static List<T> ParseRules<T>(string text, string name, List<string> warnings, RuleParser<T> parse)
    {
        if (parse(text, out var rules, out var errors)) return rules;

        warnings.Add($"'{name}' was ignored — {string.Join(" ", errors.Take(3))}");
        return [];
    }

    /// <summary>
    /// Opens the archive, treating a stream that cannot satisfy that seek as a malformed zip.
    ///
    /// ZipArchive locates the central directory by seeking backwards from the end, and a file
    /// that is not a zip — or is shorter than the record it is looking for — asks for a
    /// position before the start. Streams disagree on what that is: a multipart section
    /// raises ArgumentOutOfRangeException and a FileStream an IOException, neither of which
    /// means what it says here. Both are the same user error: a bad upload.
    /// </summary>
    private static ZipArchive OpenArchive(Stream archive)
    {
        try
        {
            return new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IOException)
        {
            throw new InvalidDataException("The archive's central directory could not be read.", ex);
        }
    }

    /// <summary>
    /// Points a site back at an earlier release that is still on disk. By default the release
    /// comes back with the functions it last ran; <paramref name="keepCurrentFunctions"/> makes
    /// it run whatever is live now instead, and records that on the release.
    /// </summary>
    public async Task<(bool Ok, string? Error)> RollbackAsync(
        string domain, string releaseId, string actor, bool keepCurrentFunctions = false)
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

            if (keepCurrentFunctions)
            {
                site.Releases.First(r => r.Id == releaseId).Functions = site.Current?.Functions;
                PruneFunctionBundles(site);
            }

            site.CurrentRelease = releaseId;
            await _sites.SaveAsync(site);
            _content.Evict(domain);
            await _audit.WriteAsync("site.rollback", actor,
                new { domain, release = releaseId, functions = keepCurrentFunctions ? "kept" : "restored" });
            return (true, null);
        }
        finally
        {
            _deployGate.Release();
        }
    }

    /// <summary>
    /// Moves a site to a new domain. The old domain stops answering straight away; nothing
    /// redirects from it.
    /// </summary>
    public async Task<(bool Ok, string? Error, string? Domain)> RenameAsync(string domain, string? newDomainInput, string actor)
    {
        var (to, error) = SiteStore.NormalizeDomain(newDomainInput);
        if (to is null) return (false, error, null);
        if (_options.IsManagementHost(to))
            return (false, $"'{to}' is reserved for the management interface.", null);
        if (to == domain) return (false, $"The site is already published at '{to}'.", null);

        // Same gate as a deploy: a release extracting into the old directory mid-move would
        // land in a folder that no longer exists.
        await _deployGate.WaitAsync();
        try
        {
            var (ok, failure) = await _sites.RenameAsync(domain, to);
            if (!ok) return (false, failure, null);

            _content.Evict(domain);
            _content.Evict(to);

            // The loaded functions resolve their dependencies from the old path, which is gone.
            _functions.Evict(domain);

            await _audit.WriteAsync("site.rename", actor, new { from = domain, to });
            _logger.LogInformation("Moved site {From} to {To}", domain, to);

            return (true, null, to);
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
        if (site.Releases.Count <= keep)
        {
            PruneFunctionBundles(site);
            return;
        }

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
        PruneFunctionBundles(site);
    }

    /// <summary>Drops function bundles that no retained release runs any more.</summary>
    private void PruneFunctionBundles(SiteRecord site)
    {
        var used = site.Releases.Select(r => r.Functions).OfType<string>().ToHashSet(StringComparer.Ordinal);

        foreach (var bundle in site.FunctionBundles.Where(b => !used.Contains(b.Id)).ToList())
        {
            TryDeleteDirectory(_paths.FunctionBundleDir(site.Domain, bundle.Id));
            site.FunctionBundles.Remove(bundle);
        }
    }

    /// <summary>
    /// Makes <paramref name="bundle"/> the functions of the live release, or removes them when
    /// null. Under the deploy gate because it edits the same release list a deploy does.
    /// </summary>
    public async Task<(bool Ok, string? Error)> SetFunctionsAsync(string domain, FunctionBundle? bundle)
    {
        await _deployGate.WaitAsync();
        try
        {
            var site = _sites.TryGet(domain);
            if (site is null) return (false, $"No site is published at '{domain}'.");
            if (site.Current is not { } current) return (false, "This site has no live release to attach functions to.");
            if (bundle is null && current.Functions is null) return (false, "This site has no functions to remove.");

            if (bundle is not null) site.FunctionBundles.Add(bundle);
            current.Functions = bundle?.Id;

            PruneFunctionBundles(site);
            await _sites.SaveAsync(site);
            return (true, null);
        }
        finally
        {
            _deployGate.Release();
        }
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
