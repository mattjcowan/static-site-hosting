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
using StaticSiteHost.Models;
using StaticSiteHost.Security;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;

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

builder.Services.AddSingleton<DataPaths>();
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<ApiKeyStore>();
builder.Services.AddSingleton<SiteStore>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<SitePathResolver>();
builder.Services.AddSingleton<SiteContentServer>();
builder.Services.AddSingleton<SitePasscodeGate>();
builder.Services.AddSingleton<SiteRuleService>();
builder.Services.AddSingleton<ZipDeploymentService>();
builder.Services.AddSingleton<FunctionSourceReader>();
builder.Services.AddSingleton<FunctionProjectGenerator>();
builder.Services.AddSingleton<FunctionBuildService>();
builder.Services.AddSingleton<FunctionHost>();
builder.Services.AddSingleton<FunctionBundleBuilder>();
builder.Services.AddSingleton<FunctionTestRunner>();
builder.Services.AddSingleton<FunctionDeploymentService>();
builder.Services.AddSingleton<BootstrapAdministrator>();
builder.Services.AddSingleton<LoginThrottle>();

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
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

// ---------------------------------------------------------------- helpers

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
