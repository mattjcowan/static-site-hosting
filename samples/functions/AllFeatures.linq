<Query Kind="Program">
  <NuGetReference>StaticSiteHost.Abstractions</NuGetReference>
  <Namespace>Microsoft.AspNetCore.Http</Namespace>
  <Namespace>Microsoft.AspNetCore.Mvc</Namespace>
  <Namespace>Microsoft.Extensions.DependencyInjection</Namespace>
  <Namespace>Microsoft.Extensions.Logging</Namespace>
  <Namespace>Microsoft.Extensions.Logging.Abstractions</Namespace>
  <Namespace>StaticSiteHost.Functions</Namespace>
  <Namespace>StaticSiteHost.Functions.Testing</Namespace>
  <Namespace>System.Collections.Concurrent</Namespace>
  <Namespace>System.Security.Claims</Namespace>
  <Namespace>System.Text.Json</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
  <IncludeAspNet>true</IncludeAspNet>
  <DisableMyExtensions>true</DisableMyExtensions>
</Query>

// ─────────────────────────────────────────────────────────────────────────────
//  AllFeatures: every attribute and every parameter type a function can use,
//  in one file that deploys as a site's functions. Find the one you need; the
//  comment beside it says why it is there.
//
//  Deploy it as an administrator: Sites → the domain → Functions → upload, or
//  PUT /api/v1/sites/{domain}/functions. AllFeatures.cs is the same code as a
//  .cs file. Upload one of the two, not both: they declare the same classes. In
//  LINQPad, Main below runs; on the server, the #if LINQPAD block is left out.
//
//  Variables. This sample is functions only, so it has no _variables.json to
//  declare them. Set them on the site (Sites → the domain → Variables, or the API):
//    GREETING    public, so pages read it from /_host/site.js too. "Hello" if unset.
//    API_TOKEN   a secret. Only an administrator can set it, and it is never shown
//                again; this file only says whether it is set. With the API:
//                PUT /api/v1/sites/{domain}/variables/API_TOKEN {"value":"…","secret":true}
//
//  Routes     GET  /features/services          the parameter types, each once
//             POST /features/count/{name}      a route value; returns text
//             any  /features/status/{code}     [Route]; returns a status code
//             POST /features/realtime/publish  GET /features/realtime
//             POST /features/ai/ask            GET /features/ai/stream
//             GET  /features/fetch?url=https://…
//             POST /features/jobs/nightly      runs the nightly job now
//  Middleware SignIn: the "who" cookie becomes HttpContext.User for the rest
//             of the request. StampEveryResponse: a header on every response.
//  Hooks      a "who" cookie names the visitor; without one they are "guest".
// ─────────────────────────────────────────────────────────────────────────────

#if LINQPAD
async Task Main()
{
    // A site with both variables, two pages in one group, and an AI with its answer queued.
    var site = new FakeSite { Domain = "demo.localhost" };
    site.Variables.Set("GREETING", "Clear skies", isPublic: true).Set("API_TOKEN", "not-a-real-token");
    site.Realtime.Connect("page-1", user: "ada");
    site.Realtime.Connect("page-2", user: "guest");
    await site.Realtime.AddToGroupAsync("page-1", "public:news");
    await site.Realtime.AddToGroupAsync("page-2", "public:news");
    site.Ai.Reply("Vega, Deneb and Altair.");

    // What [ConfigureServices] registers, built the way the server builds it, and a scope for one request.
    var services = new ServiceCollection();
    FeatureServices.Configure(services, site, NullLogger.Instance);
    site.Services = services.BuildServiceProvider();
    using var scope = site.Services.CreateScope();
    var request = scope.ServiceProvider;

    var context = new DefaultHttpContext { RequestServices = request };
    context.UseSite(site);
    context.Request.Headers.Cookie = "who=ada";

    // 1. The middleware, run by hand as the server would: SignIn turns the who cookie into HttpContext.User.
    await Features.SignIn(context, () => Task.CompletedTask);

    // 2. Every parameter type a handler takes, and the User the middleware set.
    (await Features.Services(request.GetRequiredService<Counter>(), request.GetRequiredService<RequestId>(), request,
        NullLogger.Instance, site, site.Variables, site.Variables.All, site.Data, CancellationToken.None, context.Request))
        .Dump("GET /features/services");

    // 3. Publishing, then what the pages were sent, and who is connected.
    await Features.Publish(site.Realtime, "Jupiter is up", CancellationToken.None);
    site.Realtime.Published.Dump("published");
    Features.Connections(site.Realtime).Dump("GET /features/realtime");

    // 4. The AI, answered from the queue above, and what it was asked.
    (await Features.Ask(context.Request, site.Ai, "Name the Summer Triangle.", CancellationToken.None)).Dump("POST /features/ai/ask");
    site.Ai.Requests.Dump("asked");

    // A hook is a plain method: call it with a request to see what it decides.
    FeatureHooks.Join(context.Request, "staff").Dump("ada may join staff");
    FeatureHooks.Join(new DefaultHttpContext().Request, "staff").Dump("a guest may join staff");
}
#endif

