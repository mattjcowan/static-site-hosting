using System.IO.Compression;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services;

/// <summary>A download of a bundle's source: the file itself when there is one, a zip when there are several.</summary>
public sealed record FunctionSourceDownload(string FileName, string ContentType, byte[] Content);

/// <summary>
/// Makes built bundles live, for one site or for every site, and keeps the editor's drafts.
///
/// A scope is a domain, or null for the global functions. Everything that fails leaves the
/// functions already live untouched.
/// </summary>
public sealed class FunctionDeploymentService
{
    private const string DraftsDirName = ".drafts";
    private static readonly TimeSpan DraftLifetime = TimeSpan.FromHours(2);

    private readonly DataPaths _paths;
    private readonly SiteStore _sites;
    private readonly ZipDeploymentService _deployer;
    private readonly FunctionHost _host;
    private readonly FunctionBundleBuilder _builder;
    private readonly AuditLog _audit;
    private readonly ILogger<FunctionDeploymentService> _logger;

    public FunctionDeploymentService(
        DataPaths paths,
        SiteStore sites,
        ZipDeploymentService deployer,
        FunctionHost host,
        FunctionBundleBuilder builder,
        AuditLog audit,
        ILogger<FunctionDeploymentService> logger)
    {
        _paths = paths;
        _sites = sites;
        _deployer = deployer;
        _host = host;
        _builder = builder;
        _audit = audit;
        _logger = logger;
    }

    // ---------------------------------------------------------------- deploying

