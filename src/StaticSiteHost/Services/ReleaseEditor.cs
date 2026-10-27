using System.Text;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;

namespace StaticSiteHost.Services;

/// <summary>Why a change to a release's files was refused: a string key (<c>site.files.error.*</c>), the values it names, and any detail lines.</summary>
public sealed record FileEditError(string Key, object? Values = null, IReadOnlyList<string>? Details = null);

/// <summary>The error's text in English, for the API and the tools, whose answers stay English.</summary>
public static class FileEditMessages
{
    public static string English(StringCatalog catalog, FileEditError error)
    {
        var template = catalog.English.TryGetValue(error.Key, out var found) ? found : error.Key;
        var text = TextFormat.Text(template, TextFormat.Values([error.Values]));
        return error.Details is { Count: > 0 } details ? text + " " + string.Join(" ", details) : text;
    }
}

/// <summary>What a change to a release's files did, or why it was refused.</summary>
/// <param name="Live">The release changed is the live one, so visitors got the change at once.</param>
/// <param name="Paths">The paths written or moved to, as the site holds them.</param>
public sealed record FileEditResult(
    FileEditError? Error,
    ReleaseRecord? Release = null,
    bool Live = false,
    IReadOnlyList<string>? Paths = null,
    IReadOnlyList<string>? Deleted = null,
    IReadOnlyList<string>? Warnings = null)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Changes the files of one release where they are: writes, deletes and moves files in its folder,
/// and keeps its record (file count, size, rules, variable declarations) in step. Editing the live
/// release changes what visitors get at once; a copy made first (<see cref="DuplicateAsync"/>) is
/// the way back. A file is written beside the old one and then renamed over it, so a visitor never
/// gets half of one.
///
/// <c>_headers</c>, <c>_redirects</c> and <c>_variables.json</c> are the release's rules and
/// declarations, written and read as the files a deploy reads; one that does not read is refused.
///
/// Holds no lock of its own: <see cref="ZipDeploymentService"/> calls it under the deploy gate,
/// saves the site afterwards and tells the server's caches.
/// </summary>
public sealed class ReleaseEditor
{
    private static readonly string[] ConfigurationFiles = [HeaderRuleText.FileName, RedirectRuleText.FileName, VariableFileText.FileName];

    private readonly DataPaths _paths;
    private readonly SiteVariableService _variables;
    private readonly SiteHostingOptions _options;

    public ReleaseEditor(DataPaths paths, SiteVariableService variables, IOptions<SiteHostingOptions> options)
    {
        _paths = paths;
        _variables = variables;
        _options = options.Value;
    }

    public static bool IsConfiguration(string path) => ConfigurationFiles.Contains(path, StringComparer.Ordinal);

