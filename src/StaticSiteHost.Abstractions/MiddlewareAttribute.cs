using Microsoft.AspNetCore.Http;

namespace StaticSiteHost.Functions;

/// <summary>
/// Marks a method that runs for every request to the site before anything answers it: a
/// sign-in gate over a whole section, headers on every response, a line of logging.
/// </summary>
/// <remarks>
/// <para>
/// The method is <c>public static</c> on a public class. It takes the <see cref="HttpContext"/>
/// and a <c>Func&lt;Task&gt; next</c>, plus any parameter a handler may take (an
/// <see cref="ISite"/>, a <see cref="DirectoryInfo"/>, a <see cref="CancellationToken"/>, a
/// simple value from the query string and so on), and returns <see cref="Task"/>,
/// <see cref="ValueTask"/> or nothing. Calling <c>next</c> goes on to the rest of the site; not
/// calling it ends the request with whatever the method wrote. A method that breaks any of
/// these rules fails the build, with a message saying what to change.
/// </para>
/// <para>
/// Middleware runs for every request to the site, its files included, and around both the
/// site's functions and the global ones. The global functions' middleware runs first, outside
/// the site's: global middleware, the site's middleware, the site's functions, the global
/// functions, the site's files.
/// </para>
/// <para>
/// The server finds the attribute by its name, so an attribute of your own called
/// <c>MiddlewareAttribute</c> with an <c>int Order</c> property works the same.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Middleware(Order = 10)]
/// public static async Task Gate(HttpContext context, Func&lt;Task&gt; next)
/// {
///     if (context.Request.Path.StartsWithSegments("/private") &amp;&amp; !context.Request.Headers.ContainsKey("X-Token"))
///     {
///         context.Response.StatusCode = StatusCodes.Status401Unauthorized;
///         return;
///     }
///
///     await next();
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class MiddlewareAttribute : Attribute
{
    /// <summary>
    /// Where the method runs among the other middleware of the same functions: lower runs
    /// first, and so wraps everything after it. Defaults to 0. Ties run in order of class name,
    /// then method name.
    /// </summary>
    public int Order { get; init; }
}
