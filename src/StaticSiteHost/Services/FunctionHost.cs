using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using StaticSiteHost.Functions;
using StaticSiteHost.Models;
using StaticSiteHost.Services.Ai;
using StaticSiteHost.Services.Realtime;

namespace StaticSiteHost.Services;

/// <summary>
/// What a site's access hook answered (see <see cref="FunctionAccessHooks"/>), from
/// <see cref="FunctionHost.InvokeRealtimeConnectAsync"/> and its siblings. Plain values only, so
/// the caller holds nothing of the functions.
/// </summary>
/// <param name="Declared">
/// The site's live functions declare the hook. When they do not, nothing was asked, and
/// <paramref name="Allowed"/> is true: the caller's own rule applies, which for realtime is to let
/// everyone in and for AI is the site's "let every visitor chat" setting.
/// </param>
/// <param name="Allowed">The hook let the request through. False when it refused, threw, or could not be run.</param>
/// <param name="User">Who a realtime connection belongs to, when the connect hook answered with one.</param>
/// <param name="Error">Why the hook gave no answer, when it threw or the functions could not be loaded. Already logged.</param>
public sealed record FunctionHookResult(bool Declared, bool Allowed, string? User = null, string? Error = null)
{
    /// <summary>The site declares no such hook.</summary>
    public static readonly FunctionHookResult NotDeclared = new(Declared: false, Allowed: true);
}

/// <summary>
/// The loaded functions, one set per site plus the global one, and the entry point requests
/// go through.
///
/// A request to a site runs, in order: the global middleware, the site's middleware, the
/// site's handlers, the global handlers, and last the site's files. Middleware wraps
/// everything after it, so a global gate covers a site's own functions and files too; a
/// handler whose path matches ends the request, and a path no handler matches carries on to
/// the next step. Each set runs with its own data directory, its own <see cref="ISite"/> in
/// <c>HttpContext.Items</c>, and its own scope of its services, made as the request enters the
/// set and disposed when the request ends. The data directory and site are switched as the
/// request enters a set, and switched back when it returns into a set's middleware, so code
/// after <c>await next()</c> sees its own set's again.
///
/// A set is keyed by the bin directory it came from, so whatever moves that directory or
/// changes which bundle is live (a deploy, a rollback, a rename, a restart) is picked up by
/// comparing one string. Most functions are loaded on their first request. Functions with work
/// of their own, background services or jobs, cannot wait for one: they are loaded as soon as
/// the server is listening (<see cref="WarmUpAsync"/>) and straight after anything that changes
/// them (<see cref="RefreshAsync"/>), which deploys, rollbacks, renames and functions changes
/// call. Every change is swap-then-retire: the new set is published first, so new requests go
/// there, and the old one retires in the background in a fixed order (see
/// <see cref="FunctionSet"/>): stop its background work, let its requests finish, dispose its
/// services, unload it. Only the bundle the records name now is ever loaded: whoever asks for a
/// set says which bin directory it read, and the records are read again under the scope's gate
/// before anything loads, so a caller that read them just before a change can never bring back the
/// set that change retired. A delete and a rename, which remove or move the site's directory, wait
/// for its functions to be gone first (<see cref="EvictAsync"/>), and a bundle directory that old
/// releases no longer need is deleted only once nothing loaded from it is left
/// (<see cref="DeleteWhenRetired"/>).
///
/// A set that fails to load, or is refused, is remembered as failed for its bin directory and
/// not retried per request; <see cref="StatusAsync"/> reports why. What happens to the site
/// meanwhile depends on what the functions were for. Without middleware, the failure is logged
/// and the site's files are served as usual. With middleware, the site answers <c>503</c>
/// instead, until the functions are fixed: middleware is how a site gates a section or a
/// whole site behind a sign-in, and a gate that fails open is worse than a site that is down,
/// because nobody notices the one and everyone notices the other. The global functions'
/// middleware covers every site, so when it fails every site answers <c>503</c>.
///
/// The site's access hooks, which decide who may connect to its realtime hub, join a group or
/// chat through its AI, run through here too (<see cref="InvokeRealtimeConnectAsync"/>,
/// <see cref="InvokeRealtimeJoinAsync"/>, <see cref="InvokeAiAccessAsync"/>), entering the site's
/// set as a request does. They fail closed for the same reason middleware does: functions that
/// declare a hook but cannot be loaded, or a hook that throws, refuse. Only the site's own
/// functions are asked; the global functions' hooks never decide for a site.
/// </summary>
public sealed class FunctionHost
{
    /// <summary>Key of the global set; a domain is never empty.</summary>
    private const string GlobalKey = "";

    /// <summary>How long an unloaded build may linger before it is worth a warning.</summary>
    private static readonly TimeSpan LingerWarning = TimeSpan.FromMinutes(5);

    /// <summary>How long a retiring set's background services and jobs have to stop once asked.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(15);

    /// <summary>How long the requests already inside a retiring set have to finish.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(30);

    /// <summary>This server's StaticSiteHost.Abstractions as numbers; null for a development build, which refuses nothing.</summary>
    private static readonly (int, int, int)? HostAbstractions =
        ParseVersion(FunctionProjectGenerator.HostAbstractionsVersion) is { } host && host != (0, 0, 0) ? host : null;

    /// <param name="Set">Null when the bundle failed to load or was refused, so it is not retried per request.</param>
    /// <param name="Error">Why, when <paramref name="Set"/> is null, in a sentence.</param>
    /// <param name="Utc">When it was loaded, or failed to be.</param>
    /// <param name="Gated">
    /// The build has middleware, found or refused, whatever its record says. A bundle built before
    /// the server knew <c>[Middleware]</c> records none, and when its middleware cannot run the site
    /// must still fail closed.
    /// </param>
    private sealed record Loaded(string BinDir, FunctionSet? Set, string? Error, DateTimeOffset Utc, bool Gated = false);