    /// <summary>
    /// Writes and deletes files of a release: the one named, or the live one. A site with no release
    /// gets its first, live, from the files written. A path to delete may be a folder.
    /// With <paramref name="createOnly"/>, a file is written only where there is none yet.
    /// </summary>
    public async Task<FileEditResult> EditAsync(
        SiteRecord site, string? releaseId, IReadOnlyList<ZipDeploymentService.FileChange> writes, IReadOnlyList<string> deletes,
        string actor, string source, bool isAdministrator, bool createOnly, CancellationToken ct)
    {
        if (writes.Count == 0 && deletes.Count == 0) return Refused("site.files.error.nothing");

        // Every path checked as a zip entry's would be, before anything is touched.
        var toWrite = new Dictionary<string, ZipDeploymentService.FileChange>(StringComparer.Ordinal);
        foreach (var change in writes)
        {
            if (CheckPath(change.Path) is { } pathError) return new FileEditResult(pathError);
            var path = ZipDeploymentService.NormalizeSitePath(change.Path)!;
            var limit = IsConfiguration(path) ? ZipDeploymentService.MaxRuleFileBytes : _options.MaxEntryBytes;
            if (change.Length > limit) return Refused("site.files.error.tooLarge", new { path, size = Format.Bytes(limit) });
            toWrite[path] = change;
        }

        var toDelete = new List<string>();
        foreach (var raw in deletes)
        {
            if (ZipDeploymentService.NormalizeSitePath(raw) is not { } path) return Refused("site.files.error.badPath", new { path = raw });
            if (!toDelete.Contains(path)) toDelete.Add(path);
        }

        var release = SiteFiles.ReleaseOf(site, releaseId);
        if (release is null && !IsLiveName(releaseId)) return Refused("site.files.error.noRelease", new { id = releaseId });
        if (release is null && toWrite.Count == 0) return Refused("site.files.error.notFound", new { path = toDelete[0] });

        var dir = release is null ? null : _paths.ReleaseDir(site.Domain, release.Id);
        if (dir is not null && !Directory.Exists(dir)) return Refused("site.files.error.noRelease", new { id = release!.Id });

        var warnings = new List<string>();

        // The rules and declarations: written, deleted, or left as the release has them.
        var headers = release?.Headers ?? [];
        var redirects = release?.Redirects ?? [];
        var variables = release?.Variables ?? [];
        var rulesChanged = false;
        var variablesChanged = false;
        foreach (var name in ConfigurationFiles)
        {
            var had = name == HeaderRuleText.FileName ? headers.Count > 0 : name == RedirectRuleText.FileName ? redirects.Count > 0 : variables.Count > 0;
            if (toDelete.Remove(name))
            {
                if (!had) warnings.Add(ServerText.Get("site.files.warning.nothingToDelete", new { path = name }));
                if (name == HeaderRuleText.FileName) headers = [];
                else if (name == RedirectRuleText.FileName) redirects = [];
                else (variables, variablesChanged) = ([], true);
                rulesChanged = true;
            }

            if (!toWrite.Remove(name, out var change)) continue;
            if (createOnly && had) return Refused("site.files.error.exists", new { path = name });

            var bytes = change.SourceFile is null ? change.Content : await File.ReadAllBytesAsync(change.SourceFile, ct);
            var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            IReadOnlyList<string> errors;
            var reads = name switch
            {
                _ when name == HeaderRuleText.FileName => HeaderRuleText.TryParse(text, out headers, out errors),
                _ when name == RedirectRuleText.FileName => RedirectRuleText.TryParse(text, out redirects, out errors),
                _ => VariableFileText.TryParse(text, out variables, out errors)
            };
            if (!reads) return new FileEditResult(new FileEditError("site.files.error.unreadable", new { path = name }, errors));
            if (name == VariableFileText.FileName) variablesChanged = true;
            rulesChanged = true;
        }

        // Where each file goes, and what it replaces, checked before anything is written.
        long addedBytes = 0;
        var addedFiles = 0;
        foreach (var (path, change) in toWrite)
        {
            if (dir is null) continue;
            var full = FullPath(dir, path);
            if (Directory.Exists(full)) return Refused("site.files.error.isFolder", new { path });
            if (ParentFile(dir, path) is { } parent) return Refused("site.files.error.parentIsFile", new { path, parent });
            if (File.Exists(full))
            {
                if (createOnly) return Refused("site.files.error.exists", new { path });
                addedBytes -= new FileInfo(full).Length;
            }
            else
            {
                addedFiles++;
            }

            addedBytes += change.Length;
        }

        if (dir is null) addedFiles = toWrite.Count;
        if (dir is null) addedBytes = toWrite.Values.Sum(c => c.Length);

        var removed = new List<(string Path, bool Folder)>();
        foreach (var path in toDelete)
        {
            var full = dir is null ? null : FullPath(dir, path);
            if (full is not null && File.Exists(full))
            {
                removed.Add((path, false));
                addedBytes -= new FileInfo(full).Length;
                addedFiles--;
            }
            else if (full is not null && Directory.Exists(full))
            {
                removed.Add((path, true));
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    addedBytes -= new FileInfo(file).Length;
                    addedFiles--;
                }
            }
            else if (toWrite.Count == 0 && toDelete.Count == 1 && !rulesChanged)
            {
                return Refused("site.files.error.notFound", new { path });
            }
            else
            {
                warnings.Add(ServerText.Get("site.files.warning.nothingToDelete", new { path }));
            }
        }

