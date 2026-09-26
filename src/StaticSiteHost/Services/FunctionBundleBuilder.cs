using System.Text;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services;

/// <param name="Kept">Files already live that a merge upload carried over unchanged.</param>
public sealed record FunctionDeployResult(
    bool Ok,
    string? Error,
    FunctionBundle? Bundle = null,
    IReadOnlyList<FunctionDiagnostic>? Diagnostics = null,
    IReadOnlyList<string>? Kept = null)
{
    public static FunctionDeployResult Failed(string error, IReadOnlyList<FunctionDiagnostic>? diagnostics = null) =>
        new(false, error, null, diagnostics ?? []);
}

/// <summary>
/// Turns uploaded function files into a bundle on disk: read each file, generate one project,
/// publish it, and load the result once to find its routes. The one pipeline behind every way
/// functions arrive — an upload, a zip's _functions/ folder, the editor's Check and Deploy — so
/// they all accept and refuse exactly the same things.
///
/// It only builds. Making a bundle live is the caller's business; a failed build leaves
/// nothing behind.
/// </summary>
public sealed class FunctionBundleBuilder
{
    public const int MaxFiles = 50;

    /// <summary>All files together. Each is also held to <see cref="FunctionSourceReader.MaxSourceBytes"/>.</summary>
    public const long MaxTotalBytes = 4 * 1024 * 1024;

    private readonly FunctionSourceReader _reader;
    private readonly FunctionProjectGenerator _generator;
    private readonly FunctionBuildService _builder;
    private readonly ILogger<FunctionBundleBuilder> _logger;

    // One build at a time across the whole server. A build is a few hundred MB of memory and
    // most of a CPU for seconds; two at once gain nothing on a small box.
    private readonly SemaphoreSlim _buildGate = new(1, 1);

    public FunctionBundleBuilder(
        FunctionSourceReader reader,
        FunctionProjectGenerator generator,
        FunctionBuildService builder,
        ILogger<FunctionBundleBuilder> logger)
    {
        _reader = reader;
        _generator = generator;
        _builder = builder;
        _logger = logger;
    }

    /// <summary>
    /// Builds <paramref name="files"/> into a new bundle directory under <paramref name="parentDir"/>.
    /// On success the bundle's id names that directory.
    /// </summary>
    public async Task<FunctionDeployResult> BuildAsync(
        string parentDir,
        IReadOnlyList<FunctionFile> files,
        string actor,
        string source,
        CancellationToken ct = default)
    {
        var (named, fileError) = Validate(files);
        if (named is null) return FunctionDeployResult.Failed(fileError!);

        var sources = new List<(string FileName, FunctionSource Source)>();
        foreach (var file in named)
        {
            var read = _reader.Read(file.Name, file.Text);
            if (read.Source is null) return FunctionDeployResult.Failed($"{file.Name}: {read.Error}");
            sources.Add((file.Name, read.Source));
        }

        var id = NewId();
        var bundleDir = Path.Combine(parentDir, id);

        await _buildGate.WaitAsync(ct);
        try
        {
            var sourceDir = DataPaths.FunctionSourceDir(bundleDir);
            Directory.CreateDirectory(sourceDir);
            foreach (var file in named)
            {
                await File.WriteAllTextAsync(Path.Combine(sourceDir, file.Name), file.Text, new UTF8Encoding(false), ct);
            }

            var (project, mergeError) = await _generator.WriteAsync(sources, DataPaths.FunctionBuildDir(bundleDir), ct);
            if (project is null)
            {
                TryDeleteDirectory(bundleDir);
                return FunctionDeployResult.Failed(mergeError!);
            }

            var result = await _builder.PublishAsync(
                project.ProjectPath, DataPaths.FunctionBinDir(bundleDir), project.SourceMap, ct);

            // The generated project and its obj tree are only needed to produce bin.
            TryDeleteDirectory(DataPaths.FunctionBuildDir(bundleDir));

            if (!result.Ok)
            {
                TryDeleteDirectory(bundleDir);
                return FunctionDeployResult.Failed(result.Error!, result.Diagnostics);
            }

            var (routes, routeSources, routeError) = DiscoverRoutes(DataPaths.FunctionBinDir(bundleDir), project.SourceMap, named);
            if (routes is null)
            {
                TryDeleteDirectory(bundleDir);
                return FunctionDeployResult.Failed(routeError!, result.Diagnostics);
            }

            var bundle = new FunctionBundle
            {
                Id = id,
                Files = named.Select(f => f.Name).ToList(),
                UploadedBy = actor,
                Source = source,
                Routes = routes,
                RouteSources = routeSources!,
                Warnings = result.Diagnostics?.Count(d => d.Severity == "warning") ?? 0,
            };

            _logger.LogInformation("Built function bundle {Id} from {Files} file(s) with {Routes} route(s)",
                id, named.Count, routes.Count);
            return new FunctionDeployResult(true, null, bundle, result.Diagnostics);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Function build in {Dir} failed while writing to disk", bundleDir);
            TryDeleteDirectory(bundleDir);
            return FunctionDeployResult.Failed("The functions could not be written to disk. Check the server logs.");
        }
        finally
        {
            _buildGate.Release();
        }
    }

