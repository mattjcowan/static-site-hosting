using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Models;
using StaticSiteHost.Services;

namespace StaticSiteHost.Serving;

/// <summary>
/// Serves files out of a site's current release directory.
///
/// Resolution is done here (see <see cref="SitePathResolver"/>) and the request path is
/// then rewritten to the exact file, which is handed to a per-release
/// <see cref="StaticFileMiddleware"/>. That reuse buys correct content types, ETags,
/// conditional requests and range support without reimplementing them.
/// </summary>
public sealed class SiteContentServer
{
    private const string StatusOverrideKey = "ssh.status-override";
    private const string FallbackKey = "ssh.fallback";
    private const string PrivateKey = "ssh.private";
    private const string RulesKey = "ssh.header-rules";
    private const string RequestPathKey = "ssh.request-path";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly ConcurrentDictionary<string, StaticFileMiddleware> _servers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SiteRules> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileExtensionContentTypeProvider _contentTypes = CreateContentTypeProvider();

    private readonly SitePathResolver _resolver;
    private readonly SiteStore _sites;
    private readonly DataPaths _paths;
    private readonly IWebHostEnvironment _environment;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SiteHostingOptions _options;

    public SiteContentServer(
        SitePathResolver resolver,
        SiteStore sites,
        DataPaths paths,
        IWebHostEnvironment environment,
        ILoggerFactory loggerFactory,
        IOptions<SiteHostingOptions> options)
    {
        _resolver = resolver;
        _sites = sites;
        _paths = paths;
        _environment = environment;
        _loggerFactory = loggerFactory;
        _options = options.Value;
    }

    /// <summary>
    /// Drops the cached file providers and compiled rules for a domain. Called after a
    /// deploy, a rollback, a delete or a change to the site's rules — anything that makes what
    /// is cached here no longer describe what should be served.
    /// </summary>
    public void Evict(string domain)
    {
        _rules.TryRemove(domain, out _);

        var prefix = _paths.SiteDir(domain);
        foreach (var key in _servers.Keys)
        {
            if (key.Equals(prefix, PathComparison) || key.StartsWith(prefix + Path.DirectorySeparatorChar, PathComparison))
            {
                _servers.TryRemove(key, out _);
            }
        }
    }

    public async Task ServeAsync(HttpContext context, SiteRecord site)
    {
        var domain = site.Domain;

        // A passcode-protected site has already been unlocked by the time it gets here.
        // Its files still must not sit in a shared cache or a search index, where the
        // gate no longer applies.
        if (site.IsPasscodeProtected) context.Items[PrivateKey] = true;

        var root = _sites.GetCurrentReleasePath(domain);
        if (root is null)
        {
            await WriteMessageAsync(context, StatusCodes.Status503ServiceUnavailable,
                "Nothing deployed yet", $"No content has been published for {WebUtility.HtmlEncode(domain)}.");
            return;
        }

        var method = context.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET, HEAD";
            return;
        }

        // Compiled once per domain and dropped by Evict. The request path is stashed with the
        // header rules because SendFileAsync is about to rewrite it to the file that answers,
        // and a rule should still see the URL the visitor asked for.
        var rules = _rules.GetOrAdd(domain, static (_, s) => SiteRules.Compile(s), site);
        var requestPath = context.Request.Path.Value ?? "/";
        var accept = context.Request.Headers.Accept;

        if (!rules.Headers.IsEmpty)
        {
            context.Items[RulesKey] = rules.Headers;
            context.Items[RequestPathKey] = requestPath;
        }

        SiteResolution resolution;

        if (!rules.Redirects.IsEmpty &&
            rules.Redirects.TryMatch(requestPath, path => _resolver.Exists(root, path), out var rule))
        {
            if (rule.IsRedirect)
            {
                SendRedirect(
                    context,
                    rules.Headers,
                    requestPath,
                    rule.Target.Contains('?') ? rule.Target : rule.Target + context.Request.QueryString,
                    rule.Status);
                return;
            }

            resolution = ResolveRewrite(root, rule, accept);
        }
        else
        {
            resolution = _resolver.Resolve(root, requestPath, accept);
        }