// ── services ─────────────────────────────────────────────────────────────────

/// <summary>Registered as a singleton: one for as long as these functions are live, shared by every request.</summary>
public sealed class Counter
{
    private readonly ConcurrentDictionary<string, int> _counts = new();
    public int Next(string name) => _counts.AddOrUpdate(name, 1, (_, count) => count + 1);
}

/// <summary>Registered as scoped: a new one for each request, and for each run of a job.</summary>
public sealed class RequestId
{
    public string Value { get; } = Guid.NewGuid().ToString("N")[..12];
}

public static class FeatureServices
{
    // Runs once, as the functions load. Besides IServiceCollection it may take only ISite, ISiteVariables, IRealtime,
    // IAiChat, DirectoryInfo, the variables dictionary, ILogger and CancellationToken: nothing else exists yet.
    [ConfigureServices]
    public static void Configure(IServiceCollection services, ISite site, ILogger log)
    {
        services.AddSingleton<Counter>();
        services.AddScoped<RequestId>();

        // A named client: a handler asks IHttpClientFactory for "fetch" and gets these settings.
        services.AddHttpClient("fetch", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);         // a slow site cannot hold the request for long
            client.MaxResponseContentBufferSize = 1_000_000;  // nor fill the server's memory
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AllFeatures/1.0");
        });

        log.LogInformation("AllFeatures: services registered for {Domain}", site.Domain);
    }
}

// ── middleware and handlers ──────────────────────────────────────────────────

