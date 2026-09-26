using System.Security;
using System.Text;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services;

/// <summary>Where a compiled file came from, so compiler messages can point at the author's file.</summary>
public sealed record FunctionSourceMapEntry(string FileName, int LineOffset);

public sealed record GeneratedFunctionProject(
    string ProjectPath,
    IReadOnlyDictionary<string, FunctionSourceMapEntry> SourceMap);

/// <summary>
/// Writes one or more <see cref="FunctionSource"/>s out as a throwaway project the SDK can build.
///
/// Generating a project rather than driving Roslyn in-process is what buys NuGet resolution,
/// native assets (a package like ScottPlot pulls SkiaSharp binaries that no MetadataReference
/// list would deploy), implicit usings and the reference closure — all for free, and all
/// things that had to be hand-built otherwise.
///
/// The project is always a library. An uploaded file has no entry point once its
/// <c>#if LINQPAD</c> harness is compiled away, and asking for an executable would fail
/// CS5001; conversely a file that did keep top-level statements cannot be a library
/// (CS8805). Library plus a guarded harness is the only combination that holds for both
/// formats, which is why the template guards its harness.
///
/// Several files become one project: each keeps its own name (so compiler messages can say
/// which file), and what they ask of the project — packages, imports, properties — is merged.
/// Merging refuses rather than guesses when two files disagree, because either answer would
/// leave one file running against something it was not written for.
/// </summary>
public sealed class FunctionProjectGenerator
{
    public const string ProjectFileName = "functions.csproj";

    /// <summary>Assembly name of the built output, so callers know what to load.</summary>
    public const string AssemblyName = "SiteFunctions";

    /// <summary>
    /// Namespaces imported into every function project, matching LINQPad's own default set.
    ///
    /// LINQPad compiles a query against these whether or not the .linq header lists them, and
    /// the header is where the server learns what to import — so without this a file that
    /// builds in the editor fails on upload with CS0246 for a namespace the author never had
    /// to think about. Making the server's baseline match the editor's is what keeps "runs in
    /// LINQPad" and "builds on the server" the same statement.
    ///
    /// ImplicitUsings covers some of these already; duplicates are dropped below.
    /// </summary>
    public static readonly string[] DefaultUsings =
    [
        "System",
        "System.Collections",
        "System.Collections.Generic",
        "System.Data",
        "System.Diagnostics",
        "System.IO",
        "System.Linq",
        "System.Linq.Expressions",
        "System.Reflection",
        "System.Text",
        "System.Text.RegularExpressions",
        "System.Threading",
        "System.Transactions",
        "System.Xml",
        "System.Xml.Linq",
        "System.Xml.XPath",
    ];

    private readonly ILogger<FunctionProjectGenerator> _logger;

    public FunctionProjectGenerator(ILogger<FunctionProjectGenerator> logger) => _logger = logger;

