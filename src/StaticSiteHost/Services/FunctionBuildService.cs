using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services;

/// <summary>One compiler message, with the file and line mapped back to what the author uploaded.</summary>
/// <param name="File">The uploaded file it is about, or empty when it is about the build itself.</param>
public sealed record FunctionDiagnostic(
    string Severity, string Code, string Message, int Line, int Column, string File = "")
{
    public override string ToString()
    {
        var where = (File, Line) switch
        {
            ({ Length: > 0 }, > 0) => $" ({File} line {Line})",
            ({ Length: > 0 }, _) => $" ({File})",
            (_, > 0) => $" (line {Line})",
            _ => "",
        };

        return $"{Severity}{(Code.Length > 0 ? " " + Code : "")}{where}: {Message}";
    }
}

public sealed record FunctionBuildResult(
    bool Ok,
    string? Error,
    string? AssemblyPath = null,
    IReadOnlyList<FunctionDiagnostic>? Diagnostics = null,
    string? RawOutput = null)
{
    public static FunctionBuildResult Failed(
        string error, IReadOnlyList<FunctionDiagnostic>? diagnostics = null, string? raw = null) =>
        new(false, error, null, diagnostics ?? [], raw);

    public IReadOnlyList<FunctionDiagnostic> Errors =>
        (Diagnostics ?? []).Where(d => d.Severity == "error").ToList();
}

/// <summary>
/// Builds a generated function project by running the .NET SDK, and reports what the compiler
/// said in terms of the author's own file.
///
/// The SDK is invoked rather than hosted because only it resolves NuGet and lays out native
/// assets. The price is that everything arrives as text on stdout, which is why parsing and —
/// above all — checking the exit code is this class's real job: <c>dotnet build</c> writes
/// compiler errors to stdout and leaves stderr empty, so a service that watches stderr sees a
/// silent success, finds last deploy's assembly still sitting in the output directory, and
/// serves stale code. The exit code is the only trustworthy signal.
/// </summary>
public sealed class FunctionBuildService
{
    /// <summary>
    /// Ceiling on a build. A first build restores packages and can legitimately take a while;
    /// past this something is wrong and the deploy has to fail rather than hang forever.
    /// </summary>
    public static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Cap on captured build output, so a pathological build cannot exhaust memory.</summary>
    private const int MaxCapturedChars = 256 * 1024;