public static class Features
{
    // Serialise with options owned by this file, so their cache is thrown away with it on the next deploy.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Tells the rest of the request who the visitor is. Later middleware, this site's handlers and the global handlers
    // all read HttpContext.User, so this is how a site's own sign-in reaches everything else. The hooks run without it.
    [Middleware(Order = 0)]
    public static Task SignIn(HttpContext context, Func<Task> next)
    {
        var who = FeatureHooks.Who(context.Request);
        if (who != "guest") // the second argument, any non-empty name, is what makes the identity authenticated
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, who)], "who-cookie"));

        return next();
    }

    // Runs before every request to the site, files included. A lower Order runs first, and so wraps the rest.
    [Middleware(Order = 10)]
    public static Task StampEveryResponse(HttpContext context, Func<Task> next)
    {
        context.Response.Headers["X-All-Features"] = "on"; // before next(): by the time it returns, a file's headers are sent
        return next();                                    // not calling next() would end the request here
    }

    // The parameter types a handler can take. The server fills each one by its type, not its name.
    [HttpGet("/features/services")]
    public static async Task<IResult> Services(
        Counter counter,                          // your own singleton, from [ConfigureServices]
        RequestId requestId,                      // your own scoped service: a new one for this request
        IServiceProvider services,                // the request's scope, to resolve something by hand
        ILogger log,                              // a logger with the category functions:<domain>
        ISite site,                               // the site: domain, data folder, variables, services, realtime, AI
        ISiteVariables variables,                 // the variables, with helper methods
        IReadOnlyDictionary<string, string> all,  // the same variables as a plain dictionary, secrets included
        DirectoryInfo data,                       // the site's data folder, which is never served
        CancellationToken ct,                     // cancelled if the visitor goes away
        HttpRequest request)                      // the request itself; HttpContext and HttpResponse work the same way
    {
        log.LogInformation("Request {Id} for {Domain}", requestId.Value, site.Domain);
        var stamp = Path.Combine(data.FullName, "stamp.txt");

        return Results.Json(new
        {
            domain = site.Domain,
            visits = counter.Next("services"),
            requestId = requestId.Value,
            sameScope = ReferenceEquals(services.GetRequiredService<RequestId>(), requestId), // one scope per request
            greeting = variables.Get("GREETING", "Hello"),             // the fallback when it is missing or empty
            greetingIsPublic = variables.IsPublic("GREETING"),         // true when pages can read it as well
            apiTokenSet = variables.Get("API_TOKEN").Length > 0,       // whether it is set: never send a secret back
            apiTokenIsPublic = variables.IsPublic("API_TOKEN"),        // false: a secret never reaches a browser
            variableNames = all.Keys.Order(StringComparer.Ordinal),    // names only, since the values include secrets
            dataFiles = data.GetFiles().Select(file => file.Name).Order(StringComparer.Ordinal),
            stampedUtc = File.Exists(stamp) ? await File.ReadAllTextAsync(stamp, ct) : null,
            userAgent = request.Headers.UserAgent.ToString(),
            authenticated = request.HttpContext.User.Identity?.IsAuthenticated == true, // true once SignIn above set a User
            user = request.HttpContext.User.Identity?.Name,                            // the who cookie, or null for a guest
        }, Json);
    }

    // {name} fills the parameter called name; a query string value would too. A string is sent as text/plain.
    [HttpPost("/features/count/{name}")]
    public static string Count(Counter counter, string name) => $"{name}: {counter.Next(name)}";

    // [Route] answers every HTTP method. An int is the status code, with no body; "/features/status/abc" gets a 400.
    [Route("/features/status/{code}")]
    public static int Status(int code) => code is >= 200 and <= 599 ? code : StatusCodes.Status400BadRequest;

    // Sends "note" three ways, then answers 204, because it returns nothing. (Anyone may call this one; gate your own.)
    [HttpPost("/features/realtime/publish")]
    public static async Task Publish(IRealtime realtime, string? message, CancellationToken ct)
    {
        var note = new { message = message ?? "Hello", utc = DateTimeOffset.UtcNow };
        await realtime.PublishAsync("note", note, Json, ct);                        // every page open on the site
        await realtime.PublishToGroupAsync("public:news", "note", note, Json, ct);  // the pages that joined public:news
        await realtime.PublishToUserAsync("ada", "note", note, Json, ct);           // every tab whose who cookie is ada
    }

    // Who is connected, and to which groups: snapshots, taken as you read them. (Show this to administrators only.)
    [HttpGet("/features/realtime")]
    public static IResult Connections(IRealtime realtime) => Results.Json(new
    {
        count = realtime.ConnectionCount,
        connections = realtime.Connections.Select(c => new { c.Id, c.User, c.ConnectedUtc, c.Groups }),
        groups = realtime.Groups,
    }, Json);

    // One question, one answer. Every call is billed to the provider's key, and the [AiAccess] hook guards only
    // /_host/ai/chat, so a function applies its own rule: here, the same one.
    [HttpPost("/features/ai/ask")]
    public static async Task<IResult> Ask(HttpRequest request, IAiChat ai, string? question, CancellationToken ct)
    {
        if (!ai.IsConfigured) return Results.Json(new { error = "This site has no AI. An administrator chooses a provider under Sites → the domain → AI." }, Json, statusCode: 503);
        if (FeatureHooks.Who(request) == "guest") return Results.Json(new { error = "Guests may not ask." }, Json, statusCode: 403);

        try
        {
            var answer = await ai.CompleteAsync(new AiChatRequest
            {
                Messages = [AiMessage.User(question ?? "Name one bright star.")],
                MaxTokens = 64, // a function sets its own limit; a page gets the server's
            }, ct);

            return Results.Json(new { answer.Text, answer.Model, answer.InputTokens, answer.OutputTokens }, Json);
        }
        catch (AiChatException ex)
        {
            return Results.Json(new { error = ex.Message, providerStatus = ex.StatusCode }, Json, statusCode: 502); // the provider failed
        }
    }

    // The answer as it is written, as server-sent events: data: {"text":…} for each piece, then data: {"done":true,…}.
    [HttpGet("/features/ai/stream")]
    public static async Task<IResult> Stream(HttpContext context, HttpResponse response, IAiChat ai, string? question)
    {
        if (!ai.IsConfigured) return Results.Json(new { error = "This site has no AI." }, Json, statusCode: 503);
        if (FeatureHooks.Who(context.Request) == "guest") return Results.Json(new { error = "Guests may not ask." }, Json, statusCode: 403);

        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";

        var chat = new AiChatRequest { Messages = [AiMessage.User(question ?? "Describe the Moon in ten words.")], MaxTokens = 64 };
        await foreach (var chunk in ai.StreamAsync(chat, context.RequestAborted))
        {
            object data = chunk.Final is { } final
                ? new { done = true, final.Model, final.InputTokens, final.OutputTokens }
                : new { text = chunk.Text };
            await response.WriteAsync($"data: {JsonSerializer.Serialize(data, Json)}\n\n", context.RequestAborted);
            await response.Body.FlushAsync(context.RequestAborted); // send each piece now, not when a buffer fills
        }

        return Results.Empty; // the body is written: returning nothing would try to set a 204 now, and log an error
    }

    // Any service the functions hold can be a parameter: IHttpClientFactory here, TimeProvider in a job below.
    [HttpGet("/features/fetch")]
    public static async Task<IResult> Fetch(IHttpClientFactory http, string? url, CancellationToken ct)
    {
        // https only: a function that fetches whatever it is given is an open proxy. (A real one would refuse private addresses too.)
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return Results.Json(new { error = "Give an https address: /features/fetch?url=https://example.com/" }, Json, statusCode: 400);

        try
        {
            using var answer = await http.CreateClient("fetch").GetAsync(uri, ct);
            var body = await answer.Content.ReadAsByteArrayAsync(ct);
            return Results.Json(new { url = uri, status = (int)answer.StatusCode, length = body.Length }, Json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Results.Json(new { url = uri, error = ex.Message }, Json, statusCode: 502); // refused, unreachable, too big or too slow
        }
    }
}

