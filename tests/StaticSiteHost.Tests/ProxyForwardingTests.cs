using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StaticSiteHost.Configuration;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;

namespace StaticSiteHost.Tests;

/// <summary>
/// Backends on loopback, and a front server that runs <see cref="SiteProxy"/> the way
/// <see cref="SiteHostingMiddleware"/> does: a request no route takes answers 299 "files" here,
/// standing in for the site's files. Loopback is allowed on the front, since that is where the
/// backends are; one test starts a second front with the default blocked networks.
/// </summary>
public sealed class ProxyServers : IAsyncLifetime
{
    public const string Domain = "app.test";
    public const int FilesStatus = 299;

    private readonly List<WebApplication> _apps = [];

    public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "ssh-proxy-tests-" + Guid.NewGuid().ToString("n"));
    public string BackendUrl { get; private set; } = "";
    public string Http2BackendUrl { get; private set; } = "";
    public string FrontUrl { get; private set; } = "";

    /// <summary>The same front, on a port that speaks HTTP/2 only (h2c), as SiteHosting:H2cPort does.</summary>
    public string Http2FrontUrl { get; private set; } = "";
    public SiteRecord Site { get; } = new() { Domain = Domain };
    public IServiceProvider FrontServices { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        BackendUrl = await StartAsync(Backend());
        Http2BackendUrl = await StartAsync(Backend(http2Only: true));

        var front = Front(DataRoot, blockedNetworks: "169.254.0.0/16", () => Site);
        FrontUrl = await StartAsync(front);
        FrontServices = front.Services;
        Http2FrontUrl = await StartAsync(Front(DataRoot, blockedNetworks: "169.254.0.0/16", () => Site, http2Only: true));

        // A release with one file, for "Files first".
        var paths = front.Services.GetRequiredService<DataPaths>();
        var release = Directory.CreateDirectory(paths.ReleaseDir(Domain, "r1"));
        await File.WriteAllTextAsync(Path.Combine(release.FullName, "hello.txt"), "a file");
        Site.Releases.Add(new ReleaseRecord { Id = "r1" });
        Site.CurrentRelease = "r1";
        await front.Services.GetRequiredService<SiteStore>().SaveAsync(Site);
    }

    public async Task<string> StartAsync(WebApplication app)
    {
        _apps.Add(app);
        await app.StartAsync();
        return app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public static WebApplication Front(string dataRoot, string blockedNetworks, Func<SiteRecord> site, bool http2Only = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (http2Only)
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        else
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.Configure<SiteHostingOptions>(options =>
        {
            options.DataRoot = dataRoot;
            options.ProxyBlockedNetworks = blockedNetworks;
            options.ProxyConnectTimeoutSeconds = 2;
        });
        builder.Services.AddSiteHostStrings();
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<BrandingStore>();
        builder.Services.AddSingleton<GoogleFonts>();
        builder.Services.AddSingleton<BrandingService>();
        builder.Services.AddSingleton<DataPaths>();
        builder.Services.AddSingleton<SitePathResolver>();
        builder.Services.AddSingleton<SiteStore>();
        builder.Services.AddSingleton<SiteContentServer>();
        builder.Services.AddSingleton<AuditLog>();
        builder.Services.AddHttpForwarder();
        builder.Services.AddSingleton<ProxyInvokers>();
        builder.Services.AddSingleton<SiteProxy>();
        builder.Services.AddSingleton<SiteProxyService>();

        var app = builder.Build();
        app.Run(async context =>
        {
            if (await context.RequestServices.GetRequiredService<SiteProxy>().TryForwardAsync(context, site())) return;
            context.Response.StatusCode = FilesStatus;
            await context.Response.WriteAsync("files");
        });
        return app;
    }

    private static WebApplication Backend(bool http2Only = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
            listen.Protocols = http2Only ? HttpProtocols.Http2 : HttpProtocols.Http1AndHttp2));

        var app = builder.Build();
        app.UseWebSockets();

        app.MapGet("/set-cookie", (HttpContext context) =>
        {
            context.Response.Headers.Append("Set-Cookie", "a=1; Path=/; Domain=backend.internal");
            context.Response.Headers.Append("Set-Cookie", "ssh.auth=forged; Path=/");
            return "cookies";
        });

        app.MapGet("/redirect", (HttpContext context) =>
            Results.Redirect($"{context.Request.Scheme}://{context.Request.Host}/next?x=1"));

        app.MapGet("/redirect-away", () => Results.Redirect("https://accounts.example.com/sign-in"));

        app.MapGet("/slow", async (CancellationToken ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return "late";
        });

        app.MapGet("/events", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: one\n\n");
            await context.Response.Body.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(3));
            await context.Response.WriteAsync("data: two\n\n");
        });

        app.Map("/ws", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[1024];
            var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
            await socket.SendAsync(buffer.AsMemory(0, received.Count), WebSocketMessageType.Text, true, CancellationToken.None);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        });

        // gRPC, as far as the proxy can tell: the request's frames come back as they were sent, with
        // what arrived in a header and the status in the trailers.
        app.MapPost("/grpc.Echo/Say", async (HttpContext context) =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            context.Response.ContentType = "application/grpc+proto";
            context.Response.Headers["x-seen"] = $"{context.Request.Protocol} {context.Request.ContentType} te={context.Request.Headers.TE}";
            await context.Response.Body.WriteAsync(body.ToArray());
            if (!context.Response.SupportsTrailers()) return;
            context.Response.AppendTrailer("grpc-status", "0");
            context.Response.AppendTrailer("x-trailer", "t1");
        });

        // A gRPC error after the headers: no body at all, then the status in the trailers.
        app.MapPost("/grpc.Echo/Fail", async (HttpContext context) =>
        {
            await context.Request.Body.CopyToAsync(Stream.Null);
            context.Response.ContentType = "application/grpc";
            await context.Response.StartAsync();
            if (!context.Response.SupportsTrailers()) return;
            context.Response.AppendTrailer("grpc-status", "5");
            context.Response.AppendTrailer("grpc-message", "no such thing");
        });

        // Everything else: what arrived, as JSON.
        app.Map("/{**rest}", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            return Results.Json(new Echo(
                context.Request.Method,
                context.Request.Path.Value ?? "",
                context.Request.QueryString.Value ?? "",
                context.Request.Protocol,
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                body));
        });

        return app;
    }

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        try { Directory.Delete(DataRoot, recursive: true); } catch (IOException) { }
    }
}

