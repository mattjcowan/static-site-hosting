using System.Reflection;

namespace StaticSiteHost.Services;

/// <summary>
/// One loaded build of a site's (or the global) functions: its isolated context and the
/// routes discovered in it.
///
/// Replacing a set is a swap-then-unload: publish the new set, then <see cref="Unload"/> the
/// old one. Requests already running on the old set keep it alive until they finish — they
/// hold references to its methods — and only then can the runtime actually collect it. No
/// draining is needed on our side.
///
/// What it costs in practice is discipline about references. The context is collected only
/// once nothing reachable points into it, so anything in the host that caches a Type,
/// delegate or instance from a function keeps a whole build alive for the life of the
/// process. The known offender is System.Text.Json's metadata cache, which is why
/// <see cref="FunctionRouter"/> refuses to serialise handler return values itself.
/// </summary>
public sealed class FunctionSet
{
    private readonly FunctionLoadContext _context;
    private int _unloaded;

    public FunctionRouter Router { get; }

    /// <summary>Directory the set was loaded from, for diagnostics.</summary>
    public string BinDir { get; }

    private FunctionSet(FunctionLoadContext context, FunctionRouter router, string binDir)
    {
        _context = context;
        Router = router;
        BinDir = binDir;
    }

    /// <summary>
    /// Loads <c>SiteFunctions.dll</c> from a publish output directory. The main assembly is
    /// read from bytes so the file stays unlocked for the next deploy; its dependencies load
    /// by path through the context's resolver.
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
            return new FunctionSet(context, FunctionRouter.Discover(assembly), binDir);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    /// <summary>
    /// Requests collection of the context. Safe to call more than once. Returns the process-wide
    /// hooks that had to be released first (see <see cref="FunctionHooks"/>), for logging.
    /// </summary>
    public IReadOnlyList<string> Unload()
    {
        if (Interlocked.Exchange(ref _unloaded, 1) != 0) return [];

        var released = FunctionHooks.Release(_context);
        _context.Unload();
        ClearSerializerCaches();
        return released;
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
