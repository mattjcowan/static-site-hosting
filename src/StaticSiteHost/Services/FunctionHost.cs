using System.Collections.Concurrent;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services;

/// <summary>
/// The loaded functions, one set per site plus the global one, and the entry point requests
/// go through.
///
/// Nothing is loaded ahead of time. A site's set is loaded on its first request and keyed by
/// the bin directory it came from, so whatever moves that directory or changes which bundle is
/// live — a deploy, a rollback, a rename, a restart — is picked up by comparing one string on
/// the next request, and the superseded set is unloaded then. There is no reload step for
/// callers to forget.
/// </summary>
public sealed class FunctionHost
{
    /// <summary>Key of the global set; a domain is never empty.</summary>
    private const string GlobalKey = "";

    /// <param name="Set">Null when the bundle failed to load, so it is not retried per request.</param>
    private sealed record Loaded(string BinDir, FunctionSet? Set);

    private readonly ConcurrentDictionary<string, Loaded> _loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long an unloaded build may linger before it is worth a warning.</summary>
    private static readonly TimeSpan LingerWarning = TimeSpan.FromMinutes(5);

    /// <summary>Builds unloaded but not yet collected, watched so a leak shows up in the log rather than only in memory.</summary>
    private readonly List<(WeakReference Probe, string Owner, DateTime UnloadedUtc)> _unloading = [];
    private readonly Lock _loadGate = new();
    private readonly DataPaths _paths;
    private readonly JsonFileStore<GlobalFunctionsRecord> _global;
    private readonly ILogger<FunctionHost> _logger;

    public FunctionHost(DataPaths paths, ILogger<FunctionHost> logger)
    {
        _paths = paths;
        _logger = logger;
        _global = new JsonFileStore<GlobalFunctionsRecord>(paths.GlobalFunctionsFile);
    }

    /// <summary>The record behind the global functions. Owned here so there is one cache of it.</summary>
    internal JsonFileStore<GlobalFunctionsRecord> GlobalStore => _global;

    /// <summary>
    /// Answers the request from the site's functions, then the global ones. Returns false when
    /// no function's path matches, so the caller serves static content as usual.
    /// </summary>
    public async Task<bool> TryHandleAsync(HttpContext context, SiteRecord site)
    {
        if (ForSite(site) is { } siteSet &&
            await DispatchAsync(siteSet, context, site.Domain, _paths.SiteDataDir(site.Domain))) return true;

        var global = (await _global.ReadAsync()).Current;
        return global is not null &&
               Get(GlobalKey, DataPaths.FunctionBinDir(_paths.GlobalFunctionBundleDir(global.Id))) is { } globalSet &&
               await DispatchAsync(globalSet, context, "global", _paths.GlobalFunctionsDataDir);
    }

    private FunctionSet? ForSite(SiteRecord site)
    {
        if (site.CurrentFunctions is not { } bundle)
        {
            Evict(site.Domain);
            return null;
        }

        return Get(site.Domain, DataPaths.FunctionBinDir(_paths.FunctionBundleDir(site.Domain, bundle.Id)));
    }

    private async Task<bool> DispatchAsync(FunctionSet set, HttpContext context, string owner, string dataDir)
    {
        context.Items[FunctionRouter.DataDirectoryItem] = dataDir;

        try
        {
            return await set.Router.TryDispatchAsync(context);
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogError(ex, "A function for {Owner} failed on {Method} {Path}",
                owner, context.Request.Method, context.Request.Path);

            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("The function failed. The details are in the server log.");
            }

            return true;
        }
    }

    private FunctionSet? Get(string key, string binDir)
    {
        if (_loaded.TryGetValue(key, out var loaded) && loaded.BinDir == binDir) return loaded.Set;

        lock (_loadGate)
        {
            if (_loaded.TryGetValue(key, out loaded) && loaded.BinDir == binDir) return loaded.Set;

            FunctionSet? set = null;
            try
            {
                set = FunctionSet.Load(binDir, key == GlobalKey ? "functions:global" : $"functions:{key}");
                _logger.LogInformation("Loaded {Count} function route(s) for {Owner} from {Dir}",
                    set.Router.Routes.Count, key == GlobalKey ? "global" : key, binDir);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not load the functions in {Dir}; they will not answer until redeployed", binDir);
            }

            _loaded[key] = new Loaded(binDir, set);
            if (loaded?.Set is { } previous) Retire(previous, key);
            return set;
        }
    }

    /// <summary>Unloads a site's set now rather than on its next request. For deletes and renames.</summary>
    public void Evict(string domain)
    {
        if (_loaded.TryRemove(domain, out var loaded) && loaded.Set is { } set) Retire(set, domain);
    }

    private void Retire(FunctionSet set, string key)
    {
        var owner = key == GlobalKey ? "global" : key;
        var probe = set.UnloadProbe();
        var released = set.Unload();

        if (released.Count > 0)
            _logger.LogInformation("Released {Count} process-wide hook(s) left by {Owner}'s functions: {Hooks}",
                released.Count, owner, string.Join("; ", released));

        lock (_unloading)
        {
            // Anything still alive long after its unload is holding memory until restart; say so once.
            _unloading.RemoveAll(entry =>
            {
                if (!entry.Probe.IsAlive) return true;
                if (DateTime.UtcNow - entry.UnloadedUtc < LingerWarning) return false;

                _logger.LogWarning(
                    "Functions for {Owner} unloaded at {When:u} are still in memory, so something outside them still " +
                    "refers into them. The memory is reclaimed on restart.", entry.Owner, entry.UnloadedUtc);
                return true;
            });

            _unloading.Add((probe, owner, DateTime.UtcNow));
        }
    }

    public void EvictGlobal() => Evict(GlobalKey);
}
