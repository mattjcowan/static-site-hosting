#:package StaticSiteHost.Abstractions@*

// The journal's database, as a service. One BlogDatabase is registered as the functions load: it knows
// where blog.db is, opens connections to it, and creates the tables (and the first account) the first
// time anyone uses it.
//
// A handler, hook or job asks for it by taking a BlogDatabase parameter, as Posts.cs does. Code that has
// only the request calls Blog.OpenAsync(context), which asks the same service.

using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StaticSiteHost.Functions;

public static class DatabaseSetup
{
    /// <summary>A singleton, so the "set up once" work below happens once for as long as the functions are live.</summary>
    [ConfigureServices]
    public static void Configure(IServiceCollection services) => services.AddSingleton<BlogDatabase>();
}

/// <summary>
/// blog.db in the site's data folder. The host hands the constructor the site (its data folder sits beside the
/// releases, is never served, and outlives every deploy) and its loggers.
/// </summary>
public sealed class BlogDatabase
{
    private readonly ISite _site;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _initialised;

    // Dapper maps created_utc to CreatedUtc. Every connection comes from here, so this is always set first.
    static BlogDatabase() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public BlogDatabase(ISite site, ILoggerFactory loggers)
    {
        _site = site;
        _log = loggers.CreateLogger("NightSky");
        Path = System.IO.Path.Combine(site.Data.FullName, "blog.db");
    }

    /// <summary>The database file. The jobs in Housekeeping.cs use it without opening a connection through here.</summary>
    public string Path { get; }

    /// <summary>An open connection. The caller disposes it (await using).</summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var db = new SqliteConnection($"Data Source={Path};Foreign Keys=True");
        await db.OpenAsync(ct);

        if (!_initialised) await InitialiseAsync(db);
        return db;
    }

    private async Task InitialiseAsync(SqliteConnection db)
    {
        await _initGate.WaitAsync();
        try
        {
            if (_initialised) return;

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

            await SeedAdministratorAsync(db);
            await StarterPosts.SeedAsync(db);

            _initialised = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>
    /// The first account, created the first time the blog is used. Its password is generated, written to the server
    /// log and to initial-admin-password.txt in the data folder, and must be changed at first sign-in. The same
    /// approach the host takes for its own administrator.
    /// </summary>
    private async Task SeedAdministratorAsync(SqliteConnection db)
    {
        if (await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM users") > 0) return;

        var password = Blog.GeneratePassword();
        await db.ExecuteAsync("""
            INSERT INTO users (username, display_name, role, password_hash, must_change_password, security_stamp, created_utc)
            VALUES ('admin', 'Observatory Admin', 'admin', @hash, 1, @stamp, @now)
            """, new { hash = Blog.HashPassword(password), stamp = Blog.NewStamp(), now = Blog.Now() });

        var file = System.IO.Path.Combine(_site.Data.FullName, "initial-admin-password.txt");
        await File.WriteAllTextAsync(file, $"username: admin{Environment.NewLine}password: {password}{Environment.NewLine}");

        _log.LogWarning(
            """

            ==========================================================================
             Night Sky Field Notes ({Host}): the first account was created.

                 username: admin
                 password: {Password}

             It must be changed at first sign-in. Also saved to {File}.
            ==========================================================================
            """, _site.Domain, password, file);
    }
}
