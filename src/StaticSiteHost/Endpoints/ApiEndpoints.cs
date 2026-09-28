using System.Security.Claims;
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

        api.MapPost("/sites/{domain}/deploy", DeployAsync);

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
            Results.Ok((await providers.ListAsync()).Select(provider => new
            {
                id = provider.Id,
                name = provider.Name,
                kind = provider.Kind,
                baseUrl = provider.BaseUrl,
                defaultModel = provider.DefaultModel,
                hasApiKey = provider.HasApiKey,
                createdUtc = provider.CreatedUtc,
                createdBy = provider.CreatedBy,
                updatedUtc = provider.UpdatedUtc
            })))
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

        api.MapDelete("/sites/{domain}", async (
            string domain,
            ClaimsPrincipal principal,
            SiteStore sites,
            SiteContentServer content,
            FunctionHost functions,
            SiteVariableService variables,
            RealtimeRegistry realtime,
            AuditLog audit) =>
        {
            var (normalized, error) = SiteStore.NormalizeDomain(domain);
            if (normalized is null) return Results.BadRequest(new { error });

            // The site's functions are gone, background work and requests and all, before its
            // directory is, and cannot load again until it has been; then once more for good measure.
            await using (await functions.EvictAsync(normalized))
            {
                if (!await sites.DeleteAsync(normalized))
                    return Results.NotFound(new { error = $"No site is published at '{normalized}'." });
            }

            functions.Evict(normalized);
            content.Evict(normalized);
            variables.Evict(normalized);

            // Once the site is gone, not before, so no connection can arrive in between and stay.
            realtime.DisconnectSite(normalized);
            await audit.WriteAsync("site.delete", Actor(principal), new { domain = normalized });
            return Results.Ok(new { ok = true, domain = normalized });
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapFunctionEndpoints();

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

        if (body?.Event is null)
            return Results.BadRequest(new { error = "Name the event: send { \"event\": \"site.deployed\" }, with a \"payload\" if the pages need one." });

        if (RealtimeNames.EventError(body.Event) is { } eventError) return Results.BadRequest(new { error = eventError });

        if (new[] { body.Group, body.User, body.Connection }.Count(target => target is not null) > 1)
            return Results.BadRequest(new { error = "Send at most one of group, user and connection. With none, every page on the site gets the event." });

        if (body.Group is not null && RealtimeNames.GroupError(body.Group) is { } groupError)
            return Results.BadRequest(new { error = groupError });

        if (body.User is "") return Results.BadRequest(new { error = "\"user\" is empty. Name a user, or leave it out to send to every page." });
        if (body.Connection is "")
            return Results.BadRequest(new { error = "\"connection\" is empty. Name a connection, or leave it out to send to every page." });

        if (body.Payload is { } payload && RealtimeNames.PayloadError(payload) is { } payloadError)
            return Results.BadRequest(new { error = payloadError });

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

        return Results.Ok(new { ok = true, domain = site.Domain, delivered });
    }

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

    private static object Describe(SiteRecord site, SiteVariableService variables, SiteAiChatFactory ai, RealtimeRegistry realtime) => new
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
    private static object DescribeVariables(SiteRecord site, SiteVariableService variables, bool saved)
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
    private static object DescribeAi(SiteRecord site, SiteAiChatFactory ai, bool saved)
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
