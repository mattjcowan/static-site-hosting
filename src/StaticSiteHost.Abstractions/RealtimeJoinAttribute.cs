namespace StaticSiteHost.Functions;

/// <summary>
/// Marks the method that decides whether a page may join one of the site's realtime groups, which
/// a page asks for with <c>site.realtime.join(group)</c>. Without one a page may join any group.
/// </summary>
/// <remarks>
/// <para>
/// It runs each time a page asks to join a group, including when a page that lost its connection
/// joins its groups again, with the request that made the page's connection, cookies included. The
/// method is <c>public static</c> on a public class, returns <c>bool</c>, <c>Task&lt;bool&gt;</c>
/// or <c>ValueTask&lt;bool&gt;</c>, where false refuses, and takes a <c>string group</c> parameter,
/// found by its name, which receives the group the page asked for. Otherwise it may take what a
/// <see cref="RealtimeConnectAttribute">[RealtimeConnect]</see> method takes. To decide by who the
/// visitor is, look them up from the request's cookies again, as the example does: the cookies
/// are the ones the page connected with.
/// </para>
/// <para>
/// Leave the <c>HttpContext</c> as you found it. It is the connection's, and lives as long as the
/// connection does, which can be long after these functions have been replaced; the server puts back
/// its <c>Items</c> and its <c>User</c> once the method returns but cannot take back anything
/// registered on it: so no <c>Response.OnCompleted</c>, no <c>Response.RegisterForDispose</c>, and
/// nothing set in its <c>Features</c>.
/// </para>
/// <para>
/// A refused page's <c>join</c> rejects with a message saying the site did not let it join. A
/// method that throws refuses as well, and the exception goes to the server log; so do functions
/// that declare one but cannot be loaded. Only joining is checked: leaving is always allowed, and
/// a function can put a connection in any group with <see cref="IRealtime.AddToGroupAsync"/>.
/// </para>
/// <para>
/// Only the site's own functions are asked. One method per set of functions may carry the
/// attribute; a second fails the build, naming both. So does a method that breaks the rules
/// above, with a message saying what to change.
/// </para>
/// <para>
/// The server finds the attribute by its name, so an attribute of your own called
/// <c>RealtimeJoinAttribute</c> works the same.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [RealtimeJoin]
/// public static async Task&lt;bool&gt; MayJoin(HttpContext context, DirectoryInfo data, string group) =>
///     group == "news" || await Accounts.CurrentUserAsync(context, data) is not null;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RealtimeJoinAttribute : Attribute;
