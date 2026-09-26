using StaticSiteHost.Models;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages.Shared;

/// <summary>What _FunctionBundle renders: a live bundle and where its links point.</summary>
/// <param name="SiteUrl">Base address for clickable GET routes; null for global functions, which have no single host.</param>
/// <param name="EditorUrl">The editor for this scope; null when the viewer may not edit, and files are then plain text.</param>
/// <param name="RemoveFileAction">Where a file's × posts; null to offer none.</param>
public sealed record FunctionBundleView(
    FunctionBundle Bundle, string? SiteUrl, string SourceUrl, string? EditorUrl = null, string? RemoveFileAction = null)
{
    /// <summary>The editor opened on one file, at a line when known.</summary>
    public string? EditorLink(string file, int line = 0) =>
        EditorUrl is null ? null
        : $"{EditorUrl}?file={Uri.EscapeDataString(file)}{(line > 0 ? $"&line={line}" : "")}";
}

/// <summary>What _FunctionErrors renders after a refused upload.</summary>
public sealed record FunctionErrorsView(string? Error, IReadOnlyList<FunctionDiagnostic> Diagnostics);

public static class FunctionUploads
{
    /// <summary>Reads the files picked in an upload form, .zip files included, with the same limits as the API.</summary>
    public static async Task<(List<FunctionFile>? Files, string? Error)> ReadAsync(
        IReadOnlyList<IFormFile> uploads, CancellationToken ct)
    {
        var files = new List<FunctionFile>();

        foreach (var upload in uploads.Where(u => u.Length > 0))
        {
            if (upload.Length > FunctionBundleBuilder.MaxTotalBytes)
                return (null, $"{upload.FileName} is over the {Format.Bytes(FunctionBundleBuilder.MaxTotalBytes)} upload limit.");

            using var buffer = new MemoryStream();
            await upload.CopyToAsync(buffer, ct);
            if (FunctionUploadReader.Read(upload.FileName, buffer.ToArray(), files) is { } error) return (null, error);
        }

        return files.Count == 0 ? (null, "Choose .cs or .linq files, or a .zip of them, to upload.") : (files, null);
    }

    /// <summary>The status line after an upload: what went live, and what a merge kept.</summary>
    public static string Describe(FunctionDeployResult result, string where)
    {
        var bundle = result.Bundle!;
        var kept = result.Kept is { Count: > 0 } k ? $" Kept {string.Join(", ", k)} as it was." : "";
        var warnings = bundle.Warnings > 0 ? $" {bundle.Warnings} compiler warning(s)." : "";
        return $"{bundle.Routes.Count} route(s) live on {where} from {bundle.Label}.{kept}{warnings}";
    }
}

/// <summary>What _FunctionUpload renders: where the form posts and what it offers.</summary>
/// <param name="Live">The bundle live now, if any; it decides whether merge and remove are offered.</param>
/// <param name="RemoveAction">Where the remove form posts; null to offer no remove.</param>
public sealed record FunctionUploadView(
    string Action, FunctionBundle? Live, string EditorUrl, string? RemoveAction, string RemoveLabel, string RemoveConfirm);
