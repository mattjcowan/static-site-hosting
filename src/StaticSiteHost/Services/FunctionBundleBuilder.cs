using System.Reflection;
using System.Text;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services;

/// <param name="Diagnostics">
/// What the compiler reported, then any "info" note the host adds about the build (see
/// <see cref="FunctionDiagnostic"/>). Present on a failed build too, when it got as far as
/// compiling.
/// </param>
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
/// publish it, and load the result once to find its routes, middleware, services, jobs and hooks.
/// The one pipeline behind every way functions arrive — an upload, a zip's _functions/ folder, the
/// editor's Check and Deploy — so they all accept and refuse exactly the same things.
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

            var diagnostics = WithAbstractionsNote(result.Diagnostics, project);

            if (!result.Ok)
            {
                TryDeleteDirectory(bundleDir);
                return FunctionDeployResult.Failed(result.Error!, diagnostics);
            }

            var (found, discoverError) = Discover(DataPaths.FunctionBinDir(bundleDir), project.SourceMap, named);
            if (found is null)
            {
                TryDeleteDirectory(bundleDir);
                return FunctionDeployResult.Failed(discoverError!, diagnostics);
            }

            var bundle = new FunctionBundle
            {
                Id = id,
                Files = named.Select(f => f.Name).ToList(),
                UploadedBy = actor,
                Source = source,
                Routes = found.Routes,
                Middleware = found.Middleware,
                Services = found.Services,
                BackgroundServices = found.BackgroundServices,
                Jobs = found.Jobs,
                Hooks = found.Hooks,
                RouteSources = found.Sources,
                Warnings = diagnostics.Count(d => d.Severity == "warning"),
                AbstractionsVersion = project.AbstractionsVersion,
            };

            _logger.LogInformation(
                "Built function bundle {Id} from {Files} file(s) with {Routes} route(s), {Middleware} middleware, " +
                "{Background} background service(s), {Jobs} job(s) and {Hooks} hook(s)",
                id, named.Count, found.Routes.Count, found.Middleware.Count, found.BackgroundServices.Count, found.Jobs.Count,
                found.Hooks.Count);
            return new FunctionDeployResult(true, null, bundle, diagnostics);
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
    /// The build's diagnostics, plus a note naming the StaticSiteHost.Abstractions version when
    /// the files use the package. The version an author wrote is quietly not the one compiled
    /// against, which is worth saying, and most of all when the build failed on a member their
    /// newer package has and this server does not. On a failed build too, for that reason.
    /// </summary>
    private static IReadOnlyList<FunctionDiagnostic> WithAbstractionsNote(
        IReadOnlyList<FunctionDiagnostic>? diagnostics, GeneratedFunctionProject project)
    {
        if (project.AbstractionsVersion is not { } version) return diagnostics ?? [];

        var note = new FunctionDiagnostic("info", "",
            $"Compiled against {FunctionProjectGenerator.AbstractionsName} {version}, the copy this server runs; " +
            "the version named in your file is not used.",
            Line: 0, Column: 0);

        return [.. diagnostics ?? [], note];
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

    /// <summary>What the probe load found, as a bundle records it for display.</summary>
    private sealed record Discovered(
        List<string> Routes,
        List<string> Middleware,
        List<string> Services,
        List<string> BackgroundServices,
        List<string> Jobs,
        List<string> Hooks,
        Dictionary<string, FunctionRouteSource> Sources);

    /// <summary>
    /// Loads the build once, in its own context, to read its routes, middleware, services, jobs
    /// and hooks back out, then unloads it. Nothing of it runs: [ConfigureServices] methods
    /// included, which is why one that throws is only found when the functions go live. This is
    /// also the check that the output loads at all, that every [Middleware], [ConfigureServices],
    /// [BackgroundService], [Schedule], [Every], [RealtimeConnect], [RealtimeJoin] and [AiAccess]
    /// method can run (its signature, its parameters, its schedule), that there is at most one of
    /// each hook, and that no two handlers claim the same route, before anything depends on it. A
    /// build of middleware alone is fine: it gates or decorates a site whose answers come from its
    /// files. So is one of jobs alone, or of hooks alone.
    /// </summary>
    private (Discovered? Found, string? Error) Discover(
        string binDir, IReadOnlyDictionary<string, FunctionSourceMapEntry> sourceMap, IReadOnlyList<FunctionFile> files)
    {
        var lines = files.ToDictionary(f => f.Name, f => f.Text.Replace("\r\n", "\n").Split('\n'), StringComparer.OrdinalIgnoreCase);

        FunctionSet? set = null;
        try
        {
            set = FunctionSet.Load(binDir, "functions:probe");
            var router = set.Router;
            var jobs = set.Jobs;
            var hooks = set.Hooks;

            if (router.MiddlewareProblems.Count > 0 || jobs.Problems.Count > 0 || hooks.Problems.Count > 0)
                return (null, string.Join(" ", [.. router.MiddlewareProblems, .. jobs.Problems, .. hooks.Problems]));

            if (router.Routes.Count == 0 && router.Middleware.Count == 0 && jobs.IsEmpty && hooks.IsEmpty)
            {
                return (null,
                    "It compiled, but no handlers were found. Mark public static methods with an attribute such as " +
                    "[HttpGet(\"/hello\")] or [HttpPost(\"/items/{id}\")], with [Middleware] for one that runs " +
                    "before every request, with [Every(\"5m\")], [Schedule(\"0 * * * *\")] or [BackgroundService] " +
                    "for one that runs on its own, or with [RealtimeConnect], [RealtimeJoin] or [AiAccess] for one " +
                    "that decides who may use the site's realtime hub or AI.");
            }

            var conflicts = router.FindConflicts();
            if (conflicts.Count > 0) return (null, "Two handlers claim the same route: " + string.Join(" ", conflicts));

            var found = new Discovered([], [], [], [], [], [], []);
            using var pdb = FunctionSourceLocator.OpenPdb(binDir);

            // Everything shares the one map of sources. "GET /x", "0 Gate.Run", "every 5m Feed.Refresh",
            // "*/5 * * * * Feed.Refresh" and "AiAccess Gate.SignedIn" cannot collide, and a method has only
            // one of the roles named "Class.Method" (FunctionJobs refuses a second), so neither can those.
            void Locate(string text, MethodInfo method)
            {
                if (pdb is null || FunctionSourceLocator.Locate(pdb.GetMetadataReader(), method, sourceMap) is not { } source) return;

                if (lines.TryGetValue(source.File, out var fileLines))
                    source.Line = SnapToSignature(fileLines, source.Line, method.Name);
                found.Sources[text] = source;
            }

            foreach (var route in router.Routes)
            {
                var text = $"{(route.Verb == "*" ? "ANY" : route.Verb)} {route.Template}";
                found.Routes.Add(text);
                Locate(text, route.Method);
            }

            foreach (var middleware in router.Middleware)
            {
                var text = $"{middleware.Order} {middleware.Method.DeclaringType?.Name}.{middleware.Method.Name}";
                found.Middleware.Add(text);
                Locate(text, middleware.Method);
            }

            foreach (var method in jobs.Configure)
            {
                found.Services.Add(FunctionJobs.NameOf(method));
                Locate(FunctionJobs.NameOf(method), method);
            }

            foreach (var method in jobs.BackgroundServices)
            {
                found.BackgroundServices.Add(FunctionJobs.NameOf(method));
                Locate(FunctionJobs.NameOf(method), method);
            }

            foreach (var job in jobs.Scheduled)
            {
                found.Jobs.Add(job.Display);
                Locate(job.Display, job.Method);
            }

            foreach (var (display, method) in hooks.All)
            {
                found.Hooks.Add(display);
                Locate(display, method);
            }

            return (found, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A function build in {Dir} compiled but did not load", binDir);
            return (null, $"It compiled, but could not be loaded: {ex.GetBaseException().Message}");
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