        var fileCount = (release?.FileCount ?? 0) + addedFiles;
        var totalBytes = (release?.TotalBytes ?? 0) + addedBytes;
        if (fileCount > _options.MaxEntries) return Refused("site.files.error.tooMany", new { count = _options.MaxEntries.ToString("N0") });
        if (totalBytes > _options.MaxExtractedBytes) return Refused("site.files.error.siteTooLarge", new { size = Format.Bytes(_options.MaxExtractedBytes) });

        // A site with no release yet gets its first, live.
        if (release is null)
        {
            release = new ReleaseRecord { Id = ZipDeploymentService.NewReleaseId(), DeployedBy = actor, Source = source, ArchiveName = "files written one by one" };
            dir = _paths.ReleaseDir(site.Domain, release.Id);
            Directory.CreateDirectory(dir);
            site.Releases.Insert(0, release);
            site.CurrentRelease = release.Id;
            site.LastDeployedBy = actor;
        }

        try
        {
            var staging = _paths.StagingDir(site.Domain);
            Directory.CreateDirectory(staging);
            foreach (var (path, change) in toWrite)
            {
                // Written beside it, then renamed over it: a visitor gets the old file or the new one, never half.
                var temp = Path.Combine(staging, $"edit-{Guid.NewGuid():n}");
                try
                {
                    if (change.SourceFile is { } sourceFile) File.Copy(sourceFile, temp);
                    else await File.WriteAllBytesAsync(temp, change.Content, ct);

                    var full = FullPath(dir!, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.Move(temp, full, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
            }

            foreach (var (path, folder) in removed)
            {
                var full = FullPath(dir!, path);
                if (folder) Directory.Delete(full, recursive: true);
                else File.Delete(full);
                RemoveEmptyFolders(dir!, Path.GetDirectoryName(full)!);
            }
        }
        catch (IOException)
        {
            // Some of it may be done: the record follows what is on disk.
            Recount(dir!, release);
            return Refused("site.files.error.disk");
        }

        release.FileCount = fileCount;
        release.TotalBytes = totalBytes;
        release.HasRootIndex = File.Exists(Path.Combine(dir!, "index.html"));
        if (rulesChanged)
        {
            if (variablesChanged)
            {
                var before = release.Variables;
                _variables.PrepareForRelease(variables, isAdministrator, warnings);
                SiteVariableService.KeepSecretDefaults(variables, before);
            }

            // Replaced whole, never edited in place: a request reading them never sees half a change.
            release.Headers = [.. headers];
            release.Redirects = [.. redirects];
            release.Variables = [.. variables];
        }

        Touch(release, actor);
        return new FileEditResult(null, release, site.CurrentRelease == release.Id,
            [.. toWrite.Keys, .. ConfigurationFiles.Where(n => writes.Any(w => ZipDeploymentService.NormalizeSitePath(w.Path) == n))],
            [.. removed.Select(r => r.Path)], warnings);
    }

    /// <summary>Moves a file, or a folder and everything in it, to another path in the same release.</summary>
    public async Task<FileEditResult> MoveAsync(
        SiteRecord site, string? releaseId, string from, string to, string actor, bool isAdministrator, CancellationToken ct)
    {
        if (ZipDeploymentService.NormalizeSitePath(from) is not { } source) return Refused("site.files.error.badPath", new { path = from });
        if (CheckPath(to) is { } pathError) return new FileEditResult(pathError);
        var target = ZipDeploymentService.NormalizeSitePath(to)!;

        if (SiteFiles.ReleaseOf(site, releaseId) is not { } release) return Refused("site.files.error.noRelease", new { id = releaseId });
        var dir = _paths.ReleaseDir(site.Domain, release.Id);
        if (target == source) return new FileEditResult(null, release, site.CurrentRelease == release.Id, [target], []);

        // A rules file is not a file on disk: moving to or from one is writing one and deleting the other.
        if (IsConfiguration(source) || IsConfiguration(target))
        {
            byte[]? bytes = IsConfiguration(source)
                ? ConfigurationText(release, source) is { } text ? Encoding.UTF8.GetBytes(text) : null
                : File.Exists(FullPath(dir, source)) ? await File.ReadAllBytesAsync(FullPath(dir, source), ct) : null;
            if (bytes is null) return Refused("site.files.error.notFound", new { path = source });
            if (TargetTaken(release, dir, target) is { } taken) return new FileEditResult(taken);

            var moved = await EditAsync(site, release.Id, [new(target, bytes)], [source], actor, "web", isAdministrator, createOnly: true, ct);
            return moved with { Paths = moved.Ok ? [target] : null };
        }

        var sourceFull = FullPath(dir, source);
        var isFolder = Directory.Exists(sourceFull);
        if (!isFolder && !File.Exists(sourceFull)) return Refused("site.files.error.notFound", new { path = source });
        if (isFolder && target.StartsWith(source + "/", StringComparison.Ordinal))
            return Refused("site.files.error.intoItself", new { from = source, to = target });
        if (TargetTaken(release, dir, target) is { } targetError) return new FileEditResult(targetError);
        if (isFolder && Directory.EnumerateFiles(sourceFull, "*", SearchOption.AllDirectories)
                .Select(f => target + "/" + Path.GetRelativePath(sourceFull, f).Replace(Path.DirectorySeparatorChar, '/'))
                .Any(p => p.Split('/')[0].Equals(ZipDeploymentService.FunctionsFolder, StringComparison.OrdinalIgnoreCase)))
            return Refused("site.files.error.functions");

        try
        {
            var targetFull = FullPath(dir, target);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFull)!);
            if (isFolder) Directory.Move(sourceFull, targetFull);
            else File.Move(sourceFull, targetFull);
            RemoveEmptyFolders(dir, Path.GetDirectoryName(sourceFull)!);
        }
        catch (IOException)
        {
            return Refused("site.files.error.disk");
        }

        release.HasRootIndex = File.Exists(Path.Combine(dir, "index.html"));
        Touch(release, actor);
        return new FileEditResult(null, release, site.CurrentRelease == release.Id, [target], [source]);
    }