    /// <summary>
    /// Matches an MSBuild/csc diagnostic:
    /// <c>/path/Handlers.cs(12,34): error CS0103: The name 'Foo' does not exist [/path/x.csproj]</c>,
    /// or one without a code, which is how NuGet reports some restore failures:
    /// <c>/sdk/NuGet.targets(196,5): error : '1.x' is not a valid version string.</c>
    /// </summary>
    private static readonly Regex DiagnosticPattern = new(
        @"^(?<file>[^(\r\n]+?)(?:\((?<line>\d+),(?<col>\d+)\))?\s*:\s*(?<severity>error|warning)(?:\s+(?<code>[A-Za-z]+\d+))?\s*:\s*(?<message>.*?)(?:\s*\[[^\]]*\])?$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private readonly ILogger<FunctionBuildService> _logger;

    public FunctionBuildService(ILogger<FunctionBuildService> logger) => _logger = logger;

    /// <summary>
    /// Publishes <paramref name="projectPath"/> into <paramref name="outputDir"/>.
    ///
    /// Publish rather than build: it produces the whole dependency closure including the
    /// <c>runtimes/*/native</c> tree that a package's native binaries live in, and a
    /// <c>.deps.json</c> laid out for that directory — which is exactly what an
    /// AssemblyDependencyResolver needs to load the result.
    /// </summary>
    public async Task<FunctionBuildResult> PublishAsync(
        string projectPath,
        string outputDir,
        IReadOnlyDictionary<string, FunctionSourceMapEntry> sourceMap,
        CancellationToken ct = default)
    {
        if (!File.Exists(projectPath))
            return FunctionBuildResult.Failed("The generated project file is missing.");

        // Publishing into a directory that already holds an assembly is how stale output
        // survives a failed build, so start from nothing.
        if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        Directory.CreateDirectory(outputDir);

        var arguments = new[]
        {
            "publish", projectPath,
            "--configuration", "Release",
            "--output", outputDir,
            "--nologo",
            "-v", "quiet",
            "-consoleLoggerParameters:NoSummary",

            // By default a build leaves MSBuild worker nodes and the Roslyn compiler server
            // running afterwards, a few hundred MB each, waiting for a next build that on this
            // server may be hours away. One-shot builds should leave nothing behind.
            "-nodeReuse:false",
            "-p:UseSharedCompilation=false",
        };

        var started = Stopwatch.StartNew();
        ProcessRun run;

        try
        {
            run = await RunAsync("dotnet", arguments, Path.GetDirectoryName(projectPath)!, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Function build timed out after {Timeout}", BuildTimeout);
            return FunctionBuildResult.Failed(
                $"The build did not finish within {BuildTimeout.TotalMinutes:0} minutes and was stopped.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            _logger.LogError(ex, "The dotnet SDK could not be started");
            return FunctionBuildResult.Failed(
                "The .NET SDK is not available on the server, so functions cannot be compiled.");
        }

        var combined = Truncate(run.Output + "\n" + run.Error);
        var diagnostics = ParseDiagnostics(combined, sourceMap);

        // The exit code decides, not the presence of text on either stream.
        if (run.ExitCode != 0)
        {
            var errors = diagnostics.Where(d => d.Severity == "error").ToList();

            _logger.LogInformation(
                "Function build failed with exit code {ExitCode} and {Errors} error(s) in {Elapsed}ms",
                run.ExitCode, errors.Count, started.ElapsedMilliseconds);

            return FunctionBuildResult.Failed(
                errors.Count > 0
                    ? errors[0].Code.StartsWith("CS", StringComparison.Ordinal)
                        ? $"The function did not compile: {errors[0]}"
                        : $"The build failed: {errors[0]}"
                    : $"The build failed with exit code {run.ExitCode}.",
                diagnostics,
                combined);
        }

        var assemblyPath = Path.Combine(outputDir, FunctionProjectGenerator.AssemblyName + ".dll");

        // A zero exit code with no assembly would otherwise become a confusing load failure
        // much later, far from the cause.
        if (!File.Exists(assemblyPath))
        {
            return FunctionBuildResult.Failed(
                "The build reported success but produced no assembly.", diagnostics, combined);
        }

        _logger.LogInformation(
            "Function build succeeded in {Elapsed}ms with {Warnings} warning(s)",
            started.ElapsedMilliseconds, diagnostics.Count(d => d.Severity == "warning"));

        return new FunctionBuildResult(true, null, assemblyPath, diagnostics, combined);
    }

    /// <summary>
    /// Turns build output into diagnostics, naming the uploaded file each one is about and
    /// shifting its line by the header the reader stripped, so a message points at the line
    /// the author sees in their editor.
    /// </summary>
    public static IReadOnlyList<FunctionDiagnostic> ParseDiagnostics(
        string output, IReadOnlyDictionary<string, FunctionSourceMapEntry> sourceMap)
    {
        var diagnostics = new List<FunctionDiagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var match in DiagnosticPattern.Matches(output).Cast<Match>())
        {
            var file = match.Groups["file"].Value.Trim();

            // MSBuild repeats each diagnostic once per target; and positions in the generated
            // project file or the SDK's targets are not about anything the author wrote.
            sourceMap.TryGetValue(Path.GetFileName(file), out var author);

            var line = author is not null && int.TryParse(match.Groups["line"].Value, out var parsed)
                ? parsed + author.LineOffset
                : 0;

            var diagnostic = new FunctionDiagnostic(
                match.Groups["severity"].Value.ToLowerInvariant(),
                match.Groups["code"].Value.ToUpperInvariant(),
                match.Groups["message"].Value.Trim(),
                line,
                author is not null && int.TryParse(match.Groups["col"].Value, out var column) ? column : 0,
                author?.FileName ?? "");

            if (seen.Add(diagnostic.ToString())) diagnostics.Add(diagnostic);
        }

        return diagnostics;
    }

    private sealed record ProcessRun(int ExitCode, string Output, string Error);

    private async Task<ProcessRun> RunAsync(
        string command, string[] arguments, string workingDirectory, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        // The SDK writes telemetry notices and first-run banners into the output otherwise,
        // which is noise in a diagnostics list.
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        // The environment-variable form of -nodeReuse:false, which also covers the nodes the
        // restore step starts before the command-line switch applies.
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Read both streams concurrently: a build that fills the stderr pipe while nobody
        // drains it blocks forever, which is the classic deadlock here.
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(BuildTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessRun(process.ExitCode, await output, await error);
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not kill the timed-out build process");
        }
    }

    private static string Truncate(string text) =>
        text.Length <= MaxCapturedChars
            ? text.Trim()
            : text[..MaxCapturedChars].Trim() + "\n… output truncated";
}
