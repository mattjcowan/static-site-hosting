using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Net.Http.Headers;
using StaticSiteHost.Hubs;
using StaticSiteHost.Models;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Ai;

namespace StaticSiteHost.Serving;

/// <summary>
/// The paths under <c>/_host/</c>, which every site answers with the host's own endpoints rather
/// than with its files or its functions. The prefix is reserved on every site host: a request
/// under it reaches this class after the passcode gate and before anything the site deployed, so a
/// site cannot shadow it and a function cannot answer in its place.
///
///   GET  /_host/site.js         the browser library, with the site's public variables inlined
///   GET  /_host/variables.json  the same public variables, as a plain object
///   GET  /_host/signalr.js      the SignalR browser client, which site.js loads for site.realtime
///   POST /_host/ai/chat         chat through the site's AI provider; see <see cref="AiChatEndpoint"/>
///        /_host/realtime        the realtime hub, <see cref="SiteHub"/>, which never reaches this
///                               class: SiteHostingMiddleware passes it on to routing
///
/// Anything else under the prefix is a 404 with a JSON body, never a page from the site, so a
/// script that asks for something that does not exist is told so in a form it can read.
///
/// site.js and variables.json are <c>no-cache</c> with an ETag, so a page always sees current
/// values and pays for a 304 when nothing changed. signalr.js is the same bytes on every site until
/// the host is upgraded, and site.js asks for it with the host's version in the query string, so it
/// is cached for a day. On a site behind a passcode all three are <c>private</c>, so a shared cache
/// never holds them. Chat answers are <c>no-store</c>.
///
/// Every answer under the prefix, errors and the hub's included, carries
/// <c>Cross-Origin-Resource-Policy: same-origin</c> (<see cref="MarkSameOrigin"/>). Without it, a page
/// on another site could include site.js with <c>&lt;script src&gt;</c>, which a browser runs from
/// anywhere, and read <c>window.site</c>. From a sibling site under the same parent domain that
/// request carries the visitor's cookies for this one, the passcode's included, so a private site's
/// variables would reach a page that was never let in.
/// </summary>
public sealed class SiteHostEndpoints
{
    /// <summary>The reserved prefix. Matched without regard to case, like a function's routes.</summary>
    public const string Prefix = "/_host";

    private const string ScriptPath = "host/site.js";

    /// <summary>The vendored SignalR browser client; see <c>wwwroot/host/VENDORED.md</c>.</summary>
    private const string ClientPath = "host/signalr.min.js";

    /// <summary>A day: <c>site.js</c> names the host's version in the URL it loads it from, so an upgrade is not held up.</summary>
    private const int ClientMaxAgeSeconds = 24 * 60 * 60;

    private static readonly JsonSerializerOptions ScriptJson = new()
    {
        // The default encoder already escapes '<', '>', '&' and every non-ASCII character, so the
        // inlined object cannot end the <script> element it is loaded into. '/' is escaped below.
        Encoder = JavaScriptEncoder.Default
    };

    private readonly IWebHostEnvironment _environment;
    private readonly SiteVariableService _variables;
    private readonly SiteAiChatFactory _ai;
    private readonly AiChatEndpoint _aiChat;
    private readonly ILogger<SiteHostEndpoints> _logger;

    /// <summary>Read once; re-read on every request in Development, so edits show without a restart.</summary>
    private readonly byte[]? _script;

    /// <summary>Read once, since it only changes with the host, with its ETag.</summary>
    private readonly byte[]? _client;

    private readonly EntityTagHeaderValue? _clientTag;

    /// <summary>The host's version, without build metadata. Sent to the browser as <c>site.version</c>.</summary>
    private readonly string _version;

    public SiteHostEndpoints(
        IWebHostEnvironment environment,
        SiteVariableService variables,
        SiteAiChatFactory ai,
        AiChatEndpoint aiChat,
        ILogger<SiteHostEndpoints> logger)
    {
        _environment = environment;
        _variables = variables;
        _ai = ai;
        _aiChat = aiChat;
        _logger = logger;
        _script = ReadFile(ScriptPath);
        _client = ReadFile(ClientPath);
        if (_client is not null) _clientTag = TagFor(_client);

        var informational = typeof(SiteHostEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        _version = informational.Split('+')[0];
    }

    public static bool IsReserved(PathString path) => path.StartsWithSegments(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Keeps a response for the site's own pages: a browser refuses it to a page on any other origin that includes it.</summary>
    internal static void MarkSameOrigin(HttpResponse response) =>
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";

    /// <summary>Answers a request under <see cref="Prefix"/>. Returns false for any other path.</summary>
    public async Task<bool> TryHandleAsync(HttpContext context, SiteRecord site)
    {
        var request = context.Request;
        if (!request.Path.StartsWithSegments(Prefix, StringComparison.OrdinalIgnoreCase, out var rest)) return false;

        var endpoint = rest.Value?.TrimStart('/') ?? "";

        if (endpoint.Equals(AiChatEndpoint.Path, StringComparison.OrdinalIgnoreCase))
        {
            await _aiChat.HandleAsync(context, site);
            return true;
        }

        var isScript = endpoint.Equals("site.js", StringComparison.OrdinalIgnoreCase);
        var isVariables = endpoint.Equals("variables.json", StringComparison.OrdinalIgnoreCase);
        var isClient = endpoint.Equals("signalr.js", StringComparison.OrdinalIgnoreCase);

        if (!isScript && !isVariables && !isClient)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound,
                $"There is nothing at {Prefix}/{endpoint}. This site answers {Prefix}/site.js, {Prefix}/variables.json, " +
                $"{Prefix}/signalr.js, POST {Prefix}/{AiChatEndpoint.Path} and the realtime hub at {SiteHub.Path}.");
            return true;
        }

        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            context.Response.Headers.Allow = "GET, HEAD";
            await WriteErrorAsync(context, StatusCodes.Status405MethodNotAllowed, $"{request.Path} answers GET and HEAD only.");
            return true;
        }

