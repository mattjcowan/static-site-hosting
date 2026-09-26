using System.Globalization;
using System.Reflection;

namespace StaticSiteHost.Services;

/// <summary>
/// Maps requests onto the handler methods of an uploaded function assembly.
///
/// Handlers are discovered by attribute <em>name</em> — <c>[HttpGet("/x")]</c>,
/// <c>[HttpPost]</c>, … and <c>[Route]</c> for any verb — rather than by type. The attributes
/// are ASP.NET's own, so an uploaded file needs no SDK assembly of ours, and reading them
/// through <see cref="MethodInfo.GetCustomAttributesData"/> never instantiates anything from
/// the loaded context.
///
/// A handler may take: an <see cref="HttpContext"/>, a <see cref="CancellationToken"/>, a
/// <see cref="DirectoryInfo"/> (its data directory, see <see cref="DataDirectoryItem"/>), or,
/// by name, any simple value bound from the route first and the query string second. It returns
/// (optionally wrapped in a Task or ValueTask) an <see cref="IResult"/>, a string, an int
/// status code, or nothing.
///
/// What it may <em>not</em> return is an arbitrary object for the host to serialise. The host's
/// System.Text.Json metadata cache would then hold the handler's types, and a cache in the
/// default context outlives every deploy — the collectible context could never unload, and
/// each redeploy would leak a full copy of the function's assemblies. Handlers serialise their
/// own results with a <c>JsonSerializerOptions</c> held in a static inside the uploaded file
/// (see samples/functions), so that cache dies with the context.
/// </summary>
public sealed class FunctionRouter
{
    /// <summary>
    /// HttpContext.Items key holding the full path of the data directory that belongs to the
    /// functions answering this request: the site's for a site's functions, a shared one for the
    /// global functions. A handler parameter of type <see cref="DirectoryInfo"/> receives it,
    /// created on first use; helper code without that parameter can read it from here. Plain
    /// strings and framework types only, so an uploaded file needs nothing of ours to use it.
    /// </summary>
    public const string DataDirectoryItem = "StaticSiteHost.DataDirectory";

    public sealed record Route(string Verb, string Template, string[] Segments, MethodInfo Method);

    public IReadOnlyList<Route> Routes { get; }

    private FunctionRouter(IReadOnlyList<Route> routes) => Routes = routes;

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

        // Literal-heavy templates first, so /draw/fixed wins over /draw/{text}.
        return new FunctionRouter(routes
            .OrderByDescending(r => r.Segments.Count(s => !s.StartsWith('{')))
            .ToList());
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

        if (!TryBindArguments(matched.Method, context, routeValues, out var arguments, out var bindError))
        {
            await Results.Text(bindError, statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context);
            return true;
        }

        object? returned;
        try
        {
            returned = matched.Method.Invoke(null, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Surface the handler's own exception, not reflection's wrapper around it.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        var result = await UnwrapAsync(returned, matched.Method.ReturnType);
        await WriteAsync(context, result, matched.Method);
        return true;
    }

    /// <summary>
    /// Awaits a Task / ValueTask and extracts its value. The value is read only when the
    /// declared return type is generic: a non-generic Task returned by an async method is a
    /// Task&lt;VoidTaskResult&gt; at runtime, whose Result must not be mistaken for a body.
    /// </summary>
    private static async Task<object?> UnwrapAsync(object? returned, Type declared)
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
            case null:
                context.Response.StatusCode = StatusCodes.Status204NoContent;
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

    private static bool TryBindArguments(
        MethodInfo method,
        HttpContext context,
        Dictionary<string, string> routeValues,
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

            if (type == typeof(DirectoryInfo))
            {
                arguments[i] = context.Items[DataDirectoryItem] is string dir ? Directory.CreateDirectory(dir) : null;
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