    /// <summary>One set as a request runs it: whose it is, and the data directory and site it sees.</summary>
    private sealed record Scope(FunctionSet Set, string Owner, string DataDir, SiteContext Site);

    private readonly ConcurrentDictionary<string, Loaded> _loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The bin directory each scope is loading from right now, for <see cref="StatusAsync"/>.</summary>
    private readonly ConcurrentDictionary<string, string> _loading = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One gate per scope for loading and replacing its set, so one site's slow
    /// <c>[ConfigureServices]</c> holds up only that site's requests, and those wait for it without
    /// holding a thread.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Domains whose functions are being evicted (<see cref="EvictAsync"/>), with how many evictions hold each, so nothing loads them meanwhile.</summary>
    private readonly ConcurrentDictionary<string, int> _closed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Guards <see cref="_retiring"/> and <see cref="_pendingDeletes"/>.</summary>
    private readonly Lock _retireGate = new();

    /// <summary>
    /// Every set still retiring, with its scope and a task that completes once it is unloaded, so an
    /// eviction can wait for them and a pruned bundle directory is deleted only once nothing loaded
    /// from it is left. An entry goes as its set is unloaded, so nothing here outlives the set.
    /// </summary>
    private readonly Dictionary<FunctionSet, (string Key, Task Retired)> _retiring = [];

    /// <summary>Bundle directories to delete once no set loaded from them is left (<see cref="DeleteWhenRetired"/>).</summary>
    private readonly HashSet<string> _pendingDeletes = new(StringComparer.Ordinal);

    /// <summary>Builds unloaded but not yet collected, watched so a leak shows up in the log rather than only in memory.</summary>
    private readonly List<(WeakReference Probe, string Owner, DateTime UnloadedUtc)> _unloading = [];
    private readonly DataPaths _paths;
    private readonly SiteStore _sites;
    private readonly SiteVariableService _variables;
    private readonly SiteRealtimeFactory _realtime;
    private readonly SiteAiChatFactory _ai;
    private readonly ILoggerFactory _loggers;
    private readonly IDataProtectionProvider _protection;
    private readonly JsonFileStore<GlobalFunctionsRecord> _global;
    private readonly ILogger _jobLog;
    private readonly ILogger<FunctionHost> _logger;

    /// <summary>Set once the server is stopping, after which loaded functions answer but start no background work.</summary>
    private volatile bool _shuttingDown;

    public FunctionHost(
        DataPaths paths,
        SiteStore sites,
        SiteVariableService variables,
        SiteRealtimeFactory realtime,
        SiteAiChatFactory ai,
        ILoggerFactory loggers,
        IDataProtectionProvider protection,
        ILogger<FunctionHost> logger)
    {
        _paths = paths;
        _sites = sites;
        _variables = variables;
        _realtime = realtime;
        _ai = ai;
        _loggers = loggers;
        _protection = protection;
        _logger = logger;
        _jobLog = loggers.CreateLogger<FunctionJobRunner>();
        _global = new JsonFileStore<GlobalFunctionsRecord>(paths.GlobalFunctionsFile);
    }

    /// <summary>The record behind the global functions. Owned here so there is one cache of it.</summary>
    internal JsonFileStore<GlobalFunctionsRecord> GlobalStore => _global;

    // ---------------------------------------------------------------- requests

    /// <summary>
    /// Answers a request to <paramref name="site"/> through its functions and the global ones,
    /// in the order the class remarks give, with <paramref name="serveContent"/> as the last
    /// step for whatever they pass on. With no functions anywhere it is just
    /// <paramref name="serveContent"/>.
    /// </summary>
    public async Task HandleAsync(HttpContext context, SiteRecord site, Func<Task> serveContent)
    {
        var siteBundle = site.CurrentFunctions;
        var globalBundle = (await _global.ReadAsync()).Current;

        if (siteBundle is null) Evict(site.Domain);

        if (siteBundle is null && globalBundle is null)
        {
            await serveContent();
            return;
        }

        FunctionSet.ServiceScope? siteEntry = null;
        FunctionSet.ServiceScope? globalEntry = null;
        try
        {
            if (siteBundle is not null) (siteEntry, siteBundle) = await EnterLiveAsync(site.Domain, siteBundle);
            if (globalBundle is not null) (globalEntry, globalBundle) = await EnterLiveAsync(GlobalKey, globalBundle);

            // Fail closed: see the class remarks.
            if ((siteEntry is null && IsGated(site.Domain, siteBundle)) || (globalEntry is null && IsGated(GlobalKey, globalBundle)))
            {
                await RefuseAsync(context);
                return;
            }

            if (siteEntry is null && globalEntry is null)
            {
                await serveContent();
                return;
            }

            // Both sets see the site the request is for, global functions included, with its
            // realtime side and AI; only the data directory and the services differ between them.
            var variables = _variables.Resolve(site);
            context.Items[FunctionRouter.VariablesItem] = variables.All;
            var (realtime, ai) = (_realtime.Create(site), _ai.Create(site));

            var siteScope = siteEntry is null ? null
                : NewScope(siteEntry, site.Domain, _paths.SiteDataDir(site.Domain), site, variables, realtime, ai);
            var globalScope = globalEntry is null ? null
                : NewScope(globalEntry, SiteContext.GlobalDomain, _paths.GlobalFunctionsDataDir, site, variables, realtime, ai);

            // Built from the inside out. A set without middleware adds no step.
            Func<Task> handlers = () => HandlersAsync(context, siteScope, globalScope, serveContent);
            var pipeline = WithMiddleware(context, globalScope, WithMiddleware(context, siteScope, handlers));

            await pipeline();
        }
        finally
        {
            await LeaveAsync(siteEntry, site.Domain);
            await LeaveAsync(globalEntry, SiteContext.GlobalDomain);
        }
    }

