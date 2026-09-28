using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging.Abstractions;
using StaticSiteHost.Functions;

namespace StaticSiteHost.Services;

/// <summary>
/// Maps requests onto the handler methods of an uploaded function assembly, and runs its
/// middleware around them.
///
/// Handlers are discovered by attribute <em>name</em> — <c>[HttpGet("/x")]</c>,
/// <c>[HttpPost]</c>, … and <c>[Route]</c> for any verb — rather than by type, and so is
/// <c>[Middleware]</c>. The route attributes are ASP.NET's own and <c>[Middleware]</c> is
/// StaticSiteHost.Abstractions', but matching on the name means an uploaded file needs no
/// assembly of ours: an attribute of its own with the same name does as well. Reading them
/// through <see cref="MethodInfo.GetCustomAttributesData"/> never instantiates anything from
/// the loaded context.
///
/// A handler may take: an <see cref="HttpContext"/>, <see cref="HttpRequest"/> or
/// <see cref="HttpResponse"/>, a <see cref="CancellationToken"/>, a <see cref="DirectoryInfo"/>
/// (its data directory, see <see cref="DataDirectoryItem"/>), an
/// <c>IReadOnlyDictionary&lt;string, string&gt;</c> (the site's variables, see
/// <see cref="VariablesItem"/>), an <see cref="ISite"/>, <see cref="ISiteVariables"/>,
/// <see cref="IRealtime"/> or <see cref="IAiChat"/> (from the site at
/// <see cref="SiteHttpContextExtensions.ItemKey"/>, so always the request's site's, for global
/// functions too), an <see cref="IServiceProvider"/> (the
/// functions' services, scoped to the request: <see cref="ISite.Services"/>), a non-generic
/// <see cref="ILogger"/> (category <c>functions:{owner}</c>), anything else the functions'
/// services hold, or, by name, any simple value bound from the route first and the query
/// string second. It returns (optionally wrapped in a Task or ValueTask) an
/// <see cref="IResult"/>, a string, an int status code, or nothing.
///
/// Middleware takes the same, plus the <c>Func&lt;Task&gt;</c> that runs the rest of the
/// request, and has no route values, so its simple values come from the query string. It
/// returns Task, ValueTask or nothing, and answers, if it answers at all, by writing to the
/// response. See <see cref="CheckMiddleware"/> for what a build refuses. The access hooks
/// (<see cref="FunctionAccessHooks"/>) are bound the same way, by <see cref="FunctionHost"/>.
///
/// What a handler may <em>not</em> return is an arbitrary object for the host to serialise.
/// The host's System.Text.Json metadata cache would then hold the handler's types, and a cache
/// in the default context outlives every deploy — the collectible context could never unload,
/// and each redeploy would leak a full copy of the function's assemblies. Handlers serialise
/// their own results with a <c>JsonSerializerOptions</c> held in a static inside the uploaded
/// file (see samples/functions), so that cache dies with the context.
/// </summary>
public sealed class FunctionRouter
{
    /// <summary>
    /// HttpContext.Items key holding the full path of the data directory that belongs to the
    /// functions running now: the site's for a site's handlers and middleware, a shared one for
    /// the global functions'. <see cref="FunctionHost"/> switches it, with the
    /// <see cref="ISite"/>, as a request passes from one set to the other. A parameter of type
    /// <see cref="DirectoryInfo"/> receives it, created on first use; helper code without that
    /// parameter can read it from here. Plain strings and framework types only, so an uploaded
    /// file needs nothing of ours to use it.
    /// </summary>
    public const string DataDirectoryItem = "StaticSiteHost.DataDirectory";

    /// <summary>
    /// HttpContext.Items key holding the variables of the site the request is for, as an
    /// <c>IReadOnlyDictionary&lt;string, string&gt;</c>: every one, secrets included, with
    /// <c>${env:…}</c> already expanded. The global functions get the variables of whichever site
    /// they are answering for. A handler parameter of that type receives it, or an empty one when
    /// nothing put it there; helper code without that parameter can read it from here.
    /// </summary>
    public const string VariablesItem = "StaticSiteHost.Variables";

    /// <summary>Matched by name, like the route attributes; see the class remarks.</summary>
    private const string MiddlewareAttributeName = "MiddlewareAttribute";

    public sealed record Route(string Verb, string Template, string[] Segments, MethodInfo Method);

    /// <summary>A <c>[Middleware]</c> method, and its place among the others: lower <paramref name="Order"/> runs first.</summary>
    public sealed record MiddlewareMethod(int Order, MethodInfo Method);

