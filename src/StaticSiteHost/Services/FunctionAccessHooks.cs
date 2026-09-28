using System.Reflection;
using StaticSiteHost.Functions;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services;

/// <summary>
/// The methods a site's functions decide with who may do what from a browser: whether a page may
/// connect to the site's realtime hub and who it belongs to (<c>[RealtimeConnect]</c>), whether it
/// may join a group (<c>[RealtimeJoin]</c>), and whether a browser may chat through the site's AI
/// (<c>[AiAccess]</c>). <see cref="FunctionHost"/> invokes them through the
/// <see cref="MethodInfo"/>s held here, which belong to the <see cref="FunctionSet"/> and go with it.
///
/// Found by attribute name, like everything else in a bundle, and on every method rather than
/// only the public static ones. A hook that is not found lets everyone in, which nobody would
/// notice, so one that is misdeclared is reported in <see cref="Problems"/> instead, and the build
/// is refused. So is a second method with the same attribute: which of two gates applies must
/// never depend on the order the compiler laid the types out in.
///
/// A hook runs with a request: the one that made the realtime connection, or the chat request.
/// It may take whatever a handler takes from one, except simple values, which a handler reads from
/// the route or the query string. A hook has no route, and the query string of a realtime
/// connection is SignalR's own, so such a parameter could only ever receive something meaningless.
/// The one exception is the join hook's <c>string group</c>, which is bound to the group the page
/// asked for, by name, the way a handler's route values are.
/// </summary>
public sealed class FunctionAccessHooks
{
    /// <summary>The three hooks, named as their attributes are without the suffix, and as a bundle records them.</summary>
    public enum Kind
    {
        RealtimeConnect,
        RealtimeJoin,
        AiAccess,
    }

    /// <summary>The join hook's parameter that receives the group's name.</summary>
    public const string GroupParameter = "group";

    private static readonly Kind[] Kinds = Enum.GetValues<Kind>();

    /// <summary>The attributes of the other roles a method can have, which a hook cannot share.</summary>
    private static readonly string[] OtherRoles =
        ["MiddlewareAttribute", "ConfigureServicesAttribute", "BackgroundServiceAttribute", "ScheduleAttribute", "EveryAttribute"];

    private static readonly Type[] AllowAnswers = [typeof(bool), typeof(Task<bool>), typeof(ValueTask<bool>)];

    private static readonly Type[] UserAnswers = [typeof(string), typeof(Task<string>), typeof(ValueTask<string>)];

    private readonly Dictionary<Kind, MethodInfo> _methods;

    private FunctionAccessHooks(Dictionary<Kind, MethodInfo> methods, List<string> problems)
    {
        _methods = methods;
        Problems = problems;
    }

    /// <summary>
    /// Hook methods that cannot run, and second methods for the same hook, one or two sentences
    /// each saying what to change. A build that has any is refused (see <see cref="FunctionBundleBuilder"/>).
    /// </summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>True when the functions declare no hook.</summary>
    public bool IsEmpty => _methods.Count == 0;

    /// <summary>The method for a hook, or null when the functions declare none.</summary>
    public MethodInfo? Get(Kind kind) => _methods.GetValueOrDefault(kind);

    /// <summary>Each hook as a bundle records it (see <see cref="Display"/>), in the order of <see cref="Kind"/>.</summary>
    public IEnumerable<(string Display, MethodInfo Method)> All =>
        Kinds.Where(_methods.ContainsKey).Select(kind => (Display(kind, _methods[kind]), _methods[kind]));

    /// <summary>How <see cref="FunctionBundle.Hooks"/> records a hook: <c>RealtimeConnect Realtime.Who</c>.</summary>
    public static string Display(Kind kind, MethodInfo method) => $"{kind} {FunctionJobs.NameOf(method)}";

    /// <summary>
    /// True when <paramref name="bundle"/> declares the hook, going by what its build recorded.
    /// This is what decides whether the hook is asked at all, so the Functions card and the gate
    /// never disagree.
    /// </summary>
    public static bool Declares(FunctionBundle? bundle, Kind kind)
    {
        if (bundle is null) return false;

        var prefix = kind + " ";
        return bundle.Hooks.Any(hook => hook.StartsWith(prefix, StringComparison.Ordinal));
    }