    /// <summary>
    /// The live set for a scope, entered, and the bundle it was entered for. <paramref name="bundle"/>
    /// is what the caller read from the records a moment ago. When nothing could be entered and the
    /// records have since moved on to another bundle (a deploy, a rollback, a change of functions),
    /// the bundle live now is tried once instead. Otherwise the answer keeps the bundle first read,
    /// so its middleware still decides whether the request fails closed; that includes a site that has
    /// just been deleted.
    /// </summary>
    private async ValueTask<(FunctionSet.ServiceScope? Entry, FunctionBundle? Bundle)> EnterLiveAsync(string key, FunctionBundle bundle)
    {
        if (await EnterAsync(key, BinDirOf(key, bundle)) is { } entry) return (entry, bundle);

        var (exists, now) = await LiveBundleAsync(key);
        if (!exists || now?.Id == bundle.Id) return (null, bundle);
        if (now is null) return (null, null);

        return (await EnterAsync(key, BinDirOf(key, now)), now);
    }

    /// <summary>
    /// The live set for a scope's bin directory, entered: loaded first if need be, and null when
    /// there is none that loads. A set can begin retiring between being looked up and entered, and
    /// by then its successor is already published, so it is looked up again. A third miss means the
    /// scope is being replaced over and over, and this request goes without.
    /// </summary>
    private async ValueTask<FunctionSet.ServiceScope?> EnterAsync(string key, string binDir)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await GetAsync(key, binDir) is not { } set) return null;
            if (set.TryEnter() is { } entry) return entry;
        }

        return null;
    }

    /// <summary>
    /// True when a request that could not enter the scope's functions must be refused rather than
    /// served without them: their record lists middleware, or the build turned out to have
    /// middleware that could not run.
    /// </summary>
    private bool IsGated(string key, FunctionBundle? bundle) =>
        bundle is not null &&
        (bundle.Middleware.Count > 0 ||
         (_loaded.TryGetValue(key, out var loaded) && loaded.BinDir == BinDirOf(key, bundle) && loaded.Gated));

    private async Task LeaveAsync(FunctionSet.ServiceScope? entry, string owner)
    {
        if (entry is null) return;

        try
        {
            await entry.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Disposing a request's services from {Owner}'s functions threw", owner);
        }
    }

    private static Scope NewScope(
        FunctionSet.ServiceScope entry, string owner, string dataDir, SiteRecord site, ResolvedVariables variables,
        IRealtime realtime, IAiChat ai) =>
        new(entry.Set, owner, dataDir,
            new SiteContext(site.Domain, dataDir, variables, entry.Services, realtime, ai, set: entry.Set.Site));

    private static Task RefuseAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync(
            "This site's functions could not be loaded, so it is not being served until they are. The details are in the server log.");
    }

    private async Task HandlersAsync(HttpContext context, Scope? siteScope, Scope? globalScope, Func<Task> serveContent)
    {
        if (siteScope is not null && await DispatchAsync(context, siteScope)) return;
        if (globalScope is not null && await DispatchAsync(context, globalScope)) return;

        await serveContent();
    }

    private Func<Task> WithMiddleware(HttpContext context, Scope? scope, Func<Task> next) =>
        scope is null || scope.Set.Router.Middleware.Count == 0 ? next : () => RunMiddlewareAsync(context, scope, next);

    private async Task RunMiddlewareAsync(HttpContext context, Scope scope, Func<Task> next)
    {
        // What the rest of the request throws is not this set's to report: the other set reports
        // its own, and anything else, from serving a file or a dropped connection, goes on up as
        // it always has. It is recognised by identity, so middleware that catches it and throws
        // something of its own is still the one blamed.
        Exception? passedThrough = null;

        Enter(context, scope);
        try
        {
            await scope.Set.Router.InvokeMiddlewareAsync(context, async () =>
            {
                try
                {
                    await next();
                }
                catch (Exception ex)
                {
                    passedThrough = ex;
                    throw;
                }
                finally
                {
                    // The other set may have run in there; this set's middleware carries on with its own.
                    Enter(context, scope);
                }
            });
        }
        catch (Exception ex) when (ex != passedThrough && !context.RequestAborted.IsCancellationRequested)
        {
            await FailAsync(context, scope, "Middleware", ex);
        }
    }

    /// <summary>True when one of the set's handlers answered, or it failed trying.</summary>
    private async Task<bool> DispatchAsync(HttpContext context, Scope scope)
    {
        Enter(context, scope);
        try
        {
            return await scope.Set.Router.TryDispatchAsync(context);
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            await FailAsync(context, scope, "A function", ex);
            return true;
        }
    }

    /// <summary>Makes <paramref name="scope"/>'s data directory and site the ones its code sees.</summary>
    private static void Enter(HttpContext context, Scope scope)
    {
        context.Items[FunctionRouter.DataDirectoryItem] = scope.DataDir;
        context.Items[SiteHttpContextExtensions.ItemKey] = scope.Site;
    }

    private async Task FailAsync(HttpContext context, Scope scope, string what, Exception ex)
    {
        _logger.LogError(ex, "{What} for {Owner} failed on {Method} {Path}",
            what, scope.Owner, context.Request.Method, context.Request.Path);

        if (context.Response.HasStarted) return;

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("The function failed. The details are in the server log.");
    }

    // ---------------------------------------------------------------- loading

    /// <summary>
    /// The live set for a scope, loaded if need be, which retires the one before.
    ///
    /// <paramref name="binDir"/> is the bundle the caller read from the records, which by the time
    /// it gets here may no longer be live: a request that read them just before a deploy, a hub
    /// handshake finishing after a delete, a warm-up working through a list made before either. So
    /// under the scope's gate the records are read again, and only the bundle live now is ever
    /// loaded, and only its load replaces the set in use. For a bin directory that is no longer live
    /// the answer is the set already loaded for the one that is, or null; so is it for a domain
    /// being evicted (<see cref="EvictAsync"/>). The records are read once more after loading, which
    /// can take a while, and a set they have moved on from meanwhile is unloaded unused.
    /// </summary>
    private async ValueTask<FunctionSet?> GetAsync(string key, string binDir)
    {
        if (_loaded.TryGetValue(key, out var loaded) && loaded.BinDir == binDir) return loaded.Set;

        var gate = GateFor(key);
        await gate.WaitAsync();
        try
        {
            loaded = _loaded.GetValueOrDefault(key);
            if (loaded is not null && loaded.BinDir == binDir) return loaded.Set;

            if (_closed.ContainsKey(key)) return null;

            var (_, live) = await LiveBundleAsync(key);
            var liveBinDir = live is null ? null : BinDirOf(key, live);
            if (live is null || liveBinDir != binDir)
                return loaded is not null && loaded.BinDir == liveBinDir ? loaded.Set : null;

            var owner = OwnerOf(key);

            _loading[key] = binDir;
            try
            {
                var (set, error, gated) = Load(key, live, binDir);

                if (_closed.ContainsKey(key) || (await LiveBundleAsync(key)).Bundle?.Id != live.Id)
                {
                    if (set is not null)
                    {
                        _logger.LogInformation(
                            "Did not keep the functions just loaded for {Owner} from {Dir}: while they loaded, they stopped being live",
                            owner, binDir);
                        Unload(set, owner);
                    }

                    return null;
                }

                // Published before the old set retires, so a request that finds the old one retiring finds this on a second look.
                _loaded[key] = new Loaded(binDir, set, error, DateTimeOffset.UtcNow, gated);
                if (set is not null) Start(set, owner, binDir);
                if (loaded?.Set is { } previous) Retire(previous, key);

                return set;
            }
            finally
            {
                _loading.TryRemove(key, out _);
            }
        }
        finally
        {
            gate.Release();
            DeletePendingIfFree();
        }
    }

    /// <summary>
    /// Loads a bundle for live use: refuses one built for a newer server, loads the build and
    /// builds its services, but starts none of its background work (<see cref="Start"/> does, once
    /// it is published). Never throws; a failure comes back as a sentence for the status, and is logged.
    /// </summary>
    /// <returns>The set or why not, and whether the build has middleware, found or refused (see <see cref="Loaded.Gated"/>).</returns>
    private (FunctionSet? Set, string? Error, bool Gated) Load(string key, FunctionBundle bundle, string binDir)
    {
        var owner = OwnerOf(key);

        if (IsNewerThanHostAbstractions(bundle.AbstractionsVersion))
        {
            _logger.LogError(
                "The functions for {Owner} in {Dir} were built against StaticSiteHost.Abstractions {Version}, newer " +
                "than the {HostVersion} this server runs, so they are not loaded and will not answer. Redeploy them " +
                "to build them against this server's copy, or run the newer server again.",
                owner, binDir, bundle.AbstractionsVersion, FunctionProjectGenerator.HostAbstractionsVersion);
            return (null,
                $"They were compiled against StaticSiteHost.Abstractions {bundle.AbstractionsVersion}, newer than the " +
                $"{FunctionProjectGenerator.HostAbstractionsVersion} this server runs, so redeploy them to compile them " +
                "against this server's copy.", false);
        }

        FunctionSet set;
        try
        {
            set = FunctionSet.Load(binDir, $"functions:{owner}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load the functions in {Dir}; they will not answer until redeployed", binDir);
            return (null, $"The build could not be loaded: {ex.GetBaseException().Message}", false);
        }

        // Only a bundle built before this server knew the attribute can carry one of these; the build refuses them now.
        foreach (var problem in set.Router.MiddlewareProblems)
            _logger.LogError("In {Owner}'s functions: {Problem}", owner, problem);
        foreach (var problem in set.Jobs.Problems)
            _logger.LogWarning("In {Owner}'s functions: {Problem} It does not run.", owner, problem);
        foreach (var problem in set.Hooks.Problems)
            _logger.LogWarning("In {Owner}'s functions: {Problem} It is not asked.", owner, problem);

        var router = set.Router;
        var gated = router.Middleware.Count > 0 || router.MiddlewareProblems.Count > 0;

        // Middleware is how a site gates itself, so a build whose middleware is not all there is not
        // run at all: see the class remarks. Either some of it cannot run, or the build has less of it
        // than its record, which would mean it is not the build that was checked.
        if (router.MiddlewareProblems.Count > 0 || router.Middleware.Count < bundle.Middleware.Count)
        {
            var message = router.MiddlewareProblems.Count > 0
                ? $"Their middleware cannot all run, so none of the functions do: {string.Join(" ", router.MiddlewareProblems)} " +
                  "Deploy them again once that is fixed."
                : $"The build has {router.Middleware.Count} middleware method(s) where its record lists {bundle.Middleware.Count}, " +
                  "so it is not the build that was checked. Deploy them again.";
            _logger.LogError(
                "The functions for {Owner} in {Dir} did not load: {Message} Until they do, the site answers 503 rather than " +
                "going ungated.", owner, binDir, message);

            Unload(set, owner);
            return (null, message, true);
        }

        var dataDir = key == GlobalKey ? _paths.GlobalFunctionsDataDir : _paths.SiteDataDir(key);
        var site = SiteContext.ForSet(key == GlobalKey ? null : key, dataDir, _sites, _variables, _realtime, _ai);

        try
        {
            set.BuildServices(new FunctionSetEnvironment(owner, site, _loggers, _protection));
        }
        catch (Exception ex)
        {
            var message = ex is FunctionLoadException ? ex.Message : $"Their services could not be set up: {ex.GetBaseException().Message}";
            _logger.LogError(ex,
                "The functions for {Owner} in {Dir} did not load: {Message} They will not answer until this is fixed and " +
                "they are deployed again.", owner, binDir, message);

            Unload(set, owner);
            return (null, message, gated);
        }

        return (set, null, gated);
    }

    /// <summary>Starts a published set's background work, unless the server is stopping.</summary>
    private void Start(FunctionSet set, string owner, string binDir)
    {
        if (_shuttingDown)
        {
            _logger.LogInformation("The server is stopping, so the background work of {Owner}'s functions is not started", owner);
        }
        else
        {
            set.Start(_jobLog);
        }

        _logger.LogInformation(
            "Loaded {Routes} route(s), {Middleware} middleware, {Background} background service(s), {Jobs} job(s) and " +
            "{Hooks} hook(s) for {Owner} from {Dir}",
            set.Router.Routes.Count, set.Router.Middleware.Count, set.Jobs.BackgroundServices.Count, set.Jobs.Scheduled.Count,
            set.Hooks.All.Count(), owner, binDir);
    }

    /// <summary>
    /// The bundle live for a scope according to the records now, and whether its site exists at all:
    /// false for a deleted or renamed-away domain. The global functions always exist.
    /// </summary>
    private async ValueTask<(bool Exists, FunctionBundle? Bundle)> LiveBundleAsync(string key)
    {
        if (key == GlobalKey) return (true, (await _global.ReadAsync()).Current);

        return _sites.TryGet(key) is { } site ? (true, site.CurrentFunctions) : (false, null);
    }

    // ---------------------------------------------------------------- access hooks

    /// <summary>
    /// Asks the site's <c>[RealtimeConnect]</c> hook whether the page connecting with
    /// <paramref name="http"/> may, and who it belongs to. See <see cref="InvokeHookAsync"/>.
    /// </summary>
    public Task<FunctionHookResult> InvokeRealtimeConnectAsync(SiteRecord site, HttpContext http) =>
        InvokeHookAsync(site, http, FunctionAccessHooks.Kind.RealtimeConnect, group: null);

    /// <summary>
    /// Asks the site's <c>[RealtimeJoin]</c> hook whether the page whose connection was made with
    /// <paramref name="http"/> may join <paramref name="group"/>. See <see cref="InvokeHookAsync"/>.
    /// </summary>
    public Task<FunctionHookResult> InvokeRealtimeJoinAsync(SiteRecord site, HttpContext http, string group) =>
        InvokeHookAsync(site, http, FunctionAccessHooks.Kind.RealtimeJoin, group);

    /// <summary>
    /// Asks the site's <c>[AiAccess]</c> hook whether the browser making <paramref name="http"/> may
    /// chat at <c>/_host/ai/chat</c>. See <see cref="InvokeHookAsync"/>.
    /// </summary>
    public Task<FunctionHookResult> InvokeAiAccessAsync(SiteRecord site, HttpContext http) =>
        InvokeHookAsync(site, http, FunctionAccessHooks.Kind.AiAccess, group: null);

    /// <summary>
    /// Runs one of the site's access hooks with <paramref name="http"/>, as a request to the site's
    /// functions would run: entering the site's set (loading it first if need be), with a scope of
    /// its services, the site's data directory, variables and <see cref="ISite"/> in the Items, and
    /// leaving it afterwards. Whether the hook is declared at all is read from the site's live
    /// bundle, as the Functions card shows it, and without one nothing is loaded or run.
    ///
    /// Fails closed: when the functions declare the hook but cannot be loaded, or the hook throws,
    /// the answer is a refusal, the reason is logged, and <see cref="FunctionHookResult.Error"/>
    /// carries it. Never throws.
    ///
    /// The Items and the User are put back exactly as they were, whatever the hook did to them. A
    /// realtime connection's HttpContext lives as long as the connection, which can outlive the
    /// functions by any number of deploys, so nothing of theirs may stay on it: not the
    /// <see cref="ISite"/> holding a scope of their services, not anything the hook stored there
    /// itself, and not a principal of its making that the next hook would take for the host's. What
    /// a hook registers on the context in other ways (<c>Response.OnCompleted</c>,
    /// <c>RegisterForDispose</c>, a feature) cannot be taken back, which is why the hook attributes
    /// tell authors not to.
    /// </summary>
    /// <param name="group">The join hook's group, bound to its <c>group</c> parameter; null for the others.</param>
    private async Task<FunctionHookResult> InvokeHookAsync(
        SiteRecord site, HttpContext http, FunctionAccessHooks.Kind kind, string? group)
    {
        var bundle = site.CurrentFunctions;
        if (bundle is null || !FunctionAccessHooks.Declares(bundle, kind)) return FunctionHookResult.NotDeclared;

        var domain = site.Domain;
        var label = FunctionAccessHooks.Label(kind);
        var items = new Dictionary<object, object?>(http.Items);
        var user = http.User;
        FunctionSet.ServiceScope? entry = null;

        try
        {
            (entry, _) = await EnterLiveAsync(domain, bundle);
            if (entry is null)
            {
                _logger.LogWarning(
                    "Refused a request to {Domain} that its {Hook} hook decides on: the site's functions could not be loaded, " +
                    "so there is nothing to ask. The Functions card on the site's page says why.", domain, label);
                return new FunctionHookResult(true, false,
                    Error: $"The site's functions declare {label} but could not be loaded, so nothing is let in until they are fixed.");
            }

            if (entry.Set.Hooks.Get(kind) is not { } method)
            {
                // Replaced, between reading the record and entering, by functions without the hook.
                if (!FunctionAccessHooks.Declares(_sites.TryGet(domain)?.CurrentFunctions, kind)) return FunctionHookResult.NotDeclared;

                _logger.LogError(
                    "Refused a request to {Domain}: its functions were built with a {Hook} hook, but the loaded build has none. " +
                    "Deploy them again.", domain, label);
                return new FunctionHookResult(true, false, Error: $"The loaded functions have no {label} method. Deploy them again.");
            }

            var variables = _variables.Resolve(site);
            var dataDir = _paths.SiteDataDir(domain);
            http.Items[FunctionRouter.DataDirectoryItem] = dataDir;
            http.Items[FunctionRouter.VariablesItem] = variables.All;
            http.Items[SiteHttpContextExtensions.ItemKey] = new SiteContext(
                domain, dataDir, variables, entry.Services, _realtime.Create(site), _ai.Create(site), set: entry.Set.Site);

            IReadOnlyDictionary<string, string> values = group is null
                ? FrozenDictionary<string, string>.Empty
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [FunctionAccessHooks.GroupParameter] = group };

            // The build refuses every parameter that could fail to bind, so this is belt and braces.
            if (!FunctionRouter.TryBindArguments(method, http, values, next: null, out var arguments, out var bindError))
                throw new InvalidOperationException(bindError);

            var answer = await FunctionRouter.UnwrapAsync(FunctionRouter.Invoke(method, arguments), method.ReturnType);
            var (allowed, who) = FunctionAccessHooks.ReadAnswer(answer);
            return new FunctionHookResult(true, allowed, kind == FunctionAccessHooks.Kind.RealtimeConnect ? who : null);
        }
        catch (Exception ex)
        {
            if (http.RequestAborted.IsCancellationRequested)
                _logger.LogDebug(ex, "The {Hook} hook of {Domain} was cut short: the request went away", label, domain);
            else
                _logger.LogError(ex, "The {Hook} hook of {Domain} failed, so the request it was asked about is refused", label, domain);

            return new FunctionHookResult(true, false, Error: $"{label} threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            http.Items.Clear();
            foreach (var (key, value) in items) http.Items[key] = value;
            http.User = user;

            await LeaveAsync(entry, domain);
        }
    }

    // ---------------------------------------------------------------- keeping up with the records

    /// <summary>
    /// Brings a scope's loaded set in line with its records now, rather than on its next
    /// request: after a deploy, a rollback, a rename, or its functions being deployed or
    /// removed. Functions with background work load straight away, and the set they replace
    /// retires; others only retire the old set, and load on their next request. Never throws:
    /// whatever goes wrong is logged, and the next request tries again.
    /// </summary>
    /// <param name="domain">The site, or null for the global functions.</param>
    public async Task RefreshAsync(string? domain)
    {
        var key = domain ?? GlobalKey;

        try
        {
            var (_, bundle) = await LiveBundleAsync(key);
            if (bundle is null)
            {
                Remove(key);
                return;
            }

            var binDir = BinDirOf(key, bundle);
            if (_loaded.TryGetValue(key, out var loaded) && loaded.BinDir == binDir) return;

            if (bundle.NeedsEagerLoad)
            {
                await GetAsync(key, binDir);
                return;
            }

            Loaded? stale = null;
            var gate = GateFor(key);
            await gate.WaitAsync();
            try
            {
                if (_loaded.TryGetValue(key, out var current) && current.BinDir != binDir) _loaded.TryRemove(key, out stale);
            }
            finally
            {
                gate.Release();
            }

            if (stale?.Set is { } set) Retire(set, key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not refresh the functions for {Owner}; the next request picks them up instead", OwnerOf(key));
        }
    }

    /// <summary>
    /// Loads every set of functions that has background work, the global one first, then each
    /// site's, one after another, so their jobs run without waiting for a request. A failure in
    /// one is logged and recorded in its status, and the rest carry on. <see cref="FunctionWarmUp"/>
    /// runs this once the server is listening. The list of sites is made up front, so by the time a
    /// site's turn comes it may have been deleted or given other functions; <see cref="GetAsync"/>
    /// reads the records again, and loads only what is live then.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        var (loaded, failed) = (0, 0);

        async Task LoadEarlyAsync(string key, FunctionBundle bundle)
        {
            _logger.LogInformation(
                "Loading {Owner}'s functions ahead of any request for their {Background} background service(s) and {Jobs} job(s)",
                OwnerOf(key), bundle.BackgroundServices.Count, bundle.Jobs.Count);

            var binDir = BinDirOf(key, bundle);
            if (await GetAsync(key, binDir) is not null) loaded++;
            else if (_loaded.TryGetValue(key, out var entry) && entry.BinDir == binDir) failed++;
        }

        try
        {
            if ((await _global.ReadAsync(ct)).Current is { NeedsEagerLoad: true } global)
                await LoadEarlyAsync(GlobalKey, global);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed++;
            _logger.LogError(ex, "Could not load the global functions ahead of any request");
        }

        foreach (var site in _sites.List())
        {
            ct.ThrowIfCancellationRequested();
            if (site.CurrentFunctions is not { NeedsEagerLoad: true } bundle) continue;

            try
            {
                await LoadEarlyAsync(site.Domain, bundle);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "Could not load {Owner}'s functions ahead of any request", site.Domain);
            }
        }

        if (loaded + failed > 0)
            _logger.LogInformation("Functions with background work: {Loaded} loaded, {Failed} did not", loaded, failed);
    }

    /// <summary>
    /// For the server stopping: asks every loaded set's background services and jobs to stop,
    /// and waits for them until <paramref name="ct"/> says the server will wait no longer. The
    /// sets keep answering whatever requests are still arriving, and start no new background work.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        _shuttingDown = true;

        var stopping = _loaded
            .Select(pair => (Owner: OwnerOf(pair.Key), pair.Value.Set))
            .Where(entry => entry.Set is { Jobs.HasBackgroundWork: true })
            .Select(entry => entry.Set!.StopAsync(StopGrace, _logger, entry.Owner))
            .ToList();

        if (stopping.Count == 0) return;

        try
        {
            await Task.WhenAll(stopping).WaitAsync(ct);
            _logger.LogInformation("Stopped the background work of {Count} set(s) of functions", stopping.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("The server stopped before every set of functions had finished its background work");
        }
    }

    // ---------------------------------------------------------------- status

    /// <summary>
    /// Where the live functions of a site, or the global ones, stand: loaded (with how their
    /// background work is going), loading, failed and why, or not loaded yet. Only the set for the
    /// bundle live now counts; one still retiring from before a deploy does not.
    /// </summary>
    /// <param name="domain">The site, or null for the global functions.</param>
    public async Task<FunctionScopeStatus> StatusAsync(string? domain)
    {
        var key = domain ?? GlobalKey;
        var (_, bundle) = await LiveBundleAsync(key);
        if (bundle is null) return FunctionScopeStatus.NotLoaded;

        var binDir = BinDirOf(key, bundle);
        if (_loading.TryGetValue(key, out var loading) && loading == binDir) return FunctionScopeStatus.Loading;
        if (!_loaded.TryGetValue(key, out var loaded) || loaded.BinDir != binDir) return FunctionScopeStatus.NotLoaded;
        if (loaded.Set is not { } set) return FunctionScopeStatus.Failed(loaded.Error);

        var (jobs, background) = set.Snapshot();
        return new FunctionScopeStatus(FunctionLoadState.Loaded, null, loaded.Utc, jobs, background);
    }

    // ---------------------------------------------------------------- versions

    /// <summary>
    /// True when a bundle was compiled against a newer StaticSiteHost.Abstractions than this
    /// server runs, as happens when the server is rolled back after a deploy. It may use
    /// members this copy lacks and fail on some later request with a MissingMethodException,
    /// so the host does not load it at all, and the Functions card says why.
    ///
    /// Versions compare as major.minor.patch, ignoring any prerelease suffix. A development
    /// build of the host (0.0.0) refuses nothing, and neither does a version that will not parse.
    /// </summary>
    public static bool IsNewerThanHostAbstractions(string? bundleVersion) =>
        HostAbstractions is { } host && ParseVersion(bundleVersion) is { } bundle && bundle.CompareTo(host) > 0;

    /// <summary>"1.2.3", "1.2.3-beta.1+abc" or "1.2" as numbers; null for anything else.</summary>
    private static (int, int, int)? ParseVersion(string? version)
    {
        var parts = (version ?? "").Split('-', '+')[0].Split('.');
        if (parts.Length is < 1 or > 4) return null;

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return null;
            if (i < numbers.Length) numbers[i] = number;
        }

        return (numbers[0], numbers[1], numbers[2]);
    }

    // ---------------------------------------------------------------- retiring

    /// <summary>
    /// Retires a site's set now, without waiting for it, and loads nothing in its place. For
    /// whatever only needs the set out of the way; a delete and a rename, which go on to remove or
    /// move the site's directory, use <see cref="EvictAsync"/>, and call this afterwards as well.
    /// </summary>
    public void Evict(string domain) => Remove(domain);

    /// <summary>
    /// Retires a site's functions and waits until they are gone, for a delete or a rename, which must
    /// not touch the site's directory while any of them still runs: a job or a request that outlived
    /// its functions would write into the folder being deleted, or recreate it under a name that no
    /// longer exists. It returns once the loaded set, and any earlier set of the site's still
    /// retiring after a deploy, has had its background work cancelled and waited for (up to 15
    /// seconds), its requests waited for (up to 30 seconds), its services disposed, and its build
    /// unloaded, which retires its <see cref="ISite"/> so nothing reaches the site through it again.
    ///
    /// The handle it returns keeps the domain's functions from loading again until it is disposed, so
    /// a request arriving in the meantime cannot bring them back before the directory is gone or
    /// moved: such a request goes without them, and a site with middleware answers 503. Dispose it
    /// once the site has been deleted or moved.
    /// </summary>
    public async Task<IAsyncDisposable> EvictAsync(string domain)
    {
        _closed.AddOrUpdate(domain, 1, static (_, count) => count + 1);
        var reopen = new Reopen(this, domain);

        try
        {
            // Under the gate, so a load already under way finishes and is then taken away, rather
            // than being published after this has looked.
            var gate = GateFor(domain);
            await gate.WaitAsync();
            try
            {
                Remove(domain);
            }
            finally
            {
                gate.Release();
            }

            Task[] retiring;
            lock (_retireGate)
            {
                retiring = [.. _retiring.Values
                    .Where(entry => string.Equals(entry.Key, domain, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.Retired)];
            }

            await Task.WhenAll(retiring);
            return reopen;
        }
        catch
        {
            await reopen.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Deletes a bundle directory nothing runs any more, for the pruning of old releases. When a set
    /// loaded from it is still live or retiring, which it can be for 45 seconds after a deploy, it may
    /// yet load an assembly from there, so the directory is deleted once that set is unloaded
    /// instead. One still waiting when the server stops is left behind, for the next start to clear.
    /// </summary>
    public void DeleteWhenRetired(string bundleDir)
    {
        lock (_retireGate)
        {
            if (IsInUse(DataPaths.FunctionBinDir(bundleDir)))
            {
                _pendingDeletes.Add(bundleDir);
                return;
            }
        }

        TryDeleteDirectory(bundleDir);
    }

    private void Remove(string key)
    {
        if (_loaded.TryRemove(key, out var removed) && removed.Set is { } set) Retire(set, key);
    }

    /// <summary>
    /// Marks the set retiring, so no new request enters it, and retires it on a task of its own
    /// so whatever replaced or removed it is not held up. Retiring a set twice does nothing. Until it
    /// is unloaded it is listed in <see cref="_retiring"/>, for <see cref="EvictAsync"/> and
    /// <see cref="DeleteWhenRetired"/>.
    /// </summary>
    private void Retire(FunctionSet set, string key)
    {
        if (!set.BeginRetire()) return;

        var owner = OwnerOf(key);
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_retireGate) _retiring[set] = (key, retired.Task);

        _ = FunctionSet.RunDetached(async () =>
        {
            try
            {
                await RetireAsync(set, owner);
            }
            finally
            {
                lock (_retireGate) _retiring.Remove(set);
                DeletePendingIfFree();
                retired.TrySetResult();
            }
        });
    }

    /// <summary>The order the class remarks give: background work, then requests, then services, then the build itself.</summary>
    private async Task RetireAsync(FunctionSet set, string owner)
    {
        var clock = Stopwatch.StartNew();

        try
        {
            var lingering = await set.StopAsync(StopGrace, _logger, owner);
            if (lingering.Count > 0)
            {
                _logger.LogWarning(
                    "Retiring {Owner}'s old functions: {Work} did not stop within {Seconds} seconds of being asked to. " +
                    "The functions are unloaded anyway, and leave memory once that returns.",
                    owner, string.Join(", ", lingering), StopGrace.TotalSeconds);
            }

            if (!await set.DrainAsync(DrainGrace))
            {
                _logger.LogWarning(
                    "Requests were still running on {Owner}'s old functions after {Seconds} seconds; their services are " +
                    "disposed under them.", owner, DrainGrace.TotalSeconds);
            }

            await set.DisposeServicesAsync(_logger, owner);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retiring {Owner}'s old functions went wrong part way; they are unloaded regardless", owner);
        }

        Unload(set, owner);
        _logger.LogInformation("Retired {Owner}'s old functions from {Dir} in {Elapsed} ms", owner, set.BinDir, clock.ElapsedMilliseconds);
    }

    private void Unload(FunctionSet set, string owner)
    {
        var probe = set.UnloadProbe();
        var released = set.Unload();

        if (released.Count > 0)
            _logger.LogInformation("Released {Count} process-wide hook(s) left by {Owner}'s functions: {Hooks}",
                released.Count, owner, string.Join("; ", released));

        lock (_unloading)
        {
            // Anything still alive long after its unload is holding memory until restart; say so once.
            // A quiet server may not have collected anything in that time, so a build that is merely
            // uncollected must not be mistaken for one that is pinned: force one full collection before
            // blaming, and only when something has been waiting that long, which is rare.
            if (_unloading.Any(entry => entry.Probe.IsAlive && DateTime.UtcNow - entry.UnloadedUtc >= LingerWarning))
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

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

    /// <summary>Deletes whichever directories waiting on <see cref="DeleteWhenRetired"/> nothing is loaded from any more.</summary>
    private void DeletePendingIfFree()
    {
        List<string> free;
        lock (_retireGate)
        {
            if (_pendingDeletes.Count == 0) return;

            free = [.. _pendingDeletes.Where(dir => !IsInUse(DataPaths.FunctionBinDir(dir)))];
            foreach (var dir in free) _pendingDeletes.Remove(dir);
        }

        foreach (var dir in free) TryDeleteDirectory(dir);
    }

    /// <summary>True when a set loaded, loading or retiring came from <paramref name="binDir"/>. Called with <see cref="_retireGate"/> held.</summary>
    private bool IsInUse(string binDir) =>
        _retiring.Keys.Any(set => set.BinDir == binDir) ||
        _loaded.Values.Any(loaded => loaded.Set is not null && loaded.BinDir == binDir) ||
        _loading.Values.Contains(binDir);

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

    /// <summary>Opens a domain closed by <see cref="EvictAsync"/> again, once, when disposed.</summary>
    private sealed class Reopen(FunctionHost host, string domain) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) host.Open(domain);
            return ValueTask.CompletedTask;
        }
    }

    private void Open(string domain)
    {
        while (_closed.TryGetValue(domain, out var count))
        {
            var done = count <= 1
                ? _closed.TryRemove(new KeyValuePair<string, int>(domain, count))
                : _closed.TryUpdate(domain, count - 1, count);
            if (done) return;
        }
    }

    // ---------------------------------------------------------------- keys and paths

    private SemaphoreSlim GateFor(string key) => _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

    private static string OwnerOf(string key) => key == GlobalKey ? SiteContext.GlobalDomain : key;

    private string BinDirOf(string key, FunctionBundle bundle) =>
        DataPaths.FunctionBinDir(key == GlobalKey
            ? _paths.GlobalFunctionBundleDir(bundle.Id)
            : _paths.FunctionBundleDir(key, bundle.Id));
}