public sealed record Echo(string Method, string Path, string Query, string Protocol, Dictionary<string, string> Headers, string Body)
{
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}

public class ProxyForwardingTests : IClassFixture<ProxyServers>
{
    private readonly ProxyServers _servers;
    private readonly HttpClient _client;

    public ProxyForwardingTests(ProxyServers servers)
    {
        _servers = servers;
        _servers.Site.Headers = [];
        _servers.Site.PasscodeHash = null;
        _client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = new Uri(servers.FrontUrl)
        };
    }

    private ProxyRoute Route(string path = "/api", string? target = null, Action<ProxyRoute>? configure = null)
    {
        var route = new ProxyRoute { Path = path, Target = target ?? _servers.BackendUrl };
        configure?.Invoke(route);
        return route;
    }

    /// <summary>Replaces the site's routes, as a save does: with a new list.</summary>
    private void Use(params ProxyRoute[] routes) => _servers.Site.Proxy = [.. routes];

    private async Task<Echo> EchoAsync(HttpRequestMessage request)
    {
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Echo>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private Task<Echo> EchoAsync(string path) => EchoAsync(new HttpRequestMessage(HttpMethod.Get, path));

    [Fact]
    public async Task Forwards_the_rest_of_the_path_and_the_query_to_the_target()
    {
        Use(Route());

        var echo = await EchoAsync("/api/users/1?q=2");

        Assert.Equal("/users/1", echo.Path);
        Assert.Equal("?q=2", echo.Query);
        Assert.Equal(new Uri(_servers.BackendUrl).Authority, echo.Header("Host"));
        Assert.Equal("/api", echo.Header("X-Forwarded-Prefix"));
        Assert.Equal(new Uri(_servers.FrontUrl).Authority, echo.Header("X-Forwarded-Host"));
        Assert.Equal("http", echo.Header("X-Forwarded-Proto"));
        Assert.Equal("1", echo.Header("X-Site-Host-Hops"));
    }

    [Fact]
    public async Task A_request_that_keeps_coming_back_through_proxy_routes_is_stopped()
    {
        Use(Route());

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/x");
        request.Headers.Add("X-Site-Host-Hops", "4");
        Assert.Equal("5", (await EchoAsync(request)).Header("X-Site-Host-Hops"));

        request = new HttpRequestMessage(HttpMethod.Get, "/api/x");
        request.Headers.Add("X-Site-Host-Hops", "5");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.LoopDetected, response.StatusCode);
    }

    [Fact]
    public async Task Keeps_the_path_when_told_to_and_appends_to_a_base_path()
    {
        Use(Route(configure: r => r.StripPath = false));
        Assert.Equal("/api/users", (await EchoAsync("/api/users")).Path);

        Use(Route(target: _servers.BackendUrl + "/base"));
        Assert.Equal("/base/users", (await EchoAsync("/api/users")).Path);
        Assert.Equal("/base", (await EchoAsync("/api")).Path);
    }

    [Fact]
    public async Task Matches_whole_segments_ignoring_case_and_the_longest_path_wins()
    {
        Use(
            Route("/", configure: r => r.RequestHeaders = [new ProxyHeader { Name = "X-Which", Value = "root" }]),
            Route("/api", configure: r => r.RequestHeaders = [new ProxyHeader { Name = "X-Which", Value = "api" }]));

        Assert.Equal("api", (await EchoAsync("/API/users")).Header("X-Which"));
        Assert.Equal("root", (await EchoAsync("/apis")).Header("X-Which"));
        Assert.Equal("root", (await EchoAsync("/")).Header("X-Which"));

        Use(Route());
        using var response = await _client.GetAsync("/apis");
        Assert.Equal(ProxyServers.FilesStatus, (int)response.StatusCode);
    }

    [Fact]
    public async Task Server_wide_routes_forward_for_the_sites_they_name_unless_the_site_overrides_or_turns_them_off()
    {
        var proxy = _servers.FrontServices.GetRequiredService<SiteProxyService>();
        var (ok, errors) = await proxy.SetServerRoutesAsync(
        [
            new ServerProxyRoute
            {
                Sites = "*.test", Path = "/shared", Target = _servers.BackendUrl,
                RequestHeaders = [new ProxyHeader { Name = "X-Which", Value = "server-wide" }]
            },
            new ServerProxyRoute { Sites = "other.example.com", Path = "/elsewhere", Target = _servers.BackendUrl }
        ], "test");
        Assert.True(ok, string.Join("; ", errors));

        try
        {
            Use();
            var echo = await EchoAsync("/shared/thing");
            Assert.Equal("/thing", echo.Path);
            Assert.Equal("server-wide", echo.Header("X-Which"));

            // Not for this site: the files answer.
            using (var response = await _client.GetAsync("/elsewhere"))
                Assert.Equal(ProxyServers.FilesStatus, (int)response.StatusCode);

            // The site's own route at the same path wins.
            Use(Route("/shared", configure: r => r.RequestHeaders = [new ProxyHeader { Name = "X-Which", Value = "own" }]));
            Assert.Equal("own", (await EchoAsync("/shared/thing")).Header("X-Which"));

            // Turned off for the site: the files answer again.
            Use();
            _servers.Site.DisabledServerRoutes = ["/shared"];
            using (var response = await _client.GetAsync("/shared/thing"))
                Assert.Equal(ProxyServers.FilesStatus, (int)response.StatusCode);
        }
        finally
        {
            _servers.Site.DisabledServerRoutes = [];
            await proxy.SetServerRoutesAsync([], "test");
        }
    }

    [Fact]
    public async Task A_disabled_route_leaves_its_paths_to_the_files()
    {
        Use(Route(configure: r => r.Enabled = false));

        using var response = await _client.GetAsync("/api/users");
        Assert.Equal(ProxyServers.FilesStatus, (int)response.StatusCode);
    }

    [Fact]
    public async Task Files_first_forwards_only_what_the_release_does_not_have()
    {
        Use(Route("/", configure: r => r.FilesFirst = true));

        using var file = await _client.GetAsync("/hello.txt");
        Assert.Equal(ProxyServers.FilesStatus, (int)file.StatusCode);

        Assert.Equal("/missing.txt", (await EchoAsync("/missing.txt")).Path);
    }

    [Fact]
    public async Task The_servers_cookies_and_the_visitors_forwarded_headers_never_reach_the_backend()
    {
        Use(Route());

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/x");
        request.Headers.Add("Cookie", "ssh.auth=session; theirs=1; ssh.pass=unlocked");
        request.Headers.Add("X-Forwarded-For", "6.6.6.6");
        request.Headers.Add("X-Forwarded-Host", "evil.test");
        var echo = await EchoAsync(request);

        Assert.Equal("theirs=1", echo.Header("Cookie"));
        Assert.Equal("127.0.0.1", echo.Header("X-Forwarded-For"));
        Assert.Equal(new Uri(_servers.FrontUrl).Authority, echo.Header("X-Forwarded-Host"));

        Use(Route(configure: r => r.ForwardedHeaders = false));
        request = new HttpRequestMessage(HttpMethod.Get, "/api/x");
        request.Headers.Add("X-Forwarded-For", "6.6.6.6");
        echo = await EchoAsync(request);

        Assert.Null(echo.Header("X-Forwarded-For"));
        Assert.Null(echo.Header("X-Forwarded-Prefix"));
    }

    [Fact]
    public async Task Sends_the_visitors_host_and_the_routes_own_headers_when_asked()
    {
        Use(Route(configure: r =>
        {
            r.KeepHost = true;
            r.RequestHeaders = [new ProxyHeader { Name = "X-Api-Key", Value = "k" }, new ProxyHeader { Name = "User-Agent", Value = "" }];
        }));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/x");
        request.Headers.UserAgent.ParseAdd("visitor/1.0");
        var echo = await EchoAsync(request);

        Assert.Equal(new Uri(_servers.FrontUrl).Authority, echo.Header("Host"));
        Assert.Equal("k", echo.Header("X-Api-Key"));
        Assert.Null(echo.Header("User-Agent"));
    }

    [Fact]
    public async Task Forwards_request_bodies()
    {
        Use(Route());

        var echo = await EchoAsync(new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = new StringContent("{\"id\":7}", Encoding.UTF8, "application/json")
        });

        Assert.Equal("POST", echo.Method);
        Assert.Equal("{\"id\":7}", echo.Body);
        Assert.StartsWith("application/json", echo.Header("Content-Type"));
    }

    [Fact]
    public async Task A_body_over_the_routes_limit_is_refused()
    {
        Use(Route(configure: r => r.MaxRequestBodyBytes = 10));

        using var response = await _client.PostAsync("/api/orders", new StringContent(new string('x', 100)));
        Assert.InRange((int)response.StatusCode, 400, 499);
    }

    [Fact]
    public async Task Rewrites_cookies_and_redirects_from_the_backend()
    {
        Use(Route());

        using var cookies = await _client.GetAsync("/api/set-cookie");
        Assert.Equal(["a=1; Path=/"], cookies.Headers.GetValues("Set-Cookie"));

        using var redirect = await _client.GetAsync("/api/redirect");
        Assert.Equal("/api/next?x=1", redirect.Headers.Location!.OriginalString);

        using var away = await _client.GetAsync("/api/redirect-away");
        Assert.Equal("https://accounts.example.com/sign-in", away.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Applies_the_sites_header_rules_and_keeps_a_private_site_out_of_shared_caches()
    {
        Use(Route());
        _servers.Site.Headers = [new HeaderRule { For = "/api/**", Set = { ["X-Rule"] = "yes" } }];
        _servers.Site.PasscodeHash = "set";
        _servers.FrontServices.GetRequiredService<SiteContentServer>().Evict(ProxyServers.Domain);
        try
        {
            using var response = await _client.GetAsync("/api/x");

            Assert.Equal("yes", response.Headers.GetValues("X-Rule").Single());
            Assert.Contains("private", response.Headers.CacheControl!.ToString());
            Assert.Equal("noindex, nofollow", response.Headers.GetValues("X-Robots-Tag").Single());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        }
        finally
        {
            _servers.Site.Headers = [];
            _servers.Site.PasscodeHash = null;
            _servers.FrontServices.GetRequiredService<SiteContentServer>().Evict(ProxyServers.Domain);
        }
    }

    [Fact]
    public async Task A_backend_that_goes_quiet_past_the_timeout_answers_504()
    {
        Use(Route(configure: r => r.TimeoutSeconds = 1));

        using var response = await _client.GetAsync("/api/slow");
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task A_backend_that_cannot_be_reached_answers_502_without_naming_it()
    {
        Use(Route(target: $"http://127.0.0.1:{UnusedPort()}"));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/x");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("could not be reached", body);
        Assert.DoesNotContain("127.0.0.1", body);
    }

    [Fact]
    public async Task A_target_naming_an_unset_environment_variable_answers_502_and_never_falls_through_to_files()
    {
        const string name = "SSH_TEST_PROXY_BACKEND";
        Use(Route("/", "${env:" + name + "}"));

        using (var response = await _client.GetAsync("/hello.txt"))
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        Environment.SetEnvironmentVariable(name, _servers.BackendUrl);
        try
        {
            Use(Route("/", "${env:" + name + "}"));
            Assert.Equal("/x", (await EchoAsync("/x")).Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task Streams_events_as_they_come_with_streaming_on()
    {
        Use(Route(configure: r => r.Streaming = true));

        using var response = await _client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").Single());

        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[64];
        using var soon = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // The backend waits three seconds before the second event, so the first has to arrive on its own.
        var read = await stream.ReadAsync(buffer, soon.Token);
        Assert.Equal("data: one\n\n", Encoding.UTF8.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task Speaks_http2_without_tls_to_a_backend_that_only_does_http2()
    {
        Use(Route(target: _servers.Http2BackendUrl, configure: r => r.HttpVersion = ProxyHttpVersions.Http2));

        Assert.Equal("HTTP/2", (await EchoAsync("/api/x")).Protocol);
    }

    /// <summary>One gRPC message frame: not compressed, the length, then the message.</summary>
    private static byte[] GrpcFrame(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var frame = new byte[5 + bytes.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), bytes.Length);
        bytes.CopyTo(frame, 5);
        return frame;
    }

    private static HttpRequestMessage GrpcWebCall(string path, string contentType, byte[] body) => new(HttpMethod.Post, path)
    {
        Content = new ByteArrayContent(body) { Headers = { { "Content-Type", contentType } } }
    };

    [Fact]
    public async Task Translates_grpc_web_to_grpc_over_http2_and_puts_the_trailers_in_the_body()
    {
        Use(Route(target: _servers.Http2BackendUrl, configure: r => r.GrpcWeb = true));
        var frame = GrpcFrame("hello");

        using var response = await _client.SendAsync(GrpcWebCall("/api/grpc.Echo/Say", "application/grpc-web+proto", frame));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new Version(1, 1), response.Version);
        Assert.Equal("application/grpc-web+proto", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("HTTP/2 application/grpc+proto te=trailers", response.Headers.GetValues("x-seen").Single());
        Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").Single());

        Assert.Equal(frame, body[..frame.Length]);
        var trailers = body[frame.Length..];
        Assert.Equal(GrpcWeb.TrailersFlag, trailers[0]);
        Assert.Equal(trailers.Length - 5, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(trailers.AsSpan(1)));
        Assert.Equal("grpc-status:0\r\nx-trailer:t1\r\n", Encoding.ASCII.GetString(trailers, 5, trailers.Length - 5));
    }

    [Fact]
    public async Task Translates_grpc_web_text_both_ways_in_base64()
    {
        Use(Route(target: _servers.Http2BackendUrl, configure: r => r.GrpcWeb = true));
        var frame = GrpcFrame("hello");

        // Sent in two pieces, each with its own padding, as the protocol allows.
        var text = Convert.ToBase64String(frame[..4]) + "\r\n" + Convert.ToBase64String(frame[4..]);
        using var response = await _client.SendAsync(GrpcWebCall("/api/grpc.Echo/Say", "application/grpc-web-text", Encoding.ASCII.GetBytes(text)));
        var answer = await response.Content.ReadAsStringAsync();

        // The target's +proto comes back with it.
        Assert.Equal("application/grpc-web-text+proto", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("HTTP/2 application/grpc te=trailers", response.Headers.GetValues("x-seen").Single());

        // Each write is padded on its own; decode it piece by piece, as a browser does.
        var decoded = new List<byte>();
        foreach (var piece in System.Text.RegularExpressions.Regex.Matches(answer, "[^=]+=*").Select(m => m.Value))
            decoded.AddRange(Convert.FromBase64String(piece));
        Assert.Equal(frame, decoded.Take(frame.Length));
        Assert.Contains("grpc-status:0", Encoding.ASCII.GetString(decoded.Skip(frame.Length + 5).ToArray()));
    }

    [Fact]
    public async Task A_grpc_web_call_is_forwarded_untouched_when_the_route_does_not_translate_and_so_is_an_answer_that_is_not_grpc()
    {
        Use(Route(configure: r => r.GrpcWeb = false));
        var untouched = await EchoAsync(GrpcWebCall("/api/other", "application/grpc-web", GrpcFrame("x")));
        Assert.Equal("application/grpc-web", untouched.Header("Content-Type"));
        Assert.Null(untouched.Header("TE"));

        // Translated on the way in, but the target answered with JSON: it comes back as it was.
        Use(Route(target: _servers.Http2BackendUrl, configure: r => r.GrpcWeb = true));
        using var response = await _client.SendAsync(GrpcWebCall("/api/other", "application/grpc-web", GrpcFrame("x")));
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var echo = (await response.Content.ReadFromJsonAsync<Echo>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        Assert.Equal("application/grpc", echo.Header("Content-Type"));
    }

    [Fact]
    public async Task Grpc_goes_over_http2_on_an_auto_route_and_an_error_after_the_headers_keeps_its_trailers()
    {
        Use(Route(target: _servers.Http2BackendUrl));
        using var client = new HttpClient { BaseAddress = new Uri(_servers.Http2FrontUrl) };
        HttpRequestMessage Call(string path)
        {
            var call = GrpcWebCall(path, "application/grpc", GrpcFrame("hi"));
            (call.Version, call.VersionPolicy) = (HttpVersion.Version20, HttpVersionPolicy.RequestVersionExact);
            // As every gRPC client sends it, and the backend may insist on it.
            call.Headers.TE.ParseAdd("trailers");
            return call;
        }

        using var say = await client.SendAsync(Call("/api/grpc.Echo/Say"));
        await say.Content.ReadAsByteArrayAsync();
        Assert.Equal("HTTP/2 application/grpc te=trailers", say.Headers.GetValues("x-seen").Single());
        Assert.Equal("0", say.TrailingHeaders.GetValues("grpc-status").Single());

        // No Content-Length: 0 ahead of the trailers, which nginx's grpc_pass takes for the end.
        using var fail = await client.SendAsync(Call("/api/grpc.Echo/Fail"));
        // As received: once the body is read, HttpClient works a length out by itself.
        Assert.False(fail.Content.Headers.NonValidated.Contains("Content-Length"));
        Assert.Empty(await fail.Content.ReadAsByteArrayAsync());
        Assert.Equal("5", fail.TrailingHeaders.GetValues("grpc-status").Single());
        Assert.Equal("no such thing", fail.TrailingHeaders.GetValues("grpc-message").Single());
    }

    private Uri WebSocketUri => new(_servers.FrontUrl.Replace("http://", "ws://") + "/api/ws");

    private async Task<(HttpStatusCode? Status, string? Echo)> TryWebSocketAsync(string? origin = null)
    {
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);

        try
        {
            await socket.ConnectAsync(WebSocketUri, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            return (socket.HttpStatusCode, null);
        }

        await socket.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[64];
        var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
        return (socket.HttpStatusCode, Encoding.UTF8.GetString(buffer, 0, received.Count));
    }

    [Fact]
    public async Task WebSockets_are_refused_until_turned_on_and_then_only_from_the_sites_own_pages()
    {
        Use(Route());
        Assert.Equal(HttpStatusCode.Forbidden, (await TryWebSocketAsync()).Status);

        Use(Route(configure: r => r.WebSockets = true));
        Assert.Equal("hello", (await TryWebSocketAsync()).Echo);
        Assert.Equal("hello", (await TryWebSocketAsync(_servers.FrontUrl)).Echo);
        Assert.Equal(HttpStatusCode.Forbidden, (await TryWebSocketAsync("https://evil.test")).Status);

        Use(Route(configure: r => (r.WebSockets, r.CrossSiteWebSockets) = (true, true)));
        Assert.Equal("hello", (await TryWebSocketAsync("https://evil.test")).Echo);
    }

    [Fact]
    public async Task The_test_reports_what_the_target_answered()
    {
        var proxy = _servers.FrontServices.GetRequiredService<SiteProxyService>();

        var answered = await proxy.TestAsync(Route(), CancellationToken.None);
        Assert.Equal(200, answered.Status);
        Assert.Contains("answered 200", answered.Describe());

        var refused = await proxy.TestAsync(Route(target: $"http://127.0.0.1:{UnusedPort()}"), CancellationToken.None);
        Assert.False(refused.Answered);
        Assert.NotNull(refused.Error);
    }

    [Fact]
    public async Task With_the_default_settings_other_ports_on_this_machine_are_allowed_and_the_servers_own_are_not()
    {
        var site = new SiteRecord { Domain = "self.test" };
        var front = ProxyServers.Front(
            Path.Combine(_servers.DataRoot, "self"), new SiteHostingOptions().ProxyBlockedNetworks, () => site);
        var url = await _servers.StartAsync(front);
        var port = new Uri(url).Port;
        using var client = new HttpClient { BaseAddress = new Uri(url) };

        // A backend beside the server, on loopback.
        site.Proxy = [Route()];
        using (var beside = await client.GetAsync("/api/x"))
            Assert.Equal(HttpStatusCode.OK, beside.StatusCode);

        // The server itself, by every address that reaches it.
        foreach (var self in new[] { $"http://127.0.0.1:{port}", $"http://localhost:{port}", $"http://0.0.0.0:{port}" })
        {
            site.Proxy = [Route(target: self)];
            using var response = await client.GetAsync("/api/x");
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }

        var test = await front.Services.GetRequiredService<SiteProxyService>()
            .TestAsync(Route(target: $"http://127.0.0.1:{port}"), CancellationToken.None);
        Assert.Contains("is this server itself", test.Error);
    }

    [Fact]
    public async Task Blocked_networks_are_refused_when_the_connection_is_made()
    {
        var site = new SiteRecord { Domain = "blocked.test", Proxy = [Route()] };
        var front = ProxyServers.Front(Path.Combine(_servers.DataRoot, "blocked"), "127.0.0.0/8", () => site);
        var url = await _servers.StartAsync(front);

        using var client = new HttpClient { BaseAddress = new Uri(url) };
        using var response = await client.GetAsync("/api/x");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var test = await front.Services.GetRequiredService<SiteProxyService>().TestAsync(site.Proxy[0], CancellationToken.None);
        Assert.Contains("ProxyBlockedNetworks", test.Error);
    }

    private static int UnusedPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
