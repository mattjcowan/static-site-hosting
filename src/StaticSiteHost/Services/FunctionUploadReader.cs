using System.IO.Compression;
using System.Text;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services;

/// <summary>
/// Turns what someone uploaded into function files: a .cs or .linq file is taken as it is, and
/// a .zip is opened for the .cs and .linq files inside. The zip form is what "Download source"
/// hands back for a bundle of several files, so it has to go straight back up.
/// </summary>
public static class FunctionUploadReader
{
    private const int MaxZipEntries = 500;

    /// <summary>Adds the files in one upload to <paramref name="into"/>, or returns why it cannot.</summary>
    public static string? Read(string? name, byte[] content, List<FunctionFile> into)
    {
        name = Path.GetFileName(name ?? "");

        if (IsZip(name, content)) return ReadZip(name, content, into);

        // A binary file handed to the compiler comes back as pages of "unexpected character"
        // errors about line 1, which says nothing about what actually went wrong.
        if (Array.IndexOf(content, (byte)0) >= 0)
            return $"{name} is not a text file. Upload .cs or .linq files, or a .zip of them.";

        into.Add(new FunctionFile(name, Encoding.UTF8.GetString(content)));
        return null;
    }

    private static bool IsZip(string name, byte[] content) =>
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
        content is [0x50, 0x4b, 0x03, 0x04, ..];

    /// <summary>
    /// Takes the .cs and .linq files from a zip, whether they sit at its root, in a
    /// <c>_functions/</c> folder (as in a site's zip), or in one folder wrapping everything.
    /// Anything else in it is ignored; files deeper than that are ignored too, because the
    /// server compiles a flat set of files.
    /// </summary>
    private static string? ReadZip(string name, byte[] content, List<FunctionFile> into)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            return $"{name} is not a readable zip archive.";
        }

        using (zip)
        {
            if (zip.Entries.Count > MaxZipEntries)
                return $"{name} holds more than {MaxZipEntries} entries, which is more than a set of functions needs.";

            var entries = zip.Entries
                .Where(e => !e.FullName.EndsWith('/') && !e.FullName.Contains("__MACOSX"))
                .Select(e => (Entry: e, Segments: e.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)))
                .ToList();

            // One folder wrapping everything is unwrapped, as it is for a site's zip.
            if (entries.Count > 0 && entries.All(e => e.Segments.Length >= 2) &&
                entries.Select(e => e.Segments[0]).Distinct(StringComparer.Ordinal).Count() == 1)
            {
                entries = entries.Select(e => (e.Entry, e.Segments[1..])).ToList();
            }

            var found = 0;
            foreach (var (entry, segments) in entries)
            {
                var path = segments is [var folder, _] && folder.Equals(ZipDeploymentService.FunctionsFolder, StringComparison.OrdinalIgnoreCase)
                    ? segments[1..]
                    : segments;

                if (path.Length != 1 || !FunctionBundleBuilder.IsFunctionFileName(path[0])) continue;

                if (entry.Length > FunctionSourceReader.MaxSourceBytes)
                    return $"{name}: {path[0]} is over the {Format.Bytes(FunctionSourceReader.MaxSourceBytes)} limit for one file.";

                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                into.Add(new FunctionFile(path[0], reader.ReadToEnd()));
                found++;
            }

            return found == 0
                ? $"{name} has no .cs or .linq files at its top level or in a {ZipDeploymentService.FunctionsFolder}/ folder."
                : null;
        }
    }
}
