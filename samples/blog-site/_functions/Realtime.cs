#:package StaticSiteHost.Abstractions@*

// Who is on the other end of a realtime connection, and which groups they may join.
//
// Pages listen with site.realtime from /_host/site.js. The journal joins "journal" to hear about
// posts going up, changing or coming down (Posts.cs publishes them); the studio joins "studio",
// which also hears about drafts, so it is for signed-in writers only. The host asks these two
// methods as each page connects and each time it asks to join a group, with the page's own
// request, cookies and all, so they can recognise a writer exactly as the handlers do.
//
// Also here: a background service that tells the journal's readers how many of them are reading
// (readers.online), and /api/online, which lists the open connections for the studio's writers.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using StaticSiteHost.Functions;

public static class RealtimeAccess
{
    public const string JournalGroup = "journal";
    public const string StudioGroup = "studio";

    /// <summary>
    /// Every page may connect. A writer's connection carries their username, so a function can
    /// reach every tab they have open with PublishToUserAsync (Users.cs does, when an administrator
    /// changes their account); anyone else is "guest".
    /// </summary>
    [RealtimeConnect]
    public static async Task<string?> Who(HttpContext context)
    {
        await using var db = await Blog.OpenAsync(context);
        return (await Blog.CurrentUserAsync(context, db))?.Username ?? "guest";
    }

    /// <summary>The journal is for anyone and the studio for signed-in writers. There are no other groups.</summary>
    [RealtimeJoin]
    public static async Task<bool> MayJoin(HttpContext context, string group)
    {
        if (group == JournalGroup) return true;
        if (group != StudioGroup) return false;

        await using var db = await Blog.OpenAsync(context);
        return await Blog.CurrentUserAsync(context, db) is not null;
    }

    /// <summary>
    /// Every 30 seconds, for as long as the functions are live: when the number of pages in the journal group has
    /// changed, sends it to them as readers.online with { count }. journal.js shows it as "N reading now".
    /// </summary>
    [BackgroundService]
    public static async Task ReadersOnline(IRealtime realtime, ILogger log, CancellationToken stoppingToken)
    {
        var last = -1;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = realtime.Members(JournalGroup).Count;
                if (count != last)
                {
                    await realtime.PublishToGroupAsync(JournalGroup, "readers.online", new { count }, Blog.Json, stoppingToken);
                    last = count;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Log it and try again next time: a throw that escapes would restart this loop after a pause.
                log.LogWarning(ex, "Could not tell the journal how many are reading");
            }

            // Cancelled when the functions are replaced or removed; the throw ends the loop, as it should.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    /// <summary>The pages connected now, with who they belong to and their groups. For signed-in writers: the studio's Users tab.</summary>
    [HttpGet("/api/online")]
    public static async Task<IResult> Online(HttpContext context, BlogDatabase database, IRealtime realtime)
    {
        await using var db = await database.OpenAsync(context.RequestAborted);
        var (_, denied) = await Blog.RequireAsync(context, db);
        if (denied is not null) return denied;

        return Blog.Ok(new
        {
            connections = realtime.Connections.Select(c => new { id = c.Id, user = c.User, groups = c.Groups, connectedUtc = c.ConnectedUtc }),
        });
    }
}