    /// <summary>
    /// Checks the set of files before anything is written, and settles the names they are
    /// saved under. Names must be unique: they become file names on disk and in the project.
    /// </summary>
    private static (List<FunctionFile>? Files, string? Error) Validate(IReadOnlyList<FunctionFile> files)
    {
        if (files.Count == 0) return (null, "Add at least one .cs or .linq file.");
        if (files.Count > MaxFiles) return (null, $"Functions are limited to {MaxFiles} files.");

        var named = new List<FunctionFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        foreach (var file in files)
        {
            var name = SafeFileName(file.Name);
            if (!seen.Add(name)) return (null, $"Two files are named {name}. Each file needs its own name.");

            var bytes = Encoding.UTF8.GetByteCount(file.Text);
            if (bytes > FunctionSourceReader.MaxSourceBytes)
                return (null, $"{name} is over the {Format.Bytes(FunctionSourceReader.MaxSourceBytes)} limit for one file.");

            total += bytes;
            named.Add(file with { Name = name });
        }

        if (total > MaxTotalBytes)
            return (null, $"Together the files are over the {Format.Bytes(MaxTotalBytes)} limit.");

        return (named, null);
    }

    /// <summary>
    /// Loads the build once, in its own context, to read the routes back out, then unloads it.
    /// This is also the check that the output loads at all, and that no two handlers claim the
    /// same route, before anything depends on it.
    /// </summary>
    private (List<string>? Routes, Dictionary<string, FunctionRouteSource>? Sources, string? Error) DiscoverRoutes(
        string binDir, IReadOnlyDictionary<string, FunctionSourceMapEntry> sourceMap, IReadOnlyList<FunctionFile> files)
    {
        var lines = files.ToDictionary(f => f.Name, f => f.Text.Replace("\r\n", "\n").Split('\n'), StringComparer.OrdinalIgnoreCase);

        FunctionSet? set = null;
        try
        {
            set = FunctionSet.Load(binDir, "functions:probe");

            if (set.Router.Routes.Count == 0)
            {
                return (null, null,
                    "It compiled, but no handlers were found. Mark public static methods with an attribute such as " +
                    "[HttpGet(\"/hello\")] or [HttpPost(\"/items/{id}\")].");
            }

            var conflicts = set.Router.FindConflicts();
            if (conflicts.Count > 0) return (null, null, "Two handlers claim the same route: " + string.Join(" ", conflicts));

            var routes = new List<string>();
            var sources = new Dictionary<string, FunctionRouteSource>();
            using var pdb = FunctionSourceLocator.OpenPdb(binDir);

            foreach (var route in set.Router.Routes)
            {
                var text = $"{(route.Verb == "*" ? "ANY" : route.Verb)} {route.Template}";
                routes.Add(text);

                if (pdb is not null && FunctionSourceLocator.Locate(pdb.GetMetadataReader(), route.Method, sourceMap) is { } source)
                {
                    if (lines.TryGetValue(source.File, out var fileLines))
                        source.Line = SnapToSignature(fileLines, source.Line, route.Method.Name);
                    sources[text] = source;
                }
            }

            return (routes, sources, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A function build in {Dir} compiled but did not load", binDir);
            return (null, null, $"It compiled, but could not be loaded: {ex.GetBaseException().Message}");
        }
        finally
        {
            set?.Unload();
        }
    }

    /// <summary>
    /// The debug information places a block-bodied method at its first statement. Opening the
    /// editor on the declaration reads better, so step back to the line that names the method,
    /// looking no further than a few lines up so a miss can never land somewhere unrelated.
    /// </summary>
    private static int SnapToSignature(string[] lines, int line, string methodName)
    {
        for (var candidate = Math.Min(line, lines.Length); candidate >= Math.Max(1, line - 15); candidate--)
        {
            var text = lines[candidate - 1];
            var at = text.IndexOf(methodName, StringComparison.Ordinal);
            if (at >= 0 && text.AsSpan(at + methodName.Length).TrimStart().StartsWith("(")) return candidate;
        }

        return line;
    }

    /// <summary>Bundle ids sort by time, like release ids, and are safe as a directory name.</summary>
    public static string NewId() => $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x1000, 0xffff):x4}";

    /// <summary>True for something <see cref="NewId"/> could have produced, so an id from a request can name a directory.</summary>
    public static bool IsValidId(string? id) =>
        id is { Length: 20 } && id[8] == '-' && id[15] == '-' &&
        id.Where((c, i) => i is not (8 or 15)).All(char.IsAsciiHexDigit);

    /// <summary>
    /// The name a source is saved and offered for download under. Only the extension decides
    /// anything (.linq or not), so everything else is reduced to characters that are safe in a
    /// path, a project file and a Content-Disposition header.
    /// </summary>
    public static string SafeFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/'));
        var cleaned = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray()).Trim('.');

        if (cleaned.Length == 0) return "Functions.cs";
        if (cleaned.Length > 100) cleaned = cleaned[^100..];

        return cleaned.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
               cleaned.EndsWith(".linq", StringComparison.OrdinalIgnoreCase)
            ? cleaned
            : cleaned + ".cs";
    }

    public static bool IsFunctionFileName(string name) =>
        name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".linq", StringComparison.OrdinalIgnoreCase);

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }
}