    public IReadOnlyList<Route> Routes { get; }

    /// <summary>
    /// The <c>[Middleware]</c> methods, in the order they run: by <c>Order</c>, then class name,
    /// then method name, so the order never depends on how the compiler laid the types out.
    /// </summary>
    public IReadOnlyList<MiddlewareMethod> Middleware { get; }

    /// <summary>
    /// <c>[Middleware]</c> methods that cannot run, one sentence each saying what to change.
    /// They are left out of <see cref="Middleware"/>, and a build that has any is refused (see
    /// <see cref="FunctionBundleBuilder"/>), so only a bundle built before middleware existed,
    /// on which such a method never ran, can still carry one.
    /// </summary>
    public IReadOnlyList<string> MiddlewareProblems { get; }

    private FunctionRouter(IReadOnlyList<Route> routes, IReadOnlyList<MiddlewareMethod> middleware, IReadOnlyList<string> problems)
    {
        Routes = routes;
        Middleware = middleware;
        MiddlewareProblems = problems;
    }

    public static FunctionRouter Discover(Assembly assembly)
    {
        var routes = new List<Route>();

        foreach (var type in assembly.GetExportedTypes())
        {
            foreach (var method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (var attribute in method.GetCustomAttributesData())
                {
                    var name = attribute.AttributeType.Name;

                    // HttpPostAttribute -> POST. [Route] carries no verb, so it answers any.
                    var verb = name switch
                    {
                        "RouteAttribute" => "*",
                        _ when name.StartsWith("Http", StringComparison.Ordinal) &&
                               name.EndsWith("Attribute", StringComparison.Ordinal) &&
                               name.Length > "HttpAttribute".Length =>
                            name[4..^9].ToUpperInvariant(),
                        _ => null,
                    };

                    if (verb is null) continue;
                    if (attribute.ConstructorArguments.FirstOrDefault().Value is not string template) continue;

                    routes.Add(new Route(verb, template,
                        template.Split('/', StringSplitOptions.RemoveEmptyEntries), method));
                }
            }
        }

        var (middleware, problems) = DiscoverMiddleware(assembly);

        // Literal-heavy templates first, so /draw/fixed wins over /draw/{text}.
        return new FunctionRouter(
            routes.OrderByDescending(r => r.Segments.Count(s => !s.StartsWith('{'))).ToList(),
            middleware,
            problems);
    }

