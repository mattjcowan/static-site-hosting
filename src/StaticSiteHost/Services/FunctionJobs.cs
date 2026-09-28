using System.Reflection;
using System.Runtime.CompilerServices;
using StaticSiteHost.Functions;

namespace StaticSiteHost.Services;

/// <summary>
/// The code in a function assembly that runs outside any request: <c>[ConfigureServices]</c>
/// methods, which register the functions' services when they load; <c>[BackgroundService]</c>
/// methods, which run for as long as they are live; and <c>[Schedule]</c> and <c>[Every]</c>
/// methods, the jobs, which run on a timetable. <see cref="FunctionJobRunner"/> runs them.
///
/// Found by attribute name, like <see cref="FunctionRouter"/>'s routes and middleware, and read
/// through <see cref="CustomAttributeData"/>, so nothing from the loaded context is instantiated
/// and an author's own attribute of the same name does as well. Like middleware, they are looked
/// for on every method, not only public static ones: a handler that is not found answers nothing,
/// which is noticed at once, but a job that is not found simply never runs, which may not be. So a
/// misdeclared one is reported instead, in <see cref="Problems"/>, and the build is refused.
///
/// What such code may take is narrower than a handler's, because there is no request: no
/// <see cref="HttpContext"/> or anything from it, no <c>Func&lt;Task&gt;</c>, and no simple values,
/// which a handler takes from the route or the query string. Instead it may take what the
/// functions' services hold. See <see cref="BindArguments"/>.
/// </summary>
public sealed class FunctionJobs
{
    private const string ConfigureServicesAttributeName = "ConfigureServicesAttribute";
    private const string BackgroundServiceAttributeName = "BackgroundServiceAttribute";
    private const string ScheduleAttributeName = "ScheduleAttribute";
    private const string EveryAttributeName = "EveryAttribute";
    private const string MiddlewareAttributeName = "MiddlewareAttribute";

    /// <summary>What a <c>[ConfigureServices]</c> method may take: the services do not exist yet, so nothing can be resolved.</summary>
    private static readonly HashSet<Type> ConfigureParameterTypes =
    [
        typeof(IServiceCollection), typeof(CancellationToken), typeof(ISite), typeof(ISiteVariables), typeof(IRealtime),
        typeof(IAiChat), typeof(DirectoryInfo), typeof(IReadOnlyDictionary<string, string>), typeof(ILogger),
    ];

    /// <summary>A <c>[Schedule]</c> or <c>[Every]</c> method and when it runs: <paramref name="Cron"/> when set, else every <paramref name="Interval"/>.</summary>
    public sealed record Job(MethodInfo Method, CronSchedule? Cron, TimeSpan Interval, bool RunOnStart)
    {
        /// <summary>Class and method, such as <c>Cache.Refresh</c>.</summary>
        public string Name => NameOf(Method);

        /// <summary>The cron schedule, or <c>every 5m</c>.</summary>
        public string Schedule => Cron?.ToString() ?? $"every {JobInterval.Format(Interval)}";

        /// <summary>How the bundle records it, and so the key the Functions card finds it by: <c>*/5 * * * * Reports.Send</c>, <c>every 5m Cache.Refresh on start</c>.</summary>
        public string Display => $"{Schedule} {Name}{(RunOnStart ? " on start" : "")}";

        /// <summary>When it first comes due, for functions that load at <paramref name="now"/>.</summary>
        public DateTimeOffset? First(DateTimeOffset now) =>
            RunOnStart ? now : Cron is not null ? Cron.NextOccurrence(now) : now + Interval;

        /// <summary>
        /// When it comes due after the occurrence that was due at <paramref name="due"/>. An
        /// interval counts from when that occurrence was due, so runs do not drift by however
        /// long each one takes. Occurrences already in the past, because the server was busy or
        /// asleep, are passed over rather than run one after another to catch up.
        /// </summary>
        public DateTimeOffset? Next(DateTimeOffset due, DateTimeOffset now)
        {
            if (Cron is not null) return Cron.NextOccurrence(due > now ? due : now);

            var next = due + Interval;
            return next > now ? next : next + Interval * (Math.Floor((now - next) / Interval) + 1);
        }
    }

    /// <summary>The <c>[ConfigureServices]</c> methods, in the order they run: by class name, then method name.</summary>
    public IReadOnlyList<MethodInfo> Configure { get; }

    /// <summary>The <c>[BackgroundService]</c> methods, by class name, then method name.</summary>
    public IReadOnlyList<MethodInfo> BackgroundServices { get; }

