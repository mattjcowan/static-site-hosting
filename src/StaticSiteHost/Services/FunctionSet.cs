using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using StaticSiteHost.Functions;

namespace StaticSiteHost.Services;

/// <summary>
/// What the host hands a set's services and background work, none of it from the functions:
/// whose they are, their own <see cref="ISite"/>, and the host's logging and data protection.
/// </summary>
public sealed record FunctionSetEnvironment(
    string Owner, SiteContext Site, ILoggerFactory Loggers, IDataProtectionProvider DataProtection);

/// <summary>Why a set could not be made ready, in a sentence the Functions card can show as it is.</summary>
public sealed class FunctionLoadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// One loaded build of a site's (or the global) functions: its isolated context; the routes and
/// middleware discovered in it (<see cref="Router"/>); the code it runs outside requests
/// (<see cref="Jobs"/>); the hooks that decide who may use the site's realtime hub and AI
/// (<see cref="Hooks"/>); and, once it is made ready for use, its services and the background
/// work that runs its background services and jobs. The <see cref="MethodInfo"/>s live on the
/// router, the jobs and the hooks and nowhere else in the host, and everything long-lived that
/// the set starts is owned here, so all of it goes when the set does.
///
/// A set is used three ways. The build loads it once to read it (<see cref="Load"/> then
/// <see cref="Unload"/>). The editor's test loads it, builds its services
/// (<see cref="BuildServices"/>) and runs one request. <see cref="FunctionHost"/> loads it for
/// live use, builds its services and starts its background work (<see cref="Start"/>); only
/// that way does anything run on its own.
///
/// Replacing a live set is swap-then-retire. The host publishes the new set first, so new
/// requests go there, then retires the old one in order: mark it retiring, so nothing new enters
/// it (<see cref="BeginRetire"/>); cancel its stopping token and wait for its background work
/// (<see cref="StopAsync"/>); wait for the requests already inside it (<see cref="DrainAsync"/>);
/// dispose what is left of its services (<see cref="DisposeServicesAsync"/>); and unload it, which
/// retires its own <see cref="Site"/>, so code that outlived all of that reaches nothing of the site
/// through it.
///
/// What it costs in practice is discipline about references. The context is collected only
/// once nothing reachable points into it, so anything in the host that caches a Type,
/// delegate or instance from a function keeps a whole build alive for the life of the
/// process. The known offenders are System.Text.Json's metadata cache, which is why
/// <see cref="FunctionRouter"/> refuses to serialise handler return values itself, and the
/// dependency injection container's compiled resolvers, which is why the host turns them off
/// (see the csproj). For the same reason background work starts without the caller's
/// ExecutionContext (<see cref="RunDetached"/>), so a job never holds on to the request that
/// happened to load its set.
/// </summary>
public sealed class FunctionSet
{
    private readonly FunctionLoadContext _context;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<ServiceScope, byte> _scopes = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ServiceProvider? _services;
    private JobBinding? _binding;
    private FunctionJobRunner? _runner;
    private int _inFlight;
    private int _retiring;
    private int _servicesDisposed;
    private int _unloaded;

    public FunctionRouter Router { get; }

    /// <summary>The <c>[ConfigureServices]</c>, <c>[BackgroundService]</c>, <c>[Schedule]</c> and <c>[Every]</c> methods.</summary>
    public FunctionJobs Jobs { get; }

    /// <summary>The <c>[RealtimeConnect]</c>, <c>[RealtimeJoin]</c> and <c>[AiAccess]</c> methods.</summary>
    public FunctionAccessHooks Hooks { get; }

    /// <summary>Directory the set was loaded from, for diagnostics.</summary>
    public string BinDir { get; }

    /// <summary>The functions' root services once <see cref="BuildServices"/> has run; null before, and for a set only read.</summary>
    public IServiceProvider? Services => _services;

    /// <summary>
    /// The set's own <see cref="ISite"/>, what its services and background work see, once
    /// <see cref="BuildServices"/> has run. Retired as the set is unloaded (see <see cref="SiteContext"/>);
    /// a request's context follows it for the data folder.
    /// </summary>
    public SiteContext? Site { get; private set; }