        if (isClient)
        {
            if (_client is null)
            {
                await WriteErrorAsync(context, StatusCodes.Status500InternalServerError,
                    $"The host's {ClientPath} is missing from wwwroot, so site.realtime cannot connect. Check the server log.");
                return true;
            }

            await SendAsync(context, site, "text/javascript; charset=utf-8", _client, ClientMaxAgeSeconds, _clientTag);
            return true;
        }

        var visible = Sorted(_variables.Resolve(site).Public);

        if (isVariables)
        {
            await SendAsync(context, site, "application/json; charset=utf-8",
                JsonSerializer.SerializeToUtf8Bytes(visible));
            return true;
        }

        var script = _environment.IsDevelopment() ? ReadFile(ScriptPath) : _script;
        if (script is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status500InternalServerError,
                $"The host's {ScriptPath} is missing from wwwroot, so it cannot be served. Check the server log.");
            return true;
        }

        // One line in front of the library hands it this site's details; the library itself is the
        // same bytes for every site. ai.enabled says whether this page may try to chat: the site has
        // a provider, and either lets every visitor use it or has an [AiAccess] hook to decide (whose
        // answer depends on the request, so a page it refuses still sees true, and its chat a 403).
        // realtime says where the hub is, and where the SignalR client is, with the host's version so
        // an upgrade reaches past a day's cache.
        var chat = _ai.Create(site);
        var ai = new
        {
            enabled = chat.IsConfigured &&
                      (chat.AllowsVisitors || FunctionAccessHooks.Declares(site.CurrentFunctions, FunctionAccessHooks.Kind.AiAccess))
        };
        var realtime = new { path = SiteHub.Path, script = $"{Prefix}/signalr.js?v={Uri.EscapeDataString(_version)}" };
        var settings = JsonSerializer.Serialize(new { domain = site.Domain, vars = visible, version = _version, ai, realtime }, ScriptJson)
            .Replace("/", "\\/", StringComparison.Ordinal);
        var prefix = Encoding.UTF8.GetBytes($"var __siteHost = {settings};\n");

        await SendAsync(context, site, "text/javascript; charset=utf-8", [.. prefix, .. script]);
        return true;
    }

    /// <summary>
    /// Writes a body with the reserved prefix's caching rules: revalidate every time unless told
    /// otherwise, never in a shared cache when the site is gated, and a 304 when the browser already
    /// has these bytes.
    /// </summary>
    /// <param name="maxAgeSeconds">How long it may be used without asking again; null to ask every time.</param>
    /// <param name="etag">The body's ETag when it is known already; otherwise it is hashed here.</param>
    private static async Task SendAsync(
        HttpContext context, SiteRecord site, string contentType, byte[] body,
        int? maxAgeSeconds = null, EntityTagHeaderValue? etag = null)
    {
        var response = context.Response;
        etag ??= TagFor(body);

        var lifetime = maxAgeSeconds is { } seconds ? $"max-age={seconds.ToString(CultureInfo.InvariantCulture)}" : "no-cache";
        response.Headers.XContentTypeOptions = "nosniff";
        MarkSameOrigin(response);
        response.Headers.CacheControl =
            site.IsPasscodeProtected ? $"private, {lifetime}" :
            maxAgeSeconds is null ? lifetime :
            $"public, {lifetime}";
        if (site.IsPasscodeProtected) response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        response.Headers.ETag = etag.ToString();

        var ifNoneMatch = context.Request.GetTypedHeaders().IfNoneMatch;
        if (ifNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(etag, useStrongComparison: false)))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = contentType;
        response.ContentLength = body.Length;

        if (!HttpMethods.IsHead(context.Request.Method)) await response.Body.WriteAsync(body, context.RequestAborted);
    }

    /// <summary>The error answer of every path under the prefix: the status and <c>{ "error": "…" }</c>, never stored.</summary>
    internal static async Task WriteErrorAsync(HttpContext context, int statusCode, string error)
    {
        if (context.Response.HasStarted) return;

        context.Response.StatusCode = statusCode;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        MarkSameOrigin(context.Response);
        await context.Response.WriteAsJsonAsync(new { error }, context.RequestAborted);
    }

    private static EntityTagHeaderValue TagFor(byte[] body) =>
        new($"\"{Convert.ToHexStringLower(SHA256.HashData(body))[..32]}\"", isWeak: true);

    /// <summary>In name order, so the same variables always produce the same bytes and the same ETag.</summary>
    private static SortedDictionary<string, string> Sorted(IReadOnlyDictionary<string, string> variables)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in variables) sorted[name] = value;
        return sorted;
    }

    private byte[]? ReadFile(string path)
    {
        var file = _environment.WebRootFileProvider.GetFileInfo(path);
        if (!file.Exists)
        {
            _logger.LogError("wwwroot/{Path} is missing, so it cannot be served under {Prefix}/", path, Prefix);
            return null;
        }

        using var stream = file.CreateReadStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        // A byte-order mark would land in the middle of the response, after the line put in front.
        var bytes = buffer.ToArray();
        return bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes[Encoding.UTF8.Preamble.Length..] : bytes;
    }
}