    /// <summary>
    /// Copies a release, files, rules, declarations and functions, as a new release that is not
    /// live. The copy is the way back once the original is edited, or the place to work before
    /// making it live.
    /// </summary>
    public FileEditResult Duplicate(SiteRecord site, string releaseId, string actor)
    {
        if (site.Releases.FirstOrDefault(r => r.Id == releaseId) is not { } original) return Refused("site.files.error.noRelease", new { id = releaseId });
        var dir = _paths.ReleaseDir(site.Domain, original.Id);
        if (!Directory.Exists(dir)) return Refused("site.files.error.noRelease", new { id = releaseId });

        var copy = new ReleaseRecord
        {
            Id = ZipDeploymentService.NewReleaseId(),
            DeployedBy = actor,
            Source = ReleaseRecord.CopySource,
            CopiedFrom = original.Id,
            FileCount = original.FileCount,
            TotalBytes = original.TotalBytes,
            ArchiveName = original.ArchiveName,
            StrippedRootFolder = original.StrippedRootFolder,
            HasRootIndex = original.HasRootIndex,
            Headers = [.. original.Headers],
            Redirects = [.. original.Redirects],
            Variables = [.. original.Variables],
            Functions = original.Functions
        };

        // Copied into staging and moved into place whole, as a deploy's files are.
        var staging = Path.Combine(_paths.StagingDir(site.Domain), copy.Id);
        try
        {
            CopyFolder(dir, staging);
            Directory.CreateDirectory(_paths.ReleasesDir(site.Domain));
            Directory.Move(staging, _paths.ReleaseDir(site.Domain, copy.Id));
        }
        catch (IOException)
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { }
            return Refused("site.files.error.disk");
        }