    /// <summary>The <c>[Schedule]</c> and <c>[Every]</c> methods, by class name, then method name.</summary>
    public IReadOnlyList<Job> Scheduled { get; }

    /// <summary>
    /// Methods marked with one of these attributes that cannot run, one or two sentences each
    /// saying what to change. They are left out of the lists above, and a build that has any is
    /// refused (see <see cref="FunctionBundleBuilder"/>).
    /// </summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>True when there is nothing here at all.</summary>
    public bool IsEmpty => Configure.Count == 0 && !HasBackgroundWork;

    /// <summary>True when the functions have work of their own to do, and so want loading ahead of any request.</summary>
    public bool HasBackgroundWork => BackgroundServices.Count > 0 || Scheduled.Count > 0;

    private FunctionJobs(List<MethodInfo> configure, List<MethodInfo> background, List<Job> scheduled, List<string> problems)
    {
        Configure = configure;
        BackgroundServices = background;
        Scheduled = scheduled;
        Problems = problems;
    }

    public static FunctionJobs Discover(Assembly assembly)
    {
        const BindingFlags everyMethod = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        var configure = new List<MethodInfo>();
        var background = new List<MethodInfo>();
        var scheduled = new List<Job>();
        var problems = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(everyMethod))
            {
                var attributes = method.GetCustomAttributesData();
                CustomAttributeData? Find(string name) => attributes.FirstOrDefault(a => a.AttributeType.Name == name);

                var configureAttribute = Find(ConfigureServicesAttributeName);
                var backgroundAttribute = Find(BackgroundServiceAttributeName);
                var scheduleAttribute = Find(ScheduleAttributeName);
                var everyAttribute = Find(EveryAttributeName);

                if (configureAttribute is null && backgroundAttribute is null && scheduleAttribute is null && everyAttribute is null)
                    continue;

                // A method has one of these roles. Routes are another matter: a job may also be a
                // handler, say to run it on demand, and FunctionRouter finds it as one regardless.
                var roles = new[]
                    {
                        (configureAttribute, "[ConfigureServices]"), (backgroundAttribute, "[BackgroundService]"),
                        (scheduleAttribute, "[Schedule]"), (everyAttribute, "[Every]"), (Find(MiddlewareAttributeName), "[Middleware]"),
                    }
                    .Where(role => role.Item1 is not null)
                    .Select(role => role.Item2)
                    .ToList();

                if (roles.Count > 1)
                {
                    problems.Add($"{NameOf(method)} is marked {string.Join(" and ", roles)}, but a method can have only one of " +
                                 "these. Give each its own method.");
                    continue;
                }

                if (configureAttribute is not null)
                {
                    if (CheckConfigure(method) is { } problem) problems.Add(problem);
                    else configure.Add(method);
                }
                else if (backgroundAttribute is not null)
                {
                    if (CheckBackground(method) is { } problem) problems.Add(problem);
                    else background.Add(method);
                }
                else
                {
                    var (job, problem) = ReadJob(method, scheduleAttribute ?? everyAttribute!, isSchedule: scheduleAttribute is not null);
                    if (problem is not null) problems.Add(problem);
                    else scheduled.Add(job!);
                }
            }
        }

        return new FunctionJobs(
            [.. configure.OrderBy(m => m, MethodOrder.Instance)],
            [.. background.OrderBy(m => m, MethodOrder.Instance)],
            [.. scheduled.OrderBy(j => j.Method, MethodOrder.Instance)],
            problems);
    }

    /// <summary>"Class.Method", as messages and the Functions card name a method.</summary>
    public static string NameOf(MethodInfo method) => $"{method.DeclaringType?.Name}.{method.Name}";

    // ---------------------------------------------------------------- checks

    private static string? CheckConfigure(MethodInfo method)
    {
        const string attribute = "[ConfigureServices]";
        var name = NameOf(method);

        if ((CheckShape(method, attribute) ?? CheckParameters(method, attribute, isConfigure: true)) is { } problem) return problem;

        if (method.ReturnType != typeof(void))
        {
            return $"{name} is marked {attribute} but returns {FunctionRouter.TypeName(method.ReturnType)}. Return nothing: " +
                   "it only adds to the IServiceCollection, and the services are built once every such method has returned.";
        }

        if (IsAsync(method))
        {
            return $"{name} is marked {attribute} but is async void, which returns at its first await, so the services " +
                   "would be built before it finished. Make it synchronous.";
        }

        if (!method.GetParameters().Any(p => p.ParameterType == typeof(IServiceCollection)))
        {
            return $"{name} is marked {attribute} but takes no IServiceCollection, so it has nothing to register services " +
                   "in. Add IServiceCollection services.";
        }

        return null;
    }

    private static string? CheckBackground(MethodInfo method)
    {
        const string attribute = "[BackgroundService]";
        var name = NameOf(method);

        if ((CheckShape(method, attribute) ?? CheckParameters(method, attribute, isConfigure: false)) is { } problem) return problem;

        if (method.ReturnType != typeof(Task) && method.ReturnType != typeof(ValueTask))
        {
            return $"{name} is marked {attribute} but returns {FunctionRouter.TypeName(method.ReturnType)}. A background " +
                   "service runs until it is told to stop, so make it async Task.";
        }

        if (!method.GetParameters().Any(p => p.ParameterType == typeof(CancellationToken)))
        {
            return $"{name} is marked {attribute} but takes no CancellationToken, so it could never be told to stop when " +
                   "the functions are replaced. Add CancellationToken stoppingToken, and return once it is cancelled.";
        }

        return null;
    }

    private static (Job? Job, string? Problem) ReadJob(MethodInfo method, CustomAttributeData attribute, bool isSchedule)
    {
        var label = isSchedule ? "[Schedule]" : "[Every]";
        var name = NameOf(method);

        if ((CheckShape(method, label) ?? CheckParameters(method, label, isConfigure: false)) is { } problem) return (null, problem);

        var returns = method.ReturnType;
        if (returns != typeof(Task) && returns != typeof(ValueTask) && returns != typeof(void))
        {
            return (null, $"{name} is marked {label} but returns {FunctionRouter.TypeName(returns)}. Return Task, ValueTask " +
                          "or nothing: nothing uses what a job returns.");
        }

        if (returns == typeof(void) && IsAsync(method))
        {
            return (null, $"{name} is marked {label} but is async void, which cannot be waited for, so its runs could " +
                          "overlap and its failures would go unnoticed. Make it async Task.");
        }

        var text = attribute.ConstructorArguments.FirstOrDefault().Value as string;
        var runOnStart = attribute.NamedArguments
            .FirstOrDefault(argument => argument.MemberName == "RunOnStart").TypedValue.Value as bool? ?? false;

        if (isSchedule)
        {
            if (text is null)
                return (null, $"{name} is marked [Schedule] without a schedule. Give it one, as in [Schedule(\"*/5 * * * *\")].");

            return CronSchedule.TryParse(text, out var cron, out var error)
                ? (new Job(method, cron, TimeSpan.Zero, runOnStart), null)
                : (null, $"{name} has [Schedule(\"{text}\")], which this server cannot run. {error}");
        }

        if (text is null)
            return (null, $"{name} is marked [Every] without an interval. Give it one, as in [Every(\"5m\")].");

        return JobInterval.TryParse(text, out var interval, out var intervalError)
            ? (new Job(method, null, interval, runOnStart), null)
            : (null, $"{name} has [Every(\"{text}\")], which this server cannot run. {intervalError}");
    }

    /// <summary>What every role asks of the method itself.</summary>
    private static string? CheckShape(MethodInfo method, string attribute)
    {
        var name = NameOf(method);

        if (!method.IsPublic || !method.IsStatic || method.DeclaringType is { IsVisible: false })
        {
            return $"{name} is marked {attribute} but is not a public static method of a public class. Declare it public " +
                   "static, in a public class.";
        }

        return method.ContainsGenericParameters
            ? $"{name} is marked {attribute} but is generic, and nothing says what to call it with. Remove its type parameters."
            : null;
    }

    /// <summary>Parameters nothing outside a request could fill in.</summary>
    private static string? CheckParameters(MethodInfo method, string attribute, bool isConfigure)
    {
        foreach (var parameter in method.GetParameters())
        {
            var type = parameter.ParameterType;
            var takes = $"{NameOf(method)} is marked {attribute} and takes {FunctionRouter.TypeName(type.IsByRef ? type.GetElementType()! : type)} {parameter.Name}";

            if (type.IsByRef) return $"{takes} by reference, which nothing can fill in. Take it by value.";

            if (type == typeof(HttpContext) || type == typeof(HttpRequest) || type == typeof(HttpResponse))
            {
                return $"{takes}, but it runs outside any request, so there is none to give it. Remove the parameter; an " +
                       "ISite parameter gives the site, its data folder and its variables.";
            }

            if (type == typeof(Func<Task>)) return $"{takes}, which only [Middleware] is given. Remove the parameter.";

            if (type == typeof(IServiceCollection) && !isConfigure)
            {
                return $"{takes}, which only [ConfigureServices] methods are given, while the services are being set up. " +
                       "Register services there, and take them here as parameters.";
            }

            if (FunctionRouter.IsSimpleValue(type))
            {
                return $"{takes}, a value a handler would take from the route or the query string, but it runs outside any " +
                       "request. Remove the parameter, or read the value from a site variable with ISiteVariables.";
            }

            if (isConfigure && !ConfigureParameterTypes.Contains(type))
            {
                return $"{takes}, but it runs before the services exist, so there is nothing to resolve it from yet. It can " +
                       "take IServiceCollection, ISite, ISiteVariables, IRealtime, IAiChat, DirectoryInfo, " +
                       "IReadOnlyDictionary<string, string>, ILogger and CancellationToken.";
            }
        }

        return null;
    }

    private static bool IsAsync(MethodInfo method) => method.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false);

    // ---------------------------------------------------------------- running

    /// <summary>
    /// The arguments for a method that runs outside a request. <paramref name="services"/> is
    /// what an <see cref="IServiceProvider"/> parameter gets and what anything else is resolved
    /// from: the root provider for a background service, a scope made for the run for a job, and
    /// null for a <c>[ConfigureServices]</c> method, which the build allows only the types it can
    /// fill without one. <see cref="IRealtime"/> and <see cref="IAiChat"/> are the functions' own
    /// site's, and the global functions, which have no site outside a request, cannot take them.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A parameter's type is not among the services and has no default, or the global functions'
    /// method takes <see cref="IRealtime"/> or <see cref="IAiChat"/>.
    /// </exception>
    public static object?[] BindArguments(
        MethodInfo method, JobBinding binding, IServiceProvider? services, IServiceCollection? collection = null)
    {
        var parameters = method.GetParameters();
        var arguments = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var type = parameter.ParameterType;

            arguments[i] =
                type == typeof(IServiceCollection) ? collection
                : type == typeof(CancellationToken) ? binding.Stopping
                : type == typeof(ISite) ? binding.Site
                : type == typeof(ISiteVariables) ? binding.Site.Variables
                : type == typeof(IRealtime) ? binding.Site.Realtime
                : type == typeof(IAiChat) ? binding.Site.Ai
                : type == typeof(DirectoryInfo) ? binding.Site.Data
                : type == typeof(IReadOnlyDictionary<string, string>) ? binding.Site.Variables.All
                : type == typeof(ILogger) ? binding.Logger
                : type == typeof(IServiceProvider) ? services
                : services?.GetService(type) is { } service ? service
                : parameter.HasDefaultValue ? parameter.DefaultValue
                : throw new InvalidOperationException(
                    $"{NameOf(method)} takes {FunctionRouter.TypeName(type)} {parameter.Name}, which is not among the " +
                    "functions' services. Register it in a [ConfigureServices] method, or remove the parameter.");
        }

        return arguments;
    }

    /// <summary>Calls the method and waits for it, surfacing its own exception rather than reflection's wrapper.</summary>
    public static async Task InvokeAsync(MethodInfo method, object?[] arguments)
    {
        switch (FunctionRouter.Invoke(method, arguments))
        {
            case Task task:
                await task;
                break;

            case ValueTask valueTask:
                await valueTask;
                break;
        }
    }

    /// <summary>Class name, then method name, both ordinal, so the order never depends on how the compiler laid the types out.</summary>
    private sealed class MethodOrder : IComparer<MethodInfo>
    {
        public static readonly MethodOrder Instance = new();

        public int Compare(MethodInfo? x, MethodInfo? y)
        {
            var byClass = string.CompareOrdinal(x?.DeclaringType?.Name, y?.DeclaringType?.Name);
            return byClass != 0 ? byClass : string.CompareOrdinal(x?.Name, y?.Name);
        }
    }
}

/// <summary>
/// What code outside a request is given besides the functions' services: the functions' own
/// <see cref="ISite"/>, the logger for their category, and the token cancelled when they retire.
/// All host types, so a job holding one holds nothing of the functions but its own.
/// </summary>
public sealed record JobBinding(SiteContext Site, ILogger Logger, CancellationToken Stopping);
