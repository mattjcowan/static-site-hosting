#:package StaticSiteHost.Abstractions@*

// Looks after the journal's database file, on a timer rather than on anyone's request.
//
// In WAL mode (Database.cs turns it on) SQLite writes each change to blog.db-wal first and copies it
// into blog.db at a checkpoint, which it runs by itself as the log grows. What it does not do on
// its own is shrink the log again, so after a busy evening in the studio the -wal file can stay
// large for good. Once an hour, and once as the functions load, Checkpoint copies everything across
// and empties the log.
//
// Every night, Backup copies the database to data/backups/blog-<yyyyMMdd>.db and keeps the newest
// seven. The data folder is never served, so the copies are as private as the database.

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using StaticSiteHost.Functions;

public static class Housekeeping
{
    // Once an hour, and once as the functions load.
    [Every("1h", RunOnStart = true)]
    public static async Task Checkpoint(ISite site, ILogger log, CancellationToken ct)
    {
        var path = Path.Combine(site.Data.FullName, "blog.db");
        if (!File.Exists(path)) return; // Nobody has used the blog yet; the first request creates it.

        // ReadWrite, not the default ReadWriteCreate, so a file deleted in the meantime stays deleted.
        await using var db = new SqliteConnection($"Data Source={path};Mode=ReadWrite");
        await db.OpenAsync(ct);

        await using var command = db.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await using var result = await command.ExecuteReaderAsync(ct);
        await result.ReadAsync(ct);

        // One row: whether a reader kept it from finishing, then the pages in the log and the pages copied.
        var (busy, pages) = (result.GetInt64(0) != 0, result.GetInt64(1));
        if (busy)
            log.LogInformation("blog.db: a reader was busy, so the log was not emptied; the next run tries again");
        else
            log.LogInformation("blog.db: checkpointed {Pages} page(s) from the log and emptied it", Math.Max(0, pages));
    }

    // 03:00 UTC every day. A job can take any service the functions registered: here, the BlogDatabase from Database.cs,
    // for where the file is. It opens its own connections, so it never creates the database or its first account.
    [Schedule("0 3 * * *")]
    public static async Task Backup(BlogDatabase database, ILogger log, CancellationToken ct)
    {
        if (!File.Exists(database.Path)) return; // Nobody has used the blog yet.

        var folder = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(database.Path)!, "backups"));
        var target = Path.Combine(folder.FullName, $"blog-{DateTime.UtcNow:yyyyMMdd}.db");

        // SQLite's online backup copies a consistent snapshot, the log included, while writers carry on. The copy is
        // then switched out of WAL mode, so it is one self-contained file. No pooling, so nothing keeps it open after.
        await using (var source = new SqliteConnection($"Data Source={database.Path};Mode=ReadWrite"))
        await using (var copy = new SqliteConnection($"Data Source={target};Pooling=False"))
        {
            await source.OpenAsync(ct);
            await copy.OpenAsync(ct);
            source.BackupDatabase(copy);

            await using var command = copy.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = DELETE;";
            await command.ExecuteNonQueryAsync(ct);
        }

        // Keep the newest seven. The names sort by date, so the oldest come last.
        var old = folder.GetFiles("blog-*.db").OrderByDescending(file => file.Name, StringComparer.Ordinal).Skip(7).ToList();
        foreach (var file in old) file.Delete();

        log.LogInformation("blog.db: backed up to backups/{File}; removed {Removed} older backup(s)", Path.GetFileName(target), old.Count);
    }
}
