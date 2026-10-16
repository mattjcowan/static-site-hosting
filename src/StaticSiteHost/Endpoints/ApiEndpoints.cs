using System.Security.Claims;
using System.Text;
using System.Text.Json;
using StaticSiteHost.Models;
using StaticSiteHost.Security;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Ai;
using StaticSiteHost.Services.Realtime;

namespace StaticSiteHost.Endpoints;

public static class Policies
{
    /// <summary>Any active user presenting a valid API key.</summary>
    public const string ApiUser = "api-user";

    /// <summary>An API key owned by an administrator.</summary>
    public const string ApiAdministrator = "api-administrator";

    /// <summary>A signed-in administrator in the management UI.</summary>
    public const string Administrator = "administrator";
}

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1")
            .RequireAuthorization(Policies.ApiUser)
            .DisableAntiforgery();

        api.MapGet("/me", (ClaimsPrincipal principal) => Results.Ok(new
        {
            username = principal.Identity?.Name,
            displayName = principal.FindFirstValue("display_name"),
            role = principal.FindFirstValue(ClaimTypes.Role),
            apiKeyId = principal.FindFirstValue(ApiKeyAuthenticationHandler.ApiKeyIdClaim)
        }));

        api.MapGet("/sites", (SiteStore sites, SiteVariableService variables, SiteAiChatFactory ai, RealtimeRegistry realtime) =>
            Results.Ok(sites.List().Select(site => Describe(site, variables, ai, realtime))));

        api.MapGet("/sites/{domain}", (
            string domain, SiteStore sites, SiteVariableService variables, SiteAiChatFactory ai, RealtimeRegistry realtime) =>
        {
            var (normalized, error) = SiteStore.NormalizeDomain(domain);
            if (normalized is null) return Results.BadRequest(new { error });

            var site = sites.TryGet(normalized);
            return site is null
                ? Results.NotFound(new { error = $"No site is published at '{normalized}'." })
                : Results.Ok(Describe(site, variables, ai, realtime));
        });

        api.MapPost("/sites/{domain}", async (
            string domain,
            ClaimsPrincipal principal,
            ZipDeploymentService deployer,
            SiteStore sites,
            SiteVariableService variables,
            SiteAiChatFactory ai,
            RealtimeRegistry realtime) =>
        {
            var (ok, error, created, alreadyExists) = await deployer.CreateAsync(domain, Actor(principal));
            if (!ok) return alreadyExists ? Results.Conflict(new { error }) : Results.BadRequest(new { error });

            return Results.Created($"/api/v1/sites/{created}", Describe(sites.TryGet(created)!, variables, ai, realtime));
        });

        api.MapPost("/sites/{domain}/deploy", DeployAsync);

        // ---- a site's files, one at a time

        api.MapGet("/sites/{domain}/files", (string domain, string? release, SiteStore sites, SiteFiles files) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;
            if (SiteFiles.ReleaseOf(site, release) is not { } chosen)
                return Results.NotFound(new { error = release is null ? $"{site.Domain} has no release yet." : $"{site.Domain} has no release {release}." });

            return Results.Ok(new { domain = site.Domain, release = chosen.Id, files = files.List(site, chosen) });
        });

        api.MapGet("/sites/{domain}/files/{**path}", async (string domain, string path, string? release, SiteStore sites, SiteFiles files, CancellationToken ct) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;
            if (SiteFiles.ReleaseOf(site, release) is not { } chosen) return Results.NotFound(new { error = $"{site.Domain} has no such release." });

            return await files.ReadAsync(site, chosen, path, ct) is { } bytes
                ? Results.File(bytes, "application/octet-stream", Path.GetFileName(path))
                : Results.NotFound(new { error = $"Release {chosen.Id} of {site.Domain} has no file '{path}'." });
        });

        api.MapPatch("/sites/{domain}/files", async (
            string domain, SiteFilesRequest? body, ClaimsPrincipal principal, ZipDeploymentService deployer, HttpContext http, CancellationToken ct) =>
        {
            var writes = new List<ZipDeploymentService.FileChange>();
            foreach (var file in body?.Files ?? [])
            {
                try
                {
                    writes.Add(new(file.Path ?? "", file.Base64 == true ? Convert.FromBase64String(file.Content ?? "") : Encoding.UTF8.GetBytes(file.Content ?? "")));
                }
                catch (FormatException)
                {
                    return Results.BadRequest(new { error = $"{file.Path}: base64 is true but the content is not base64." });
                }
            }

            var result = await deployer.UpdateFilesAsync(domain, writes, body?.Delete ?? [], Actor(principal), "api", IsAdministrator(principal), ct);
            return result.Ok
                ? Results.Ok(new
                {
                    ok = true,
                    domain = result.Domain,
                    url = SiteLinks.For(http.Request, result.Domain!),
                    release = result.Release!.Id,
                    fileCount = result.Release.FileCount,
                    warnings = result.Warnings ?? []
                })
                : Results.BadRequest(new { error = result.Error });
        });

        api.MapGet("/sites/{domain}/releases/{releaseId}/download", async (string domain, string releaseId, SiteStore sites, SiteFiles files, CancellationToken ct) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;
            if (SiteFiles.ReleaseOf(site, releaseId) is not { } chosen)
                return Results.NotFound(new { error = $"{site.Domain} has no release {releaseId}." });

            return Results.File(await files.CreateZipAsync(site, chosen, ct), "application/zip", SiteFiles.ZipName(site, chosen));
        });

        api.MapPost("/sites/{domain}/rollback/{releaseId}", async (
            string domain,
            string releaseId,
            string? functions,
            ClaimsPrincipal principal,
            ZipDeploymentService deployer) =>
        {
            var (normalized, error) = SiteStore.NormalizeDomain(domain);
            if (normalized is null) return Results.BadRequest(new { error });

            // ?functions=keep runs the current functions on the older release; the default brings
            // back the functions that release had.
            var keep = string.Equals(functions, "keep", StringComparison.OrdinalIgnoreCase);
            var (ok, failure) = await deployer.RollbackAsync(normalized, releaseId, Actor(principal), keep);
            return ok
                ? Results.Ok(new { ok = true, domain = normalized, release = releaseId })
                : Results.BadRequest(new { error = failure });
        });

        api.MapPut("/sites/{domain}/passcode", async (
            string domain,
            PasscodeRequest? body,
            ClaimsPrincipal principal,
            SiteStore sites,
            SitePasscodeGate passcodes,
            RealtimeRegistry realtime) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            var (ok, error) = await passcodes.SetAsync(site, body?.Passcode, Actor(principal));
            if (!ok) return Results.BadRequest(new { error });

            // A new passcode shuts out everyone who entered the old one, and that includes the pages
            // they have open: nothing may go on hearing the site from before.
            realtime.DisconnectSite(site.Domain);
            return Results.Ok(new { ok = true, domain = site.Domain, passcodeProtected = true });
        });

        api.MapDelete("/sites/{domain}/passcode", async (
            string domain,
            ClaimsPrincipal principal,
            SiteStore sites,
            SitePasscodeGate passcodes) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            await passcodes.ClearAsync(site, Actor(principal));
            return Results.Ok(new { ok = true, domain = site.Domain, passcodeProtected = false });
        });

        api.MapGet("/sites/{domain}/headers", (string domain, SiteStore sites) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            return Results.Ok(new
            {
                domain = site.Domain,
                siteRules = site.Headers ?? [],
                releaseRules = site.Current?.Headers ?? []
            });
        });

        api.MapPut("/sites/{domain}/headers", async (
            string domain,
            HeaderRulesRequest? body,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteRuleService rules) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            var (ok, errors) = await rules.SetHeadersAsync(site, body?.Headers ?? [], Actor(principal));
            return ok
                ? Results.Ok(new { ok = true, domain = site.Domain, rules = site.Headers.Count })
                : Results.BadRequest(new { error = errors[0], errors });
        });

        api.MapDelete("/sites/{domain}/headers", async (
            string domain,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteRuleService rules) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            await rules.SetHeadersAsync(site, [], Actor(principal));
            return Results.Ok(new { ok = true, domain = site.Domain, rules = 0 });
        });

        api.MapGet("/sites/{domain}/redirects", (string domain, SiteStore sites) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            return Results.Ok(new
            {
                domain = site.Domain,
                siteRules = site.Redirects ?? [],
                releaseRules = site.Current?.Redirects ?? []
            });
        });

        api.MapPut("/sites/{domain}/redirects", async (
            string domain,
            RedirectRulesRequest? body,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteRuleService rules) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            var (ok, errors) = await rules.SetRedirectsAsync(site, body?.Redirects ?? [], Actor(principal));
            return ok
                ? Results.Ok(new { ok = true, domain = site.Domain, rules = site.Redirects.Count })
                : Results.BadRequest(new { error = errors[0], errors });
        });

        api.MapDelete("/sites/{domain}/redirects", async (
            string domain,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteRuleService rules) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            await rules.SetRedirectsAsync(site, [], Actor(principal));
            return Results.Ok(new { ok = true, domain = site.Domain, rules = 0 });
        });

        api.MapGet("/sites/{domain}/proxy", async (string domain, ClaimsPrincipal principal, SiteStore sites, SiteProxyService proxy) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            return Results.Ok(DescribeProxy(site, IsAdministrator(principal), await proxy.ServerRoutesAsync()));
        });

        api.MapPut("/sites/{domain}/proxy", async (
            string domain,
            ProxyRoutesRequest? body,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteProxyService proxy) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            var (ok, errors) = await proxy.SetRoutesAsync(site, body?.Routes ?? [], Actor(principal));
            return ok
                ? Results.Ok(DescribeProxy(site, administrator: true, await proxy.ServerRoutesAsync()))
                : Results.BadRequest(new { error = errors[0], errors });
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapDelete("/sites/{domain}/proxy", async (
            string domain,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteProxyService proxy) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            await proxy.SetRoutesAsync(site, [], Actor(principal));
            return Results.Ok(DescribeProxy(site, administrator: true, await proxy.ServerRoutesAsync()));
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapPut("/sites/{domain}/proxy/server-routes", async (
            string domain,
            ServerRouteForSiteRequest? body,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteProxyService proxy) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            var error = await proxy.SetServerRouteForSiteAsync(site, body?.Path, body?.Enabled ?? true, Actor(principal));
            return error is null
                ? Results.Ok(DescribeProxy(site, administrator: true, await proxy.ServerRoutesAsync()))
                : Results.BadRequest(new { error });
        }).RequireAuthorization(Policies.ApiAdministrator);

        // ---- server-wide proxy routes

        api.MapGet("/proxy", async (ClaimsPrincipal principal, SiteProxyService proxy) =>
            Results.Ok(new { routes = (await proxy.ServerRoutesAsync()).Select(r => Redacted(r, IsAdministrator(principal))) }));

        api.MapPut("/proxy", async (ServerProxyRoutesRequest? body, ClaimsPrincipal principal, SiteProxyService proxy) =>
        {
            var (ok, errors) = await proxy.SetServerRoutesAsync(body?.Routes ?? [], Actor(principal));
            return ok
                ? Results.Ok(new { routes = await proxy.ServerRoutesAsync() })
                : Results.BadRequest(new { error = errors[0], errors });
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapDelete("/proxy", async (ClaimsPrincipal principal, SiteProxyService proxy) =>
        {
            await proxy.SetServerRoutesAsync([], Actor(principal));
            return Results.Ok(new { routes = Array.Empty<ServerProxyRoute>() });
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapPost("/proxy/test", async (ServerProxyTestRequest? body, SiteProxyService proxy, CancellationToken ct) =>
        {
            var route = (await proxy.ServerRoutesAsync()).FirstOrDefault(r => r.Id == body?.Id);
            if (route is null) return Results.NotFound(new { error = "No server-wide proxy route has that id." });

            var result = await proxy.TestAsync(route, ct);
            return Results.Ok(new
            {
                id = route.Id,
                url = result.Url,
                answered = result.Answered,
                status = result.Status,
                version = result.Version,
                milliseconds = (int)result.Elapsed.TotalMilliseconds,
                error = result.Error
            });
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapPost("/sites/{domain}/proxy/test", async (
            string domain,
            ProxyTestRequest? body,
            SiteStore sites,
            SiteProxyService proxy,
            CancellationToken ct) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            var path = ProxyRouteRules.NormalizePath(body?.Path);
            var route = site.Proxy.FirstOrDefault(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (route is null) return Results.NotFound(new { error = $"{site.Domain} has no proxy route at {path}." });

            var result = await proxy.TestAsync(route, ct);
            return Results.Ok(new
            {
                path = route.Path,
                url = result.Url,
                answered = result.Answered,
                status = result.Status,
                version = result.Version,
                milliseconds = (int)result.Elapsed.TotalMilliseconds,
                error = result.Error
            });
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapGet("/sites/{domain}/variables", (string domain, SiteStore sites, SiteVariableService variables) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            return Results.Ok(DescribeVariables(site, variables, saved: false));
        });

        api.MapPut("/sites/{domain}/variables", async (
            string domain,
            VariablesRequest? body,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteVariableService variables) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            // A whole list says everything about each entry, so a flag left out is false.
            var replacement = (body?.Variables ?? [])
                .Select(item => new SiteVariable
                {
                    Name = item?.Name ?? "",
                    Value = item?.Value ?? "",
                    Public = item?.Public ?? false,
                    Secret = item?.Secret ?? false
                })
                .ToList();

            var (ok, errors) = await variables.ReplaceAsync(site, replacement, Actor(principal), IsAdministrator(principal));
            return ok
                ? Results.Ok(DescribeVariables(site, variables, saved: true))
                : Results.BadRequest(new { error = errors[0], errors });
        });

        api.MapDelete("/sites/{domain}/variables", async (
            string domain,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteVariableService variables) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            await variables.ReplaceAsync(site, [], Actor(principal), IsAdministrator(principal));
            return Results.Ok(DescribeVariables(site, variables, saved: true));
        });

        api.MapPut("/sites/{domain}/variables/{name}", async (
            string domain,
            string name,
            VariableRequest? body,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteVariableService variables) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            var (ok, error) = await variables.SetAsync(
                site, name, body?.Value, body?.Public, body?.Secret, Actor(principal), IsAdministrator(principal));
            return ok
                ? Results.Ok(DescribeVariables(site, variables, saved: true))
                : Results.BadRequest(new { error });
        });

        api.MapDelete("/sites/{domain}/variables/{name}", async (
            string domain,
            string name,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteVariableService variables) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            var (removed, error) = await variables.RemoveAsync(site, name, Actor(principal), IsAdministrator(principal));
            return removed ? Results.Ok(DescribeVariables(site, variables, saved: true))
                : error is not null ? Results.BadRequest(new { error })
                : Results.NotFound(new { error = $"{site.Domain} has no value of its own for '{name}'." });
        });

        // Realtime is for any API user, like deploying: publishing is how a CI job tells the pages
        // open on a site that a new version is up.

        api.MapGet("/sites/{domain}/realtime", (string domain, SiteStore sites, RealtimeRegistry realtime) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            return Results.Ok(new
            {
                domain = site.Domain,
                connections = realtime.ConnectionCount(site.Domain),
                groups = realtime.GroupSizes(site.Domain).Select(group => new { name = group.Name, members = group.Members })
            });
        });

        api.MapPost("/sites/{domain}/realtime/publish", PublishAsync);

        // AI settings decide whose key a site spends and whether strangers may spend it, so all of
        // this is for administrators, like the site page's AI card.

        api.MapGet("/sites/{domain}/ai", (string domain, SiteStore sites, SiteAiChatFactory ai) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            return Results.Ok(DescribeAi(site, ai, saved: false));
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapPut("/sites/{domain}/ai", async (
            string domain,
            AiSettingsRequest? body,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteAiSettingsService settings,
            SiteAiChatFactory ai) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            var (ok, error) = await settings.SetAsync(
                site, body?.ProviderId, body?.Model, body?.SystemPrompt, body?.AllowVisitors ?? false, Actor(principal));
            return ok
                ? Results.Ok(DescribeAi(site, ai, saved: true))
                : Results.BadRequest(new { error });
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapDelete("/sites/{domain}/ai", async (
            string domain,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteAiSettingsService settings,
            SiteAiChatFactory ai) =>
        {
            var (site, failure) = Find(domain, sites);
            if (site is null) return failure!;

            await settings.RemoveAsync(site, Actor(principal));
            return Results.Ok(DescribeAi(site, ai, saved: true));
        }).RequireAuthorization(Policies.ApiAdministrator);

        // Keys are write-only: the list says whether each provider has one and nothing more.
        api.MapGet("/ai/providers", async (AiProviderStore providers) =>
            Results.Ok((await providers.ListAsync()).Select(AdminEndpoints.DescribeProvider)))
            .RequireAuthorization(Policies.ApiAdministrator);

        api.MapPost("/sites/{domain}/rename", async (
            string domain,
            RenameRequest? body,
            ClaimsPrincipal principal,
            ZipDeploymentService deployer,
            HttpContext http) =>
        {
            var (normalized, error) = SiteStore.NormalizeDomain(domain);
            if (normalized is null) return Results.BadRequest(new { error });

            var (ok, failure, to) = await deployer.RenameAsync(normalized, body?.Domain, Actor(principal));
            return ok
                ? Results.Ok(new { ok = true, from = normalized, domain = to, url = SiteLinks.For(http.Request, to!) })
                : Results.BadRequest(new { error = failure });
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapDelete("/sites/{domain}", async (string domain, ClaimsPrincipal principal, ZipDeploymentService deployer) =>
        {
            var (normalized, error) = SiteStore.NormalizeDomain(domain);
            if (normalized is null) return Results.BadRequest(new { error });

            return await deployer.DeleteAsync(normalized, Actor(principal))
                ? Results.Ok(new { ok = true, domain = normalized })
                : Results.NotFound(new { error = $"No site is published at '{normalized}'." });
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapFunctionEndpoints();
        api.MapAdminEndpoints();
        api.MapTlsEndpoints();

        return app;
    }

    private static async Task<IResult> DeployAsync(
        string domain,
        HttpContext http,
        ClaimsPrincipal principal,
        ZipDeploymentService deployer,
        CancellationToken ct)
    {
        Stream? archive = null;
        string? archiveName = null;

        if (http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null)
                return Results.BadRequest(new { error = "Attach the zip archive as a form field named 'file'." });

            archive = file.OpenReadStream();
            archiveName = file.FileName;
        }
        else
        {
            archive = http.Request.Body;
            archiveName = http.Request.Headers["X-Archive-Name"].FirstOrDefault();
        }

        var result = await deployer.DeployAsync(domain, archive, archiveName, Actor(principal), "api",
            canDeployFunctions: principal.IsInRole(Roles.Administrator), ct);
        if (!result.Ok) return Results.BadRequest(new { error = result.Error, diagnostics = result.Diagnostics });

        return Results.Ok(new
        {
            ok = true,
            domain = result.Domain,
            url = SiteLinks.For(http.Request, result.Domain!),
            release = DescribeRelease(result.Release!),
            functions = result.Functions,
            warnings = result.Warnings ?? []
        });
    }

    /// <summary>
    /// Sends an event to a site's open pages: all of them, or those in one group, those of one user,
    /// or one connection. <c>delivered</c> is how many connections it was sent to, counted as it was
    /// sent; a page that is connecting at that moment may or may not be among them.
    /// </summary>
    private static async Task<IResult> PublishAsync(
        string domain,
        RealtimePublishRequest? body,
        SiteStore sites,
        RealtimeRegistry registry,
        SiteRealtimeFactory realtime,
        CancellationToken ct)
    {
        var (site, failure) = Find(domain, sites);
        if (site is null) return failure!;

        var (delivered, error) = await PublishToSiteAsync(site, body, registry, realtime, ct);
        return error is null
            ? Results.Ok(new { ok = true, domain = site.Domain, delivered })
            : Results.BadRequest(new { error });
    }

    /// <summary>
    /// Checks a publish request and sends it: what the API endpoint and the publish tool share.
    /// Returns how many connections it went to, or why it was refused.
    /// </summary>
    internal static async Task<(int Delivered, string? Error)> PublishToSiteAsync(
        SiteRecord site, RealtimePublishRequest? body, RealtimeRegistry registry, SiteRealtimeFactory realtime, CancellationToken ct)
    {
        if (body?.Event is null)
            return (0, "Name the event: send { \"event\": \"site.deployed\" }, with a \"payload\" if the pages need one.");

        if (RealtimeNames.EventError(body.Event) is { } eventError) return (0, eventError);

        if (new[] { body.Group, body.User, body.Connection }.Count(target => target is not null) > 1)
            return (0, "Send at most one of group, user and connection. With none, every page on the site gets the event.");

        if (body.Group is not null && RealtimeNames.GroupError(body.Group) is { } groupError)
            return (0, groupError);

        if (body.User is "") return (0, "\"user\" is empty. Name a user, or leave it out to send to every page.");
        if (body.Connection is "")
            return (0, "\"connection\" is empty. Name a connection, or leave it out to send to every page.");

        if (body.Payload is { } payload && RealtimeNames.PayloadError(payload) is { } payloadError)
            return (0, payloadError);

        var channel = realtime.Create(site);
        int delivered;

        if (body.Group is { } group)
        {
            delivered = registry.MemberCount(site.Domain, group);
            await channel.PublishToGroupAsync(group, body.Event, body.Payload, ct);
        }
        else if (body.User is { } user)
        {
            delivered = registry.ConnectionsOf(site.Domain, user).Count;
            await channel.PublishToUserAsync(user, body.Event, body.Payload, ct);
        }
        else if (body.Connection is { } connection)
        {
            delivered = registry.IsConnected(site.Domain, connection) ? 1 : 0;
            await channel.PublishToConnectionAsync(connection, body.Event, body.Payload, ct);
        }
        else
        {
            delivered = registry.ConnectionCount(site.Domain);
            await channel.PublishAsync(body.Event, body.Payload, ct);
        }

        return (delivered, null);
    }

    /// <summary>
    /// A site's proxy routes. Request header values go to administrators only: one may carry a key
    /// for the backend, typed in rather than read from <c>${env:…}</c>.
    /// </summary>
    internal static object DescribeProxy(SiteRecord site, bool administrator, IReadOnlyList<ServerProxyRoute> serverRoutes) => new
    {
        domain = site.Domain,
        routes = site.Proxy.Select(route => Redacted(route, administrator)),
        // The server-wide routes that name this site, and whether each is in effect here or why not.
        serverRoutes = ProxySites.ForSite(site.Domain, site.Proxy, serverRoutes, site.DisabledServerRoutes)
            .Select(r => new { id = r.Route.Id, sites = r.Route.Sites, path = r.Route.Path, target = r.Route.Target, state = r.State }),
        disabledServerRoutes = site.DisabledServerRoutes
    };

    /// <summary>
    /// A route as a caller may see it: in full for an administrator; for anyone else without the
    /// values of its request headers, since one may be a key typed in rather than read from
    /// <c>${env:…}</c>.
    /// </summary>
    internal static T Redacted<T>(T route, bool administrator) where T : ProxyRoute
    {
        if (administrator) return route;

        var copy = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(route, RouteJson), RouteJson)!;
        copy.RequestHeaders = route.RequestHeaders.Select(h => new ProxyHeader { Name = h.Name, Value = "" }).ToList();
        return copy;
    }

    private static readonly JsonSerializerOptions RouteJson = new(JsonSerializerDefaults.Web);

    /// <summary>Resolves a domain to a site, or the response to return instead.</summary>
    private static (SiteRecord? Site, IResult? Failure) Find(string domain, SiteStore sites)
    {
        var (normalized, error) = SiteStore.NormalizeDomain(domain);
        if (normalized is null) return (null, Results.BadRequest(new { error }));

        var site = sites.TryGet(normalized);
        return site is null
            ? (null, Results.NotFound(new { error = $"No site is published at '{normalized}'." }))
            : (site, null);
    }

    internal static object Describe(SiteRecord site, SiteVariableService variables, SiteAiChatFactory ai, RealtimeRegistry realtime) => new
    {
        domain = site.Domain,
        currentRelease = site.CurrentRelease,
        createdUtc = site.CreatedUtc,
        updatedUtc = site.UpdatedUtc,
        createdBy = site.CreatedBy,
        lastDeployedBy = site.LastDeployedBy,
        passcodeProtected = site.IsPasscodeProtected,
        passcodeSetUtc = site.PasscodeSetUtc,
        headerRules = (site.Headers?.Count ?? 0) + (site.Current?.Headers?.Count ?? 0),
        redirectRules = (site.Redirects?.Count ?? 0) + (site.Current?.Redirects?.Count ?? 0),
        proxyRoutes = site.Proxy.Count,
        variables = variables.Describe(site).Count,
        missingVariables = variables.Resolve(site).Missing.Count,
        ai = ai.ProviderOf(site) is not null,
        realtimeConnections = realtime.ConnectionCount(site.Domain),
        fileCount = site.Current?.FileCount ?? 0,
        totalBytes = site.Current?.TotalBytes ?? 0,
        functions = site.CurrentFunctions,
        releases = site.Releases.Select(DescribeRelease)
    };

    /// <summary>
    /// A release for the API: everything it records, but its variables as names and flags only. A
    /// default can be a secret's, stored encrypted, and has no business in a listing either way;
    /// <c>GET …/variables</c> shows the defaults that may be shown.
    /// </summary>
    private static object DescribeRelease(ReleaseRecord release) => new
    {
        id = release.Id,
        createdUtc = release.CreatedUtc,
        deployedBy = release.DeployedBy,
        source = release.Source,
        fileCount = release.FileCount,
        totalBytes = release.TotalBytes,
        archiveName = release.ArchiveName,
        strippedRootFolder = release.StrippedRootFolder,
        hasRootIndex = release.HasRootIndex,
        headers = release.Headers,
        redirects = release.Redirects,
        variables = release.Variables.Select(variable => new
        {
            name = variable.Name,
            @public = variable.Public,
            secret = variable.Secret,
            required = variable.Required
        }),
        functions = release.Functions
    };

    /// <summary>
    /// A site's variables for the API: every one the release declares or the site sets, and what
    /// needs attention. A secret's value and default are left out entirely rather than sent empty,
    /// so nothing reading the response can mistake one for the other.
    /// </summary>
    internal static object DescribeVariables(SiteRecord site, SiteVariableService variables, bool saved)
    {
        var resolved = variables.Resolve(site);
        var rows = variables.Describe(site).Select(variable =>
        {
            var row = new Dictionary<string, object?>
            {
                ["name"] = variable.Name,
                ["description"] = variable.Description,
                ["public"] = variable.IsPublic,
                ["secret"] = variable.IsSecret,
                ["required"] = variable.IsRequired,
                ["declared"] = variable.IsDeclared,
                ["hasValue"] = variable.HasValue
            };

            if (!variable.IsSecret)
            {
                row["value"] = variable.Value;
                row["default"] = variable.Default;
            }

            row["source"] = variable.Source;
            row["unsetEnvironment"] = variable.UnsetEnvironment;
            return row;
        }).ToList();

        return saved
            ? new { ok = true, domain = site.Domain, variables = rows, missing = resolved.Missing, unsetEnvironment = resolved.UnsetEnvironment }
            : new { domain = site.Domain, variables = rows, missing = resolved.Missing, unsetEnvironment = resolved.UnsetEnvironment };
    }

    /// <summary>
    /// A site's AI settings for the API. <c>configured</c> is false when the site has no provider,
    /// and also when the one it names was deleted, which <c>providerId</c> still shows.
    /// </summary>
    internal static object DescribeAi(SiteRecord site, SiteAiChatFactory ai, bool saved)
    {
        var settings = site.Ai;
        var provider = ai.ProviderOf(site);

        var described = new Dictionary<string, object?>();
        if (saved) described["ok"] = true;

        described["domain"] = site.Domain;
        described["configured"] = provider is not null;
        described["providerId"] = settings?.ProviderId;
        described["providerName"] = provider?.Name;
        described["model"] = settings?.Model;
        described["systemPrompt"] = settings?.SystemPrompt;
        described["allowVisitors"] = settings?.AllowVisitors ?? false;
        return described;
    }

    /// <summary>Only administrators may touch secrets and <c>${env:…}</c>: see <see cref="SiteVariableService"/>.</summary>
    private static bool IsAdministrator(ClaimsPrincipal principal) => principal.IsInRole(Roles.Administrator);

    internal static string Actor(ClaimsPrincipal principal)
    {
        var name = principal.Identity?.Name ?? "unknown";
        var keyId = principal.FindFirstValue(ApiKeyAuthenticationHandler.ApiKeyIdClaim);
        return keyId is null ? name : $"{name} (key {keyId})";
    }
}

/// <summary>Body of POST /api/v1/sites/{domain}/rename.</summary>
public sealed record RenameRequest(string? Domain);

/// <summary>Body of PUT /api/v1/sites/{domain}/passcode.</summary>
public sealed record PasscodeRequest(string? Passcode);

/// <summary>Body of PUT /api/v1/sites/{domain}/headers. Replaces the site's whole rule list.</summary>
public sealed record HeaderRulesRequest(List<HeaderRule>? Headers);

/// <summary>Body of PUT /api/v1/sites/{domain}/redirects. Replaces the site's whole rule list.</summary>
public sealed record RedirectRulesRequest(List<RedirectRule>? Redirects);

/// <summary>Body of PUT /api/v1/sites/{domain}/proxy. Replaces the site's whole route list.</summary>
public sealed record ProxyRoutesRequest(List<ProxyRoute>? Routes);

/// <summary>Body of PATCH /api/v1/sites/{domain}/files: files to write, and paths to delete; the rest of the site stays.</summary>
public sealed record SiteFilesRequest(List<SiteFileWrite>? Files, List<string>? Delete);

/// <summary>One file to write: its path in the site, and its text, or its bytes in base64 when <see cref="Base64"/> is true.</summary>
public sealed record SiteFileWrite(string? Path, string? Content, bool? Base64);

/// <summary>Body of PUT /api/v1/proxy. Replaces every server-wide route; one without an id gets one.</summary>
public sealed record ServerProxyRoutesRequest(List<ServerProxyRoute>? Routes);

/// <summary>Body of POST /api/v1/proxy/test: the id of the server-wide route to test.</summary>
public sealed record ServerProxyTestRequest(string? Id);

/// <summary>Body of PUT /api/v1/sites/{domain}/proxy/server-routes: turn the server-wide routes at a path off or on for the site.</summary>
public sealed record ServerRouteForSiteRequest(string? Path, bool? Enabled);

/// <summary>Body of POST /api/v1/sites/{domain}/proxy/test: the path of the route to test.</summary>
public sealed record ProxyTestRequest(string? Path);

/// <summary>Body of PUT /api/v1/sites/{domain}/variables. Replaces every value the site holds.</summary>
public sealed record VariablesRequest(List<VariableItem?>? Variables);

/// <summary>
/// One entry of <see cref="VariablesRequest"/>. The value travels in the clear and a secret is
/// protected on arrival. For a variable the release declares, the release has its say about the
/// flags (see <see cref="SiteVariableService.Flags"/>).
/// </summary>
public sealed record VariableItem(string? Name, string? Value, bool? Public, bool? Secret);

/// <summary>
/// Body of PUT /api/v1/sites/{domain}/variables/{name}. A value left out keeps the one the site
/// holds, a secret's included; only <c>""</c> empties it. A flag left out keeps what the variable
/// had; for a variable the release declares, the release has its say (see
/// <see cref="SiteVariableService.Flags"/>).
/// </summary>
public sealed record VariableRequest(string? Value, bool? Public, bool? Secret);

/// <summary>
/// Body of POST /api/v1/sites/{domain}/realtime/publish. <c>Event</c> is required; at most one of
/// <c>Group</c>, <c>User</c> and <c>Connection</c> narrows who receives it, and with none every page
/// connected to the site does. <c>Payload</c> is any JSON, up to 256 KB, and pages receive it as it is.
/// </summary>
public sealed record RealtimePublishRequest(string? Event, JsonElement? Payload, string? Group, string? User, string? Connection);

/// <summary>
/// Body of PUT /api/v1/sites/{domain}/ai. Replaces the site's AI settings: a model or prompt left
/// out is cleared, so the provider's default model applies, and visitors are refused unless
/// <c>allowVisitors</c> is true.
/// </summary>
public sealed record AiSettingsRequest(string? ProviderId, string? Model, string? SystemPrompt, bool? AllowVisitors);
