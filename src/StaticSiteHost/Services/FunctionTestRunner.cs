using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using StaticSiteHost.Functions;
using StaticSiteHost.Services.Ai;
using StaticSiteHost.Services.Realtime;

namespace StaticSiteHost.Services;

/// <summary>A request typed into the editor's test panel.</summary>
/// <param name="Target">"live", or the id of a draft build from Check.</param>
/// <param name="Headers">"Name: value" lines.</param>
/// <param name="Host">The host the request claims to be for. Only used for global functions; a site's is its domain.</param>
public sealed record FunctionTestRequest(
    string? Target,
    string? Method,
    string? Path,
    string? Query,
    string? Headers,
    string? Body,
    string? Host = null);

public sealed record FunctionTestResult(
    int Status,
    string? ContentType,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[] Body,
    long ElapsedMs,
    bool Matched,
    bool Truncated,
    string? Error);

/// <summary>
/// Runs one request against a build, in memory: the request is assembled here and handed to
/// the build's middleware and router directly, rather than sent over the network to the
/// site's domain. Only that build runs: a test of a site's functions goes through the site's
/// middleware but not the global middleware, and a test of the global functions the other way
/// about, so each can be tested on its own. Headers typed into the test are how to satisfy a
/// gate.
///
/// That is what makes it work for a draft that is not live anywhere, for a domain whose DNS
/// does not point here yet, and for a site behind a passcode — and it keeps the test out of
/// the browser, where a request from the management host to the site's host would be
/// cross-origin. The build is loaded for the one request and unloaded after.
///
/// Its <c>[ConfigureServices]</c> methods run, since a handler may need what they register, and
/// the request gets a scope of those services as a live one would; the services are disposed
/// with the build. Its background services and jobs never start: a test is one request, not the
/// functions going live.
///
/// Unlike a live request, a failure is reported in full, stack trace included: this is the
/// author debugging their own code. The request's <see cref="ISite"/> is the real site's, its
/// realtime side and AI included, so a test that publishes reaches the pages open on the site,
/// and one that chats is billed like any other chat.
/// </summary>
public sealed class FunctionTestRunner
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Beyond this the body is cut off. The page offers anything large as a download anyway.</summary>
    public const int MaxBodyBytes = 25 * 1024 * 1024;

    private const int MaxRequestBodyChars = 4 * 1024 * 1024;

    private readonly DataPaths _paths;
    private readonly SiteStore _sites;
    private readonly SiteVariableService _variables;
    private readonly SiteRealtimeFactory _realtime;
    private readonly SiteAiChatFactory _ai;
    private readonly ILoggerFactory _loggers;
    private readonly IDataProtectionProvider _protection;
    private readonly ILogger<FunctionTestRunner> _logger;

    public FunctionTestRunner(
        DataPaths paths,
        SiteStore sites,
        SiteVariableService variables,
        SiteRealtimeFactory realtime,
        SiteAiChatFactory ai,
        ILoggerFactory loggers,
        IDataProtectionProvider protection,
        ILogger<FunctionTestRunner> logger)
    {
        _paths = paths;
        _sites = sites;
        _variables = variables;
        _realtime = realtime;
        _ai = ai;
        _loggers = loggers;
        _protection = protection;
        _logger = logger;
    }

    /// <param name="dataDir">
    /// The same data directory live requests get. A test is not a sandbox: it reads and writes
    /// the real data, exactly as the live site would.
    /// </param>
    /// <param name="host">The host the request is for, which is also the domain its <see cref="ISite"/> reports.</param>
    /// <param name="variables">
    /// The variables the handlers see, as a live request for the same site would: the site's own
    /// for its functions, and for the global ones those of the site the test names as its host.
    /// </param>
    public async Task<FunctionTestResult> RunAsync(
        string binDir, string host, string dataDir, ResolvedVariables variables,
        FunctionTestRequest request, IServiceProvider services, CancellationToken ct)
    {
        var method = string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method.Trim().ToUpperInvariant();
        var path = "/" + (request.Path ?? "").Trim().TrimStart('/');
        var query = (request.Query ?? "").Trim().TrimStart('?');

        if ((request.Body?.Length ?? 0) > MaxRequestBodyChars)
            return Failed($"The request body is limited to {MaxRequestBodyChars / 1024 / 1024} MB.");

        using var aborted = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var body = new CappedMemoryStream(MaxBodyBytes);
        var context = BuildContext(host, method, path, query, request, services, body, aborted.Token);
        context.Items[FunctionRouter.DataDirectoryItem] = dataDir;
        context.Items[FunctionRouter.VariablesItem] = variables.All;

        // The global functions are tested against the shared data folder, and outside a request
        // they are "global", as they are live; a site's are the site's.
        var domain = dataDir == _paths.GlobalFunctionsDataDir ? null : host;

        FunctionSet? set = null;
        FunctionSet.ServiceScope? entry = null;
        var clock = Stopwatch.StartNew();
        try
        {
            set = FunctionSet.Load(binDir, "functions:test");
            set.BuildServices(new FunctionSetEnvironment(
                domain ?? SiteContext.GlobalDomain, SiteContext.ForSet(domain, dataDir, _sites, _variables, _realtime, _ai),
                _loggers, _protection));

            // The host is the site the request is for, as it would be live: a site's own domain, or
            // for the global functions whichever host the test names, which may have no site at all.
            entry = set.TryEnter()!;
            var site = SiteStore.NormalizeDomain(host).Domain ?? host;
            context.Items[SiteHttpContextExtensions.ItemKey] =
                new SiteContext(host, dataDir, variables, entry.Services, _realtime.Create(site), _ai.Create(site));

            var router = set.Router;

            // Null until the middleware goes on to the handlers. If it never does, it answered the
            // request itself, and that answer is the result.
            bool? dispatched = null;
            var run = router.InvokeMiddlewareAsync(context, async () => { dispatched = await router.TryDispatchAsync(context); });
            var finished = await Task.WhenAny(run, Task.Delay(Timeout, ct));

            if (finished != run)
            {
                // The handler keeps its thread until it notices; all a test can do is stop waiting.
                await aborted.CancelAsync();
                return Failed($"The function did not finish within {Timeout.TotalSeconds:0} seconds.", clock.ElapsedMilliseconds);
            }

            await run;
            var matched = dispatched ?? true;
            await context.Response.Body.FlushAsync(ct);

            if (!matched)
            {
                return Failed(
                    $"No route matches {method} {path}. On the live site this request would fall through to the site's files.",
                    clock.ElapsedMilliseconds, matched: false);
            }

            return new FunctionTestResult(
                context.Response.StatusCode,
                context.Response.ContentType,
                context.Response.Headers.Select(h => new KeyValuePair<string, string>(h.Key, h.Value.ToString())).ToList(),
                body.ToArray(),
                clock.ElapsedMilliseconds,
                Matched: true,
                Truncated: body.Truncated,
                Error: null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "A function test of {Method} {Path} threw", method, path);
            return new FunctionTestResult(
                StatusCodes.Status500InternalServerError,
                context.Response.ContentType,
                [],
                body.ToArray(),
                clock.ElapsedMilliseconds,
                Matched: true,
                Truncated: body.Truncated,
                Error: ex.ToString());
        }
        finally
        {
            if (entry is not null) await DisposeQuietlyAsync(entry.DisposeAsync);
            if (set is not null)
            {
                await DisposeQuietlyAsync(() => new ValueTask(set.DisposeServicesAsync(_logger, "test")));
                set.Unload();
            }
        }
    }

    /// <summary>What the functions' own Dispose throws is theirs, and must not replace the test's result.</summary>
    private async Task DisposeQuietlyAsync(Func<ValueTask> dispose)
    {
        try
        {
            await dispose();
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Disposing a function test's services threw");
        }
    }

    private static DefaultHttpContext BuildContext(
        string host, string method, string path, string query, FunctionTestRequest request,
        IServiceProvider services, Stream responseBody, CancellationToken aborted)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseBody));
        context.RequestAborted = aborted;

        var http = context.Request;
        http.Method = method;
        http.Scheme = "https";
        http.Host = new HostString(host);
        http.Path = path;
        http.QueryString = query.Length > 0 ? new QueryString("?" + query) : QueryString.Empty;

        foreach (var line in (request.Headers ?? "").Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var name = line[..colon].Trim();
            if (name.Length > 0) http.Headers.Append(name, line[(colon + 1)..].Trim());
        }

        if (!string.IsNullOrEmpty(request.Body))
        {
            var bytes = Encoding.UTF8.GetBytes(request.Body);
            http.Body = new MemoryStream(bytes);
            http.ContentLength = bytes.Length;
        }

        return context;
    }

    private static FunctionTestResult Failed(string error, long elapsed = 0, bool matched = true) =>
        new(0, null, [], [], elapsed, matched, false, error);

    /// <summary>
    /// A response body that stops growing at a limit instead of exhausting memory.
    ///
    /// The limit lives in the array overload, and everything else funnels into it. It has to be
    /// that way round: for a subclass, MemoryStream's span overload hands off to the array
    /// overload, so a span overload that called back into the base would recurse until the
    /// stack overflowed, and a stack overflow ends the process.
    /// </summary>
    private sealed class CappedMemoryStream(int limit) : MemoryStream
    {
        public bool Truncated { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var room = limit - (int)Length;
            if (count > room) Truncated = true;
            if (room > 0) base.Write(buffer, offset, Math.Min(room, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var copy = buffer.ToArray();
            Write(copy, 0, copy.Length);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void WriteByte(byte value) => Write([value], 0, 1);
    }
}
