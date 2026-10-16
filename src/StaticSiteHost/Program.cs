using System.IO.Compression;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using StaticSiteHost.Configuration;
using StaticSiteHost.Endpoints;
using StaticSiteHost.Hubs;
using StaticSiteHost.Localization;
using StaticSiteHost.Models;
using StaticSiteHost.Security;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Ai;
using StaticSiteHost.Services.Realtime;
using StaticSiteHost.Services.Dns;
using StaticSiteHost.Tls;
using StaticSiteHost.Tools;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SiteHostingOptions>(builder.Configuration.GetSection(SiteHostingOptions.SectionName));
builder.Services.Configure<BootstrapOptions>(builder.Configuration.GetSection(BootstrapOptions.SectionName));

var hostingOptions = builder.Configuration.GetSection(SiteHostingOptions.SectionName).Get<SiteHostingOptions>()
                     ?? new SiteHostingOptions();
var dataRoot = Path.GetFullPath(hostingOptions.DataRoot);

// ---------------------------------------------------------------- limits & proxying

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = hostingOptions.MaxUploadBytes;
    options.AddServerHeader = false;
});

// HTTPS in the server (SiteHosting:Tls): HTTP for Let's Encrypt's checks and redirects, HTTPS with
// a certificate per host, chosen as each connection names its host. ALPN offers HTTP/2, so gRPC
// works end to end. These replace ASPNETCORE_URLS.
if (hostingOptions.Tls.Enabled)
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(hostingOptions.Tls.HttpPort);
        options.ListenAnyIP(hostingOptions.Tls.HttpsPort, listen =>
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            listen.UseHttps(new TlsHandshakeCallbackOptions
            {
                OnConnection = context => ValueTask.FromResult(new SslServerAuthenticationOptions
                {
                    ServerCertificateContext = listen.ApplicationServices.GetRequiredService<CertificateManager>()
                        .Select(context.ClientHelloInfo.ServerName, (context.Connection.LocalEndPoint as System.Net.IPEndPoint)?.Address),
                    ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11]
                })
            });
        });
    });
}

