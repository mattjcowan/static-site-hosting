#:package Markdig@1.4.0

// The journal: posts written in Markdown, stored in SQLite, rendered to HTML here.

using System.Text.RegularExpressions;
using Dapper;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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
    public static async Task<IResult> List(HttpContext context, DirectoryInfo data, string? tag)
    {
        await using var db = await Blog.OpenAsync(context, data);
        var signedIn = await Blog.CurrentUserAsync(context, db) is not null;

        // Drafts are listed only for people who can edit them.
        var posts = (await db.QueryAsync<Post>(
                "SELECT * FROM posts WHERE published = 1 OR @signedIn ORDER BY created_utc DESC", new { signedIn }))
            .Where(p => tag is null || p.TagList.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return Blog.Ok(new { posts = posts.Select(p => p.ToListItem()) });
    }

    [HttpGet("/api/posts/{slug}")]
    public static async Task<IResult> Get(HttpContext context, DirectoryInfo data, string slug)
    {
        await using var db = await Blog.OpenAsync(context, data);
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
    public static async Task<IResult> Save(HttpContext context, DirectoryInfo data, string slug)
    {
        await using var db = await Blog.OpenAsync(context, data);
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
        return Blog.Ok(new { post = saved.ToListItem() });
    }

    [HttpDelete("/api/posts/{slug}")]
    public static async Task<IResult> Delete(HttpContext context, DirectoryInfo data, string slug)
    {
        await using var db = await Blog.OpenAsync(context, data);
        var (_, denied) = await Blog.RequireAsync(context, db);
        if (denied is not null) return denied;

        return await db.ExecuteAsync("DELETE FROM posts WHERE slug = @slug", new { slug }) == 0
            ? Blog.Error(404, "There is no post here.")
            : Blog.Ok(new { ok = true });
    }

    /// <summary>What the editor shows beside the Markdown as you type.</summary>
    [HttpPost("/api/preview")]
    public static async Task<IResult> Preview(HttpContext context, DirectoryInfo data)
    {
        await using var db = await Blog.OpenAsync(context, data);
        var (_, denied) = await Blog.RequireAsync(context, db);
        if (denied is not null) return denied;

        var body = await Blog.ReadAsync<PreviewRequest>(context);
        return Blog.Ok(new { html = Render(body?.Body ?? "") });
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
