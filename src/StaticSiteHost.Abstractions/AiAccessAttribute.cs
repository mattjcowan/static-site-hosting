namespace StaticSiteHost.Functions;

/// <summary>
/// Marks the method that decides whether a browser may chat through the site's AI at
/// <c>/_host/ai/chat</c>, which <c>site.ai.chat</c> in <c>/_host/site.js</c> calls. With one, the
/// site decides for itself, so its visitors can be let in by who they are rather than all or none.
/// </summary>
/// <remarks>
/// <para>
/// An administrator still chooses the site's provider, and without one there is no chat at all.
/// Once there is, a site whose functions declare this method answers each browser's request by
/// what it returns, and the site's "let every visitor chat" setting is not consulted. A site
/// without one lets browsers chat only when that setting is on. Either way the per-address limit
/// on <c>/_host/ai/chat</c> still applies, and <c>site.ai.enabled</c> is true when the site has a
/// provider and either the setting is on or its functions declare this method.
/// </para>
/// <para>
/// It runs for each chat request, with that request, cookies included, once every cheaper check
/// has passed: the body has been read and found to be a conversation, and the request has been
/// counted against the per-address limit, so a refused one still counts and a stranger cannot make
/// the site run this faster than the limit allows. The method is <c>public static</c> on a public
/// class and returns <c>bool</c>, <c>Task&lt;bool&gt;</c> or <c>ValueTask&lt;bool&gt;</c>, where
/// false refuses with a <c>403</c>. It may take what a
/// <see cref="RealtimeConnectAttribute">[RealtimeConnect]</see> method takes. The body has been read
/// already, so there is nothing left in it to read.
/// </para>
/// <para>
/// Leave the <c>HttpContext</c> as you found it: the server puts back its <c>Items</c> and its
/// <c>User</c> once the method returns, but cannot take back anything registered on it, which would
/// run after the method's services have been disposed. So no <c>Response.OnCompleted</c>, no
/// <c>Response.RegisterForDispose</c>, and nothing set in its <c>Features</c>, as for the realtime
/// hooks, whose context lives as long as the connection.
/// </para>
/// <para>
/// A method that throws refuses as well, and the exception goes to the server log; so do functions
/// that declare one but cannot be loaded. Only the site's own functions are asked, and middleware
/// does not run for <c>/_host/</c>. One method per set of functions may carry the attribute; a
/// second fails the build, naming both. So does a method that breaks the rules above, with a
/// message saying what to change.
/// </para>
/// <para>
/// Functions calling <see cref="ISite.Ai"/> are not affected: they decide for themselves.
/// </para>
/// <para>
/// The server finds the attribute by its name, so an attribute of your own called
/// <c>AiAccessAttribute</c> works the same.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [AiAccess]
/// public static async Task&lt;bool&gt; SignedIn(HttpContext context, DirectoryInfo data) =>
///     await Accounts.CurrentUserAsync(context, data) is not null;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AiAccessAttribute : Attribute;
