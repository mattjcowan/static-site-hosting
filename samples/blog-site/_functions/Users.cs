#:package StaticSiteHost.Abstractions@*

// Managing accounts. Administrators only.
//
// A new account and a reset both end with "must change password": whoever set it, the
// person it belongs to picks their own at first sign-in. Leave the password empty and one
// is generated and returned once, for the administrator to pass on.
//
// A change to someone's role, a password reset and a deleted account are also sent to that person's
// open pages, as account.changed, so the studio can tell them at once (see Notify below).

using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using StaticSiteHost.Functions;

public sealed record UserRequest(string? Username, string? DisplayName, string? Role, string? Password);
public sealed record ResetRequest(string? Password);

public static partial class UserHandlers
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,31}$")]
    private static partial Regex UsernamePattern();

    [HttpGet("/api/users")]
    public static async Task<IResult> List(HttpContext context)
    {
        await using var db = await Blog.OpenAsync(context);
        var (_, denied) = await Blog.RequireAsync(context, db, admin: true);
        if (denied is not null) return denied;

        var users = await db.QueryAsync<BlogUser>("SELECT * FROM users ORDER BY username");
        return Blog.Ok(new { users = users.Select(u => u.ToPublic()) });
    }

    [HttpPost("/api/users")]
    public static async Task<IResult> Create(HttpContext context)
    {
        await using var db = await Blog.OpenAsync(context);
        var (_, denied) = await Blog.RequireAsync(context, db, admin: true);
        if (denied is not null) return denied;

        var body = await Blog.ReadAsync<UserRequest>(context);
        var username = body?.Username?.Trim().ToLowerInvariant() ?? "";
        if (!UsernamePattern().IsMatch(username))
            return Blog.Error(400, "Usernames are 2–32 lowercase letters, digits, dots, dashes or underscores.");
        if (body!.Role is not ("admin" or "editor")) return Blog.Error(400, "The role is admin or editor.");

        var (password, generated, problem) = ChoosePassword(body.Password);
        if (problem is not null) return Blog.Error(400, problem);

        var user = new BlogUser
        {
            Username = username,
            DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? username : body.DisplayName.Trim(),
            Role = body.Role,
            PasswordHash = Blog.HashPassword(password),
            MustChangePassword = true,
            SecurityStamp = Blog.NewStamp(),
            CreatedUtc = Blog.Now(),
        };

        try
        {
            user.Id = await db.ExecuteScalarAsync<long>("""
                INSERT INTO users (username, display_name, role, password_hash, must_change_password, security_stamp, created_utc)
                VALUES (@Username, @DisplayName, @Role, @PasswordHash, 1, @SecurityStamp, @CreatedUtc)
                RETURNING id
                """, user);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19) // constraint
        {
            return Blog.Error(409, $"There is already an account called {username}.");
        }

        return Blog.Ok(new { user = user.ToPublic(), password = generated ? password : null });
    }

    [HttpPut("/api/users/{id}")]
    public static async Task<IResult> Update(HttpContext context, IRealtime realtime, long id)
    {
        await using var db = await Blog.OpenAsync(context);
        var (me, denied) = await Blog.RequireAsync(context, db, admin: true);
        if (denied is not null) return denied;

        var user = await db.QuerySingleOrDefaultAsync<BlogUser>("SELECT * FROM users WHERE id = @id", new { id });
        if (user is null) return Blog.Error(404, "No such account.");

        var body = await Blog.ReadAsync<UserRequest>(context);
        var role = body?.Role ?? user.Role;
        if (role is not ("admin" or "editor")) return Blog.Error(400, "The role is admin or editor.");
        if (user.IsAdmin && role != "admin" && await AdminCountAsync(db) == 1)
            return Blog.Error(400, "This is the only administrator. Make someone else an administrator first.");

        await db.ExecuteAsync("UPDATE users SET display_name = @name, role = @role WHERE id = @id", new
        {
            id,
            role,
            name = string.IsNullOrWhiteSpace(body?.DisplayName) ? user.DisplayName : body.DisplayName.Trim(),
        });

        var updated = await db.QuerySingleAsync<BlogUser>("SELECT * FROM users WHERE id = @id", new { id });
        if (updated.Role != user.Role)
            await Notify(context, realtime, updated.Username, "role", $"An administrator made you {(updated.IsAdmin ? "an administrator" : "an editor")}.");

        return Blog.Ok(new { user = updated.ToPublic(), self = me!.Id == id });
    }

    /// <summary>Sets a new password (given or generated) and signs the account out everywhere.</summary>
    [HttpPost("/api/users/{id}/reset-password")]
    public static async Task<IResult> ResetPassword(HttpContext context, IRealtime realtime, long id)
    {
        await using var db = await Blog.OpenAsync(context);
        var (me, denied) = await Blog.RequireAsync(context, db, admin: true);
        if (denied is not null) return denied;
        if (me!.Id == id) return Blog.Error(400, "Change your own password from the Account panel instead.");

        var body = await Blog.ReadAsync<ResetRequest>(context);
        var (password, generated, problem) = ChoosePassword(body?.Password);
        if (problem is not null) return Blog.Error(400, problem);

        var user = await db.QuerySingleOrDefaultAsync<BlogUser>("SELECT * FROM users WHERE id = @id", new { id });
        if (user is null) return Blog.Error(404, "No such account.");

        await db.ExecuteAsync("""
            UPDATE users SET password_hash = @hash, security_stamp = @stamp, must_change_password = 1 WHERE id = @id
            """, new { id, hash = Blog.HashPassword(password), stamp = Blog.NewStamp() });

        await Notify(context, realtime, user.Username, "password", "An administrator reset your password. Sign in again with the one they give you.");
        return Blog.Ok(new { password = generated ? password : null });
    }

    [HttpDelete("/api/users/{id}")]
    public static async Task<IResult> Delete(HttpContext context, IRealtime realtime, long id)
    {
        await using var db = await Blog.OpenAsync(context);
        var (me, denied) = await Blog.RequireAsync(context, db, admin: true);
        if (denied is not null) return denied;
        if (me!.Id == id) return Blog.Error(400, "You cannot delete your own account.");

        var user = await db.QuerySingleOrDefaultAsync<BlogUser>("SELECT * FROM users WHERE id = @id", new { id });
        if (user is null) return Blog.Error(404, "No such account.");
        if (user.IsAdmin && await AdminCountAsync(db) == 1) return Blog.Error(400, "The last administrator cannot be deleted.");

        await db.ExecuteAsync("DELETE FROM users WHERE id = @id", new { id });
        await Notify(context, realtime, user.Username, "deleted", "An administrator removed your account.");
        return Blog.Ok(new { ok = true });
    }

    /// <summary>
    /// Tells every page the person has open (every tab, on any page that connected) what an administrator just did,
    /// as account.changed with { change, message }. Realtime.cs names each connection after its signed-in writer, so
    /// PublishToUserAsync finds them by username. The change is saved whatever happens here.
    /// </summary>
    private static async Task Notify(HttpContext context, IRealtime realtime, string username, string change, string message)
    {
        try
        {
            await realtime.PublishToUserAsync(username, "account.changed", new { change, message }, Blog.Json);
        }
        catch (Exception ex)
        {
            Blog.Logger(context).LogWarning(ex, "Could not tell {Username} that their account changed ({Change})", username, change);
        }
    }

    private static Task<long> AdminCountAsync(Microsoft.Data.Sqlite.SqliteConnection db) =>
        db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM users WHERE role = 'admin'");

    private static (string Password, bool Generated, string? Problem) ChoosePassword(string? requested) =>
        string.IsNullOrEmpty(requested)
            ? (Blog.GeneratePassword(), true, null)
            : (requested, false, Blog.CheckNewPassword(requested));
}
