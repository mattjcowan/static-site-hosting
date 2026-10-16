using System.Globalization;
using Microsoft.Extensions.Localization;
using StaticSiteHost.Localization;
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
    /// otherwise the date too, written the same way in every language.
    /// </summary>
    public static string When(DateTimeOffset? utc)
    {
        if (utc is not { } value) return "—";

        var at = value.ToUniversalTime();
        return at.Date == DateTime.UtcNow.Date
            ? at.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
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
    public static Task<(List<FunctionFile>? Files, string? Error)> ReadAsync(
        IReadOnlyList<IFormFile> uploads, IStringLocalizer strings, CancellationToken ct) =>
        ReadAsync(uploads,
            (file, limit) => strings.Text("functions.upload.tooLarge", new { file, limit }),
            strings.Text("functions.upload.noFiles"), ct);

    private static async Task<(List<FunctionFile>? Files, string? Error)> ReadAsync(
        IReadOnlyList<IFormFile> uploads, Func<string, string, string> tooLarge, string noFiles, CancellationToken ct)
    {
        var files = new List<FunctionFile>();

        foreach (var upload in uploads.Where(u => u.Length > 0))
        {
            if (upload.Length > FunctionBundleBuilder.MaxTotalBytes)
                return (null, tooLarge(upload.FileName, Format.Bytes(FunctionBundleBuilder.MaxTotalBytes)));

            using var buffer = new MemoryStream();
            await upload.CopyToAsync(buffer, ct);
            if (FunctionUploadReader.Read(upload.FileName, buffer.ToArray(), files) is { } error) return (null, error);
        }

        return files.Count == 0 ? (null, noFiles) : (files, null);
    }

    /// <summary>
    /// The status line after an upload, in the viewer's language: what went live where, and what
    /// a merge kept. <paramref name="domain"/> is null for the global functions.
    /// </summary>
    public static string Describe(FunctionDeployResult result, string? domain, IStringLocalizer strings)
    {
        var bundle = result.Bundle!;
        var sentences = new List<string>
        {
            strings.Text(domain is null ? "functions.deployed.global" : "functions.deployed.site",
                new { label = Label(bundle, strings), domain, contents = Contents(bundle, strings) })
        };
        if (result.Kept is { Count: > 0 } kept)
            sentences.Add(strings.Plural("functions.deployed.kept", kept.Count, new { files = JoinList(kept, strings) }));
        if (bundle.Warnings > 0)
            sentences.Add(strings.Plural("functions.deployed.warnings", bundle.Warnings));

        return string.Join(" ", sentences);
    }

    /// <summary>The status line after one file was removed and the rest deployed again.</summary>
    public static string DescribeRemoved(FunctionDeployResult result, string fileName, string? domain, IStringLocalizer strings) =>
        strings.Text("functions.files.removed", new { file = fileName, summary = Describe(result, domain, strings) });

    /// <summary>How a bundle reads in a table or a message, in the viewer's language: "Orders.cs", or "Orders.cs + 2 more".</summary>
    public static string Label(FunctionBundle bundle, IStringLocalizer strings) => bundle.Files.Count switch
    {
        0 => strings.Text("functions.bundle.noFiles"),
        1 => bundle.Files[0],
        _ => strings.Plural("functions.bundle.more", bundle.Files.Count - 1, new { first = bundle.Files[0] })
    };

    /// <summary>"3 routes, 1 middleware and 2 jobs": the routes always, the rest when there are any.</summary>
    public static string Contents(FunctionBundle bundle, IStringLocalizer strings)
    {
        var parts = new List<string> { strings.Plural("functions.contents.routes", bundle.Routes.Count) };
        if (bundle.Middleware.Count > 0) parts.Add(strings.Plural("functions.contents.middleware", bundle.Middleware.Count));
        if (bundle.BackgroundServices.Count > 0) parts.Add(strings.Plural("functions.contents.backgroundServices", bundle.BackgroundServices.Count));
        if (bundle.Jobs.Count > 0) parts.Add(strings.Plural("functions.contents.jobs", bundle.Jobs.Count));
        if (bundle.Hooks.Count > 0) parts.Add(strings.Plural("functions.contents.hooks", bundle.Hooks.Count));

        return JoinList(parts, strings);
    }

    /// <summary>"a, b and c", joined the way the viewer's language joins a list.</summary>
    private static string JoinList(IReadOnlyList<string> items, IStringLocalizer strings)
    {
        if (items.Count == 0) return "";

        var text = items[0];
        for (var i = 1; i < items.Count; i++)
            text = strings.Text(i == items.Count - 1 ? "functions.list.last" : "functions.list.next", new { list = text, item = items[i] });
        return text;
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