// h2c (SiteHosting:H2cPort): a port of its own, since Kestrel offers HTTP/2 on a plain port only to
// clients that start with it, and so only on a port that offers nothing else. Added to the
// addresses the server already listens on (ASPNETCORE_URLS, or HTTPS in the server's two ports),
// which binding a port in code would otherwise replace.
if (hostingOptions.H2cPort > 0)
{
    var h2cPort = hostingOptions.H2cPort;

    // Endpoints in Kestrel's own configuration replace every address, this one included.
    if (builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
        throw new InvalidOperationException(
            "SiteHosting:H2cPort cannot be used with Kestrel:Endpoints, which replaces every other address. Add an endpoint with Protocols Http2 there instead.");

    if (hostingOptions.Tls.Enabled)
    {
        if (h2cPort == hostingOptions.Tls.HttpPort || h2cPort == hostingOptions.Tls.HttpsPort)
            throw new InvalidOperationException(
                $"SiteHosting:H2cPort ({h2cPort}) must be a port of its own, not SiteHosting:Tls:HttpPort or HttpsPort.");

        builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(h2cPort, listen => listen.Protocols = HttpProtocols.Http2));
    }
    else
    {
        // What Kestrel would listen on otherwise: the URLs, else the HTTP and HTTPS ports, else its default.
        static IEnumerable<string> Ports(string? list, string scheme) =>
            (list ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(port => $"{scheme}://*:{port}");

        var urls = builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey) is { Length: > 0 } configured
            ? configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Ports(builder.WebHost.GetSetting(WebHostDefaults.HttpPortsKey), "http")
                .Concat(Ports(builder.WebHost.GetSetting(WebHostDefaults.HttpsPortsKey), "https"))
                .DefaultIfEmpty("http://localhost:5000")
                .ToArray();

        if (urls.Any(url => url.TrimEnd('/').EndsWith($":{h2cPort}", StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"SiteHosting:H2cPort ({h2cPort}) must be a port of its own; the server already listens on it ({string.Join(";", urls)}).");

        builder.WebHost.UseUrls([.. urls, $"http://*:{h2cPort}"]);
        builder.WebHost.ConfigureKestrel(options => options.ConfigureEndpointDefaults(listen =>
        {
            if (listen.IPEndPoint?.Port == h2cPort) listen.Protocols = HttpProtocols.Http2;
        }));
    }
}

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = hostingOptions.MaxUploadBytes;
    options.ValueLengthLimit = int.MaxValue;
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    // The proxy address is not known inside a container network.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// ---------------------------------------------------------------- services

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(Directory.CreateDirectory(Path.Combine(dataRoot, "config", "keys")))
    .SetApplicationName("StaticSiteHost");

builder.Services.AddSiteHostStrings();
builder.Services.AddSingleton<DataPaths>();
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<ApiKeyStore>();
builder.Services.AddSingleton<SiteStore>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<SitePathResolver>();
builder.Services.AddSingleton<SiteContentServer>();
builder.Services.AddSingleton<SitePasscodeGate>();
builder.Services.AddSingleton<SiteRuleService>();
builder.Services.AddSingleton<SiteProxyService>();
builder.Services.AddSingleton<SiteVariableService>();
builder.Services.AddSingleton<SiteHostEndpoints>();
builder.Services.AddSingleton<ZipDeploymentService>();
builder.Services.AddSingleton<SiteFiles>();
builder.Services.AddSingleton<ReleaseEditor>();
builder.Services.AddSingleton<FunctionSourceReader>();
builder.Services.AddSingleton<FunctionProjectGenerator>();
builder.Services.AddSingleton<FunctionBuildService>();
builder.Services.AddSingleton<FunctionHost>();
builder.Services.AddSingleton<FunctionBundleBuilder>();
builder.Services.AddSingleton<FunctionTestRunner>();
builder.Services.AddSingleton<FunctionTestService>();
builder.Services.AddSingleton<FunctionDeploymentService>();
// Loads the bundles that run background services or jobs once the server is listening, and stops them on shutdown.
builder.Services.AddHostedService<FunctionWarmUp>();
builder.Services.AddSingleton<BootstrapAdministrator>();
builder.Services.AddSingleton<UserAdminService>();

// ---------------------------------------------------------------- branding

builder.Services.AddSingleton<BrandingStore>();
builder.Services.AddSingleton<GoogleFonts>();
builder.Services.AddSingleton<BrandingService>();
// Fonts from Google, only once an administrator allows them; only Google's two font hosts are asked.
builder.Services.AddHttpClient(GoogleFonts.HttpClientName, client =>
    {
        client.Timeout = TimeSpan.FromSeconds(60);
        client.MaxResponseContentBufferSize = BrandingStore.MaxFontFileBytes;
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
builder.Services.AddSingleton<LoginThrottle>();

// ---------------------------------------------------------------- proxy routes

// YARP's direct forwarder only: matching, settings and storage are SiteProxy's own.
builder.Services.AddHttpForwarder();
builder.Services.AddSingleton<ProxyInvokers>();
builder.Services.AddSingleton<SiteProxy>();

// ---------------------------------------------------------------- DNS (Cloudflare)

builder.Services.AddSingleton<ICloudflareApi, CloudflareApi>();
builder.Services.AddSingleton<DnsService>();
builder.Services.AddHostedService(services => services.GetRequiredService<DnsService>());
builder.Services.AddHttpClient(CloudflareApi.HttpClientName, client =>
{
    client.BaseAddress = new Uri(CloudflareApi.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("StaticSiteHost/" + SiteHostTools.ServerVersion);
});
// Checking a DNS-01 record through 1.1.1.1, and this server's public address.
builder.Services.AddHttpClient(DnsService.DohClientName, client => client.Timeout = TimeSpan.FromSeconds(15));

// ---------------------------------------------------------------- HTTPS

builder.Services.AddSingleton<AcmeChallenges>();
builder.Services.AddSingleton<CertificateStore>();
builder.Services.AddSingleton<CertificateManager>();
builder.Services.AddHostedService(services => services.GetRequiredService<CertificateManager>());
builder.Services.AddHttpClient(CertificateManager.HttpClientName, client =>
    {
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StaticSiteHost/" + SiteHostTools.ServerVersion);
    })
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        // A private ACME server (step-ca, Pebble) whose own HTTPS certificate comes from its own CA.
        // The file may hold the root and intermediates; the server's own chain is used as well, so a
        // certificate issued from an intermediate (as step-ca's is) checks out against the root.
        if (hostingOptions.Tls.AcmeCaFile is { Length: > 0 } caFile)
        {
            var authorities = new X509Certificate2Collection();
            authorities.ImportFromPemFile(caFile);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, presented, errors) =>
            {
                if (errors == SslPolicyErrors.None) return true;
                if (certificate is null || errors != SslPolicyErrors.RemoteCertificateChainErrors) return false;

                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(authorities);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                if (presented is not null)
                {
                    foreach (var element in presented.ChainElements) chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                }

                using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                return chain.Build(leaf);
            };
        }

        return handler;
    });

// ---------------------------------------------------------------- MCP

// /mcp on the management host: the tools in Tools/SiteHostTools.cs, behind the same API keys as
// /api/v1. Stateless, so each call is one authenticated request with no session to keep.
builder.Services.AddHttpContextAccessor();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "static-site-host", Title = "Static Site Host", Version = SiteHostTools.ServerVersion };
        options.ServerInstructions = SiteHostTools.Instructions;
    })
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools(SiteHostTools.All.Select(tool => McpServerTool.Create(tool.Function, new McpServerToolCreateOptions
    {
        ReadOnly = tool.Effect == ToolEffect.Read,
        Destructive = tool.Effect == ToolEffect.Destructive,
        OpenWorld = false
    })));

