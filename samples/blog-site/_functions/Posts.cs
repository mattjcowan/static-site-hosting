#:package Markdig@1.4.0
#:package StaticSiteHost.Abstractions@*

// The journal: posts written in Markdown, stored in SQLite, rendered to HTML here. Every change is
// announced to the pages open on the site as it happens (see Announce below and Realtime.cs), and the
// studio's Suggest button asks the site's AI for a summary (see Suggest below).
//
// These handlers take the database as a service, BlogDatabase database (Database.cs): the host fills a
// parameter of any type the functions registered.

using System.Text.RegularExpressions;
using Dapper;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using StaticSiteHost.Functions;

public sealed class Post
{
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Body { get; set; } = "";
    public string Tags { get; set; } = "";
    public bool Published { get; set; }
    public string Author { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";

    public string[] TagList => Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public object ToListItem() => new
    {
        slug = Slug, title = Title, summary = Summary, tags = TagList, published = Published,
        author = Author, createdUtc = CreatedUtc, updatedUtc = UpdatedUtc,
        readingMinutes = Math.Max(1, (int)Math.Round(Body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length / 220.0)),
    };
}

public sealed record PostRequest(string? Title, string? Summary, string? Body, string[]? Tags, bool? Published);
public sealed record PreviewRequest(string? Body);
public sealed record SummaryRequest(string? Body);

public static partial class PostHandlers
{
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    // Raw HTML in a post is shown as text, never as markup: a post is content, not code.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    [HttpGet("/api/posts")]
    public static async Task<IResult> List(HttpContext context, BlogDatabase database, string? tag)
    {
        await using var db = await database.OpenAsync(context.RequestAborted);
        var signedIn = await Blog.CurrentUserAsync(context, db) is not null;

        // Drafts are listed only for people who can edit them.
        var posts = (await db.QueryAsync<Post>(
                "SELECT * FROM posts WHERE published = 1 OR @signedIn ORDER BY created_utc DESC", new { signedIn }))
            .Where(p => tag is null || p.TagList.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return Blog.Ok(new { posts = posts.Select(p => p.ToListItem()) });
    }

    [HttpGet("/api/posts/{slug}")]
    public static async Task<IResult> Get(HttpContext context, BlogDatabase database, string slug)
    {
        await using var db = await database.OpenAsync(context.RequestAborted);
        var post = await db.QuerySingleOrDefaultAsync<Post>("SELECT * FROM posts WHERE slug = @slug", new { slug });
        var signedIn = await Blog.CurrentUserAsync(context, db) is not null;

        if (post is null || (!post.Published && !signedIn)) return Blog.Error(404, "There is no post here.");

        return Blog.Ok(new
        {
            post = post.ToListItem(),
            html = Render(post.Body),
            body = signedIn ? post.Body : null, // the Markdown itself only goes to editors
        });
    }

    /// <summary>Creates the post at this slug, or replaces it.</summary>
    [HttpPut("/api/posts/{slug}")]
    public static async Task<IResult> Save(HttpContext context, BlogDatabase database, IRealtime realtime, string slug)
    {
        await using var db = await database.OpenAsync(context.RequestAborted);
        var (user, denied) = await Blog.RequireAsync(context, db);
        if (denied is not null) return denied;

        if (slug.Length > 80 || !SlugPattern().IsMatch(slug))
            return Blog.Error(400, "A slug is lowercase words joined by dashes, like orion-in-binoculars.");

        var body = await Blog.ReadAsync<PostRequest>(context);
        if (string.IsNullOrWhiteSpace(body?.Title)) return Blog.Error(400, "Give the post a title.");
        if (string.IsNullOrWhiteSpace(body.Body)) return Blog.Error(400, "The post has no text yet.");
        if (body.Body.Length > 200_000) return Blog.Error(400, "That post is longer than this journal allows.");

        var now = Blog.Now();
        var tags = string.Join(",", (body.Tags ?? []).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length is > 0 and <= 30).Distinct().Take(8));
        var before = await db.QuerySingleOrDefaultAsync<Post>("SELECT * FROM posts WHERE slug = @slug", new { slug });

        await db.ExecuteAsync("""
            INSERT INTO posts (slug, title, summary, body, tags, published, author, created_utc, updated_utc)
            VALUES (@slug, @title, @summary, @body, @tags, @published, @author, @now, @now)
            ON CONFLICT (slug) DO UPDATE SET
                title = excluded.title, summary = excluded.summary, body = excluded.body,
                tags = excluded.tags, published = excluded.published, updated_utc = excluded.updated_utc
            """, new
        {
            slug,
            title = body.Title.Trim(),
            summary = body.Summary?.Trim() ?? "",
            body = body.Body,
            tags,
            published = body.Published ?? true,
            author = user!.DisplayName,
            now,
        });

        var saved = await db.QuerySingleAsync<Post>("SELECT * FROM posts WHERE slug = @slug", new { slug });

        var action = before is null ? "created"
            : before.Published == saved.Published ? "updated"
            : saved.Published ? "published" : "unpublished";
        await Announce(context, realtime, saved, action, wasPublished: before?.Published == true);

        return Blog.Ok(new { post = saved.ToListItem() });
    }

