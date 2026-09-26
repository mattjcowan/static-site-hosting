using System.Reflection;
using System.Runtime.Loader;

namespace StaticSiteHost.Services;

/// <summary>
/// Releases the process-wide hooks that a function's code (or a package it uses) leaves behind,
/// so its load context can actually unload.
///
/// A collectible context disappears only when nothing outside it points in. Ordinary code
/// never does that, but libraries written for long-lived apps do it as a matter of course:
/// Microsoft.Data.Sqlite, for one, subscribes to AppDomain.ProcessExit and DomainUnload and
/// starts a pool-pruning timer the first time a connection opens. Each of those is a strong
/// reference from the runtime into the function, so without this every redeploy — and every
/// editor test run — kept a complete copy of the function and its packages in memory for the
/// life of the process. Measured: a SQLite handler never unloaded; with these three hooks
/// released it unloaded after two collections.
///
/// Everything is found by reflection over runtime internals that have been stable since .NET
/// Core 3, and every step is guarded. If a future runtime moves them, nothing breaks: that
/// hook simply stays, and <see cref="FunctionHost"/> logs that a build is lingering.
/// </summary>
public static class FunctionHooks
{
    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Detaches every event handler and timer that belongs to <paramref name="context"/>. Returns what was released.</summary>
    public static IReadOnlyList<string> Release(AssemblyLoadContext context)
    {
        var released = new List<string>();

        // AppDomain's events (ProcessExit, DomainUnload, ...) live on the instance; AppContext's
        // and AssemblyLoadContext's on the types; the default context's own events on it.
        ReleaseEvents(typeof(AppDomain), AppDomain.CurrentDomain, context, released);
        ReleaseEvents(typeof(AppContext), null, context, released);
        ReleaseEvents(typeof(AssemblyLoadContext), null, context, released);
        ReleaseEvents(typeof(AssemblyLoadContext), AssemblyLoadContext.Default, context, released);

        ReleaseTimers(context, released);
        return released;
    }

    private static void ReleaseEvents(Type type, object? instance, AssemblyLoadContext context, List<string> released)
    {
        FieldInfo[] fields;
        try { fields = type.GetFields(instance is null ? AnyStatic : AnyInstance); }
        catch { return; }

        foreach (var field in fields.Where(f => typeof(Delegate).IsAssignableFrom(f.FieldType)))
        {
            try
            {
                if (field.GetValue(instance) is not Delegate combined) continue;

                var remaining = combined;
                foreach (var handler in combined.GetInvocationList().Where(h => BelongsTo(h, context)))
                {
                    remaining = Delegate.Remove(remaining, handler);
                    released.Add($"{type.Name}.{field.Name.TrimStart('_')} handler {handler.Method.DeclaringType?.Name}.{handler.Method.Name}");
                }

                if (!ReferenceEquals(remaining, combined)) field.SetValue(instance, remaining);
            }
            catch
            {
                // Best effort, field by field.
            }
        }
    }

    /// <summary>
    /// Closes timers whose callback or state comes from the context. The runtime's timer queues
    /// hold every live timer, so a timer the function never disposed pins it indefinitely.
    /// </summary>
    private static void ReleaseTimers(AssemblyLoadContext context, List<string> released)
    {
        try
        {
            var queueType = typeof(Timer).Assembly.GetType("System.Threading.TimerQueue");
            var timerType = typeof(Timer).Assembly.GetType("System.Threading.TimerQueueTimer");
            if (queueType is null || timerType is null) return;

            var queues = (queueType.GetProperty("Instances", AnyStatic)?.GetValue(null)
                          ?? queueType.GetField("<Instances>k__BackingField", AnyStatic)?.GetValue(null)) as Array;
            if (queues is null) return;

            var lists = new[] { queueType.GetField("_shortTimers", AnyInstance), queueType.GetField("_longTimers", AnyInstance) };
            var sharedLock = queueType.GetProperty("SharedLock", AnyInstance);
            var next = timerType.GetField("_next", AnyInstance);
            var callback = timerType.GetField("_timerCallback", AnyInstance);
            var state = timerType.GetField("_state", AnyInstance);
            if (next is null || callback is null || sharedLock is null) return;

            var doomed = new List<IDisposable>();
            foreach (var queue in queues)
            {
                if (queue is null || sharedLock.GetValue(queue) is not Lock guard) continue;

                // The same lock the runtime takes to change these lists, so they hold still.
                using (guard.EnterScope())
                {
                    foreach (var list in lists)
                    {
                        for (var timer = list?.GetValue(queue); timer is not null; timer = next.GetValue(timer))
                        {
                            var target = callback.GetValue(timer) as Delegate;
                            var timerState = state?.GetValue(timer);

                            if ((target is not null && BelongsTo(target, context)) ||
                                (timerState is not null && IsFrom(timerState.GetType(), context)))
                            {
                                if (timer is IDisposable disposable) doomed.Add(disposable);
                            }
                        }
                    }
                }
            }

            // Disposed outside the lock: disposing takes it again to unlink the timer.
            foreach (var timer in doomed)
            {
                var target = callback.GetValue(timer) as Delegate;
                timer.Dispose();
                released.Add($"timer {target?.Method.DeclaringType?.Name}.{target?.Method.Name}");
            }
        }
        catch
        {
            // Internals moved; see the class remarks.
        }
    }

    private static bool BelongsTo(Delegate handler, AssemblyLoadContext context) =>
        IsFrom(handler.Method.DeclaringType, context) || (handler.Target is { } target && IsFrom(target.GetType(), context));

    private static bool IsFrom(Type? type, AssemblyLoadContext context) =>
        type is not null && AssemblyLoadContext.GetLoadContext(type.Assembly) == context;
}
