namespace StaticSiteHost.Functions;

/// <summary>
/// Marks the method that decides whether a page may connect to the site's realtime hub, and who
/// the connection belongs to. Without one every page may connect, and no connection has a user.
/// </summary>
/// <remarks>
/// <para>
/// It runs as each page connects (<c>site.realtime</c> in <c>/_host/site.js</c>), with the request
/// that makes the connection, cookies included, so it can recognise a signed-in visitor the way a
/// handler does. The method is <c>public static</c> on a public class and returns one of:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>string?</c>, <c>Task&lt;string?&gt;</c> or <c>ValueTask&lt;string?&gt;</c>: who the
/// connection belongs to, which becomes <see cref="RealtimeConnection.User"/> and what
/// <see cref="IRealtime.PublishToUserAsync"/> reaches. Null, or an empty string, refuses the
/// connection.
/// </description></item>
/// <item><description>
/// <c>bool</c>, <c>Task&lt;bool&gt;</c> or <c>ValueTask&lt;bool&gt;</c>: true lets the page
/// connect with no user, false refuses it.
/// </description></item>
/// </list>
/// <para>
/// It may take what a handler takes from the request: the <c>HttpContext</c>, an
/// <see cref="ISite"/>, the data folder as a <see cref="DirectoryInfo"/>, the variables, an
/// <see cref="IRealtime"/>, an <see cref="IAiChat"/>, a <see cref="CancellationToken"/>, a
/// non-generic <c>ILogger</c>, the functions' services, and anything registered in a
/// <see cref="ConfigureServicesAttribute">[ConfigureServices]</see> method. Not a value from the
/// query string: the request that connects is SignalR's, and its query string is SignalR's too.
/// </para>
/// <para>
/// Leave the <c>HttpContext</c> as you found it. It lives as long as the connection does, which can
/// be long after these functions have been replaced, and the server puts back its <c>Items</c> and its
/// <c>User</c> once the method returns but cannot take back anything registered on it: so no
/// <c>Response.OnCompleted</c>, no <c>Response.RegisterForDispose</c>, and nothing set in its
/// <c>Features</c>.
/// </para>
/// <para>
/// A refused page is closed as soon as it has connected, and does not try again. A method that
/// throws refuses the page as well, and the exception goes to the server log; so do functions
/// that declare one but cannot be loaded. A gate that fails open is worse than one that fails
/// shut. Middleware does not run for the hub, so this method is the whole of the check.
/// </para>
/// <para>
/// Only the site's own functions are asked. The global functions' hooks never decide for a
/// site. One method per set of functions may carry the attribute; a second fails the build,
/// naming both. So does a method that breaks the rules above, with a message saying what to change.
/// </para>
/// <para>
/// The server finds the attribute by its name, so an attribute of your own called
/// <c>RealtimeConnectAttribute</c> works the same.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [RealtimeConnect]
/// public static async Task&lt;string?&gt; Who(HttpContext context, DirectoryInfo data)
/// {
///     var user = await Accounts.CurrentUserAsync(context, data);
///     return user?.Name ?? "guest";
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RealtimeConnectAttribute : Attribute;
