#:sdk Microsoft.NET.Sdk.Web
#:package Microsoft.Data.Sqlite@10.0.12
#:package Dapper@2.1.89

// ─────────────────────────────────────────────────────────────────────────────
//  Night Sky Field Notes: shared plumbing for the other function files.
//
//  Everything the blog stores lives in one SQLite database in the site's data
//  folder, which the host hands to any handler that asks for a DirectoryInfo.
//  That folder sits beside the site's releases: deploys and rollbacks never touch
//  it, and it is never served.
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public sealed class BlogUser
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "editor";
    public string PasswordHash { get; set; } = "";
    public bool MustChangePassword { get; set; }
    public string SecurityStamp { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string? LastLoginUtc { get; set; }

    public bool IsAdmin => Role == "admin";

    /// <summary>What the browser is allowed to see: never the hash or the stamp.</summary>
    public object ToPublic() => new
    {
        id = Id, username = Username, displayName = DisplayName, role = Role,
        mustChangePassword = MustChangePassword, createdUtc = CreatedUtc, lastLoginUtc = LastLoginUtc,
    };
}

public static class Blog
{
    // Serialise with options owned by this file, so their cache is discarded with it on redeploy.
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string SessionCookie = "nightsky_session";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Every request that changes something must carry this header. A browser will not add a
    /// custom header to a cross-origin request without a CORS preflight, which nothing here
    /// grants, so another site cannot make a signed-in visitor's browser post to this one.
    /// SameSite alone is not enough: sibling sites on the same parent domain count as "same site".
    /// </summary>
    public const string CsrfHeader = "X-Night-Sky";

    private static readonly ConcurrentDictionary<string, bool> Initialised = new();
    private static readonly SemaphoreSlim InitGate = new(1, 1);

    static Blog() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    // ── database ───────────────────────────────────────────────────────────────

    public static async Task<SqliteConnection> OpenAsync(HttpContext context, DirectoryInfo data)
    {
        var path = Path.Combine(data.FullName, "blog.db");
        var db = new SqliteConnection($"Data Source={path};Foreign Keys=True");
        await db.OpenAsync();

        if (!Initialised.ContainsKey(path)) await InitialiseAsync(context, db, data, path);
        return db;
    }