    private FunctionSet(FunctionLoadContext context, FunctionRouter router, FunctionJobs jobs, FunctionAccessHooks hooks, string binDir)
    {
        _context = context;
        Router = router;
        Jobs = jobs;
        Hooks = hooks;
        BinDir = binDir;
    }

    /// <summary>
    /// Loads <c>SiteFunctions.dll</c> from a publish output directory. The main assembly is
    /// read from bytes so the file stays unlocked for the next deploy; its dependencies load
    /// by path through the context's resolver. Nothing of the functions runs.
    /// </summary>
    public static FunctionSet Load(string binDir, string name)
    {
        var assemblyPath = Path.Combine(binDir, FunctionProjectGenerator.AssemblyName + ".dll");
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("No built functions assembly was found.", assemblyPath);

        var context = new FunctionLoadContext(assemblyPath, name);

        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(assemblyPath));
            var assembly = context.LoadFromStream(stream);
            return new FunctionSet(
                context, FunctionRouter.Discover(assembly), FunctionJobs.Discover(assembly), FunctionAccessHooks.Discover(assembly), binDir);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    // ---------------------------------------------------------------- services

    /// <summary>
    /// Builds the functions' services: a fresh collection holding what the host provides, then
    /// whatever each <c>[ConfigureServices]</c> method adds, in order, then a provider that
    /// checks every registration can be built, so a mistake shows now and not on a later
    /// request. <see cref="FunctionSetEnvironment.Site"/> gets the provider as its services.
    ///
    /// <see cref="IRealtime"/> and <see cref="IAiChat"/> are registered as factories reading the
    /// set's own site, so a service that takes one gets the functions' site's. The global
    /// functions have none outside a request, and resolving either for them throws the site's
    /// explanation; a factory rather than an instance, so that happens when a service asks and not
    /// here, for every set of global functions.
    /// </summary>
    /// <exception cref="FunctionLoadException">A method threw, or a registration cannot be built. The message says which.</exception>
    public void BuildServices(FunctionSetEnvironment environment)
    {
        if (_services is not null) throw new InvalidOperationException("The functions' services are already built.");

        var logger = environment.Loggers.CreateLogger($"functions:{environment.Owner}");
        Site = environment.Site;
        _binding = new JobBinding(environment.Site, logger, _stopping.Token);

        // Instances the host owns: a provider never disposes an instance it was handed, so these
        // outlive it as they must. The logger factory and ILogger<T> go in before AddHttpClient,
        // whose AddLogging then keeps them rather than adding a factory of its own.
        var collection = new ServiceCollection();
        collection.AddSingleton<ILoggerFactory>(environment.Loggers);
        collection.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        collection.AddSingleton<ILogger>(logger);
        collection.AddSingleton<IDataProtectionProvider>(environment.DataProtection);
        collection.AddSingleton<TimeProvider>(TimeProvider.System);
        collection.AddSingleton<ISite>(environment.Site);
        collection.AddSingleton<ISiteVariables>(environment.Site.Variables);
        collection.AddSingleton<IRealtime>(_ => environment.Site.Realtime);
        collection.AddSingleton<IAiChat>(_ => environment.Site.Ai);
        collection.AddHttpClient();

        // The factory keeps each handler it hands out for two minutes, then expires it on a timer.
        // The process-wide timer queue holds the timer, the timer the factory, and the factory this
        // provider, which refers to the functions' own types once they register any. Measured: a
        // retired build that had used a typed client stayed in memory for 128 seconds, against
        // half a second with this. With no expiry there is no timer. The pooled connection lifetime
        // still closes a connection after five minutes, so a changed DNS record is picked up as it
        // would have been.
        collection.ConfigureHttpClientDefaults(builder => builder
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }));

        foreach (var method in Jobs.Configure)
        {
            try
            {
                FunctionRouter.Invoke(method, FunctionJobs.BindArguments(method, _binding, services: null, collection));
            }
            catch (Exception ex)
            {
                throw new FunctionLoadException(
                    $"{FunctionJobs.NameOf(method)} threw {ex.GetType().Name}: {ex.Message}", ex);
            }
        }

        try
        {
            _services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        }
        catch (Exception ex)
        {
            var reasons = ex is AggregateException aggregate ? aggregate.InnerExceptions.Select(e => e.Message) : [ex.Message];
            throw new FunctionLoadException($"Their services cannot all be built: {string.Join(" ", reasons)}", ex);
        }

        environment.Site.UseServices(_services);
    }

    /// <summary>
    /// Starts the background services and jobs, each on a task of its own, for a set loaded for
    /// live use. Never for the build's read or the editor's test.
    /// </summary>
    public void Start(ILogger log)
    {
        if (_services is null || _binding is null) throw new InvalidOperationException("Build the functions' services before starting them.");
        if (_runner is not null || !Jobs.HasBackgroundWork) return;

        _runner = new FunctionJobRunner(this, Jobs, _binding, _services, log);
        _runner.Start();
    }

    /// <summary>
    /// The request's way into the set: a scope of the functions' services for the request to
    /// use, counted as in flight until it is disposed. Null once the set is retiring, when the
    /// caller should look up the set that replaced it.
    /// </summary>
    public ServiceScope? TryEnter()
    {
        // Counted before the check, and the retire marks before it counts, each with a full
        // fence, so a request either sees the mark and backs out or is counted and waited for.
        Interlocked.Increment(ref _inFlight);
        if (Volatile.Read(ref _retiring) != 0)
        {
            Exit();
            return null;
        }

        try
        {
            return NewScope(isRequest: true);
        }
        catch
        {
            Exit();
            throw;
        }
    }

    /// <summary>A scope for one job run. Not a request, so not waited for as one; retiring disposes it if the run has not.</summary>
    internal ServiceScope CreateJobScope() => NewScope(isRequest: false);

    private ServiceScope NewScope(bool isRequest)
    {
        var services = _services ?? throw new InvalidOperationException("The functions' services have not been built.");
        var scope = new ServiceScope(this, services.CreateAsyncScope(), isRequest);
        _scopes.TryAdd(scope, 0);
        return scope;
    }

    private void Exit()
    {
        if (Interlocked.Decrement(ref _inFlight) == 0 && Volatile.Read(ref _retiring) != 0) _drained.TrySetResult();
    }

    // ---------------------------------------------------------------- retiring

    /// <summary>Marks the set as retiring, so no new request enters it. False when it already was.</summary>
    public bool BeginRetire()
    {
        if (Interlocked.Exchange(ref _retiring, 1) != 0) return false;

        if (Volatile.Read(ref _inFlight) == 0) _drained.TrySetResult();
        return true;
    }

    /// <summary>
    /// Cancels the stopping token and waits up to <paramref name="grace"/> for the background
    /// services and job loops to finish. Returns what was still running then, such as "the
    /// background service Worker.Run", for the warning.
    /// </summary>
    public async Task<IReadOnlyList<string>> StopAsync(TimeSpan grace, ILogger log, string owner)
    {
        try
        {
            // Callbacks the functions registered on the token run here; one that throws must not stop the retire.
            await _stopping.CancelAsync();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Code registered on the stopping token of {Owner}'s functions threw as it was cancelled", owner);
        }

        return _runner is null ? [] : await _runner.WaitAsync(grace);
    }

    /// <summary>Waits up to <paramref name="grace"/> for the requests inside the set to leave. False when some were still there.</summary>
    public async Task<bool> DrainAsync(TimeSpan grace)
    {
        try
        {
            await _drained.Task.WaitAsync(grace);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Disposes the scopes still open, which is only those of requests or job runs that outlived
    /// their grace, then the functions' services, asynchronously so services that are only
    /// IAsyncDisposable are disposed too. Safe to call more than once.
    /// </summary>
    public async Task DisposeServicesAsync(ILogger log, string owner)
    {
        foreach (var scope in _scopes.Keys)
        {
            try
            {
                await scope.DisposeAsync();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Disposing a scope of {Owner}'s old functions' services threw", owner);
            }
        }

        if (_services is null || Interlocked.Exchange(ref _servicesDisposed, 1) != 0) return;

        try
        {
            await _services.DisposeAsync();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Disposing {Owner}'s old functions' services threw", owner);
        }
    }

    /// <summary>
    /// What the background services and jobs have done so far, for the Functions card. Empty for
    /// a set whose background work was never started.
    /// </summary>
    public (IReadOnlyList<FunctionJobStatus> Jobs, IReadOnlyList<FunctionBackgroundStatus> BackgroundServices) Snapshot() =>
        _runner?.Snapshot() ?? ([], []);

    /// <summary>
    /// Requests collection of the context. Safe to call more than once, and on its own: for a
    /// set that was only read or tested it is the whole of retiring. It cancels the stopping
    /// token and disposes the services if nothing has yet, without waiting for anything. Returns
    /// the process-wide hooks that had to be released first (see <see cref="FunctionHooks"/>), for
    /// logging.
    /// </summary>
    public IReadOnlyList<string> Unload()
    {
        if (Interlocked.Exchange(ref _unloaded, 1) != 0) return [];

        BeginRetire();

        try
        {
            _stopping.Cancel();
        }
        catch (AggregateException)
        {
            // A callback of the functions' threw; they are being unloaded regardless.
        }

        if (_services is not null && Interlocked.Exchange(ref _servicesDisposed, 1) == 0)
        {
            try
            {
                // Rarely more than synchronous: the host and the test runner dispose asynchronously first.
                _services.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Their own Dispose threw; the context is unloaded regardless.
            }
        }

        _services = null;
        _runner = null;
        _binding = null;

        // Anything of the functions still running, past every grace, reaches nothing of the site
        // through it now: see SiteContext.
        Site?.Retire();

        var released = FunctionHooks.Release(_context);
        _context.Unload();
        ClearSerializerCaches();
        return released;
    }

    /// <summary>
    /// Starts <paramref name="work"/> on the thread pool without the caller's ExecutionContext,
    /// so a set's background work, or the retiring of one, never carries the AsyncLocals (the
    /// current Activity, a logging scope) of whichever request happened to start it.
    /// </summary>
    internal static Task RunDetached(Func<Task> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work);
        }
    }

    /// <summary>
    /// A scope of the functions' services: one per request that enters the set, one per job run.
    /// The set keeps track of those still open so retiring can dispose any that outlived their
    /// grace; disposing one again does nothing.
    /// </summary>
    public sealed class ServiceScope : IAsyncDisposable
    {
        private readonly FunctionSet _set;
        private readonly AsyncServiceScope _scope;
        private readonly bool _isRequest;
        private int _disposed;

        internal ServiceScope(FunctionSet set, AsyncServiceScope scope, bool isRequest)
        {
            _set = set;
            _scope = scope;
            _isRequest = isRequest;
        }

        /// <summary>The set the scope belongs to.</summary>
        public FunctionSet Set => _set;

        public IServiceProvider Services => _scope.ServiceProvider;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _set._scopes.TryRemove(this, out _);
            try
            {
                await _scope.DisposeAsync();
            }
            finally
            {
                if (_isRequest) _set.Exit();
            }
        }
    }

    /// <summary>
    /// Flushes System.Text.Json's process-wide caches, which otherwise keep the old build alive.
    ///
    /// Even when a handler serialises with its own options, STJ stores the Reflection.Emit
    /// accessors it generates for the handler's types in a static cache in the default context.
    /// Entries expire after a second, but the cache is only swept the next time metadata is
    /// built for some <em>other</em> new type, which on a quiet server may be never. Measured:
    /// a JSON-returning handler kept its context alive indefinitely until this ran, and was
    /// collected immediately after.
    ///
    /// The hook is the one hot reload uses, found through the assembly's
    /// [MetadataUpdateHandler] attribute rather than by internal type name. If a future runtime
    /// drops it, nothing breaks: unload just goes back to happening eventually instead of now.
    /// Requests still running on the old build can repopulate the cache after this runs; the
    /// next sweep reclaims those.
    /// </summary>
    private static void ClearSerializerCaches()
    {
        try
        {
            var handlers = typeof(System.Text.Json.JsonSerializer).Assembly
                .GetCustomAttributes<System.Reflection.Metadata.MetadataUpdateHandlerAttribute>();

            foreach (var handler in handlers)
            {
                handler.HandlerType
                    .GetMethod("ClearCache", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        [typeof(Type[])])
                    ?.Invoke(null, [null]);
            }
        }
        catch (Exception)
        {
            // Best effort by design; see above.
        }
    }

    /// <summary>
    /// A handle that reports whether the context has actually been collected, for tests and
    /// diagnostics. Holding it does not keep the context alive.
    /// </summary>
    public WeakReference UnloadProbe() => new(_context);
}