        switch (resolution.Kind)
        {
            case SiteResolutionKind.Redirect:
                SendRedirect(
                    context,
                    rules.Headers,
                    requestPath,
                    resolution.Location + context.Request.QueryString,
                    StatusCodes.Status301MovedPermanently);
                return;

            case SiteResolutionKind.Serve:
                await SendFileAsync(context, root, resolution);
                return;

            default:
                await WriteMessageAsync(context, StatusCodes.Status404NotFound,
                    "Not found", $"{WebUtility.HtmlEncode(context.Request.GetDisplayUrl())} could not be found.");
                return;
        }
    }

    /// <summary>
    /// Answers with a redirect. Header rules are applied here as well as to files: a redirect
    /// carries no Cache-Control of its own, and a browser will hold a 301 for a long time, so
    /// being able to say otherwise from a rule is the difference between a redirect that can
    /// be taken back and one that cannot.
    /// </summary>
    private static void SendRedirect(
        HttpContext context, SiteHeaderRules headers, string requestPath, string location, int status)
    {
        var response = context.Response;
        response.Headers.Location = location;
        response.StatusCode = status;

        headers.Apply(response.Headers, requestPath, null);

        if (context.Items.ContainsKey(PrivateKey))
        {
            response.Headers.CacheControl = KeepPrivate(response.Headers.CacheControl.ToString());
            response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        }
    }

    /// <summary>
    /// Works out which file answers a rewrite. The target is resolved straight against the
    /// filesystem and never run back through the rules, so one rewrite cannot trigger
    /// another and no rule set can loop.
    /// </summary>
    private SiteResolution ResolveRewrite(string root, RedirectMatch rule, string? accept)
    {
        var target = rule.Target;

        // A query on a rewrite target names no file; the visitor's own query is untouched.
        var query = target.IndexOf('?');
        if (query >= 0) target = target[..query];

        var resolution = _resolver.Resolve(root, target, accept);

        // A target naming a directory resolves to the 301 a visitor would have got. Inside a
        // rewrite that is not an answer, so follow it the one step to the directory's index.
        if (resolution.Kind == SiteResolutionKind.Redirect)
            resolution = _resolver.Resolve(root, resolution.Location!, accept);

        if (resolution.Kind != SiteResolutionKind.Serve) return SiteResolution.NotFound;

        // A 200 rule keeps whatever the target resolved to, so a rewrite pointing at nothing
        // still answers 404 rather than dressing the 404 page up as success.
        return SiteResolution.Serve(
            resolution.RelativePath!,
            isFallback: true,
            statusCode: rule.Status == StatusCodes.Status200OK ? resolution.StatusCode : rule.Status);
    }

    private async Task SendFileAsync(HttpContext context, string root, SiteResolution resolution)
    {
        var originalPath = context.Request.Path;

        if (resolution.StatusCode != StatusCodes.Status200OK)
            context.Items[StatusOverrideKey] = resolution.StatusCode;
        if (resolution.IsFallback)
            context.Items[FallbackKey] = true;

        context.Request.Path = resolution.RelativePath;
        try
        {
            await GetServer(root).Invoke(context);
        }
        finally
        {
            context.Request.Path = originalPath;
        }
    }

    private StaticFileMiddleware GetServer(string releaseDir) => _servers.GetOrAdd(releaseDir, CreateServer);

    private StaticFileMiddleware CreateServer(string releaseDir)
    {
        // ExclusionFilters.None so legitimate dot-directories such as /.well-known are served.
        // Dangerous names (.git, .env, …) are dropped during extraction instead.
        var options = new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(releaseDir, ExclusionFilters.None),
            ContentTypeProvider = _contentTypes,
            ServeUnknownFileTypes = true,
            DefaultContentType = "application/octet-stream",
            OnPrepareResponse = OnPrepareResponse
        };

        return new StaticFileMiddleware(
            NotFoundAsync,
            _environment,
            Options.Create(options),
            _loggerFactory);
    }

    private void OnPrepareResponse(StaticFileResponseContext context)
    {
        var http = context.Context;
        var response = http.Response;
        response.Headers["X-Content-Type-Options"] = "nosniff";

        if (http.Items.TryGetValue(StatusOverrideKey, out var value) &&
            value is int status &&
            response.StatusCode == StatusCodes.Status200OK)
        {
            response.StatusCode = status;
        }

        var isHtml = response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true;
        var isFallback = http.Items.ContainsKey(FallbackKey);

        // "private" keeps a proxy or CDN from holding a copy that would be handed to a
        // visitor who never passed the gate. The browser cache is per-person already, so
        // asset lifetimes are left alone.
        var isPrivate = http.Items.ContainsKey(PrivateKey);
        var scope = isPrivate ? "private" : "public";

        response.Headers.CacheControl = isHtml || isFallback
            ? (isPrivate ? "private, no-cache" : "no-cache")
            : $"{scope}, max-age={_options.AssetCacheSeconds}";

        if (http.Items.TryGetValue(RulesKey, out var compiled) && compiled is SiteHeaderRules rules)
        {
            var requestPath = http.Items.TryGetValue(RequestPathKey, out var original) && original is string path
                ? path
                : http.Request.Path.Value ?? "/";

            rules.Apply(response.Headers, requestPath, http.Request.Path.Value);
        }

        // Last word, after the rules: a gated site's content must not end up in a shared
        // cache or an index, and a rule saying "public, max-age=…" would put it there.
        if (isPrivate)
        {
            response.Headers.CacheControl = KeepPrivate(response.Headers.CacheControl.ToString());
            response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        }
    }

    /// <summary>Strips "public" out of a Cache-Control value and makes sure it says private.</summary>
    private static string KeepPrivate(string cacheControl)
    {
        var directives = cacheControl
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(d => !d.Equals("public", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!directives.Any(d => d.Equals("private", StringComparison.OrdinalIgnoreCase) ||
                                 d.Equals("no-store", StringComparison.OrdinalIgnoreCase)))
        {
            directives.Insert(0, "private");
        }

        return string.Join(", ", directives);
    }

    private static Task NotFoundAsync(HttpContext context) =>
        WriteMessageAsync(context, StatusCodes.Status404NotFound, "Not found", "The requested file could not be found.");

    private static async Task WriteMessageAsync(HttpContext context, int statusCode, string title, string detail)
    {
        if (context.Response.HasStarted) return;

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";

        var html = $$"""
            <!doctype html>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{statusCode}} — {{WebUtility.HtmlEncode(title)}}</title>
            <style>
              body { font: 16px/1.6 system-ui, sans-serif; margin: 0; display: grid; place-items: center;
                     min-height: 100vh; background: #0f1115; color: #d7dbe3; }
              main { max-width: 34rem; padding: 2rem; text-align: center; }
              h1 { font-size: 3.5rem; margin: 0; letter-spacing: -0.03em; }
              h2 { font-size: 1.25rem; font-weight: 600; margin: 0.25rem 0 1rem; }
              p { color: #8b93a5; word-break: break-all; }
            </style>
            <main><h1>{{statusCode}}</h1><h2>{{WebUtility.HtmlEncode(title)}}</h2><p>{{detail}}</p></main>
            """;

        await context.Response.WriteAsync(html, Encoding.UTF8);
    }

    private static FileExtensionContentTypeProvider CreateContentTypeProvider()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".avif"] = "image/avif";
        provider.Mappings[".webmanifest"] = "application/manifest+json";
        provider.Mappings[".wasm"] = "application/wasm";
        provider.Mappings[".mjs"] = "text/javascript";
        provider.Mappings[".cjs"] = "text/javascript";
        provider.Mappings[".map"] = "application/json";
        provider.Mappings[".md"] = "text/markdown; charset=utf-8";
        provider.Mappings[".yaml"] = "text/yaml; charset=utf-8";
        provider.Mappings[".yml"] = "text/yaml; charset=utf-8";
        provider.Mappings[".jsonld"] = "application/ld+json";
        return provider;
    }
}