    /// <summary>
    /// Finds <c>[Middleware]</c> on every method of every type, not only the public static
    /// methods of public classes that handlers are looked for on. A handler that is not found
    /// answers nothing, which the author notices at once; a gate that is not found lets every
    /// request through, which they may never notice. So a misdeclared one is reported instead.
    /// </summary>
    private static (List<MiddlewareMethod> Middleware, List<string> Problems) DiscoverMiddleware(Assembly assembly)
    {
        const BindingFlags everyMethod = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        var middleware = new List<MiddlewareMethod>();
        var problems = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(everyMethod))
            {
                var attribute = method.GetCustomAttributesData()
                    .FirstOrDefault(a => a.AttributeType.Name == MiddlewareAttributeName);
                if (attribute is null) continue;

                if (CheckMiddleware(method) is { } problem)
                {
                    problems.Add(problem);
                    continue;
                }

                var order = attribute.NamedArguments
                    .FirstOrDefault(argument => argument.MemberName == "Order").TypedValue.Value as int? ?? 0;
                middleware.Add(new MiddlewareMethod(order, method));
            }
        }

        var ordered = middleware
            .OrderBy(m => m.Order)
            .ThenBy(m => m.Method.DeclaringType?.Name, StringComparer.Ordinal)
            .ThenBy(m => m.Method.Name, StringComparer.Ordinal)
            .ToList();

        return (ordered, problems);
    }

    /// <summary>
    /// Why a <c>[Middleware]</c> method cannot run, or null when it can: it must be public static
    /// on a public class, take the <c>Func&lt;Task&gt;</c> it goes on with, and return Task,
    /// ValueTask or nothing. An <c>async void</c> method is refused as well: it returns at its
    /// first await, so the request would carry on, and even finish, while it still runs.
    /// </summary>
    private static string? CheckMiddleware(MethodInfo method)
    {
        var name = $"{method.DeclaringType?.Name}.{method.Name}";

        if (!method.IsPublic || !method.IsStatic || method.DeclaringType is { IsVisible: false })
        {
            return $"{name} is marked [Middleware] but is not a public static method of a public class. " +
                   "Declare it public static, in a public class.";
        }

        if (!method.GetParameters().Any(p => p.ParameterType == typeof(Func<Task>)))
        {
            return $"{name} is marked [Middleware] but has no Func<Task> parameter. Add Func<Task> next, and " +
                   "await next() to go on to the rest of the site; not calling it ends the request.";
        }

        var returns = method.ReturnType;
        if (returns != typeof(Task) && returns != typeof(ValueTask) && returns != typeof(void))
        {
            return $"{name} is marked [Middleware] but returns {TypeName(returns)}. Return Task, ValueTask or " +
                   "nothing: middleware answers by writing to context.Response, if it answers at all.";
        }

        if (returns == typeof(void) && method.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false))
        {
            return $"{name} is marked [Middleware] but is async void, which cannot be waited for, so the " +
                   "request would go on without it. Make it async Task.";
        }

        return null;
    }

    /// <summary>A type as it would be written in C#'s own terms, near enough: Task&lt;Boolean&gt; rather than Task`1.</summary>
    internal static string TypeName(Type type)
    {
        var tick = type.Name.IndexOf('`');
        return type.IsGenericType && tick > 0
            ? $"{type.Name[..tick]}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>"
            : type.Name;
    }

    /// <summary>
    /// Routes that would answer exactly the same requests, so that one of them could never run:
    /// the same method (or [Route], which takes any) and the same path shape, whatever the
    /// parameters are called. Easy to do by accident once handlers are spread over several files.
    /// </summary>
    public IReadOnlyList<string> FindConflicts()
    {
        static string Shape(Route route) => "/" + string.Join('/', route.Segments.Select(segment =>
            !segment.StartsWith('{') ? segment.ToLowerInvariant()
            : segment.EndsWith("?}") ? "{?}"
            : "{}"));

        static string Name(Route route) => $"{route.Method.DeclaringType?.Name}.{route.Method.Name}";

        var conflicts = new List<string>();

        for (var i = 0; i < Routes.Count; i++)
        {
            for (var j = i + 1; j < Routes.Count; j++)
            {
                var (a, b) = (Routes[i], Routes[j]);
                var sameVerb = a.Verb == b.Verb || a.Verb == "*" || b.Verb == "*";

                if (sameVerb && Shape(a) == Shape(b))
                {
                    conflicts.Add(
                        $"{(a.Verb == "*" ? b.Verb : a.Verb)} {a.Template} is handled by both {Name(a)} and {Name(b)}.");
                }
            }
        }

        return conflicts;
    }

    /// <summary>
    /// Handles the request if a route matches. Returns false when no route's path matches, so
    /// the caller can fall through to static content; a path match with the wrong verb is
    /// answered here with 405 rather than falling through.
    /// </summary>
    public async Task<bool> TryDispatchAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var pathMatched = false;
        Route? matched = null;
        Dictionary<string, string> routeValues = new(StringComparer.OrdinalIgnoreCase);

        foreach (var route in Routes)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!TryMatch(route.Segments, segments, values)) continue;

            pathMatched = true;

            if (route.Verb is not "*" &&
                !route.Verb.Equals(context.Request.Method, StringComparison.OrdinalIgnoreCase)) continue;

            matched = route;
            routeValues = values;
            break;
        }

        if (matched is null)
        {
            if (!pathMatched) return false;

            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return true;
        }

        foreach (var pair in routeValues) context.Request.RouteValues[pair.Key] = pair.Value;

        if (!TryBindArguments(matched.Method, context, routeValues, next: null, out var arguments, out var bindError))
        {
            await Results.Text(bindError, statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context);
            return true;
        }

        var result = await UnwrapAsync(Invoke(matched.Method, arguments), matched.Method.ReturnType);
        await WriteAsync(context, result, matched.Method);
        return true;
    }

    /// <summary>
    /// Runs <see cref="Middleware"/> around <paramref name="next"/>: the first gets a
    /// <c>Func&lt;Task&gt;</c> that runs the second, and so on, and the last one's runs
    /// <paramref name="next"/>. A method that returns without calling its <c>next</c> ends the
    /// request there. With no middleware this is simply <paramref name="next"/>.
    /// </summary>
    /// <remarks>
    /// A simple value that does not convert gets a 400, as it would for a handler, and the
    /// request goes no further. Exceptions are the caller's, as a handler's are. Each method's
    /// <c>next</c> runs the rest of the request once: a second call throws, rather than running
    /// the handlers, or serving the file, a second time into a response already under way.
    /// </remarks>
    public Task InvokeMiddlewareAsync(HttpContext context, Func<Task> next) => InvokeMiddlewareAsync(0, context, next);

    private Task InvokeMiddlewareAsync(int index, HttpContext context, Func<Task> next) =>
        index == Middleware.Count ? next() : InvokeOneMiddlewareAsync(index, context, next);

    private async Task InvokeOneMiddlewareAsync(int index, HttpContext context, Func<Task> next)
    {
        var method = Middleware[index].Method;
        var called = 0;
        Func<Task> rest = () => Interlocked.Exchange(ref called, 1) == 0
            ? InvokeMiddlewareAsync(index + 1, context, next)
            : throw new InvalidOperationException(
                $"next was already called: {method.DeclaringType?.Name}.{method.Name} called it a second time. Middleware " +
                "runs the rest of the request once, so call next() at most once, and await it.");

        if (!TryBindArguments(method, context, FrozenDictionary<string, string>.Empty, rest, out var arguments, out var bindError))
        {
            await Results.Text(bindError, statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context);
            return;
        }

        await UnwrapAsync(Invoke(method, arguments), method.ReturnType);
    }

    internal static object? Invoke(MethodInfo method, object?[] arguments)
    {
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Surface the function's own exception, not reflection's wrapper around it.
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    /// <summary>
    /// Awaits a Task / ValueTask and extracts its value. The value is read only when the
    /// declared return type is generic: a non-generic Task returned by an async method is a
    /// Task&lt;VoidTaskResult&gt; at runtime, whose Result must not be mistaken for a body.
    /// </summary>
    internal static async Task<object?> UnwrapAsync(object? returned, Type declared)
    {
        switch (returned)
        {
            case Task task:
                await task;
                return declared.IsGenericType ? task.GetType().GetProperty("Result")?.GetValue(task) : null;

            case ValueTask valueTask:
                await valueTask;
                return null;

            case not null when declared.IsGenericType &&
                               declared.GetGenericTypeDefinition() == typeof(ValueTask<>):
                var asTask = (Task)declared.GetMethod(nameof(ValueTask<int>.AsTask))!.Invoke(returned, null)!;
                await asTask;
                return asTask.GetType().GetProperty("Result")?.GetValue(asTask);

            default:
                return returned;
        }
    }

    /// <summary>
    /// Turns what a handler returned into a response. IResult lives in the shared framework,
    /// not in the uploaded assembly, so this works across the load boundary without reflection.
    /// </summary>
    private static Task WriteAsync(HttpContext context, object? value, MethodInfo method)
    {
        switch (value)
        {
            // Nothing returned is 204, unless the handler already wrote the response itself, in
            // which case what it wrote stands: the status of a started response cannot change.
            case null:
                if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;

            case IResult result:
                return result.ExecuteAsync(context);

            // A bare int is a status code, not a body.
            case int status:
                context.Response.StatusCode = status;
                return Task.CompletedTask;

            case string text:
                return Results.Text(text).ExecuteAsync(context);

            // Anything else would have to go through the host's serializer, which pins the
            // function's types for the life of the process. See the class remarks.
            default:
                throw new InvalidOperationException(
                    $"{method.DeclaringType?.Name}.{method.Name} returned {value.GetType().Name}. " +
                    "Handlers must return IResult, string, int or nothing; serialise objects with " +
                    "Results.Json(value, options) using options held in a static field of your file.");
        }
    }

    /// <param name="next">What a middleware method's <c>Func&lt;Task&gt;</c> runs; null for a handler or a hook.</param>
    internal static bool TryBindArguments(
        MethodInfo method,
        HttpContext context,
        IReadOnlyDictionary<string, string> routeValues,
        Func<Task>? next,
        out object?[] arguments,
        out string error)
    {
        var parameters = method.GetParameters();
        arguments = new object?[parameters.Length];
        error = "";

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var type = parameter.ParameterType;

            if (type == typeof(HttpContext)) { arguments[i] = context; continue; }
            if (type == typeof(HttpRequest)) { arguments[i] = context.Request; continue; }
            if (type == typeof(HttpResponse)) { arguments[i] = context.Response; continue; }
            if (type == typeof(CancellationToken)) { arguments[i] = context.RequestAborted; continue; }
            if (type == typeof(Func<Task>) && next is not null) { arguments[i] = next; continue; }
            if (type == typeof(ISite)) { arguments[i] = SiteOf(context, method); continue; }
            if (type == typeof(ISiteVariables)) { arguments[i] = SiteOf(context, method).Variables; continue; }
            if (type == typeof(IRealtime)) { arguments[i] = SiteOf(context, method).Realtime; continue; }
            if (type == typeof(IAiChat)) { arguments[i] = SiteOf(context, method).Ai; continue; }

            if (type == typeof(DirectoryInfo))
            {
                // A request still running after its functions were unloaded may have outlived its site,
                // and must not recreate the folder a delete removed (see SiteContext).
                if (context.TryGetSite(out var owner) && owner is SiteContext { IsRetired: true } retired) throw retired.RetiredError();

                arguments[i] = context.Items[DataDirectoryItem] is string dir ? Directory.CreateDirectory(dir) : null;
                continue;
            }

            if (type == typeof(IReadOnlyDictionary<string, string>))
            {
                arguments[i] = context.Items[VariablesItem] as IReadOnlyDictionary<string, string>
                               ?? FrozenDictionary<string, string>.Empty;
                continue;
            }

            if (type == typeof(IServiceProvider)) { arguments[i] = SiteOf(context, method).Services; continue; }

            if (type == typeof(ILogger))
            {
                arguments[i] = SiteOf(context, method).Services.GetService<ILogger>() ?? NullLogger.Instance;
                continue;
            }

            // The functions' own services come before the route and the query, but only for types
            // that could not come from them: a string registered as a service would otherwise take
            // over every string parameter.
            if (!IsSimpleValue(type) && context.TryGetSite(out var site) && site.Services.GetService(type) is { } service)
            {
                arguments[i] = service;
                continue;
            }

            // Request.Path and the query are already decoded, so values are taken as they are.
            string? raw = routeValues.TryGetValue(parameter.Name!, out var fromRoute)
                ? fromRoute
                : context.Request.Query.TryGetValue(parameter.Name!, out var fromQuery)
                    ? fromQuery.ToString()
                    : null;

            if (raw is null)
            {
                arguments[i] = parameter.HasDefaultValue
                    ? parameter.DefaultValue
                    : type.IsValueType && Nullable.GetUnderlyingType(type) is null
                        ? Activator.CreateInstance(type)
                        : null;
                continue;
            }

            if (!TryConvert(raw, type, out arguments[i]))
            {
                error = $"'{raw}' is not a valid value for '{parameter.Name}'.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True for the types a handler takes from the route or the query string: strings, numbers,
    /// booleans, enums, dates, times and Guids, and their nullable forms. Code that runs outside a
    /// request may not take them (see <see cref="FunctionJobs"/>), and they are never looked for
    /// among the functions' services.
    /// </summary>
    internal static bool IsSimpleValue(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        return underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(string) || underlying == typeof(decimal) ||
               underlying == typeof(Guid) || underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset) ||
               underlying == typeof(TimeSpan) || underlying == typeof(DateOnly) || underlying == typeof(TimeOnly);
    }

    /// <summary>The request's <see cref="ISite"/>, which the host (or the test runner) always puts there before a function runs.</summary>
    private static ISite SiteOf(HttpContext context, MethodInfo method) =>
        context.TryGetSite(out var site)
            ? site
            : throw new InvalidOperationException(
                $"{method.DeclaringType?.Name}.{method.Name} takes the site, but none was put on this request. The host " +
                "does that before any function runs, so this is a fault in Static Site Host rather than in the function.");

    private static bool TryConvert(string value, Type target, out object? converted)
    {
        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        converted = null;

        if (underlying == typeof(string)) { converted = value; return true; }

        if (underlying == typeof(Guid))
        {
            if (!Guid.TryParse(value, out var guid)) return false;
            converted = guid;
            return true;
        }

        if (underlying.IsEnum)
        {
            if (!Enum.TryParse(underlying, value, ignoreCase: true, out var member)) return false;
            converted = member;
            return true;
        }

        try
        {
            converted = Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>Literals must match; "{x}" captures; "{x?}" captures or may be absent.</summary>
    private static bool TryMatch(string[] template, string[] actual, Dictionary<string, string> values)
    {
        var required = template.Count(segment => !segment.EndsWith("?}"));
        if (actual.Length < required || actual.Length > template.Length) return false;

        for (var i = 0; i < template.Length; i++)
        {
            var segment = template[i];

            if (segment.StartsWith('{') && segment.EndsWith('}'))
            {
                if (i < actual.Length) values[segment[1..^1].TrimEnd('?')] = actual[i];
                continue;
            }

            if (i >= actual.Length) return false;
            if (!segment.Equals(actual[i], StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }
}
