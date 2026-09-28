#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk.Web
#:package StaticSiteHost.Abstractions@*

// Global functions: upload this file under Functions in the top bar (administrators only), and it runs
// for every site on the server. Global middleware runs before each site's own middleware; global handlers
// answer after each site's own handlers, so a site can still have a /whoami of its own. README.md in this
// folder says what to expect. Global.linq is the same code for LINQPad; upload one of the two, not both.
//
// Global middleware stands in front of every site, so if these functions ever fail to load, every site
// answers 503 until they are fixed. Check them in the editor before you upload a change.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using StaticSiteHost.Functions;
using StaticSiteHost.Functions.Testing;

#if LINQPAD
async Task Main()
{
    // A request to a site, with a response that runs its OnStarting callbacks when told to, as the server does.
    var site = new FakeSite { Domain = "blog.localhost" };
    var response = new StartableResponse();
    var context = new DefaultHttpContext();
    context.Features.Set<IHttpResponseFeature>(response);
    context.UseSite(site);
    context.Request.Method = "GET";
    context.Request.Host = new HostString("blog.localhost");
    context.Request.Path = "/about.html";

    using (var loggers = LoggerFactory.Create(logging => logging.AddSimpleConsole(options => options.SingleLine = true)))
    {
        await GlobalFunctions.SafeDefaults(context, loggers.CreateLogger("functions:global"), () =>
        {
            context.Response.Headers["Referrer-Policy"] = "no-referrer"; // as the site's own _headers file might
            return Task.CompletedTask;
        });
    }

    await response.StartAsync();
    context.Response.Headers.Dump("headers"); // the site's Referrer-Policy stays; the other two are added

    GlobalFunctions.WhoAmI(site, site.Data).Dump("GET /whoami");

    await GlobalFunctions.Heartbeat(site, CancellationToken.None);
    File.ReadAllText(Path.Combine(site.Data.FullName, "heartbeat.txt")).Dump("heartbeat.txt");
}

/// <summary>Keeps OnStarting callbacks and runs them on StartAsync, as the server does just before it sends the headers.</summary>
sealed class StartableResponse : HttpResponseFeature
{
    private readonly List<(Func<object, Task> Callback, object State)> _starting = [];

    public override void OnStarting(Func<object, Task> callback, object state) => _starting.Add((callback, state));

    public async Task StartAsync()
    {
        foreach (var (callback, state) in _starting) await callback(state);
    }
}
#endif

public static class GlobalFunctions
{
    // Serialise with options owned by this file, so their cache goes with it when the file is replaced.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Safe defaults for every site. A site that sets its own, in a handler or a _headers rule, keeps its value.
    private static readonly (string Name, string Value)[] Defaults =
    [
        ("X-Content-Type-Options", "nosniff"),
        ("Referrer-Policy", "strict-origin-when-cross-origin"),
        ("Permissions-Policy", "camera=(), microphone=(), geolocation=()"),
    ];

    /// <summary>Adds the default headers to every response of every site, and logs one line per request.</summary>
    [Middleware]
    public static async Task SafeDefaults(HttpContext context, ILogger log, Func<Task> next)
    {
        var started = Stopwatch.GetTimestamp();

        // A response's headers are settled only just before they are sent, after the site's handlers, files and header
        // rules have had their say. So look then, and add only what is missing.
        context.Response.OnStarting(() =>
        {
            foreach (var (name, value) in Defaults)
            {
                if (!context.Response.Headers.ContainsKey(name)) context.Response.Headers[name] = value;
            }

            return Task.CompletedTask;
        });

        await next();

        // The category is functions:global. Code after next() runs once the site has answered.
        log.LogInformation("{Method} {Host}{Path} {Status} {Elapsed:0} ms", context.Request.Method, context.Request.Host.Host,
            context.Request.Path, context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>
    /// GET /whoami on any site: ISite is the site the request is for, and the data folder is the one every site's
    /// global functions share. It shows a path on the server, which is fine for checking a setup; remove it after.
    /// </summary>
    [HttpGet("/whoami")]
    public static IResult WhoAmI(ISite site, DirectoryInfo data) =>
        Results.Json(new { domain = site.Domain, globalData = data.FullName }, Json);

    /// <summary>
    /// Every quarter of an hour, in UTC. Outside a request the global functions belong to no site: ISite.Domain is
    /// "global" and ISite.Data is the shared folder. (ISite.Realtime and ISite.Ai throw here; a job that needs them
    /// belongs in a site's own functions.)
    /// </summary>
    [Schedule("*/15 * * * *")]
    public static Task Heartbeat(ISite site, CancellationToken ct) =>
        File.WriteAllTextAsync(Path.Combine(site.Data.FullName, "heartbeat.txt"), $"{site.Domain} {DateTimeOffset.UtcNow:O}", ct);
}
