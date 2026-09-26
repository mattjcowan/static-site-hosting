using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http.Features;

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
/// the router directly, rather than sent over the network to the site's domain.
///
/// That is what makes it work for a draft that is not live anywhere, for a domain whose DNS
/// does not point here yet, and for a site behind a passcode — and it keeps the test out of
/// the browser, where a request from the management host to the site's host would be
/// cross-origin. The build is loaded for the one request and unloaded after.
///
/// Unlike a live request, a failure is reported in full, stack trace included: this is the
/// author debugging their own code.
/// </summary>
public sealed class FunctionTestRunner
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Beyond this the body is cut off. The page offers anything large as a download anyway.</summary>
    public const int MaxBodyBytes = 25 * 1024 * 1024;

    private const int MaxRequestBodyChars = 4 * 1024 * 1024;

    private readonly ILogger<FunctionTestRunner> _logger;

    public FunctionTestRunner(ILogger<FunctionTestRunner> logger) => _logger = logger;

    /// <param name="dataDir">
    /// The same data directory live requests get. A test is not a sandbox: it reads and writes
    /// the real data, exactly as the live site would.
    /// </param>
    public async Task<FunctionTestResult> RunAsync(
        string binDir, string host, string dataDir, FunctionTestRequest request, IServiceProvider services, CancellationToken ct)
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

        FunctionSet? set = null;
        var clock = Stopwatch.StartNew();
        try
        {
            set = FunctionSet.Load(binDir, "functions:test");

            var dispatch = set.Router.TryDispatchAsync(context);
            var finished = await Task.WhenAny(dispatch, Task.Delay(Timeout, ct));

            if (finished != dispatch)
            {
                // The handler keeps its thread until it notices; all a test can do is stop waiting.
                await aborted.CancelAsync();
                return Failed($"The function did not finish within {Timeout.TotalSeconds:0} seconds.", clock.ElapsedMilliseconds);
            }

            var matched = await dispatch;
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
            set?.Unload();
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
