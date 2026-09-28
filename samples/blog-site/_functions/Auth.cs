// Sign in, sign out, who am I, and changing your own password.

using System.Collections.Concurrent;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

public sealed record LoginRequest(string? Username, string? Password);
public sealed record PasswordRequest(string? Current, string? Next);

public static class AuthHandlers
{
    // Five misses for one name from one address, then a quarter of an hour's wait.
    private const int MaxFailures = 5;
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, (int Failures, DateTime Until)> Attempts = new();

    [HttpPost("/api/auth/login")]
    public static async Task<IResult> Login(HttpContext context)
    {
        if (!context.Request.Headers.ContainsKey(Blog.CsrfHeader))
            return Blog.Error(400, $"Send the {Blog.CsrfHeader} header.");

        var body = await Blog.ReadAsync<LoginRequest>(context);
        var username = body?.Username?.Trim() ?? "";
        var key = $"{context.Connection.RemoteIpAddress}|{username.ToLowerInvariant()}";

        if (Attempts.TryGetValue(key, out var attempt) && attempt.Until > DateTime.UtcNow)
            return Blog.Error(429, "Too many attempts. Try again in a few minutes.");

        await using var db = await Blog.OpenAsync(context);
        var user = await db.QuerySingleOrDefaultAsync<BlogUser>(
            "SELECT * FROM users WHERE username = @username", new { username });

        // Verify against something either way, so an unknown name takes as long as a wrong password.
        var valid = Blog.VerifyPassword(body?.Password ?? "", user?.PasswordHash ?? Blog.DecoyHash) && user is not null;

        if (!valid)
        {
            var failures = (Attempts.TryGetValue(key, out var previous) ? previous.Failures : 0) + 1;
            Attempts[key] = failures >= MaxFailures ? (0, DateTime.UtcNow + Lockout) : (failures, DateTime.MinValue);
            return Blog.Error(401, "That username and password do not match.");
        }

        Attempts.TryRemove(key, out _);
        await db.ExecuteAsync("UPDATE users SET last_login_utc = @now WHERE id = @id", new { now = Blog.Now(), id = user!.Id });

        Blog.SignIn(context, user);
        return Blog.Ok(new { user = user.ToPublic(), mustChangePassword = user.MustChangePassword });
    }

    [HttpPost("/api/auth/logout")]
    public static IResult Logout(HttpContext context)
    {
        Blog.SignOut(context);
        return Blog.Ok(new { ok = true });
    }

    /// <summary>Always 200, so a signed-out visitor's page load does not log an error in the console.</summary>
    [HttpGet("/api/auth/me")]
    public static async Task<IResult> Me(HttpContext context)
    {
        await using var db = await Blog.OpenAsync(context);
        var user = await Blog.CurrentUserAsync(context, db);
        return Blog.Ok(new { user = user?.ToPublic() });
    }

    [HttpPost("/api/auth/password")]
    public static async Task<IResult> ChangePassword(HttpContext context, DirectoryInfo data)
    {
        await using var db = await Blog.OpenAsync(context);
        var (user, denied) = await Blog.RequireAsync(context, db, allowPendingPasswordChange: true);
        if (denied is not null) return denied;

        var body = await Blog.ReadAsync<PasswordRequest>(context);
        if (!Blog.VerifyPassword(body?.Current ?? "", user!.PasswordHash))
            return Blog.Error(400, "Your current password is not right.");
        if (Blog.CheckNewPassword(body?.Next) is { } problem) return Blog.Error(400, problem);
        if (body!.Next == body.Current) return Blog.Error(400, "Choose a password different from the current one.");

        // A new stamp ends every other session; this one is reissued below.
        user.PasswordHash = Blog.HashPassword(body.Next!);
        user.SecurityStamp = Blog.NewStamp();
        user.MustChangePassword = false;

        await db.ExecuteAsync("""
            UPDATE users SET password_hash = @PasswordHash, security_stamp = @SecurityStamp, must_change_password = 0
            WHERE id = @Id
            """, user);

        // The generated first password has done its job; do not leave it lying around.
        if (user.IsAdmin) File.Delete(Path.Combine(data.FullName, "initial-admin-password.txt"));

        Blog.SignIn(context, user);
        return Blog.Ok(new { user = user.ToPublic() });
    }
}