    /// <summary>
    /// Builds and makes live a set of function files.
    ///
    /// With <paramref name="replace"/> the upload is the whole set, and live files it leaves out
    /// are gone. Without it the upload is merged into what is live: a file with the same name is
    /// replaced and every other live file is carried over. Either way the result is compiled as
    /// one complete set, so a merge is checked exactly as thoroughly as a replace.
    /// </summary>
    public async Task<FunctionDeployResult> DeployAsync(
        string? domain, IReadOnlyList<FunctionFile> files, string actor, string source, bool replace,
        CancellationToken ct = default)
    {
        IReadOnlyList<string> kept = [];
        if (!replace)
        {
            var uploaded = files.Select(f => FunctionBundleBuilder.SafeFileName(f.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var carried = (await LiveFilesAsync(domain)).Where(f => !uploaded.Contains(f.Name)).ToList();

            kept = carried.Select(f => f.Name).ToList();
            files = [.. carried, .. files];
        }

        var result = await DeployCoreAsync(domain, files, actor, source, ct);
        return result.Ok ? result with { Kept = kept } : result;
    }

    private async Task<FunctionDeployResult> DeployCoreAsync(
        string? domain, IReadOnlyList<FunctionFile> files, string actor, string source, CancellationToken ct)
    {
        if (domain is null) return await DeployGlobalAsync(files, actor, source, ct);

        var site = _sites.TryGet(domain);
        if (site?.Current is null)
            return FunctionDeployResult.Failed("Functions attach to the live release, and this site has none yet. Deploy its content first.");

        var built = await _builder.BuildAsync(_paths.FunctionsDir(domain), files, actor, source, ct);
        if (!built.Ok) return built;

        var bundle = built.Bundle!;
        var (ok, error) = await _deployer.SetFunctionsAsync(domain, bundle);
        if (!ok)
        {
            TryDeleteDirectory(_paths.FunctionBundleDir(domain, bundle.Id));
            return FunctionDeployResult.Failed(error!);
        }

        await _audit.WriteAsync("site.functions.deploy", actor,
            new { domain, bundle = bundle.Id, files = bundle.Files, routes = bundle.Routes.Count });
        return built;
    }

    /// <summary>
    /// Takes one file out of the live set by building and deploying the rest, in one step. A
    /// bundle is a compiled whole, so there is no such thing as a file that is deleted but still
    /// running; and if another file depended on this one, the build fails and nothing changes.
    /// </summary>
    public async Task<FunctionDeployResult> RemoveFileAsync(
        string? domain, string fileName, string actor, string source, CancellationToken ct = default)
    {
        var files = await LiveFilesAsync(domain);
        if (files.Count == 0) return FunctionDeployResult.Failed("No functions are deployed here.");

        var remaining = files.Where(f => !f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (remaining.Count == files.Count) return FunctionDeployResult.Failed($"{fileName} is not one of the live files.");
        if (remaining.Count == 0)
            return FunctionDeployResult.Failed($"{fileName} is the only file. Remove the functions altogether instead.");

        return await DeployAsync(domain, remaining, actor, source, replace: true, ct);
    }

    public async Task<(bool Ok, string? Error)> RemoveAsync(string? domain, string actor)
    {
        if (domain is null)
        {
            if ((await GlobalAsync()).Current is null) return (false, "No global functions are deployed.");

            await ReplaceGlobalAsync(null);
            await _audit.WriteAsync("functions.remove", actor, new { });
            return (true, null);
        }

        var (ok, error) = await _deployer.SetFunctionsAsync(domain, null);
        if (ok) await _audit.WriteAsync("site.functions.remove", actor, new { domain });
        return (ok, error);
    }

    /// <summary>The data directory handlers in this scope receive.</summary>
    public string DataDirectory(string? domain) =>
        domain is null ? _paths.GlobalFunctionsDataDir : _paths.SiteDataDir(domain);

    public async Task<GlobalFunctionsRecord> GlobalAsync() => await _host.GlobalStore.ReadAsync();

    /// <summary>The bundle live in a scope, if any.</summary>
    public async Task<FunctionBundle?> LiveAsync(string? domain) =>
        domain is null ? (await GlobalAsync()).Current : _sites.TryGet(domain)?.CurrentFunctions;

    private async Task<FunctionDeployResult> DeployGlobalAsync(
        IReadOnlyList<FunctionFile> files, string actor, string source, CancellationToken ct)
    {
        var built = await _builder.BuildAsync(_paths.GlobalFunctionsDir, files, actor, source, ct);
        if (!built.Ok) return built;

        var bundle = built.Bundle!;
        await ReplaceGlobalAsync(bundle);

        await _audit.WriteAsync("functions.deploy", actor,
            new { bundle = bundle.Id, files = bundle.Files, routes = bundle.Routes.Count });
        return built;
    }

    private async Task ReplaceGlobalAsync(FunctionBundle? bundle)
    {
        var keep = await _host.GlobalStore.UpdateAsync(record =>
        {
            record.PreviousId = record.Current?.Id;
            record.Current = bundle;
            return (new[] { record.Current?.Id, record.PreviousId }, true);
        });

        _host.EvictGlobal();

        // Everything but the live bundle and the one before it: requests that started on the
        // previous bundle may still need to load assemblies out of its directory. Drafts live
        // in a dot-directory and are left to their own expiry.
        if (!Directory.Exists(_paths.GlobalFunctionsDir)) return;
        foreach (var dir in Directory.EnumerateDirectories(_paths.GlobalFunctionsDir))
        {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith('.') && !keep.Contains(name)) TryDeleteDirectory(dir);
        }
    }

    // ---------------------------------------------------------------- drafts

    /// <summary>
    /// Builds files exactly as a deploy would, without making them live. The draft is kept for a
    /// while so the editor can run requests against it before deciding to deploy.
    /// </summary>
    public async Task<FunctionDeployResult> CheckAsync(
        string? domain, IReadOnlyList<FunctionFile> files, string actor, CancellationToken ct = default)
    {
        var drafts = DraftsDir(domain);
        RemoveExpiredDrafts(drafts);
        return await _builder.BuildAsync(drafts, files, actor, "editor", ct);
    }

    /// <summary>
    /// The bin directory a test should run against: "live" for the bundle serving now, or the id
    /// of a draft from <see cref="CheckAsync"/>. Null when there is no such build.
    /// </summary>
    public async Task<string?> ResolveBuildAsync(string? domain, string? target)
    {
        string? bundleDir;

        if (target is null or "live")
        {
            var live = await LiveAsync(domain);
            bundleDir = live is null ? null : BundleDir(domain, live.Id);
        }
        else
        {
            bundleDir = FunctionBundleBuilder.IsValidId(target) ? Path.Combine(DraftsDir(domain), target) : null;
        }

        var bin = bundleDir is null ? null : DataPaths.FunctionBinDir(bundleDir);
        return bin is not null && Directory.Exists(bin) ? bin : null;
    }

    private string DraftsDir(string? domain) =>
        Path.Combine(domain is null ? _paths.GlobalFunctionsDir : _paths.FunctionsDir(domain), DraftsDirName);

    private void RemoveExpiredDrafts(string drafts)
    {
        if (!Directory.Exists(drafts)) return;

        foreach (var dir in Directory.EnumerateDirectories(drafts))
        {
            if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > DraftLifetime) TryDeleteDirectory(dir);
        }
    }

    // ---------------------------------------------------------------- source

    /// <summary>The live bundle's files, exactly as uploaded, for the editor.</summary>
    public async Task<IReadOnlyList<FunctionFile>> LiveFilesAsync(string? domain)
    {
        var live = await LiveAsync(domain);
        if (live is null) return [];

        var sourceDir = DataPaths.FunctionSourceDir(BundleDir(domain, live.Id));
        var files = new List<FunctionFile>();

        foreach (var name in live.Files)
        {
            var path = Path.Combine(sourceDir, name);
            if (File.Exists(path)) files.Add(new FunctionFile(name, await File.ReadAllTextAsync(path)));
        }

        return files;
    }

    public async Task<FunctionSourceDownload?> DownloadAsync(string? domain)
    {
        var files = await LiveFilesAsync(domain);

        if (files.Count == 0) return null;
        if (files.Count == 1)
            return new FunctionSourceDownload(files[0].Name, "text/plain; charset=utf-8", System.Text.Encoding.UTF8.GetBytes(files[0].Text));

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                await using var entry = zip.CreateEntry(file.Name).Open();
                await entry.WriteAsync(System.Text.Encoding.UTF8.GetBytes(file.Text));
            }
        }

        // Named as the folder it would sit in inside a site's zip, so it can go straight back in.
        return new FunctionSourceDownload($"{domain ?? "global"}-_functions.zip", "application/zip", buffer.ToArray());
    }

    private string BundleDir(string? domain, string bundleId) =>
        domain is null ? _paths.GlobalFunctionBundleDir(bundleId) : _paths.FunctionBundleDir(domain, bundleId);

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