// ── background service and jobs ──────────────────────────────────────────────

public static class FeatureJobs
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Runs from when the functions load until they are replaced or removed. It must return when the token is cancelled.
    [BackgroundService]
    public static async Task Heartbeat(IRealtime realtime, ILogger log, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await realtime.PublishAsync("heartbeat", new { utc = DateTimeOffset.UtcNow, pages = realtime.ConnectionCount }, Json, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Log and carry on: an exception that escapes restarts the method, after a pause of up to a minute.
                log.LogWarning(ex, "The heartbeat was not sent");
            }

            // Throws once the token is cancelled, which ends the method: the server counts that as stopped, not failed.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    // Five-field cron, in UTC: 03:00 every day. A job may also have a route, so POST /features/jobs/nightly runs it now.
    [Schedule("0 3 * * *")]
    [HttpPost("/features/jobs/nightly")]
    public static Task Nightly(ISite site, CancellationToken ct) =>
        File.WriteAllTextAsync(Path.Combine(site.Data.FullName, "nightly.txt"), DateTimeOffset.UtcNow.ToString("O"), ct);

    // Every ten minutes, and once as soon as the functions load. Runs never overlap, and missed runs are not made up.
    [Every("10m", RunOnStart = true)]
    public static Task Stamp(ISite site, TimeProvider clock, CancellationToken ct) =>
        File.WriteAllTextAsync(Path.Combine(site.Data.FullName, "stamp.txt"), clock.GetUtcNow().ToString("O"), ct);
}

// ── hooks ────────────────────────────────────────────────────────────────────

// Who may use realtime and AI from a browser. Each hook is optional; without it, everything is allowed.
public static class FeatureHooks
{
    // Who the visitor is: a cookie anyone can set, standing in for your own sign-in code.
    public static string Who(HttpRequest request) =>
        request.Cookies["who"] is { Length: > 0 and <= 64 } who ? who : "guest";

    // As each page connects to /_host/realtime. A string is its user, which PublishToUserAsync reaches; null refuses it.
    [RealtimeConnect]
    public static string? Connect(HttpRequest request) => Who(request);

    // Each time a page asks to join a group: public:* for everyone, anything else for visitors with a name.
    [RealtimeJoin]
    public static bool Join(HttpRequest request, string group) =>
        group.StartsWith("public:", StringComparison.Ordinal) || Who(request) != "guest";

    // Each time a browser chats at /_host/ai/chat. With this hook, the site's "let every visitor chat" setting is not used.
    [AiAccess]
    public static bool Chat(HttpRequest request) => Who(request) != "guest";
}