    [HttpDelete("/api/posts/{slug}")]
    public static async Task<IResult> Delete(HttpContext context, BlogDatabase database, IRealtime realtime, string slug)
    {
        await using var db = await database.OpenAsync(context.RequestAborted);
        var (_, denied) = await Blog.RequireAsync(context, db);
        if (denied is not null) return denied;

        var post = await db.QuerySingleOrDefaultAsync<Post>("SELECT * FROM posts WHERE slug = @slug", new { slug });
        if (post is null || await db.ExecuteAsync("DELETE FROM posts WHERE slug = @slug", new { slug }) == 0)
            return Blog.Error(404, "There is no post here.");

        await Announce(context, realtime, post, "deleted", wasPublished: post.Published);
        return Blog.Ok(new { ok = true });
    }

    /// <summary>
    /// Tells the pages open on the site that a post changed, as post.changed with { slug, title,
    /// action }: the studio's writers about every post, and the journal's readers about the ones
    /// they can see, or could until now. A draft is nobody else's business, so its title never
    /// reaches the journal. The post is saved whatever happens here.
    /// </summary>
    private static async Task Announce(HttpContext context, IRealtime realtime, Post post, string action, bool wasPublished)
    {
        var change = new { slug = post.Slug, title = post.Title, action };
        try
        {
            await realtime.PublishToGroupAsync(RealtimeAccess.StudioGroup, "post.changed", change, Blog.Json);
            if (post.Published || wasPublished)
                await realtime.PublishToGroupAsync(RealtimeAccess.JournalGroup, "post.changed", change, Blog.Json);
        }
        catch (Exception ex)
        {
            Blog.Logger(context).LogWarning(ex, "Could not announce that {Slug} was {Action}", post.Slug, action);
        }
    }

    /// <summary>What the editor shows beside the Markdown as you type.</summary>
    [HttpPost("/api/preview")]
    public static async Task<IResult> Preview(HttpContext context, BlogDatabase database)
    {
        await using var db = await database.OpenAsync(context.RequestAborted);
        var (_, denied) = await Blog.RequireAsync(context, db);
        if (denied is not null) return denied;

        var body = await Blog.ReadAsync<PreviewRequest>(context);
        return Blog.Ok(new { html = Render(body?.Body ?? "") });
    }

    /// <summary>
    /// A summary for the studio's Suggest button, written by the site's AI from the Markdown the editor sends, or
    /// from the saved post when it sends none. The [AiAccess] hook in Ai.cs guards /_host/ai/chat only, and a
    /// function is never asked it, so this one checks for a writer itself: every answer is billed to the key an
    /// administrator chose under Sites → the domain → AI on the host.
    /// </summary>
    [HttpPost("/api/posts/{slug}/summary")]
    public static async Task<IResult> Suggest(HttpContext context, BlogDatabase database, IAiChat ai, string slug)
    {
        await using var db = await database.OpenAsync(context.RequestAborted);
        var (_, denied) = await Blog.RequireAsync(context, db);
        if (denied is not null) return denied;

        if (!ai.IsConfigured)
            return Blog.Error(503, "This site has no AI yet. An administrator chooses a provider under Sites → the domain → AI on the host.");

        var markdown = (await Blog.ReadAsync<SummaryRequest>(context))?.Body;
        if (string.IsNullOrWhiteSpace(markdown))
            markdown = (await db.QuerySingleOrDefaultAsync<Post>("SELECT * FROM posts WHERE slug = @slug", new { slug }))?.Body;
        if (string.IsNullOrWhiteSpace(markdown)) return Blog.Error(400, "Write the post first, then ask for a summary.");

        try
        {
            var answer = await ai.CompleteAsync(new AiChatRequest
            {
                System = "You write the one- or two-sentence summary shown under a post's title in an astronomy journal. " +
                         "Plain text, no quotation marks, under 300 characters.",
                // The opening is enough for a summary, and every token sent is billed.
                Messages = [AiMessage.User(markdown.Length > 12_000 ? markdown[..12_000] : markdown)],
                MaxTokens = 120,
            }, context.RequestAborted);

            return Blog.Ok(new { summary = answer.Text.Trim() });
        }
        catch (AiChatException ex)
        {
            Blog.Logger(context).LogWarning(ex, "The AI did not write a summary for {Slug}", slug);
            return Blog.Error(502, "The AI did not answer. Try again in a moment.");
        }
    }

    /// <summary>
    /// Markdown to HTML. Raw HTML is already disabled by the pipeline; links and images are
    /// also limited to web, mail and relative addresses, because Markdig leaves a
    /// <c>[click me](javascript:…)</c> link alone.
    /// </summary>
    private static string Render(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);

        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!IsSafeUrl(link.Url)) link.Url = "#";
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        return writer.ToString();
    }

    private static bool IsSafeUrl(string? url)
    {
        var value = (url ?? "").Trim();
        var colon = value.IndexOf(':');
        var firstBoundary = value.IndexOfAny(['/', '?', '#']);

        // No scheme at all: a relative link or an anchor.
        if (colon < 0 || (firstBoundary >= 0 && firstBoundary < colon)) return true;

        return value[..colon].ToLowerInvariant() is "http" or "https" or "mailto";
    }
}