    public static FunctionAccessHooks Discover(Assembly assembly)
    {
        const BindingFlags everyMethod = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        var found = new Dictionary<Kind, List<MethodInfo>>();
        var problems = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(everyMethod))
            {
                var names = method.GetCustomAttributesData().Select(a => a.AttributeType.Name).ToList();
                var kinds = Kinds.Where(kind => names.Contains(AttributeName(kind))).ToList();
                if (kinds.Count == 0) continue;

                var roles = kinds.Select(Label)
                    .Concat(OtherRoles.Where(names.Contains).Select(name => $"[{name[..^"Attribute".Length]}]"))
                    .ToList();

                if (roles.Count > 1)
                {
                    problems.Add($"{FunctionJobs.NameOf(method)} is marked {string.Join(" and ", roles)}, but a method can have " +
                                 "only one of these. Give each its own method.");
                    continue;
                }

                if (Check(method, kinds[0]) is { } problem)
                {
                    problems.Add(problem);
                    continue;
                }

                if (!found.TryGetValue(kinds[0], out var list)) found[kinds[0]] = list = [];
                list.Add(method);
            }
        }

        var methods = new Dictionary<Kind, MethodInfo>();
        foreach (var (kind, list) in found)
        {
            if (list.Count == 1)
            {
                methods[kind] = list[0];
                continue;
            }

            var named = list.Select(FunctionJobs.NameOf).Order(StringComparer.Ordinal).ToList();
            var (all, others) = named.Count == 2 ? ("both", "the other") : ("all", "the others");
            problems.Add($"{string.Join(" and ", named)} are {all} marked {Label(kind)}, but a set of functions can have only " +
                         $"one, since it alone decides who is let in. Keep one, and have it call {others} if need be.");
        }

        return new FunctionAccessHooks(methods, problems);
    }

    /// <summary>
    /// Why <paramref name="method"/> cannot be the <paramref name="kind"/> hook, or null when it can:
    /// public static on a public class, not generic, returning an answer the hook can give, taking
    /// nothing a request cannot fill, and for the join hook taking the group's name.
    /// </summary>
    public static string? Check(MethodInfo method, Kind kind)
    {
        var name = FunctionJobs.NameOf(method);
        var label = Label(kind);

        if (!method.IsPublic || !method.IsStatic || method.DeclaringType is { IsVisible: false })
        {
            return $"{name} is marked {label} but is not a public static method of a public class. Declare it public " +
                   "static, in a public class.";
        }

        if (method.ContainsGenericParameters)
            return $"{name} is marked {label} but is generic, and nothing says what to call it with. Remove its type parameters.";

        var returns = method.ReturnType;
        var answers = kind == Kind.RealtimeConnect ? [.. AllowAnswers, .. UserAnswers] : AllowAnswers;
        if (!answers.Contains(returns))
        {
            return kind == Kind.RealtimeConnect
                ? $"{name} is marked {label} but returns {FunctionRouter.TypeName(returns)}. Return string? (who the " +
                  "connection belongs to, or null to refuse it) or bool (true to let it connect), or a Task or ValueTask of either."
                : $"{name} is marked {label} but returns {FunctionRouter.TypeName(returns)}. Return bool, Task<bool> or " +
                  $"ValueTask<bool>: true to {(kind == Kind.RealtimeJoin ? "let the page join" : "let the browser chat")}, " +
                  "false to refuse.";
        }

        var takesGroup = false;
        foreach (var parameter in method.GetParameters())
        {
            var type = parameter.ParameterType;
            var takes = $"{name} is marked {label} and takes {FunctionRouter.TypeName(type.IsByRef ? type.GetElementType()! : type)} {parameter.Name}";

            if (type.IsByRef) return $"{takes} by reference, which nothing can fill in. Take it by value.";
            if (type == typeof(Func<Task>)) return $"{takes}, which only [Middleware] is given. Remove the parameter.";

            if (type == typeof(IServiceCollection))
            {
                return $"{takes}, which only [ConfigureServices] methods are given, while the services are being set up. " +
                       "Register services there, and take them here as parameters.";
            }

            var isGroup = string.Equals(parameter.Name, GroupParameter, StringComparison.OrdinalIgnoreCase);
            if (kind == Kind.RealtimeJoin && isGroup)
            {
                if (type != typeof(string))
                    return $"{takes}, but the group is a name. Make it string {GroupParameter}.";

                takesGroup = true;
                continue;
            }

            if (FunctionRouter.IsSimpleValue(type))
            {
                return $"{takes}, a value a handler would take from the route or the query string, but a hook has " +
                       "neither of its own. Remove the parameter, and read what you need from the HttpContext, such as " +
                       "a cookie" + (kind == Kind.RealtimeJoin ? $"; the group the page asked for is string {GroupParameter}." : ".");
            }
        }

        if (kind == Kind.RealtimeJoin && !takesGroup)
        {
            return $"{name} is marked {label} but has no string {GroupParameter} parameter, so it cannot tell which group the " +
                   $"page asked to join. Add string {GroupParameter}.";
        }

        return null;
    }

    /// <summary>
    /// What a hook's answer, once awaited, says: whether it lets the request through, and for a
    /// connect hook that returned a string, who the connection belongs to. A null or empty string
    /// refuses, as does anything that is not a bool or a string.
    /// </summary>
    public static (bool Allowed, string? User) ReadAnswer(object? answer) => answer switch
    {
        bool allowed => (allowed, null),
        string { Length: > 0 } user => (true, user),
        _ => (false, null),
    };

    /// <summary>The attribute a hook is found by: <c>RealtimeConnectAttribute</c>.</summary>
    public static string AttributeName(Kind kind) => $"{kind}Attribute";

    /// <summary>The attribute as written on a method, for messages: <c>[RealtimeConnect]</c>.</summary>
    public static string Label(Kind kind) => $"[{kind}]";
}
