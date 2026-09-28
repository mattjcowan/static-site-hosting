#:package StaticSiteHost.Abstractions@*

// Who is signed in, worked out once per request and passed on to everything after it.
//
// The blog's session is a cookie of its own (Blog.cs), which nothing else knows how to read. Left at
// that, HttpContext.User stays empty for the rest of the request: for the studio gate, for the blog's
// handlers, for the global functions' handlers and for any middleware after this one. So this
// middleware reads the cookie, and when it belongs to a writer, sets HttpContext.User to a principal
// with their id, username, role, display name and whether they still owe a new password. Anything
// later reads context.User.Identity.IsAuthenticated, User.Identity.Name and User.IsInRole("admin").
//
// It also leaves the writer in HttpContext.Items (Blog.UserItem), so Blog.CurrentUserAsync answers
// from there and the handlers do not ask the database a second time.
//
// Order = 0 runs it before the studio gate (Gate.cs, Order = 10), which reads what it set. The hooks
// in Realtime.cs and Ai.cs run without middleware, so they still read the cookie themselves.

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using StaticSiteHost.Functions;

public static class Session
{
    /// <summary>The principal's authentication type. Any non-empty name makes it authenticated.</summary>
    private const string Scheme = "NightSky";

    [Middleware(Order = 0)]
    public static async Task SignIn(HttpContext context, Func<Task> next)
    {
        BlogUser? user = null;

        // Every page, script and image comes through here. Only a request with the cookie is worth a trip to the
        // database, and the connection is closed again before the rest of the request runs.
        if (context.Request.Cookies.ContainsKey(Blog.SessionCookie))
        {
            await using var db = await Blog.OpenAsync(context);
            user = await Blog.CurrentUserAsync(context, db);
        }

        // Null too, so a cookie that no longer works is not looked up again later in the request.
        context.Items[Blog.UserItem] = user;
        if (user is not null) context.User = Principal(user);

        await next();
    }

    /// <summary>Framework types and strings only, so code that has never seen a BlogUser can read it.</summary>
    private static ClaimsPrincipal Principal(BlogUser user) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.Role, user.Role), // admin or editor
        new Claim("display_name", user.DisplayName),
        new Claim("must_change_password", user.MustChangePassword ? "true" : "false"),
    ], Scheme));
}
