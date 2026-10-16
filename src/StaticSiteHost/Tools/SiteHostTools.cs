using System.ComponentModel;
using System.IO.Compression;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using StaticSiteHost.Endpoints;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Ai;
using StaticSiteHost.Services.Assistant;
using StaticSiteHost.Services.Realtime;

namespace StaticSiteHost.Tools;

/// <summary>How a tool is used, for clients that ask before running something that changes things.</summary>
public enum ToolEffect
{
    /// <summary>Only reads.</summary>
    Read,

    /// <summary>Changes something, and the change can be put back.</summary>
    Change,

    /// <summary>
    /// Removes something (a site, functions, a route, a variable's value) or makes a private site
    /// public. The management assistant asks the user before it runs one.
    /// </summary>
    Destructive
}

/// <summary>Marks a method of <see cref="SiteHostTools"/> as a tool, with the name agents call it by.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SiteHostToolAttribute(string name, ToolEffect effect) : Attribute
{
    public string Name { get; } = name;
    public ToolEffect Effect { get; } = effect;
}

/// <summary>One tool: the function to call, and how it is used.</summary>
public sealed record SiteHostTool(AIFunction Function, ToolEffect Effect);

/// <summary>A file for <see cref="SiteHostTools.DeployFiles"/>.</summary>
/// <param name="Path">Where it goes in the site, such as index.html or assets/app.js.</param>
/// <param name="Content">The file's text, or its bytes in base64 when <paramref name="Base64"/> is true.</param>
public sealed record SiteFileInput(string Path, string Content, bool Base64 = false);

/// <summary>One change for <see cref="SiteHostTools.EditSiteFile"/>.</summary>
/// <param name="Find">Text exactly as it is in the file, appearing exactly once.</param>
/// <param name="Replace">What it becomes; empty removes it.</param>
public sealed record FileEdit(string Find, string Replace);

/// <summary>A function source file for the function tools.</summary>
/// <param name="Name">The file name: .cs for C#, .linq for a LINQPad file.</param>
/// <param name="Content">The file's text.</param>
public sealed record FunctionFileInput(string Name, string Content);