        site.Releases.Insert(0, copy);
        return new FileEditResult(null, copy, false, [], []);
    }

    // ------------------------------------------------------------------ helpers

    private static FileEditResult Refused(string key, object? values = null) => new(new FileEditError(key, values));

    private static bool IsLiveName(string? releaseId) =>
        string.IsNullOrWhiteSpace(releaseId) || releaseId.Equals("current", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why no file can be written at <paramref name="path"/>, or null when one can.</summary>
    private static FileEditError? CheckPath(string? path)
    {
        if (ZipDeploymentService.NormalizeSitePath(path) is not { } normalized) return new FileEditError("site.files.error.badPath", new { path });
        if (normalized.Split('/')[0].Equals(ZipDeploymentService.FunctionsFolder, StringComparison.OrdinalIgnoreCase))
            return new FileEditError("site.files.error.functions");
        return null;
    }

    /// <summary>Why nothing can be moved to <paramref name="target"/>: something is there, or a file where a folder must be.</summary>
    private static FileEditError? TargetTaken(ReleaseRecord release, string dir, string target)
    {
        var full = FullPath(dir, target);
        if (File.Exists(full) || Directory.Exists(full) || (IsConfiguration(target) && ConfigurationText(release, target) is not null))
            return new FileEditError("site.files.error.exists", new { path = target });
        return ParentFile(dir, target) is { } parent ? new FileEditError("site.files.error.parentIsFile", new { path = target, parent }) : null;
    }

    /// <summary>A rules file as the release would write it back, or null when it has none of those rules.</summary>
    private static string? ConfigurationText(ReleaseRecord release, string name) => name switch
    {
        _ when name == HeaderRuleText.FileName => release.Headers.Count > 0 ? HeaderRuleText.Format(release.Headers) + "\n" : null,
        _ when name == RedirectRuleText.FileName => release.Redirects.Count > 0 ? RedirectRuleText.Format(release.Redirects) + "\n" : null,
        _ => release.Variables.Count > 0 ? VariableFileText.Format(release.Variables) : null
    };

    private static string FullPath(string dir, string path) => Path.Combine([dir, .. path.Split('/')]);

    /// <summary>The first folder on the way to <paramref name="path"/> that is a file instead, or null.</summary>
    private static string? ParentFile(string dir, string path)
    {
        for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
        {
            if (File.Exists(FullPath(dir, path[..slash]))) return path[..slash];
        }

        return null;
    }

    /// <summary>Removes folders left empty, from <paramref name="folder"/> up to the release's own, which stays.</summary>
    private static void RemoveEmptyFolders(string dir, string folder)
    {
        var root = Path.GetFullPath(dir);
        for (var current = Path.GetFullPath(folder); current.Length > root.Length && current.StartsWith(root, StringComparison.Ordinal); current = Path.GetDirectoryName(current)!)
        {
            if (Directory.EnumerateFileSystemEntries(current).Any()) return;
            Directory.Delete(current);
        }
    }

    private static void Recount(string dir, ReleaseRecord release)
    {
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
        release.FileCount = files.Count;
        release.TotalBytes = files.Sum(f => f.Length);
        release.HasRootIndex = File.Exists(Path.Combine(dir, "index.html"));
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var folder in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, folder)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    private static void Touch(ReleaseRecord release, string actor)
    {
        release.EditedUtc = DateTimeOffset.UtcNow;
        release.EditedBy = actor;
    }
}