// ---------------------------------------------------------------- AI providers

// IHttpClientFactory, with one named client for every call to an AI provider.
builder.Services.AddHttpClient();
builder.Services.AddHttpClient(AiChatService.HttpClientName, client =>
    {
        // The whole of an answer that is not streamed. A streamed one is timed between its events
        // by AiChatService, since HttpClient's timeout stops once the headers arrive.
        client.Timeout = AiChatService.TimeoutFor(hostingOptions);
        client.MaxResponseContentBufferSize = 16 * 1024 * 1024;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StaticSiteHost");
    })
    // A redirect would turn the POST into a GET, or carry it somewhere the base URL does not say.
    // Refused instead, with a message that points at the base URL.
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });
builder.Services.AddSingleton<AiProviderStore>();
builder.Services.AddSingleton<AiChatService>();
builder.Services.AddSingleton<SiteAiChatFactory>();
builder.Services.AddSingleton<SiteAiSettingsService>();
builder.Services.AddSingleton<AiVisitorLimiter>();
builder.Services.AddSingleton<AiChatEndpoint>();
builder.Services.AddSingleton<AiModelCatalog>();

// The management assistant: the panel on every management page, using the tools MCP serves.
builder.Services.AddSingleton<StaticSiteHost.Services.Assistant.AssistantSettingsStore>();
builder.Services.AddSingleton<StaticSiteHost.Services.Assistant.AssistantConversationStore>();
builder.Services.AddSingleton<StaticSiteHost.Services.Assistant.AssistantUsageStore>(services =>
    new StaticSiteHost.Services.Assistant.AssistantUsageStore(services.GetRequiredService<DataPaths>()));
builder.Services.AddSingleton<StaticSiteHost.Services.Assistant.AssistantService>();

// ---------------------------------------------------------------- realtime

// SignalR is in the shared framework. One hub serves every site, at /_host/realtime on the site's
// own domain; SiteHostingMiddleware lets its requests through once the passcode gate has.
builder.Services.AddSignalR(options =>
{
    // A page sends nothing but Join and Leave, each with a name of 64 characters at most.
    options.MaximumReceiveMessageSize = 32 * 1024;
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});
builder.Services.AddSingleton<RealtimeRegistry>();
builder.Services.AddSingleton<RealtimeNegotiationLimiter>();
builder.Services.AddSingleton<SiteRealtimeFactory>();
builder.Services.AddSingleton<SiteDeployedNotifier>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "ssh.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/denied";
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = ValidatePrincipalAsync;

        // The UI's own requests from scripts (/ui/…) get a status, not the sign-in page: a script
        // that followed the redirect would take the page for what it asked for.
        options.Events.OnRedirectToLogin = context => RedirectOrStatus(context, StatusCodes.Status401Unauthorized);
        options.Events.OnRedirectToAccessDenied = context => RedirectOrStatus(context, StatusCodes.Status403Forbidden);
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, _ => { });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Administrator, policy => policy
        .RequireAuthenticatedUser()
        .RequireRole(Roles.Administrator))
    .AddPolicy(Policies.ApiUser, policy => policy
        .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser())
    .AddPolicy(Policies.ApiAdministrator, policy => policy
        .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
        .RequireRole(Roles.Administrator));

builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");

builder.Services.Configure<RouteOptions>(options =>
{
    // Razor Pages derives routes from file paths, so Pages/Sites/Details.cshtml would
    // generate /Sites/Details. Matching was always case-insensitive; this makes generated
    // links lowercase to match.
    //
    // LowercaseQueryStrings is deliberately left off: invitation tokens are case-sensitive
    // base64url, and lowercasing one would silently break every activation link.
    options.LowercaseUrls = true;
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Activate");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AllowAnonymousToPage("/Denied");
    options.Conventions.AuthorizeFolder("/Admin", Policies.Administrator);
});