/// <summary>
/// Everything an agent can do on this server, as tools. Defined once here and served two ways:
/// over MCP at <c>/mcp</c> (Program.cs), and to any other caller in this process that wants to
/// hand an AI model the same abilities. Each tool calls the service the UI and the API use, with
/// the same checks, so a tool can do exactly what its caller could do through the API, no more.
///
/// The caller is whoever the current HTTP request is signed in as, read through
/// <see cref="IHttpContextAccessor"/>: an API key's owner over MCP. Results are the API's JSON
/// shapes. A tool that cannot do what was asked returns <c>{ "error": "…" }</c> rather than
/// throwing, so the model reads why and can fix the call.
/// </summary>
public static class SiteHostTools
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    /// <summary>The version the build was stamped with, without the commit suffix.</summary>
    public static string ServerVersion { get; } =
        (typeof(SiteHostTools).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0")
        .Split('+')[0];

    /// <summary>Every tool, built once.</summary>
    public static IReadOnlyList<SiteHostTool> All { get; } = Build();

    private static List<SiteHostTool> Build() =>
        typeof(SiteHostTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => (Method: method, Tool: method.GetCustomAttribute<SiteHostToolAttribute>()))
            .Where(pair => pair.Tool is not null)
            .Select(pair => new SiteHostTool(
                AIFunctionFactory.Create(pair.Method, target: null, new AIFunctionFactoryOptions
                {
                    Name = pair.Tool!.Name,
                    SerializerOptions = Json
                }),
                pair.Tool.Effect))
            .OrderBy(tool => tool.Function.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// What an agent is told about the server before its first call, over MCP and in the management
    /// assistant. Kept short: the tool descriptions carry the details, so a model can act on the
    /// first turn without reading the manual.
    /// </summary>
    public const string Instructions = """
        You manage a web hosting server. Each site is a domain. A site can serve uploaded files, run C# functions,
        and forward paths to other servers (proxy routes), all at once.

        Recipes:
        - Site in front of other servers (containers, Railway, a private network): one set_proxy_route per
          path ("/" for the app, "/api" for the API...), all in one turn. It creates the site if needed, and each
          result already reports whether the target answered.
        - The same path forwarded for many sites (such as /api for every site under a domain): one
          set_server_proxy_route with sites "*.that-domain"; a site's own route at that path overrides it.
        - Site from files: deploy_files (creates the site if needed; it replaces every file).
        - Change an existing site's files: read_site_file, then edit_site_file for each change, or
          write_site_files for new files. Both keep every other file. Never deploy_files for a small change.
        - Private site: set_passcode. Caching or security headers: set_header_rules. Moved pages: set_redirect_rules.
        - Server code: check_functions, then test_functions, then deploy_functions.
        - Change an existing function: read_function_files, then edit_function_file, which compiles and deploys it
          and keeps the other files. Then test_functions with target "live" if it needs checking.

        Work fast:
        - Act on what you are told. Do not list or read things you already know from the context given.
        - Put every call you already know you need in the same turn. They run in the order given, so steps that
          depend on each other can go together, such as deploy_files then deploy_functions.
        - Do not call read_docs unless a tool's description does not cover what is needed.
        - A result with "error" or "errors" says what to fix: fix the call and try again once.
        - When done, answer in one or two short sentences: what changed, and anything that failed.
        """;

    /// <summary>How function files are written, for the function tools' descriptions.</summary>
    private const string FunctionsGuide =
        "Function files are .NET 10 file-based C# apps. Minimal file: " +
        "'#:sdk Microsoft.NET.Sdk.Web' first line; then 'using Microsoft.AspNetCore.Http; using Microsoft.AspNetCore.Mvc;' and " +
        "'public static class Api { [HttpGet(\"/api/hello/{name?}\")] public static IResult Hello(string? name) => Results.Json(new { hi = name ?? \"you\" }, Json); " +
        "private static readonly System.Text.Json.JsonSerializerOptions Json = new(); }'. " +
        "Rules: handlers are public static methods of a public class with [HttpGet], [HttpPost], [HttpPut], [HttpDelete] or [Route] naming the full path ({x} one segment, {x?} optional). " +
        "Parameters are filled by type: HttpContext, HttpRequest, HttpResponse, CancellationToken, ILogger, DirectoryInfo (the site's data folder, for SQLite or files), " +
        "IHttpClientFactory (for HTTP calls: take it as a parameter and call CreateClient(); do not keep an HttpClient of your own, which outlives the function when it is replaced), " +
        "or by name from the route then the query string for strings, numbers, bool, Guid and dates. " +
        "Return IResult (Results.Json with a static JsonSerializerOptions field, Results.Text, Results.File...), string (text/plain), int (a status code), or nothing (204). " +
        "For site types add '#:package StaticSiteHost.Abstractions@*' and 'using StaticSiteHost.Functions;': ISite, ISiteVariables (the site's variables), IAiChat, IRealtime as parameters; " +
        "[Middleware] public static async Task Gate(HttpContext context, Func<Task> next) runs before every request to the site, files and proxy routes included (call next() to continue). " +
        "NuGet packages: '#:package Name@version'.";

    // ================================================================ you and the server

    [SiteHostTool("who_am_i", ToolEffect.Read)]
    [Description("Who this connection acts as: user name and role. Administrators can use every tool; members cannot change functions, proxy routes, AI, users, or rename or delete sites.")]
    public static JsonElement WhoAmI(IServiceProvider services)
    {
        var caller = Caller.Of(services);
        return ToJson(new
        {
            username = caller.Principal.Identity?.Name,
            displayName = caller.Principal.FindFirstValue("display_name"),
            role = caller.Principal.FindFirstValue(ClaimTypes.Role),
            administrator = caller.IsAdministrator
        });
    }

    [SiteHostTool("read_docs", ToolEffect.Read)]
    [Description("The server's manual. With no section, lists the section titles. With a section title (or part of one, such as 'Proxy routes' or 'Redirects'), returns that section's text.")]
    public static JsonElement ReadDocs(
        [Description("A section title or part of one. Leave empty to list the sections.")] string? section = null)
    {
        var sections = Manual.Sections;
        if (string.IsNullOrWhiteSpace(section))
            return ToJson(new { sections = sections.Select(s => s.Title) });

        var match = sections.FirstOrDefault(s => s.Title.Equals(section.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? sections.FirstOrDefault(s => s.Title.Contains(section.Trim(), StringComparison.OrdinalIgnoreCase));
        return match is null
            ? Error($"No section is called '{section}'. Call read_docs with no section for the list.")
            : ToJson(new { title = match.Title, text = match.Text });
    }

    [SiteHostTool("read_audit_log", ToolEffect.Read)]
    [Description("Recent changes on the server, newest first: who deployed, what settings changed. Administrators only.")]
    public static async Task<JsonElement> ReadAuditLog(
        IServiceProvider services,
        [Description("How many entries, 1 to 1000.")] int limit = 50,
        [Description("Only this action or its kind, such as 'site.deploy' or 'site'.")] string? action = null,
        [Description("Only entries about this site.")] string? domain = null)
    {
        if (Caller.Of(services).AdministratorsOnly() is { } refused) return refused;
        return ToJson(await services.GetRequiredService<AuditLog>().ReadAsync(limit, action, domain));
    }

    // ================================================================ sites

    [SiteHostTool("list_sites", ToolEffect.Read)]
    [Description("Every site on the server, with its current release, rule counts, proxy routes and functions.")]
    public static JsonElement ListSites(IServiceProvider services) =>
        ToJson(services.GetRequiredService<SiteStore>().List().Select(site => DescribeSite(services, site)));

    [SiteHostTool("get_site", ToolEffect.Read)]
    [Description("One site in full: releases, counts of rules and routes, functions, whether it has a passcode.")]
    public static JsonElement GetSite(IServiceProvider services, [Description("The site's domain.")] string domain) =>
        WithSite(services, domain, site => DescribeSite(services, site));

    [SiteHostTool("create_site", ToolEffect.Change)]
    [Description("Creates a site with no files at a domain (fails if the domain already has one). Rarely needed: set_proxy_route and deploy_files both create the site when it does not exist, so call them directly. Point the domain's DNS at this server for visitors to reach it.")]
    public static async Task<JsonElement> CreateSite(IServiceProvider services, [Description("The domain, such as app.example.com.")] string domain)
    {
        var caller = Caller.Of(services);
        var (ok, error, created, _) = await services.GetRequiredService<ZipDeploymentService>().CreateAsync(domain, caller.Actor);
        return ok
            ? ToJson(DescribeSite(services, services.GetRequiredService<SiteStore>().TryGet(created)!))
            : Error(error);
    }

    [SiteHostTool("deploy_files", ToolEffect.Change)]
    [Description("Publishes a new release of a site from the files given, creating the site if it does not exist. The files REPLACE THE WHOLE previous release: any file not given is gone from the live site (the old release stays available to roll back to). Use it for a new site or a full rebuild; to change some files of an existing site, use edit_site_file or write_site_files, which keep the rest. Put index.html at the root; a path with no file falls back to the nearest index.html, so a single-page app works as is; a link to /about is answered by about.html. Optional files read from the release: _headers and _redirects (the formats of set_header_rules and set_redirect_rules), _variables.json (declares variables), and C# function files in a top-level _functions/ folder (administrators only).")]
    public static async Task<JsonElement> DeployFiles(
        IServiceProvider services,
        [Description("The site's domain.")] string domain,
        [Description("Every file of the site. Text goes in content as is; binary files as base64 with base64 true.")] IReadOnlyList<SiteFileInput> files,
        CancellationToken ct = default)
    {
        var caller = Caller.Of(services);
        if (files is not { Count: > 0 }) return Error("Give at least one file, such as index.html.");

        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                byte[] bytes;
                try
                {
                    bytes = file.Base64 ? Convert.FromBase64String(file.Content ?? "") : Encoding.UTF8.GetBytes(file.Content ?? "");
                }
                catch (FormatException)
                {
                    return Error($"{file.Path}: base64 is true but the content is not base64.");
                }

                await using var entry = archive.CreateEntry((file.Path ?? "").TrimStart('/')).Open();
                await entry.WriteAsync(bytes, ct);
            }
        }

        zip.Position = 0;
        var result = await services.GetRequiredService<ZipDeploymentService>().DeployAsync(
            domain, zip, "files.zip", caller.Actor, "mcp", canDeployFunctions: caller.IsAdministrator, ct);

        return result.Ok
            ? ToJson(new
            {
                ok = true,
                domain = result.Domain,
                release = result.Release!.Id,
                fileCount = result.Release.FileCount,
                warnings = result.Warnings ?? []
            })
            : ToJson(new { error = result.Error, diagnostics = result.Diagnostics?.Select(d => d.ToString()) });
    }

    // ================================================================ a site's files, one at a time

    [SiteHostTool("list_site_files", ToolEffect.Read)]
    [Description("The files of a site's live release, with their sizes. Includes _headers, _redirects and _variables.json when the site has rules or variable declarations from its files.")]
    public static async Task<JsonElement> ListSiteFiles(IServiceProvider services, [Description("The site's domain.")] string domain) =>
        await WithSiteAsync(services, domain, site => Task.FromResult<object>(SiteFiles.ReleaseOf(site, null) is { } release
            ? new { domain = site.Domain, release = release.Id, files = services.GetRequiredService<SiteFiles>().List(site, release).Take(500) }
            : new { error = $"{site.Domain} has no files yet. deploy_files or write_site_files adds some." }));

    [SiteHostTool("read_site_file", ToolEffect.Read)]
    [Description("One file of a site's live release, as text. For a large file the text may be cut short (the result says so): then change it with edit_site_file, which needs only the parts it changes, never with write_site_files, which would drop the part not read.")]
    public static async Task<JsonElement> ReadSiteFile(
        IServiceProvider services,
        [Description("The site's domain.")] string domain,
        [Description("The file's path in the site, such as index.html or css/site.css.")] string path,
        CancellationToken ct = default) =>
        await WithSiteAsync(services, domain, async site =>
        {
            if (SiteFiles.ReleaseOf(site, null) is not { } release) return new { error = $"{site.Domain} has no files yet." };
            var bytes = await services.GetRequiredService<SiteFiles>().ReadAsync(site, release, path, ct);
            if (bytes is null) return new { error = $"{site.Domain} has no file '{path}'. list_site_files shows what it has." };

            string text;
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return (object)new { path, size = bytes.Length, binary = true, note = "A binary file: it cannot be shown as text. Replace it whole with write_site_files (base64) if needed." };
            }

            const int Shown = 60_000;
            return text.Length <= Shown
                ? new { path, size = bytes.Length, text }
                : new { path, size = bytes.Length, text = text[..Shown], cutShort = $"Only the first {Shown:N0} of {text.Length:N0} characters are shown. Change this file with edit_site_file." };
        });

    [SiteHostTool("edit_site_file", ToolEffect.Change)]
    [Description("Changes parts of one file of a site, and keeps every other file: each edit replaces text that appears exactly once in the file. The safe way to change an existing page, script or stylesheet; read_site_file shows its text first. Makes a new release, so it can be rolled back.")]
    public static async Task<JsonElement> EditSiteFile(
        IServiceProvider services,
        [Description("The site's domain.")] string domain,
        [Description("The file's path in the site, such as index.html.")] string path,
        [Description("The edits, applied in order: find (text exactly as it is in the file, appearing once) and replace (what it becomes; empty removes it).")] IReadOnlyList<FileEdit> edits,
        CancellationToken ct = default)
    {
        var caller = Caller.Of(services);
        if (edits is not { Count: > 0 }) return Error("Give at least one edit.");

        return await WithSiteAsync(services, domain, async site =>
        {
            if (SiteFiles.ReleaseOf(site, null) is not { } release) return new { error = $"{site.Domain} has no files yet." };
            var bytes = await services.GetRequiredService<SiteFiles>().ReadAsync(site, release, path, ct);
            if (bytes is null) return new { error = $"{site.Domain} has no file '{path}'. Use write_site_files to add it." };

            var (text, editError) = ApplyEdits(Encoding.UTF8.GetString(bytes), edits, path, "read_site_file");
            if (text is null) return new { error = editError };

            var result = await services.GetRequiredService<ZipDeploymentService>().UpdateFilesAsync(
                site.Domain, [new(path, Encoding.UTF8.GetBytes(text))], [], caller.Actor, "tools", caller.IsAdministrator, ct);
            return result.Ok
                ? new { ok = true, path, release = result.Release!.Id, edits = edits.Count, warnings = result.Warnings ?? [] }
                : (object)new { error = result.Error };
        });
    }

    [SiteHostTool("write_site_files", ToolEffect.Change)]
    [Description("Adds files to a site, or replaces them whole, and keeps every other file. For new files, or to rewrite a file you have read in full; to change part of a file, use edit_site_file. Creates the site's first release when it has none. Makes a new release, so it can be rolled back.")]
    public static async Task<JsonElement> WriteSiteFiles(
        IServiceProvider services,
        [Description("The site's domain.")] string domain,
        [Description("The files: path, and the text as content (binary: base64 content with base64 true).")] IReadOnlyList<SiteFileInput> files,
        CancellationToken ct = default)
    {
        var caller = Caller.Of(services);
        if (files is not { Count: > 0 }) return Error("Give at least one file.");

        var writes = new List<ZipDeploymentService.FileChange>();
        foreach (var file in files)
        {
            try
            {
                writes.Add(new(file.Path ?? "", file.Base64 ? Convert.FromBase64String(file.Content ?? "") : Encoding.UTF8.GetBytes(file.Content ?? "")));
            }
            catch (FormatException)
            {
                return Error($"{file.Path}: base64 is true but the content is not base64.");
            }
        }

        var result = await services.GetRequiredService<ZipDeploymentService>().UpdateFilesAsync(domain, writes, [], caller.Actor, "tools", caller.IsAdministrator, ct);
        return result.Ok
            ? ToJson(new { ok = true, domain = result.Domain, release = result.Release!.Id, fileCount = result.Release.FileCount, warnings = result.Warnings ?? [] })
            : Error(result.Error);
    }

    [SiteHostTool("delete_site_files", ToolEffect.Destructive)]
    [Description("Deletes files from a site and keeps the rest. Makes a new release, so the old one can be rolled back to.")]
    public static async Task<JsonElement> DeleteSiteFiles(
        IServiceProvider services,
        [Description("The site's domain.")] string domain,
        [Description("The paths of the files to delete.")] IReadOnlyList<string> paths,
        CancellationToken ct = default)
    {
        var caller = Caller.Of(services);
        if (paths is not { Count: > 0 }) return Error("Give at least one path.");

        var result = await services.GetRequiredService<ZipDeploymentService>().UpdateFilesAsync(domain, [], paths, caller.Actor, "tools", caller.IsAdministrator, ct);
        return result.Ok
            ? ToJson(new { ok = true, domain = result.Domain, release = result.Release!.Id, fileCount = result.Release.FileCount, warnings = result.Warnings ?? [] })
            : Error(result.Error);
    }

    [SiteHostTool("rollback_site", ToolEffect.Change)]
    [Description("Makes an earlier release of a site live again. get_site lists the releases.")]
    public static async Task<JsonElement> RollbackSite(
        IServiceProvider services,
        [Description("The site's domain.")] string domain,
        [Description("The release id to go back to.")] string releaseId,
        [Description("True runs the site's current functions on the older release; false brings back the functions it had.")] bool keepFunctions = false) =>
        await WithSiteAsync(services, domain, async site =>
        {
            var (ok, error) = await services.GetRequiredService<ZipDeploymentService>()
                .RollbackAsync(site.Domain, releaseId, Caller.Of(services).Actor, keepFunctions);
            return ok ? new { ok = true, domain = site.Domain, release = releaseId } : new { error };
        });

    [SiteHostTool("rename_site", ToolEffect.Change)]
    [Description("Moves a site to another domain. The old domain stops answering at once. Administrators only.")]
    public static async Task<JsonElement> RenameSite(
        IServiceProvider services,
        [Description("The site's current domain.")] string domain,
        [Description("The new domain.")] string newDomain)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        return await WithSiteAsync(services, domain, async site =>
        {
            var (ok, error, to) = await services.GetRequiredService<ZipDeploymentService>().RenameAsync(site.Domain, newDomain, caller.Actor);
            return ok ? new { ok = true, from = site.Domain, domain = to } : (object)new { error };
        });
    }

    [SiteHostTool("delete_site", ToolEffect.Destructive)]
    [Description("Deletes a site and everything it has: every release, its settings, functions and data. It cannot be undone. Administrators only.")]
    public static async Task<JsonElement> DeleteSite(IServiceProvider services, [Description("The site's domain.")] string domain)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        var (normalized, error) = SiteStore.NormalizeDomain(domain);
        if (normalized is null) return Error(error);

        return await services.GetRequiredService<ZipDeploymentService>().DeleteAsync(normalized, caller.Actor)
            ? ToJson(new { ok = true, domain = normalized })
            : Error($"No site is published at '{normalized}'.");
    }

    // ================================================================ visibility

    [SiteHostTool("set_passcode", ToolEffect.Change)]
    [Description("Makes a site private: visitors must enter this passcode before anything is served, proxied paths included. At least 8 characters. Replacing it signs out everyone who entered the old one.")]
    public static async Task<JsonElement> SetPasscode(
        IServiceProvider services,
        [Description("The site's domain.")] string domain,
        [Description("The passcode visitors will enter.")] string passcode) =>
        await WithSiteAsync(services, domain, async site =>
        {
            var (ok, error) = await services.GetRequiredService<SitePasscodeGate>().SetAsync(site, passcode, Caller.Of(services).Actor);
            if (!ok) return new { error };

            services.GetRequiredService<RealtimeRegistry>().DisconnectSite(site.Domain);
            return (object)new { ok = true, domain = site.Domain, passcodeProtected = true };
        });

    [SiteHostTool("remove_passcode", ToolEffect.Destructive)]
    [Description("Makes a site public again.")]
    public static async Task<JsonElement> RemovePasscode(IServiceProvider services, [Description("The site's domain.")] string domain) =>
        await WithSiteAsync(services, domain, async site =>
        {
            await services.GetRequiredService<SitePasscodeGate>().ClearAsync(site, Caller.Of(services).Actor);
            return new { ok = true, domain = site.Domain, passcodeProtected = false };
        });

    // ================================================================ rules

    [SiteHostTool("get_rules", ToolEffect.Read)]
    [Description("A site's header rules and redirects, in the _headers and _redirects text formats: the site's own, which set_header_rules and set_redirect_rules replace, and the ones its current release brought.")]
    public static JsonElement GetRules(IServiceProvider services, [Description("The site's domain.")] string domain) =>
        WithSite(services, domain, site => new
        {
            domain = site.Domain,
            headerRules = HeaderRuleText.Format(site.Headers),
            redirectRules = RedirectRuleText.Format(site.Redirects),
            releaseHeaderRules = HeaderRuleText.Format(site.Current?.Headers),
            releaseRedirectRules = RedirectRuleText.Format(site.Current?.Redirects)
        });

    [SiteHostTool("set_header_rules", ToolEffect.Change)]
    [Description("Replaces a site's own header rules (the whole list; get_rules shows the current one). The _headers format: a line starting with / is a path pattern, and the indented 'Name: value' lines under it are the headers it sets. Example: \"/assets/**\\n  Cache-Control: public, max-age=31536000, immutable\\n/**\\n  X-Frame-Options: DENY\". In patterns * matches within one segment and ** across segments. Every matching rule applies; a later rule wins for the same header. They apply to files and proxied answers. Set-Cookie, Content-Encoding and transport headers cannot be set. Empty text removes them all.")]
    public static async Task<JsonElement> SetHeaderRules(
        IServiceProvider services,
        [Description("The site's domain.")] string domain,
        [Description("The rules, in the _headers format.")] string rules) =>
        await WithSiteAsync(services, domain, async site =>
        {
            if (!HeaderRuleText.TryParse(rules, out var parsed, out var parseErrors)) return new { errors = parseErrors };

            var (ok, errors) = await services.GetRequiredService<SiteRuleService>().SetHeadersAsync(site, parsed, Caller.Of(services).Actor);
            return ok ? new { ok = true, domain = site.Domain, rules = parsed.Count } : (object)new { errors };
        });

    [SiteHostTool("set_redirect_rules", ToolEffect.Change)]
    [Description("Replaces a site's own redirects and rewrites (the whole list). The _redirects format: one rule per line, 'from to status', such as \"/old /new 301\", \"/blog/* /articles/:1 301\" (:1 is what * matched) or \"/docs/** https://docs.example.com/:1 302\". A 3xx sends the visitor elsewhere; 200 serves another file of the site at the same address; 404 answers not found. A rule is skipped where a real file exists unless the status ends in ! (301!). The first matching rule wins. Empty text removes them all. To answer a path from another server, use set_proxy_route instead.")]
    public static async Task<JsonElement> SetRedirectRules(
        IServiceProvider services,
        [Description("The site's domain.")] string domain,
        [Description("The rules, in the _redirects format.")] string rules) =>
        await WithSiteAsync(services, domain, async site =>
        {
            if (!RedirectRuleText.TryParse(rules, out var parsed, out var parseErrors)) return new { errors = parseErrors };

            var (ok, errors) = await services.GetRequiredService<SiteRuleService>().SetRedirectsAsync(site, parsed, Caller.Of(services).Actor);
            return ok ? new { ok = true, domain = site.Domain, rules = parsed.Count } : (object)new { errors };
        });

    // ================================================================ variables

    [SiteHostTool("get_variables", ToolEffect.Read)]
    [Description("A site's variables: each one's value (secrets hidden), whether it is public to the browser, and where the value comes from.")]
    public static JsonElement GetVariables(IServiceProvider services, [Description("The site's domain.")] string domain) =>
        WithSite(services, domain, site => ApiEndpoints.DescribeVariables(site, services.GetRequiredService<SiteVariableService>(), saved: false));

    [SiteHostTool("set_variable", ToolEffect.Change)]
    [Description("Sets one variable on a site: a setting kept on the server rather than in the files, such as an API URL or a key. Functions read it (ISiteVariables); with isPublic true, the site's pages read it too, with site.get('NAME') after loading /_host/site.js. Only administrators may set secrets or values that read the server's environment with ${env:NAME}.")]
    public static async Task<JsonElement> SetVariable(
        IServiceProvider services,
        [Description("The site's domain.")] string domain,
        [Description("The variable's name, such as API_URL.")] string name,
        [Description("Its value.")] string value,
        [Description("True to let the site's pages read it through /_host/site.js. Leave out to keep what it has.")] bool? isPublic = null,
        [Description("True to store it encrypted and never show it again. Administrators only. Leave out to keep what it has.")] bool? isSecret = null)
    {
        var caller = Caller.Of(services);
        return await WithSiteAsync(services, domain, async site =>
        {
            var variables = services.GetRequiredService<SiteVariableService>();
            var (ok, error) = await variables.SetAsync(site, name, value, isPublic, isSecret, caller.Actor, caller.IsAdministrator);
            return ok ? ApiEndpoints.DescribeVariables(site, variables, saved: true) : new { error };
        });
    }

    [SiteHostTool("remove_variable", ToolEffect.Destructive)]
    [Description("Removes a site's own value for a variable, so it goes back to the release's default, if it has one.")]
    public static async Task<JsonElement> RemoveVariable(
        IServiceProvider services,
        [Description("The site's domain.")] string domain,
        [Description("The variable's name.")] string name)
    {
        var caller = Caller.Of(services);
        return await WithSiteAsync(services, domain, async site =>
        {
            var variables = services.GetRequiredService<SiteVariableService>();
            var (removed, error) = await variables.RemoveAsync(site, name, caller.Actor, caller.IsAdministrator);
            return removed
                ? ApiEndpoints.DescribeVariables(site, variables, saved: true)
                : new { error = error ?? $"{site.Domain} has no value of its own for {name}." };
        });
    }

    // ================================================================ proxy routes

    [SiteHostTool("get_proxy_routes", ToolEffect.Read)]
    [Description("A site's proxy routes: the paths it forwards to other servers, each route's settings, and the server-wide routes that apply to it (serverRoutes, each with whether it is in effect here).")]
    public static async Task<JsonElement> GetProxyRoutes(IServiceProvider services, [Description("The site's domain.")] string domain)
    {
        var serverRoutes = await services.GetRequiredService<SiteProxyService>().ServerRoutesAsync();
        return WithSite(services, domain, site => ApiEndpoints.DescribeProxy(site, Caller.Of(services).IsAdministrator, serverRoutes));
    }

    [SiteHostTool("set_proxy_route", ToolEffect.Change)]
    [Description("Adds a proxy route to a site, or changes the one at the same path: every request under the path is forwarded to the target server and its answer sent back. Path '/' forwards everything; '/api' forwards /api and everything under it (not /apis), and by default removes '/api' first, so /api/users goes to {target}/users. The longest matching path wins, so '/' and '/api' can be set together. Settings left out take their defaults. Creates the site first when it does not exist, so no create_site is needed. Call it once per route; several calls can go in one turn. The result includes 'check': a GET sent to the target just now, with its status, so there is no need to call test_proxy_route after it. Administrators only.")]
    public static async Task<JsonElement> SetProxyRoute(
        IServiceProvider services,
        [Description("The site's domain.")] string domain,
        [Description("The route. Required: path and target (an http:// or https:// URL, such as http://api:8080). Optional settings: stripPath (default true), enabled, webSockets, crossSiteWebSockets, streaming (server-sent events), httpVersion ('auto', '1.1' or '2'; '2' for gRPC), timeoutSeconds (default 100), maxRequestBodyBytes, keepHost, forwardedHeaders, requestHeaders ([{name, value}], empty value removes), cookieMode ('host', 'keep' or 'parent') with cookieParentDomain, rewriteRedirects, filesFirst, acceptAnyCertificate.")] ProxyRoute route,
        [Description("The path the route is saved under now, to change its path. Leave out otherwise.")] string? originalPath = null)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        // A route for a domain with no site yet makes the site, as a deploy does: one call, not two.
        var (normalized, domainError) = SiteStore.NormalizeDomain(domain);
        if (normalized is null) return Error(domainError);
        if (services.GetRequiredService<SiteStore>().TryGet(normalized) is null)
        {
            // Checked first, so a route that cannot be saved does not leave an empty site behind.
            if (SiteProxyService.CheckNewSiteRoute(normalized, route) is { Count: > 0 } routeErrors)
                return ToJson(new { errors = routeErrors });

            // Someone else creating it at the same moment is as good as creating it here.
            var (created, createError, _, alreadyExists) = await services.GetRequiredService<ZipDeploymentService>().CreateAsync(normalized, caller.Actor);
            if (!created && !alreadyExists) return Error(createError);
        }

        return await WithSiteAsync(services, normalized, async site =>
        {
            var proxy = services.GetRequiredService<SiteProxyService>();
            var (ok, errors) = await proxy.SaveRouteAsync(site, originalPath ?? route.Path, route, caller.Actor);
            if (!ok) return new { errors };

            // Saves the model a round: whether the target answers comes back with the save. Short,
            // so a target that is down costs a few seconds, not the full test timeout.
            var saved = site.Proxy.First(r => r.Path.Equals(ProxyRouteRules.NormalizePath(route.Path), StringComparison.OrdinalIgnoreCase));
            var check = saved.Enabled ? await proxy.TestAsync(saved, CancellationToken.None, TimeSpan.FromSeconds(3)) : null;

            return (object)new
            {
                saved = new { saved.Path, saved.Target },
                check = check is null ? null : new { answered = check.Answered, status = check.Status, error = check.Error, summary = check.Describe() },
                routes = site.Proxy.Select(r => new { r.Path, r.Target, r.Enabled, r.StripPath, r.WebSockets, r.Streaming, r.HttpVersion })
            };
        });
    }

    [SiteHostTool("remove_proxy_route", ToolEffect.Destructive)]
    [Description("Removes a site's proxy route at a path. Its paths go back to the site's files. Administrators only.")]
    public static async Task<JsonElement> RemoveProxyRoute(
        IServiceProvider services,
        [Description("The site's domain.")] string domain,
        [Description("The route's path.")] string path)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        return await WithSiteAsync(services, domain, async site =>
            await services.GetRequiredService<SiteProxyService>().RemoveRouteAsync(site, path, caller.Actor)
                ? ApiEndpoints.DescribeProxy(site, administrator: true, await services.GetRequiredService<SiteProxyService>().ServerRoutesAsync())
                : new { error = $"{site.Domain} has no proxy route at {ProxyRouteRules.NormalizePath(path)}." });
    }

    // ================================================================ server-wide proxy routes

    [SiteHostTool("get_server_proxy_routes", ToolEffect.Read)]
    [Description("The server-wide proxy routes: routes that apply to every site whose domain matches their 'sites' pattern, each with its id.")]
    public static async Task<JsonElement> GetServerProxyRoutes(IServiceProvider services)
    {
        var administrator = Caller.Of(services).IsAdministrator;
        var routes = await services.GetRequiredService<SiteProxyService>().ServerRoutesAsync();
        return ToJson(new { routes = routes.Select(r => ApiEndpoints.Redacted(r, administrator)) });
    }

    [SiteHostTool("set_server_proxy_route", ToolEffect.Change)]
    [Description("Adds a server-wide proxy route, or changes the one with the same id: like set_proxy_route, but for every site whose domain matches 'sites': '*' for every site, '*.apps.example.com' for every site under that domain, or a domain; several separated by commas. Use it when many sites need the same path forwarded, such as /api to one API for every site under a domain. A site's own route at the same path wins over it, and a site can turn it off with set_server_route_for_site. When two server-wide routes share a path, the one whose sites names a site most closely wins (a domain, then the longest *. pattern, then *). The result includes 'check', a GET sent to the target just now. Administrators only.")]
    public static async Task<JsonElement> SetServerProxyRoute(
        IServiceProvider services,
        [Description("The route: 'sites', 'path' and 'target' are required; the other settings are those of set_proxy_route. Give 'id' to change an existing route; leave it out to add one.")] ServerProxyRoute route)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        var proxy = services.GetRequiredService<SiteProxyService>();
        var (ok, errors, saved) = await proxy.SaveServerRouteAsync(route, caller.Actor);
        if (!ok) return ToJson(new { errors });

        var check = saved!.Enabled ? await proxy.TestAsync(saved, CancellationToken.None, TimeSpan.FromSeconds(3)) : null;
        return ToJson(new
        {
            saved = new { saved.Id, saved.Sites, saved.Path, saved.Target },
            check = check is null ? null : new { answered = check.Answered, status = check.Status, error = check.Error, summary = check.Describe() },
            routes = (await proxy.ServerRoutesAsync()).Select(r => new { r.Id, r.Sites, r.Path, r.Target, r.Enabled })
        });
    }

    [SiteHostTool("remove_server_proxy_route", ToolEffect.Destructive)]
    [Description("Removes a server-wide proxy route, by id, from every site it applies to. Its paths go back to each site's own files and routes. Administrators only.")]
    public static async Task<JsonElement> RemoveServerProxyRoute(IServiceProvider services, [Description("The route's id, from get_server_proxy_routes.")] string id)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        return await services.GetRequiredService<SiteProxyService>().RemoveServerRouteAsync(id, caller.Actor)
            ? ToJson(new { ok = true, id })
            : Error("No server-wide proxy route has that id.");
    }

    [SiteHostTool("set_server_route_for_site", ToolEffect.Change)]
    [Description("Turns the server-wide proxy routes at a path off for one site, so that path is the site's own again, or back on. To send one site's path somewhere else instead, give the site its own route at that path with set_proxy_route. Administrators only.")]
    public static async Task<JsonElement> SetServerRouteForSite(
        IServiceProvider services,
        [Description("The site's domain.")] string domain,
        [Description("The path of the server-wide route, such as /api.")] string path,
        [Description("False turns it off for this site; true turns it back on.")] bool enabled)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        var proxy = services.GetRequiredService<SiteProxyService>();
        return await WithSiteAsync(services, domain, async site =>
            await proxy.SetServerRouteForSiteAsync(site, path, enabled, caller.Actor) is { } error
                ? new { error }
                : ApiEndpoints.DescribeProxy(site, administrator: true, await proxy.ServerRoutesAsync()));
    }

    [SiteHostTool("test_proxy_route", ToolEffect.Read)]
    [Description("Sends a GET to a proxy route's target from the server and reports the status, the time and the HTTP version, or why it failed. Use it after set_proxy_route. Administrators only.")]
    public static async Task<JsonElement> TestProxyRoute(
        IServiceProvider services,
        [Description("The site's domain.")] string domain,
        [Description("The route's path.")] string path,
        CancellationToken ct = default)
    {
        if (Caller.Of(services).AdministratorsOnly() is { } refused) return refused;

        return await WithSiteAsync(services, domain, async site =>
        {
            var normalized = ProxyRouteRules.NormalizePath(path);
            var route = site.Proxy.FirstOrDefault(r => r.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (route is null) return new { error = $"{site.Domain} has no proxy route at {normalized}." };

            var result = await services.GetRequiredService<SiteProxyService>().TestAsync(route, ct);
            return (object)new { answered = result.Answered, status = result.Status, version = result.Version, url = result.Url, milliseconds = (int)result.Elapsed.TotalMilliseconds, error = result.Error, summary = result.Describe() };
        });
    }

    // ================================================================ functions

    [SiteHostTool("get_functions", ToolEffect.Read)]
    [Description("A site's live functions (or the global ones, with no domain): their routes, middleware, jobs and hooks, and whether they are loaded or failed and why.")]
    public static async Task<JsonElement> GetFunctions(
        IServiceProvider services,
        [Description("The site's domain, or empty for the global functions that answer on every site.")] string? domain = null)
    {
        var host = services.GetRequiredService<FunctionHost>();
        if (string.IsNullOrWhiteSpace(domain))
        {
            var global = await services.GetRequiredService<FunctionDeploymentService>().GlobalAsync();
            return ToJson(new { functions = global.Current, status = await host.StatusAsync(null) });
        }

        return await WithSiteAsync(services, domain, async site =>
            new { domain = site.Domain, functions = site.CurrentFunctions, status = await host.StatusAsync(site.Domain) });
    }

    [SiteHostTool("read_function_files", ToolEffect.Read)]
    [Description("The source of a site's live functions (or the global ones, with no domain), file by file, exactly as deployed. Read it before changing a function, then change it with edit_function_file.")]
    public static async Task<JsonElement> ReadFunctionFiles(
        IServiceProvider services,
        [Description("The site's domain, or empty for the global functions.")] string? domain = null,
        [Description("One file's name, such as Weather.cs. Leave out for every file.")] string? name = null)
    {
        var (scope, scopeError) = FunctionScope(services.GetRequiredService<SiteStore>(), domain);
        if (scopeError is not null) return Error(scopeError);

        var files = await services.GetRequiredService<FunctionDeploymentService>().LiveFilesAsync(scope);
        if (files.Count == 0) return Error(scope is null ? "No global functions are deployed." : $"{scope} has no functions.");

        if (!string.IsNullOrWhiteSpace(name))
        {
            files = files.Where(f => f.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (files.Count == 0) return Error($"No function file is called '{name}'. read_function_files with no name shows them all.");
        }

        // The same budget as read_site_file: past it, a file comes back cut short and is changed
        // with edit_function_file, which needs only the parts it changes.
        const int Shown = 60_000;
        var left = Shown;
        var shown = new List<object>();
        foreach (var file in files)
        {
            if (file.Text.Length <= left)
            {
                shown.Add(new { name = file.Name, text = file.Text });
                left -= file.Text.Length;
            }
            else
            {
                shown.Add(new { name = file.Name, text = file.Text[..Math.Max(left, 0)], cutShort = $"Only the first {Math.Max(left, 0):N0} of {file.Text.Length:N0} characters are shown. Read it alone with name, or change it with edit_function_file." });
                left = 0;
            }
        }

        return ToJson(new { domain = scope, files = shown });
    }

    [SiteHostTool("edit_function_file", ToolEffect.Change)]
    [Description("Changes parts of one function file and deploys it, keeping every other function file: each edit replaces text that appears exactly once in the file. read_function_files shows the text first. Nothing changes if the result does not compile; the errors come back with file and line. Administrators only.")]
    public static async Task<JsonElement> EditFunctionFile(
        IServiceProvider services,
        [Description("The file's name, such as Weather.cs.")] string name,
        [Description("The edits, applied in order: find (text exactly as it is in the file, appearing once) and replace (what it becomes; empty removes it).")] IReadOnlyList<FileEdit> edits,
        [Description("The site's domain, or empty for the global functions.")] string? domain = null,
        CancellationToken ct = default)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;
        if (edits is not { Count: > 0 }) return Error("Give at least one edit.");

        var (scope, scopeError) = FunctionScope(services.GetRequiredService<SiteStore>(), domain);
        if (scopeError is not null) return Error(scopeError);

        var functions = services.GetRequiredService<FunctionDeploymentService>();
        var file = (await functions.LiveFilesAsync(scope)).FirstOrDefault(f => f.Name.Equals((name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (file is null) return Error($"No function file is called '{name}'. read_function_files shows them; deploy_functions adds a new one.");

        var (text, editError) = ApplyEdits(file.Text, edits, file.Name, "read_function_files");
        if (text is null) return Error(editError);

        // The live files are kept as deployed, so the edited one goes back as it is, like
        // FunctionDeploymentService.RemoveFileAsync does with the rest.
        var result = await functions.DeployAsync(scope, [new FunctionFile(file.Name, text)], caller.Actor, "mcp", replace: false, ct);
        return result.Ok
            ? ToJson(new { ok = true, name = file.Name, edits = edits.Count, functions = result.Bundle, diagnostics = result.Diagnostics })
            : ToJson(new { error = result.Error, diagnostics = result.Diagnostics });
    }

    [SiteHostTool("remove_function_file", ToolEffect.Destructive)]
    [Description("Removes one function file and deploys the rest, which must still compile. To stop all of a site's functions, use remove_functions. Administrators only.")]
    public static async Task<JsonElement> RemoveFunctionFile(
        IServiceProvider services,
        [Description("The file's name, such as Weather.cs.")] string name,
        [Description("The site's domain, or empty for the global functions.")] string? domain = null,
        CancellationToken ct = default)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        var (scope, scopeError) = FunctionScope(services.GetRequiredService<SiteStore>(), domain);
        if (scopeError is not null) return Error(scopeError);

        var result = await services.GetRequiredService<FunctionDeploymentService>().RemoveFileAsync(scope, name ?? "", caller.Actor, "mcp", ct);
        return result.Ok
            ? ToJson(new { ok = true, domain = scope, removed = name, functions = result.Bundle })
            : ToJson(new { error = result.Error, diagnostics = result.Diagnostics });
    }

    [SiteHostTool("check_functions", ToolEffect.Read)]
    [Description("Compiles function files exactly as a deploy would, without making them live. Returns a draft id for test_functions, the routes found, and compiler errors with file and line. Files are written as deploy_functions describes. Administrators only.")]
    public static Task<JsonElement> CheckFunctions(
        IServiceProvider services,
        [Description("The files: C# (.cs) or LINQPad (.linq).")] IReadOnlyList<FunctionFileInput> files,
        [Description("The site's domain, or empty for the global functions.")] string? domain = null,
        [Description("True when the files are the whole set; false merges them into the live files, replacing same-named ones.")] bool replace = false,
        CancellationToken ct = default) =>
        BuildFunctionsAsync(services, files, domain, replace, deploy: false, ct);

    [SiteHostTool("deploy_functions", ToolEffect.Change)]
    [Description("Compiles function files and makes them live. Nothing changes if they do not compile; the errors come back with file and line, so this can be called directly without check_functions when no test is needed. Works on a site with no files too, such as one made only of proxy routes: a [Middleware] there runs before the proxied paths, so it can guard them with a sign-in. Administrators only. " + FunctionsGuide)]
    public static Task<JsonElement> DeployFunctions(
        IServiceProvider services,
        [Description("The files: C# (.cs) or LINQPad (.linq).")] IReadOnlyList<FunctionFileInput> files,
        [Description("The site's domain, or empty for the global functions.")] string? domain = null,
        [Description("True when the files are the whole set; false merges them into the live files, replacing same-named ones.")] bool replace = false,
        CancellationToken ct = default) =>
        BuildFunctionsAsync(services, files, domain, replace, deploy: true, ct);

    [SiteHostTool("test_functions", ToolEffect.Read)]
    [Description("Runs one HTTP request against a site's functions in memory, live or a draft from check_functions, and returns the status, headers and body. Nothing goes over the network and nothing goes live. Administrators only.")]
    public static async Task<JsonElement> TestFunctions(
        IServiceProvider services,
        [Description("The site's domain, or empty for the global functions.")] string? domain = null,
        [Description("'live', or a draft id from check_functions.")] string target = "live",
        [Description("The HTTP method.")] string method = "GET",
        [Description("The path, such as /api/orders.")] string path = "/",
        [Description("The query string, without the ?.")] string? query = null,
        [Description("Request headers, one 'Name: value' per line.")] string? headers = null,
        [Description("The request body.")] string? body = null,
        [Description("For global functions, the site host the request is for.")] string? host = null,
        CancellationToken ct = default)
    {
        if (Caller.Of(services).AdministratorsOnly() is { } refused) return refused;

        var (scope, scopeError) = FunctionScope(services.GetRequiredService<SiteStore>(), domain);
        if (scopeError is not null) return Error(scopeError);

        var result = await services.GetRequiredService<FunctionTestService>().RunAsync(
            scope, new FunctionTestRequest(target, method, path, query, headers, body, host), services, ct);
        var (text, encoding) = FunctionEndpoints.AsText(result.Body, result.ContentType);

        return ToJson(new
        {
            status = result.Status,
            contentType = result.ContentType,
            headers = result.Headers,
            body = text,
            bodyEncoding = encoding,
            elapsedMs = result.ElapsedMs,
            matched = result.Matched,
            truncated = result.Truncated,
            error = result.Error
        });
    }

    [SiteHostTool("remove_functions", ToolEffect.Destructive)]
    [Description("Stops running a site's functions (or the global ones, with no domain). Earlier releases keep theirs. Administrators only.")]
    public static async Task<JsonElement> RemoveFunctions(
        IServiceProvider services,
        [Description("The site's domain, or empty for the global functions.")] string? domain = null)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        // Never null by accident: a domain that cannot be read is an error, not the global functions.
        var (scope, scopeError) = FunctionScope(services.GetRequiredService<SiteStore>(), domain);
        if (scopeError is not null) return Error(scopeError);

        var (ok, error) = await services.GetRequiredService<FunctionDeploymentService>().RemoveAsync(scope, caller.Actor);
        return ok ? ToJson(new { ok = true, domain = scope }) : Error(error);
    }

    // ================================================================ AI

    [SiteHostTool("list_ai_providers", ToolEffect.Read)]
    [Description("The AI providers sites can chat through. Their keys are never shown. Administrators only.")]
    public static async Task<JsonElement> ListAiProviders(IServiceProvider services)
    {
        if (Caller.Of(services).AdministratorsOnly() is { } refused) return refused;
        return ToJson((await services.GetRequiredService<AiProviderStore>().ListAsync()).Select(AdminEndpoints.DescribeProvider));
    }

    [SiteHostTool("list_ai_models", ToolEffect.Read)]
    [Description("The models an AI provider offers, asked of the provider itself, fast and inexpensive ones first. Use it to choose a model for set_site_ai or add_ai_provider instead of guessing a name. Administrators only.")]
    public static async Task<JsonElement> ListAiModels(
        IServiceProvider services, [Description("The provider's id, from list_ai_providers.")] string providerId, CancellationToken ct = default)
    {
        if (Caller.Of(services).AdministratorsOnly() is { } refused) return refused;

        var provider = await services.GetRequiredService<AiProviderStore>().FindAsync(providerId);
        if (provider is null) return Error("No AI provider has that id.");

        var list = await services.GetRequiredService<AiModelCatalog>().ListAsync(provider, ct);
        return list.Error is not null
            ? Error(list.Error)
            : ToJson(new { providerId = provider.Id, models = list.Models.Take(60).Select(m => m.Fast ? $"{m.Id} (fast)" : m.Id) });
    }

    [SiteHostTool("add_ai_provider", ToolEffect.Change)]
    [Description("Adds an AI provider. Kind 'openai' is OpenAI and anything with the same API (OpenRouter, Ollama, LiteLLM); 'anthropic' is Anthropic. The key is stored encrypted and never shown again. Administrators only.")]
    public static async Task<JsonElement> AddAiProvider(
        IServiceProvider services,
        [Description("A name to choose it by.")] string name,
        [Description("'openai' or 'anthropic'.")] string kind,
        [Description("The model used when a site names none.")] string defaultModel,
        [Description("The API key.")] string? apiKey = null,
        [Description("The API's base URL. Leave out for OpenAI's or Anthropic's own; give it for a proxy or local server, such as http://ollama:11434/v1.")] string? baseUrl = null)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        var (provider, error) = await services.GetRequiredService<AiProviderStore>().AddAsync(name, kind, baseUrl, apiKey, defaultModel, caller.Actor);
        return provider is null ? Error(error) : ToJson(AdminEndpoints.DescribeProvider(provider));
    }

    [SiteHostTool("test_ai_provider", ToolEffect.Read)]
    [Description("Sends a one-word prompt to an AI provider and reports the answer, the time and the tokens, or why it failed. Billed like any other chat. Administrators only.")]
    public static async Task<JsonElement> TestAiProvider(
        IServiceProvider services, [Description("The provider's id.")] string id, CancellationToken ct = default)
    {
        if (Caller.Of(services).AdministratorsOnly() is { } refused) return refused;

        var provider = await services.GetRequiredService<AiProviderStore>().FindAsync(id);
        if (provider is null) return Error("No AI provider has that id.");

        return ToJson(await AiProviderTest.RunAsync(services.GetRequiredService<AiChatService>(), provider, ct));
    }

    [SiteHostTool("set_site_ai", ToolEffect.Change)]
    [Description("Chooses the AI provider a site's functions and pages chat through, the model and the system prompt. Functions then take an IAiChat parameter; pages call site.ai.chat('question', { onText }) after loading /_host/site.js, if allowVisitors is true. Administrators only.")]
    public static async Task<JsonElement> SetSiteAi(
        IServiceProvider services,
        [Description("The site's domain.")] string domain,
        [Description("The provider's id, from list_ai_providers.")] string providerId,
        [Description("The model. Leave out for the provider's default.")] string? model = null,
        [Description("Sent ahead of every conversation.")] string? systemPrompt = null,
        [Description("True lets every visitor's page chat, billed to the provider's key.")] bool allowVisitors = false)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        return await WithSiteAsync(services, domain, async site =>
        {
            var (ok, error) = await services.GetRequiredService<SiteAiSettingsService>()
                .SetAsync(site, providerId, model, systemPrompt, allowVisitors, caller.Actor);
            return ok ? ApiEndpoints.DescribeAi(site, services.GetRequiredService<SiteAiChatFactory>(), saved: true) : new { error };
        });
    }

    [SiteHostTool("remove_site_ai", ToolEffect.Destructive)]
    [Description("Removes a site's AI settings: its functions and pages can no longer chat until set_site_ai is called again. Administrators only.")]
    public static async Task<JsonElement> RemoveSiteAi(IServiceProvider services, [Description("The site's domain.")] string domain)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        return await WithSiteAsync(services, domain, async site =>
        {
            await services.GetRequiredService<SiteAiSettingsService>().RemoveAsync(site, caller.Actor);
            return ApiEndpoints.DescribeAi(site, services.GetRequiredService<SiteAiChatFactory>(), saved: true);
        });
    }

    [SiteHostTool("update_ai_provider", ToolEffect.Change)]
    [Description("Changes an AI provider: its name, default model, base URL or API key, such as to replace a key. Anything left out stays as it is; the kind cannot change. Sites using it pick up the change at once. Administrators only.")]
    public static async Task<JsonElement> UpdateAiProvider(
        IServiceProvider services,
        [Description("The provider's id or name, from list_ai_providers.")] string provider,
        [Description("A new name.")] string? name = null,
        [Description("A new default model.")] string? defaultModel = null,
        [Description("A new base URL; empty for OpenAI's or Anthropic's own.")] string? baseUrl = null,
        [Description("A new API key.")] string? apiKey = null)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        var store = services.GetRequiredService<AiProviderStore>();
        if (await FindProviderAsync(store, provider) is not { } found) return Error($"No AI provider is called '{provider}'. list_ai_providers shows them.");

        var (updated, error) = await store.UpdateAsync(
            found.Id, name ?? found.Name, baseUrl ?? found.BaseUrl, apiKey, defaultModel ?? found.DefaultModel, caller.Actor);
        return updated is null ? Error(error) : ToJson(AdminEndpoints.DescribeProvider(updated));
    }

    [SiteHostTool("remove_ai_provider", ToolEffect.Destructive)]
    [Description("Deletes an AI provider and its stored key. Sites that used it can no longer chat until another is chosen with set_site_ai. Administrators only.")]
    public static async Task<JsonElement> RemoveAiProvider(
        IServiceProvider services, [Description("The provider's id or name, from list_ai_providers.")] string provider)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        var store = services.GetRequiredService<AiProviderStore>();
        if (await FindProviderAsync(store, provider) is not { } found) return Error($"No AI provider is called '{provider}'. list_ai_providers shows them.");

        var usedBy = services.GetRequiredService<SiteStore>().List().Where(site => site.Ai?.ProviderId == found.Id).Select(site => site.Domain).ToList();
        var assistantUsedIt = (await services.GetRequiredService<AssistantSettingsStore>().ReadAsync()).ProviderId == found.Id;
        var removed = await store.DeleteAsync(found.Id, caller.Actor);
        return removed is null
            ? Error("That provider no longer exists.")
            : ToJson(new
            {
                ok = true,
                id = removed.Id,
                name = removed.Name,
                sitesLeftWithoutAi = usedBy,
                assistantLeftWithoutAi = assistantUsedIt ? "The management assistant used it: choose another provider with set_assistant_settings." : null
            });
    }

    [SiteHostTool("get_assistant_settings", ToolEffect.Read)]
    [Description("The management assistant's settings: which AI provider and model it uses, and whether members may use it. Administrators only.")]
    public static async Task<JsonElement> GetAssistantSettings(IServiceProvider services)
    {
        if (Caller.Of(services).AdministratorsOnly() is { } refused) return refused;
        return ToJson(await services.GetRequiredService<AssistantSettingsStore>().ReadAsync());
    }

    [SiteHostTool("set_assistant_settings", ToolEffect.Change)]
    [Description("Sets the management assistant's AI provider and model, and whether members may use it. No provider turns the assistant off. Administrators only.")]
    public static async Task<JsonElement> SetAssistantSettings(
        IServiceProvider services,
        [Description("The provider's id or name; empty turns the assistant off.")] string? provider = null,
        [Description("The model. Leave out for the provider's default.")] string? model = null,
        [Description("True lets members use the assistant too, as themselves.")] bool membersMayUse = false)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        string? providerId = null;
        if (!string.IsNullOrWhiteSpace(provider))
        {
            if (await FindProviderAsync(services.GetRequiredService<AiProviderStore>(), provider) is not { } found)
                return Error($"No AI provider is called '{provider}'. list_ai_providers shows them.");
            providerId = found.Id;
        }

        var settings = services.GetRequiredService<AssistantSettingsStore>();
        var error = await settings.SaveAsync(providerId, model, membersMayUse, caller.Actor);
        return error is null ? ToJson(await settings.ReadAsync()) : Error(error);
    }

    // ================================================================ users

    [SiteHostTool("list_users", ToolEffect.Read)]
    [Description("The server's accounts and their roles. Administrators only.")]
    public static async Task<JsonElement> ListUsers(IServiceProvider services)
    {
        if (Caller.Of(services).AdministratorsOnly() is { } refused) return refused;
        return ToJson((await services.GetRequiredService<UserStore>().ListAsync()).Select(AdminEndpoints.DescribeUser));
    }

    [SiteHostTool("create_user", ToolEffect.Change)]
    [Description("Creates an account and returns, once, a private link (or a temporary password) for the person to sign in with. Send it to them yourself. Administrators only.")]
    public static async Task<JsonElement> CreateUser(
        IServiceProvider services,
        [Description("The user name they sign in with.")] string username,
        [Description("The name shown for them.")] string? displayName = null,
        [Description("'Member' or 'Administrator'.")] string role = Roles.Member,
        [Description("True for a temporary password, false for a private link.")] bool temporaryPassword = false)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        var (created, error) = await services.GetRequiredService<UserAdminService>()
            .CreateAsync(username, displayName, RoleName(role), temporaryPassword, caller.Actor);
        if (created is null) return Error(error);

        var links = services.GetRequiredService<LinkGenerator>();
        return ToJson(new
        {
            user = AdminEndpoints.DescribeUser(created.User),
            temporaryPassword = created.TemporaryPassword,
            inviteLink = created.InviteToken is null ? null : links.GetUriByPage(caller.Http, "/Activate", null, new { token = created.InviteToken })
        });
    }

    [SiteHostTool("invite_user", ToolEffect.Change)]
    [Description("Makes a new private sign-in link for an account, such as for someone who lost theirs or never used it, and returns it once. Any earlier link stops working. Send it to them yourself. Administrators only.")]
    public static async Task<JsonElement> InviteUser(IServiceProvider services, [Description("The user's id or user name, from list_users.")] string user)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        var users = services.GetRequiredService<UserStore>();
        if (await FindUserAsync(users, user) is not { } found) return Error($"No account is called '{user}'. list_users shows them.");

        var (token, error) = await services.GetRequiredService<UserAdminService>().InviteAsync(found.Id, caller.Actor);
        if (token is null) return Error(error);

        return ToJson(new
        {
            user = AdminEndpoints.DescribeUser((await users.FindByIdAsync(found.Id))!),
            inviteLink = services.GetRequiredService<LinkGenerator>().GetUriByPage(caller.Http, "/Activate", null, new { token })
        });
    }

    [SiteHostTool("set_user_role", ToolEffect.Change)]
    [Description("Makes an account a Member or an Administrator. The last administrator cannot be made a member, and you cannot change your own role. Administrators only.")]
    public static async Task<JsonElement> SetUserRole(
        IServiceProvider services,
        [Description("The user's id or user name, from list_users.")] string user,
        [Description("'Member' or 'Administrator'.")] string role)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        var users = services.GetRequiredService<UserStore>();
        if (await FindUserAsync(users, user) is not { } found) return Error($"No account is called '{user}'. list_users shows them.");

        var error = await services.GetRequiredService<UserAdminService>().SetRoleAsync(found.Id, RoleName(role), caller.UserId, caller.Actor);
        return error is null ? ToJson(AdminEndpoints.DescribeUser((await users.FindByIdAsync(found.Id))!)) : Error(error);
    }

    [SiteHostTool("disable_user", ToolEffect.Destructive)]
    [Description("Disables an account, which signs them out and stops their API keys, or enables it again with disabled false. You cannot disable yourself. Administrators only.")]
    public static async Task<JsonElement> DisableUser(
        IServiceProvider services,
        [Description("The user's id or user name, from list_users.")] string user,
        [Description("True disables the account; false enables it again.")] bool disabled = true)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        var users = services.GetRequiredService<UserStore>();
        if (await FindUserAsync(users, user) is not { } found) return Error($"No account is called '{user}'. list_users shows them.");

        var error = await services.GetRequiredService<UserAdminService>().SetDisabledAsync(found.Id, disabled, caller.UserId, caller.Actor);
        return error is null ? ToJson(AdminEndpoints.DescribeUser((await users.FindByIdAsync(found.Id))!)) : Error(error);
    }

    [SiteHostTool("delete_user", ToolEffect.Destructive)]
    [Description("Deletes an account and its API keys. Sites it deployed stay. You cannot delete yourself or the last administrator. Administrators only.")]
    public static async Task<JsonElement> DeleteUser(IServiceProvider services, [Description("The user's id or user name, from list_users.")] string user)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;

        if (await FindUserAsync(services.GetRequiredService<UserStore>(), user) is not { } found)
            return Error($"No account is called '{user}'. list_users shows them.");

        var (username, error) = await services.GetRequiredService<UserAdminService>().DeleteAsync(found.Id, caller.UserId, caller.Actor);
        return username is null ? Error(error) : ToJson(new { ok = true, id = found.Id, username });
    }

    // ================================================================ realtime

    [SiteHostTool("get_realtime", ToolEffect.Read)]
    [Description("How many pages are connected to a site's realtime channel now, and its groups with their member counts.")]
    public static JsonElement GetRealtime(IServiceProvider services, [Description("The site's domain.")] string domain) =>
        WithSite(services, domain, site =>
        {
            var registry = services.GetRequiredService<RealtimeRegistry>();
            return new
            {
                domain = site.Domain,
                connections = registry.ConnectionCount(site.Domain),
                groups = registry.GroupSizes(site.Domain).Select(group => new { name = group.Name, members = group.Members })
            };
        });

    [SiteHostTool("publish_realtime_event", ToolEffect.Change)]
    [Description("Sends an event to the pages open on a site, which receive it with site.realtime.on('name', handler) from /_host/site.js: every page, or only one group, one user or one connection. Returns how many connections it went to.")]
    public static async Task<JsonElement> PublishRealtimeEvent(
        IServiceProvider services,
        [Description("The site's domain.")] string domain,
        [Description("The event's name, such as site.deployed or orders.updated.")] string @event,
        [Description("Any JSON the pages need with it, up to 256 KB.")] JsonElement? payload = null,
        [Description("Only the pages in this group.")] string? group = null,
        [Description("Only the pages of this signed-in user.")] string? user = null,
        [Description("Only this one connection.")] string? connection = null,
        CancellationToken ct = default) =>
        await WithSiteAsync(services, domain, async site =>
        {
            var (delivered, error) = await ApiEndpoints.PublishToSiteAsync(
                site,
                new RealtimePublishRequest(@event, payload is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } ? payload : null, group, user, connection),
                services.GetRequiredService<RealtimeRegistry>(),
                services.GetRequiredService<SiteRealtimeFactory>(),
                ct);
            return error is null ? new { ok = true, domain = site.Domain, delivered } : new { error };
        });

    // ================================================================ helpers

    private static object DescribeSite(IServiceProvider services, SiteRecord site) => ApiEndpoints.Describe(
        site,
        services.GetRequiredService<SiteVariableService>(),
        services.GetRequiredService<SiteAiChatFactory>(),
        services.GetRequiredService<RealtimeRegistry>());

    /// <summary>
    /// Applies find-and-replace edits in order. Each find must appear exactly once in the text as it
    /// stands when that edit runs, so an edit never lands somewhere the model did not mean.
    /// </summary>
    private static (string? Text, string? Error) ApplyEdits(string text, IReadOnlyList<FileEdit> edits, string name, string readTool)
    {
        for (var i = 0; i < edits.Count; i++)
        {
            var find = edits[i].Find ?? "";
            var first = find.Length == 0 ? -1 : text.IndexOf(find, StringComparison.Ordinal);
            if (first < 0) return (null, $"Edit {i + 1}: its find text is not in {name}. {readTool} shows the text as it is now.");
            if (text.IndexOf(find, first + 1, StringComparison.Ordinal) >= 0)
                return (null, $"Edit {i + 1}: its find text appears more than once in {name}. Include more of the text around it.");
            text = string.Concat(text.AsSpan(0, first), edits[i].Replace ?? "", text.AsSpan(first + find.Length));
        }

        return (text, null);
    }

    private static async Task<JsonElement> BuildFunctionsAsync(
        IServiceProvider services, IReadOnlyList<FunctionFileInput> files, string? domain, bool replace, bool deploy, CancellationToken ct)
    {
        var caller = Caller.Of(services);
        if (caller.AdministratorsOnly() is { } refused) return refused;
        if (files is not { Count: > 0 }) return Error("Give at least one file.");

        var (scope, scopeError) = FunctionScope(services.GetRequiredService<SiteStore>(), domain);
        if (scopeError is not null) return Error(scopeError);

        var read = new List<FunctionFile>();
        foreach (var file in files)
        {
            if (FunctionUploadReader.Read(file.Name, Encoding.UTF8.GetBytes(file.Content ?? ""), read) is { } readError)
                return Error(readError);
        }

        var functions = services.GetRequiredService<FunctionDeploymentService>();
        var result = deploy
            ? await functions.DeployAsync(scope, read, caller.Actor, "mcp", replace, ct)
            : await functions.CheckAsync(scope, read, caller.Actor, ct, replace, source: "mcp");

        return result.Ok
            ? ToJson(new { ok = true, draft = deploy ? null : result.Bundle?.Id, functions = result.Bundle, kept = result.Kept, diagnostics = result.Diagnostics })
            : ToJson(new { error = result.Error, diagnostics = result.Diagnostics });
    }

    /// <summary>
    /// Which functions a function tool acts on: a site's, or the global ones when no domain is
    /// given at all. A domain that is given but cannot be read, or has no site, is an error, never
    /// the global functions: those answer on every site.
    /// </summary>
    internal static (string? Scope, string? Error) FunctionScope(SiteStore sites, string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return (null, null);

        var (normalized, error) = SiteStore.NormalizeDomain(domain);
        if (normalized is null) return (null, error ?? $"'{domain}' is not a domain.");
        return sites.TryGet(normalized) is null
            ? (null, $"No site is published at '{normalized}'. Leave the domain out for the global functions.")
            : (normalized, null);
    }

    private static JsonElement WithSite(IServiceProvider services, string domain, Func<SiteRecord, object> action)
    {
        var (site, error) = Find(services, domain);
        return site is null ? Error(error) : ToJson(action(site));
    }

    private static async Task<JsonElement> WithSiteAsync(IServiceProvider services, string domain, Func<SiteRecord, Task<object>> action)
    {
        var (site, error) = Find(services, domain);
        return site is null ? Error(error) : ToJson(await action(site));
    }

    private static (SiteRecord? Site, string? Error) Find(IServiceProvider services, string domain)
    {
        var (normalized, error) = SiteStore.NormalizeDomain(domain);
        if (normalized is null) return (null, error);

        var site = services.GetRequiredService<SiteStore>().TryGet(normalized);
        return site is null ? (null, $"No site is published at '{normalized}'. create_site makes one.") : (site, null);
    }

    /// <summary>A provider by id or, failing that, by name.</summary>
    private static async Task<AiProviderRecord?> FindProviderAsync(AiProviderStore store, string? idOrName)
    {
        var key = (idOrName ?? "").Trim();
        return await store.FindAsync(key)
               ?? (await store.ListAsync()).FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A role as the server spells it, whatever the case a model sent it in.</summary>
    private static string? RoleName(string? role) =>
        Roles.All.FirstOrDefault(r => r.Equals(role?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? role;

    /// <summary>An account by id or, failing that, by user name.</summary>
    private static async Task<UserRecord?> FindUserAsync(UserStore users, string? idOrName)
    {
        var key = (idOrName ?? "").Trim();
        return await users.FindByIdAsync(key) ?? await users.FindByUsernameAsync(key);
    }

    private static JsonElement ToJson(object? value) => JsonSerializer.SerializeToElement(value, Json);

    private static JsonElement Error(string? message) => ToJson(new { error = message });

    /// <summary>Who is calling: the user the current request is signed in as.</summary>
    private sealed record Caller(HttpContext Http, ClaimsPrincipal Principal)
    {
        public static Caller Of(IServiceProvider services)
        {
            var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext
                       ?? throw new InvalidOperationException("Site host tools run inside a signed-in HTTP request.");
            return new Caller(http, http.User);
        }

        public bool IsAdministrator => Principal.IsInRole(Roles.Administrator);

        public string Actor => ApiEndpoints.Actor(Principal);

        /// <summary>The account's id, so a tool can refuse to disable, demote or delete the caller.</summary>
        public string? UserId => Principal.FindFirstValue(ClaimTypes.NameIdentifier);

        /// <summary>Null for an administrator; otherwise the refusal to return.</summary>
        public JsonElement? AdministratorsOnly() =>
            IsAdministrator ? null : Error("Only administrators can do this. who_am_i shows your role.");
    }
}