    /// <summary>
    /// Writes <c>functions.csproj</c> and one .cs per source into <paramref name="projectDir"/>,
    /// which is created if needed. Fails without writing anything when the files disagree.
    /// </summary>
    public async Task<(GeneratedFunctionProject? Project, string? Error)> WriteAsync(
        IReadOnlyList<(string FileName, FunctionSource Source)> sources,
        string projectDir,
        CancellationToken ct = default)
    {
        var (merged, error) = Merge(sources);
        if (merged is null) return (null, error);

        Directory.CreateDirectory(projectDir);

        var map = new Dictionary<string, FunctionSourceMapEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fileName, source) in sources)
        {
            var compiled = CompiledName(fileName);
            map[compiled] = new FunctionSourceMapEntry(fileName, source.LineOffset);
            await File.WriteAllTextAsync(Path.Combine(projectDir, compiled), source.Code, new UTF8Encoding(false), ct);
        }

        var projectPath = Path.Combine(projectDir, ProjectFileName);
        await File.WriteAllTextAsync(projectPath, BuildProjectFile(merged, map.Keys), Encoding.UTF8, ct);

        _logger.LogDebug(
            "Generated {Project} from {Files} file(s) with {Packages} package(s)",
            projectPath, sources.Count, merged.Packages.Count);

        return (new GeneratedFunctionProject(projectPath, map), null);
    }

    /// <summary>The name a source is compiled under: its own, plus .cs when it is not one already.</summary>
    public static string CompiledName(string fileName) =>
        fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".cs";

    private sealed record MergedSources(
        List<FunctionPackage> Packages,
        List<string> Usings,
        List<KeyValuePair<string, string>> Properties,
        bool NeedsAspNetCore);

    private static (MergedSources? Merged, string? Error) Merge(IReadOnlyList<(string FileName, FunctionSource Source)> sources)
    {
        var packages = new Dictionary<string, (FunctionPackage Package, string From)>(StringComparer.OrdinalIgnoreCase);
        var properties = new Dictionary<string, (string Value, string From)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (fileName, source) in sources)
        {
            foreach (var package in source.Packages)
            {
                if (!packages.TryGetValue(package.Name, out var existing))
                {
                    packages[package.Name] = (package, fileName);
                }
                else if (existing.Package.Version == "*")
                {
                    // An unpinned reference (every .linq one) accepts whatever another file pins.
                    packages[package.Name] = (package, fileName);
                }
                else if (package.Version != "*" && package.Version != existing.Package.Version)
                {
                    return (null,
                        $"{package.Name} is pinned to {existing.Package.Version} in {existing.From} and to " +
                        $"{package.Version} in {fileName}. Use the same version in both.");
                }
            }

            foreach (var (name, value) in source.Properties)
            {
                if (properties.TryGetValue(name, out var existing) && existing.Value != value)
                {
                    return (null,
                        $"The property {name} is {existing.Value} in {existing.From} and {value} in {fileName}. " +
                        "Set it the same way in both.");
                }

                properties[name] = (value, fileName);
            }
        }

        return (new MergedSources(
            packages.Values.Select(p => p.Package).ToList(),
            sources.SelectMany(s => s.Source.Usings).Distinct(StringComparer.Ordinal).ToList(),
            properties.Select(p => new KeyValuePair<string, string>(p.Key, p.Value.Value)).ToList(),
            sources.Any(s => s.Source.NeedsAspNetCore)), null);
    }

    private static string BuildProjectFile(MergedSources source, IEnumerable<string> compiledFiles)
    {
        var project = new StringBuilder();

        project.AppendLine("""<Project Sdk="Microsoft.NET.Sdk">""");
        project.AppendLine();
        project.AppendLine("  <PropertyGroup>");
        project.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
        project.AppendLine($"    <AssemblyName>{AssemblyName}</AssemblyName>");
        project.AppendLine($"    <RootNamespace>{AssemblyName}</RootNamespace>");

        // A library, always. See the class remarks.
        project.AppendLine("    <OutputType>Library</OutputType>");

        // The author's file assumes both, exactly as LINQPad and `dotnet run` provide them.
        project.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        project.AppendLine("    <Nullable>enable</Nullable>");

        // Without this a plain `dotnet build` emits only the handler assembly and leaves
        // every package behind, so the first request throws FileNotFoundException.
        // `dotnet publish` implies it; setting it keeps `build` viable too.
        project.AppendLine("    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>");

        // Deterministic so an unchanged upload produces an identical assembly, which makes
        // "did this deploy actually change anything" answerable by hash.
        project.AppendLine("    <Deterministic>true</Deterministic>");
        project.AppendLine("    <DebugType>portable</DebugType>");

        // Nothing here is trimmed or AOT compiled, and the analysers only produce noise that
        // would surface to the author as warnings about their own perfectly fine code.
        project.AppendLine("    <EnableTrimAnalyzer>false</EnableTrimAnalyzer>");
        project.AppendLine("    <EnableAotAnalyzer>false</EnableAotAnalyzer>");
        project.AppendLine("    <EnableSingleFileAnalyzer>false</EnableSingleFileAnalyzer>");

        // A handler returning an anonymous object relies on reflection-based serialisation,
        // which the AOT-friendly defaults would switch off.
        project.AppendLine("    <JsonSerializerIsReflectionEnabledByDefault>true</JsonSerializerIsReflectionEnabledByDefault>");

        // Only the files that were written: no stray .cs from a previous build, and no
        // accidental compile of anything the build drops into the directory.
        project.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");

        foreach (var (name, value) in source.Properties)
        {
            // Author-supplied, so it must not be able to close the element and inject MSBuild.
            if (!IsSafePropertyName(name)) continue;
            project.AppendLine($"    <{name}>{SecurityElement.Escape(value)}</{name}>");
        }

        project.AppendLine("  </PropertyGroup>");
        project.AppendLine();

        project.AppendLine("  <ItemGroup>");
        foreach (var file in compiledFiles)
        {
            project.AppendLine($"""    <Compile Include="{SecurityElement.Escape(file)}" />""");
        }
        project.AppendLine("  </ItemGroup>");
        project.AppendLine();

        if (source.NeedsAspNetCore)
        {
            project.AppendLine("  <ItemGroup>");
            project.AppendLine("""    <FrameworkReference Include="Microsoft.AspNetCore.App" />""");
            project.AppendLine("  </ItemGroup>");
            project.AppendLine();
        }

        if (source.Packages.Count > 0)
        {
            project.AppendLine("  <ItemGroup>");
            foreach (var package in source.Packages)
            {
                project.AppendLine(
                    $"""    <PackageReference Include="{SecurityElement.Escape(package.Name)}" Version="{SecurityElement.Escape(package.Version)}" />""");
            }
            project.AppendLine("  </ItemGroup>");
            project.AppendLine();
        }

        // The shared baseline, plus whatever a .linq header asked for on top. A .cs upload
        // contributes nothing here — its usings are in the code — but still gets the baseline,
        // so both formats compile against the same set.
        var usings = DefaultUsings
            .Concat(source.Usings)
            .Where(IsSafeNamespace)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (usings.Count > 0)
        {
            project.AppendLine("  <ItemGroup>");
            foreach (var ns in usings)
            {
                project.AppendLine($"""    <Using Include="{ns}" />""");
            }
            project.AppendLine("  </ItemGroup>");
            project.AppendLine();
        }

        project.AppendLine("</Project>");

        return project.ToString();
    }

    private static bool IsSafePropertyName(string name) =>
        name.Length is > 0 and <= 128 && name.All(char.IsAsciiLetterOrDigit);

    private static bool IsSafeNamespace(string ns) =>
        ns.Length is > 0 and <= 256 && ns.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_');
}
