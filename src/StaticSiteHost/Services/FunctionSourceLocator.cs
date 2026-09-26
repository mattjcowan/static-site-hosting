using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services;

/// <summary>
/// Finds which uploaded file, and which line of it, a handler method was written in.
///
/// The compiler already recorded this in the build's portable PDB: every method's sequence
/// points name the source document they came from. Reading it back is exact — no guessing
/// from method names in the source text — and needs nothing but the metadata token of a
/// method that is already loaded for route discovery.
/// </summary>
public static class FunctionSourceLocator
{
    /// <summary>
    /// Opens the PDB beside the assembly in <paramref name="binDir"/>. Null when there is none,
    /// in which case routes simply go without a file.
    /// </summary>
    public static MetadataReaderProvider? OpenPdb(string binDir)
    {
        var path = Path.Combine(binDir, FunctionProjectGenerator.AssemblyName + ".pdb");
        if (!File.Exists(path)) return null;

        try
        {
            // Read fully into memory so the file is not held open: the next deploy may replace it.
            return MetadataReaderProvider.FromPortablePdbImage(File.ReadAllBytes(path).ToImmutableArray());
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The author's file and line for <paramref name="method"/>, mapped back through the
    /// generator's source map so a .linq file's header lines are counted.
    /// </summary>
    public static FunctionRouteSource? Locate(
        MetadataReader pdb, MethodInfo method, IReadOnlyDictionary<string, FunctionSourceMapEntry> sourceMap)
    {
        // An async method's body lives in its state machine's MoveNext; the method itself is a
        // stub with no sequence points of its own.
        var body = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            .GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? method;

        try
        {
            var handle = MetadataTokens.MethodDefinitionHandle(body.MetadataToken);
            var info = pdb.GetMethodDebugInformation(handle.ToDebugInformationHandle());

            var point = info.GetSequencePoints().FirstOrDefault(p => !p.IsHidden);
            if (point.Document.IsNil) return null;

            var document = pdb.GetString(pdb.GetDocument(point.Document).Name);
            if (!sourceMap.TryGetValue(Path.GetFileName(document), out var author)) return null;

            return new FunctionRouteSource { File = author.FileName, Line = point.StartLine + author.LineOffset };
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