    private static async Task InitialiseAsync(HttpContext context, SqliteConnection db, DirectoryInfo data, string path)
    {
        await InitGate.WaitAsync();
        try
        {
            if (Initialised.ContainsKey(path)) return;

            // WAL lets readers carry on while someone saves a post.
            await db.ExecuteAsync("PRAGMA journal_mode = WAL;");
            await db.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS users (
                    id                   INTEGER PRIMARY KEY,
                    username             TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    display_name         TEXT NOT NULL,
                    role                 TEXT NOT NULL CHECK (role IN ('admin', 'editor')),
                    password_hash        TEXT NOT NULL,
                    must_change_password INTEGER NOT NULL DEFAULT 1,
                    security_stamp       TEXT NOT NULL,
                    created_utc          TEXT NOT NULL,
                    last_login_utc       TEXT
                );
                CREATE TABLE IF NOT EXISTS posts (
                    slug        TEXT PRIMARY KEY,
                    title       TEXT NOT NULL,
                    summary     TEXT NOT NULL DEFAULT '',
                    body        TEXT NOT NULL,
                    tags        TEXT NOT NULL DEFAULT '',
                    published   INTEGER NOT NULL DEFAULT 1,
                    author      TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                """);

            await SeedAdministratorAsync(context, db, data);
            await StarterPosts.SeedAsync(db);

            Initialised[path] = true;
        }
        finally
        {
            InitGate.Release();
        }
    }

    /// <summary>
    /// The first account, created the first time the blog is used. Its password is generated,
    /// written to the server log and to initial-admin-password.txt in the data folder, and must
    /// be changed at first sign-in. The same approach the host takes for its own administrator.
    /// </summary>
    private static async Task SeedAdministratorAsync(HttpContext context, SqliteConnection db, DirectoryInfo data)
    {
        if (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM users") > 0) return;

        var password = GeneratePassword();
        await db.ExecuteAsync("""
            INSERT INTO users (username, display_name, role, password_hash, must_change_password, security_stamp, created_utc)
            VALUES ('admin', 'Observatory Admin', 'admin', @hash, 1, @stamp, @now)
            """, new { hash = HashPassword(password), stamp = NewStamp(), now = Now() });

        var file = Path.Combine(data.FullName, "initial-admin-password.txt");
        await File.WriteAllTextAsync(file, $"username: admin{Environment.NewLine}password: {password}{Environment.NewLine}");

        Logger(context).LogWarning(
            """

            ==========================================================================
             Night Sky Field Notes ({Host}): the first account was created.

                 username: admin
                 password: {Password}

             It must be changed at first sign-in. Also saved to {File}.
            ==========================================================================
            """, context.Request.Host.Host, password, file);
    }

    // ── passwords ──────────────────────────────────────────────────────────────

    private const int Iterations = 210_000;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2$sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts is not ["pbkdf2", "sha256", var iterations, var salt, var hash]) return false;

        var expected = Convert.FromBase64String(hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password, Convert.FromBase64String(salt), int.Parse(iterations), HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>A hash nobody's password matches, verified against when the username is unknown so both cases take as long.</summary>
    public static readonly string DecoyHash = HashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));

    /// <summary>Readable and typeable: no 0/O or 1/l/I to squint at.</summary>
    public static string GeneratePassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(16, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        });
    }

    public static string? CheckNewPassword(string? password) =>
        password is null || password.Length < 10 ? "Passwords need at least 10 characters."
        : password.Length > 200 ? "That password is too long."
        : null;

    /// <summary>Changes whenever a password does, so every session issued before it stops working.</summary>
    public static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    // ── sessions ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The session cookie holds the user id and security stamp, encrypted and signed with the
    /// host's ASP.NET Data Protection keys (resolved from the request's services, so nothing of
    /// the host is referenced). The purpose includes the host name, so a cookie from one site
    /// is meaningless on another even though they share the keys.
    /// </summary>
    private static ITimeLimitedDataProtector Protector(HttpContext context) =>
        context.RequestServices.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("NightSky.Session.v1", context.Request.Host.Host.ToLowerInvariant())
            .ToTimeLimitedDataProtector();

    public static void SignIn(HttpContext context, BlogUser user)
    {
        var token = Protector(context).Protect($"{user.Id}|{user.SecurityStamp}", SessionLifetime);
        context.Response.Cookies.Append(SessionCookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = SessionLifetime,
        });
    }

    public static void SignOut(HttpContext context) =>
        context.Response.Cookies.Delete(SessionCookie, new CookieOptions { Path = "/", SameSite = SameSiteMode.Strict });

    /// <summary>The signed-in user, or null. A deleted user or a changed password ends the session.</summary>
    public static async Task<BlogUser?> CurrentUserAsync(HttpContext context, SqliteConnection db)
    {
        if (context.Request.Cookies[SessionCookie] is not { Length: > 0 } token) return null;

        string payload;
        try { payload = Protector(context).Unprotect(token); }
        catch (CryptographicException) { return null; }

        var split = payload.IndexOf('|');
        if (split < 0 || !long.TryParse(payload[..split], out var id)) return null;

        var user = await db.QuerySingleOrDefaultAsync<BlogUser>("SELECT * FROM users WHERE id = @id", new { id });
        return user is not null && user.SecurityStamp == payload[(split + 1)..] ? user : null;
    }

    /// <summary>
    /// The gate in front of anything that needs an account. Returns the user, or the response
    /// to send instead: 401 when signed out, 403 for the wrong role or while a password change
    /// is still owed, 400 when a change arrives without the anti-forgery header.
    /// </summary>
    public static async Task<(BlogUser? User, IResult? Denied)> RequireAsync(
        HttpContext context, SqliteConnection db, bool admin = false, bool allowPendingPasswordChange = false)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !context.Request.Headers.ContainsKey(CsrfHeader))
            return (null, Error(400, $"Requests that change something must send the {CsrfHeader} header."));

        var user = await CurrentUserAsync(context, db);
        if (user is null) return (null, Error(401, "Sign in first."));

        if (user.MustChangePassword && !allowPendingPasswordChange)
            return (null, Results.Json(new { error = "Choose a new password first.", mustChangePassword = true }, Json, statusCode: 403));

        if (admin && !user.IsAdmin) return (null, Error(403, "Only administrators can do that."));
        return (user, null);
    }

    // ── small helpers ──────────────────────────────────────────────────────────

    public static IResult Ok(object value) => Results.Json(value, Json);

    public static IResult Error(int status, string message) => Results.Json(new { error = message }, Json, statusCode: status);

    public static async Task<T?> ReadAsync<T>(HttpContext context) where T : class
    {
        try { return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, Json); }
        catch (JsonException) { return null; }
    }

    public static string Now() => DateTimeOffset.UtcNow.ToString("O");

    public static ILogger Logger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("NightSky");
}