// Before AddResponseCompression, which only adds its own provider when none is registered.
builder.Services.AddSingleton<IResponseCompressionProvider, SiteResponseCompressionProvider>();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes =
    [
        "text/plain", "text/css", "text/html", "text/javascript", "text/xml", "text/markdown",
        "application/javascript", "application/json", "application/xml", "application/manifest+json",
        "application/wasm", "image/svg+xml"
    ];
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

var app = builder.Build();

// ---------------------------------------------------------------- startup work

app.Services.GetRequiredService<SiteStore>().Load();
// Built now so a mistake in SiteHosting:ProxyBlockedNetworks stops the server here, not on a visitor's request.
app.Services.GetRequiredService<ProxyInvokers>();
await app.Services.GetRequiredService<AiProviderStore>().LoadAsync();
app.Services.GetRequiredService<ZipDeploymentService>().CleanupStaging();
await app.Services.GetRequiredService<BootstrapAdministrator>().EnsureAsync();

// ---------------------------------------------------------------- pipeline

if (hostingOptions.TrustForwardedHeaders)
{
    app.UseForwardedHeaders();
}

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/error");
}

app.UseResponseCompression();

// The language of every page from here on, the visitor screens on sites' domains included.
app.UseSiteHostLanguages();

// Let's Encrypt's checks, HTTP to HTTPS, HSTS: before any site or the management app.
app.UseMiddleware<TlsMiddleware>();

// The branding's stylesheet, logos and fonts, on every domain and ahead of any site's passcode.
app.UseMiddleware<BrandingMiddleware>();

// Everything above is shared; from here the Host header decides which app answers.
app.UseHostedSites();

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseForcedPasswordChange();

app.MapRazorPages();
app.MapApi();
app.MapHub<SiteHub>(SiteHub.Path);
app.MapAssistant();
app.MapModelSuggestions();
app.MapSiteFileEditor();
app.MapMcp("/mcp").RequireAuthorization(Policies.ApiUser).DisableAntiforgery();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

// ---------------------------------------------------------------- helpers

static Task RedirectOrStatus(RedirectContext<CookieAuthenticationOptions> context, int status)
{
    if (context.Request.Path.StartsWithSegments("/ui")) context.Response.StatusCode = status;
    else context.Response.Redirect(context.RedirectUri);
    return Task.CompletedTask;
}

/// <summary>
/// Re-checks the signed-in user on every request: a disabled account, a deleted account or a
/// password change (which rotates the security stamp) invalidates the cookie immediately.
/// Role and "must change password" claims are refreshed in place.
/// </summary>
static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
{
    var users = context.HttpContext.RequestServices.GetRequiredService<UserStore>();
    var principal = context.Principal;
    var user = await users.FindByIdAsync(principal?.FindFirstValue(ClaimTypes.NameIdentifier));

    if (principal is null || user is null || user.IsDisabled ||
        user.SecurityStamp != principal.FindFirstValue(UserStore.SecurityStampClaim))
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return;
    }

    var roleChanged = principal.FindFirstValue(ClaimTypes.Role) != user.Role;
    var mustChangeChanged =
        (principal.FindFirstValue(UserStore.MustChangePasswordClaim) == "true") != user.MustChangePassword;

    if (roleChanged || mustChangeChanged)
    {
        context.ReplacePrincipal(UserStore.CreatePrincipal(user));
        context.ShouldRenew = true;
    }
}

/// <summary>
/// A user whose password was assigned by an administrator cannot go anywhere else until
/// they have chosen their own.
/// </summary>
public static class ForcedPasswordChangeExtensions
{
    private static readonly string[] AlwaysAllowed =
        ["/account/password", "/logout", "/login", "/error", "/denied", "/healthz", "/css", "/js", "/favicon.ico"];

    public static IApplicationBuilder UseForcedPasswordChange(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var identity = context.User.Identity;
            var mustChange = identity is { IsAuthenticated: true, AuthenticationType: CookieAuthenticationDefaults.AuthenticationScheme }
                             && context.User.FindFirstValue(UserStore.MustChangePasswordClaim) == "true";

            if (mustChange && !AlwaysAllowed.Any(p => context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.Redirect("/account/password?required=true");
                return;
            }

            await next();
        });
}
