using System.Globalization;
using StaticSiteHost.Models;
using StaticSiteHost.Services;

namespace StaticSiteHost.Pages.Shared;

/// <summary>What _FunctionBundle renders: a live bundle and where its links point.</summary>
/// <param name="SiteUrl">Base address for clickable GET routes; null for global functions, which have no single host.</param>
/// <param name="EditorUrl">The editor for this scope; null when the viewer may not edit, and files are then plain text.</param>
/// <param name="RemoveFileAction">Where a file's × posts; null to offer none.</param>
/// <param name="Status">
/// Whether the bundle is loaded, and how its background services and jobs are doing, from
/// <see cref="FunctionHost.StatusAsync"/>; null to show the bundle alone.
/// </param>
public sealed record FunctionBundleView(
    FunctionBundle Bundle,
    string? SiteUrl,
    string SourceUrl,
    string? EditorUrl = null,
    string? RemoveFileAction = null,
    FunctionScopeStatus? Status = null)
{
    /// <summary>The editor opened on one file, at a line when known.</summary>
    public string? EditorLink(string file, int line = 0) =>
        EditorUrl is null ? null
        : $"{EditorUrl}?file={Uri.EscapeDataString(file)}{(line > 0 ? $"&line={line}" : "")}";

    /// <summary>The StaticSiteHost.Abstractions version this server runs and compiles against.</summary>
    public string HostAbstractionsVersion => FunctionProjectGenerator.HostAbstractionsVersion;

    /// <summary>
    /// True when the bundle was compiled against a newer StaticSiteHost.Abstractions than this
    /// server runs, so the host does not load it (see <see cref="FunctionHost.IsNewerThanHostAbstractions"/>).
    /// </summary>
    public bool NeedsNewerServer => FunctionHost.IsNewerThanHostAbstractions(Bundle.AbstractionsVersion);

    /// <summary>These are the global functions, whose middleware covers every site.</summary>
    public bool IsGlobal => SiteUrl is null;

    /// <summary>
    /// A job as the bundle records it, taken apart: "*/5 * * * * Reports.Send" or
    /// "every 5m Cache.Refresh on start" (see <see cref="FunctionBundle.Jobs"/>).
    /// </summary>
    public static (string Schedule, string Method, bool OnStart) SplitJob(string display)
    {
        var words = display.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var onStart = words.Length > 2 && words[^2] == "on" && words[^1] == "start";
        if (onStart) words = words[..^2];

        return words.Length < 2
            ? (display, "", false)
            : (string.Join(' ', words[..^1]), words[^1], onStart);
    }

    /// <summary>The loaded state of a job, when the status carries it.</summary>
    public FunctionJobStatus? JobStatus(string display) => Status?.Jobs.FirstOrDefault(job => job.Display == display);

    /// <summary>The loaded state of a background service, when the status carries it.</summary>
    public FunctionBackgroundStatus? BackgroundStatus(string method) =>
        Status?.BackgroundServices.FirstOrDefault(service => service.Method == method);

    /// <summary>
    /// A time for the jobs table, in UTC like the schedules: the time alone when it is today,
    /// otherwise the date too.
    /// </summary>
    public static string When(DateTimeOffset? utc)
    {
        if (utc is not { } value) return "—";

        var at = value.ToUniversalTime();
        return at.Date == DateTime.UtcNow.Date
            ? at.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : at.ToString("MMM d HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>How long a run took: "12 ms", "3.4 s", "2 min 5 s".</summary>
    public static string Took(TimeSpan? duration) => duration switch
    {
        null => "",
        { TotalSeconds: < 1 } d => $"{d.TotalMilliseconds:0} ms",
        { TotalMinutes: < 1 } d => $"{d.TotalSeconds:0.0} s",
        { } d => $"{(int)d.TotalMinutes} min {d.Seconds} s",
    };
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
        return $"{Contents(bundle)} live on {where} from {bundle.Label}.{kept}{warnings}";
    }

    /// <summary>"3 route(s), 1 middleware, 2 job(s) and 1 hook(s)": the routes always, the rest when there are any.</summary>
    public static string Contents(FunctionBundle bundle)
    {
        var parts = new List<string> { $"{bundle.Routes.Count} route(s)" };
        if (bundle.Middleware.Count > 0) parts.Add($"{bundle.Middleware.Count} middleware");
        if (bundle.BackgroundServices.Count > 0) parts.Add($"{bundle.BackgroundServices.Count} background service(s)");
        if (bundle.Jobs.Count > 0) parts.Add($"{bundle.Jobs.Count} job(s)");
        if (bundle.Hooks.Count > 0) parts.Add($"{bundle.Hooks.Count} hook(s)");

        return parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts[..^1])} and {parts[^1]}";
    }
}

/// <summary>What _FunctionUpload renders: where the form posts and what it offers.</summary>
/// <param name="Live">The bundle live now, if any; it decides whether merge and remove are offered.</param>
/// <param name="RemoveAction">Where the remove form posts; null to offer no remove.</param>
public sealed record FunctionUploadView(
    string Action, FunctionBundle? Live, string EditorUrl, string? RemoveAction, string RemoveLabel, string RemoveConfirm)
{
    /// <summary>The StaticSiteHost.Abstractions version an upload that uses the package compiles against.</summary>
    public string HostAbstractionsVersion => FunctionProjectGenerator.HostAbstractionsVersion;
}
