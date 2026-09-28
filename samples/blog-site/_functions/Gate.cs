#:package StaticSiteHost.Abstractions@*

// The studio is for signed-in writers. Its page holds nothing secret (everything it does goes
// through handlers that check the session themselves), but a visitor without an account has no
// use for it, so they are sent to sign in first and brought back afterwards.
//
// Middleware runs before every request to the site, its pages and files included, so all this
// has to do is recognise the studio's address and let everything else straight through. Who is
// signed in was settled by Session.cs, which runs first (Order = 0) and sets HttpContext.User, so
// the gate asks that rather than the database.

using Microsoft.AspNetCore.Http;
using StaticSiteHost.Functions;

public static class Gate
{
    [Middleware(Order = 10)]
    public static async Task Studio(HttpContext context, Func<Task> next)
    {
        if (IsStudio(context.Request.Path) && context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.Redirect("/login?return=/studio");
            return;
        }

        await next();
    }

    /// <summary>/studio, and /studio.html, the file that answers it (which _redirects sends on to /studio).</summary>
    private static bool IsStudio(PathString path) =>
        path.Equals("/studio", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/studio.html", StringComparison.OrdinalIgnoreCase);
}
