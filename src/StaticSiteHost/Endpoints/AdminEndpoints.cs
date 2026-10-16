using System.Security.Claims;
using StaticSiteHost.Models;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Ai;

namespace StaticSiteHost.Endpoints;

/// <summary>
/// The server's own administration over the API, so a script or an agent can set a server up with
/// no one signing in: the caller's API keys, AI providers, users and the audit log. Each calls the
/// service the UI uses, so the same rules apply. Keys are anyone's own; the rest is for
/// administrators.
/// </summary>
public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder api)
    {
        // ---- the caller's own API keys

        api.MapGet("/keys", async (ClaimsPrincipal principal, ApiKeyStore keys) =>
            Results.Ok((await keys.ListForUserAsync(UserId(principal))).Select(DescribeKey)));

        api.MapPost("/keys", async (ApiKeyRequest? body, ClaimsPrincipal principal, ApiKeyStore keys, AuditLog audit) =>
        {
            if (body?.ExpiresInDays is < 1 or > 3650)
                return Results.BadRequest(new { error = "expiresInDays must be between 1 and 3650, or left out for a key that does not expire." });

            var expires = body?.ExpiresInDays is { } days ? DateTimeOffset.UtcNow.AddDays(days) : (DateTimeOffset?)null;
            var (record, plainText) = await keys.CreateAsync(UserId(principal), body?.Name ?? "API key", expires);
            await audit.WriteAsync("apikey.create", ApiEndpoints.Actor(principal), new { keyId = record.Id, record.Name });

            // The only time the key is ever shown.
            return Results.Ok(new { key = plainText, details = DescribeKey(record) });
        });

        api.MapDelete("/keys/{id}", async (string id, ClaimsPrincipal principal, ApiKeyStore keys, AuditLog audit) =>
        {
            if (!await keys.RevokeAsync(id, UserId(principal)))
                return Results.NotFound(new { error = "You have no API key with that id." });

            await audit.WriteAsync("apikey.revoke", ApiEndpoints.Actor(principal), new { keyId = id });
            return Results.Ok(new { ok = true, id });
        });

        // ---- AI providers. Keys are write-only: nothing here returns one.

        api.MapPost("/ai/providers", async (AiProviderRequest? body, ClaimsPrincipal principal, AiProviderStore providers) =>
        {
            var (provider, error) = await providers.AddAsync(
                body?.Name, body?.Kind, body?.BaseUrl, body?.ApiKey, body?.DefaultModel, ApiEndpoints.Actor(principal));
            return provider is null ? Results.BadRequest(new { error }) : Results.Ok(DescribeProvider(provider));
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapPut("/ai/providers/{id}", async (string id, AiProviderRequest? body, ClaimsPrincipal principal, AiProviderStore providers) =>
        {
            if (await providers.FindAsync(id) is null) return Results.NotFound(new { error = "No AI provider has that id." });

            var (provider, error) = await providers.UpdateAsync(
                id, body?.Name, body?.BaseUrl, body?.ApiKey, body?.DefaultModel, ApiEndpoints.Actor(principal));
            return provider is null ? Results.BadRequest(new { error }) : Results.Ok(DescribeProvider(provider));
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapDelete("/ai/providers/{id}", async (string id, ClaimsPrincipal principal, AiProviderStore providers) =>
        {
            var removed = await providers.DeleteAsync(id, ApiEndpoints.Actor(principal));
            return removed is null
                ? Results.NotFound(new { error = "No AI provider has that id." })
                : Results.Ok(new { ok = true, id, name = removed.Name });
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapGet("/ai/providers/{id}/models", async (string id, AiProviderStore providers, AiModelCatalog catalog, CancellationToken ct) =>
        {
            var provider = await providers.FindAsync(id);
            if (provider is null) return Results.NotFound(new { error = "No AI provider has that id." });

            var list = await catalog.ListAsync(provider, ct);
            return Results.Ok(new { providerId = provider.Id, models = list.Models, error = list.Error });
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapPost("/ai/providers/{id}/test", async (string id, AiProviderStore providers, AiChatService chat, CancellationToken ct) =>
        {
            var provider = await providers.FindAsync(id);
            if (provider is null) return Results.NotFound(new { error = "No AI provider has that id." });

            var test = await AiProviderTest.RunAsync(chat, provider, ct);
            return Results.Ok(new
            {
                ok = test.Ok,
                elapsedMs = test.ElapsedMs,
                text = test.Text,
                model = test.Model,
                inputTokens = test.InputTokens,
                outputTokens = test.OutputTokens,
                stopReason = test.StopReason,
                error = test.Error
            });
        }).RequireAuthorization(Policies.ApiAdministrator);

        // ---- the management assistant

        api.MapGet("/assistant", async (
            StaticSiteHost.Services.Assistant.AssistantSettingsStore assistant, StaticSiteHost.Services.Assistant.AssistantUsageStore usage) =>
            Results.Ok(await DescribeAssistantAsync(assistant, usage)))
            .RequireAuthorization(Policies.ApiAdministrator);

        api.MapPut("/assistant", async (
            AssistantSettingsRequest? body, ClaimsPrincipal principal,
            StaticSiteHost.Services.Assistant.AssistantSettingsStore assistant, StaticSiteHost.Services.Assistant.AssistantUsageStore usage) =>
        {
            var error = await assistant.SaveAsync(
                body?.ProviderId, body?.Model, body?.MembersMayUse ?? false, ApiEndpoints.Actor(principal), body?.MemberMessagesPerDay);
            return error is null ? Results.Ok(await DescribeAssistantAsync(assistant, usage)) : Results.BadRequest(new { error });
        }).RequireAuthorization(Policies.ApiAdministrator);

        // ---- users

        api.MapGet("/users", async (UserStore users) =>
            Results.Ok((await users.ListAsync()).Select(DescribeUser)))
            .RequireAuthorization(Policies.ApiAdministrator);

        api.MapPost("/users", async (
            UserCreateRequest? body, ClaimsPrincipal principal, UserAdminService admin, LinkGenerator links, HttpContext http) =>
        {
            var temporaryPassword = body?.Credential == "password";
            if (body?.Credential is not (null or "link" or "password"))
                return Results.BadRequest(new { error = "credential is 'link' (the default) or 'password'." });

            var (created, error) = await admin.CreateAsync(
                body?.Username, body?.DisplayName, body?.Role, temporaryPassword, ApiEndpoints.Actor(principal));
            if (created is null) return Results.BadRequest(new { error });

            // Shown this once, like the UI does: send it to the person yourself.
            return Results.Ok(new
            {
                user = DescribeUser(created.User),
                temporaryPassword = created.TemporaryPassword,
                inviteLink = created.InviteToken is null ? null : ActivationLink(links, http, created.InviteToken)
            });
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapPost("/users/{id}/invite", async (
            string id, ClaimsPrincipal principal, UserAdminService admin, UserStore users, LinkGenerator links, HttpContext http) =>
        {
            var (token, error) = await admin.InviteAsync(id, ApiEndpoints.Actor(principal));
            return token is null
                ? Results.NotFound(new { error })
                : Results.Ok(new { user = DescribeUser((await users.FindByIdAsync(id))!), inviteLink = ActivationLink(links, http, token) });
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapPut("/users/{id}", async (
            string id, UserUpdateRequest? body, ClaimsPrincipal principal, UserAdminService admin, UserStore users) =>
        {
            if (await users.FindByIdAsync(id) is null) return Results.NotFound(new { error = "No such user." });

            if (body?.Role is { } role && await admin.SetRoleAsync(id, role, UserId(principal), ApiEndpoints.Actor(principal)) is { } roleError)
                return Results.BadRequest(new { error = roleError });

            if (body?.Disabled is { } disabled &&
                await admin.SetDisabledAsync(id, disabled, UserId(principal), ApiEndpoints.Actor(principal)) is { } disableError)
                return Results.BadRequest(new { error = disableError });

            return Results.Ok(DescribeUser((await users.FindByIdAsync(id))!));
        }).RequireAuthorization(Policies.ApiAdministrator);

        api.MapDelete("/users/{id}", async (string id, ClaimsPrincipal principal, UserAdminService admin) =>
        {
            var (username, error) = await admin.DeleteAsync(id, UserId(principal), ApiEndpoints.Actor(principal));
            return username is null ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true, id, username });
        }).RequireAuthorization(Policies.ApiAdministrator);

        // ---- audit log

        api.MapGet("/audit", async (int? limit, string? action, string? domain, DateTimeOffset? since, AuditLog audit) =>
            Results.Ok(await audit.ReadAsync(limit ?? 100, action, domain, since)))
            .RequireAuthorization(Policies.ApiAdministrator);

        return api;
    }

    private static string UserId(ClaimsPrincipal principal) => principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    private static string? ActivationLink(LinkGenerator links, HttpContext http, string token) =>
        links.GetUriByPage(http, "/Activate", handler: null, values: new { token });

    /// <summary>The assistant's settings and who has used it today.</summary>
    internal static async Task<object> DescribeAssistantAsync(
        StaticSiteHost.Services.Assistant.AssistantSettingsStore assistant, StaticSiteHost.Services.Assistant.AssistantUsageStore usage)
    {
        var settings = await assistant.ReadAsync();
        var (day, users) = await usage.TodayAsync();
        return new
        {
            providerId = settings.ProviderId,
            model = settings.Model,
            membersMayUse = settings.MembersMayUse,
            memberMessagesPerDay = settings.MemberMessagesPerDay,
            updatedUtc = settings.UpdatedUtc,
            updatedBy = settings.UpdatedBy,
            usageToday = new { day, users }
        };
    }

    internal static object DescribeKey(ApiKeyRecord key) => new
    {
        id = key.Id,
        name = key.Name,
        display = key.Display,
        createdUtc = key.CreatedUtc,
        lastUsedUtc = key.LastUsedUtc,
        expiresUtc = key.ExpiresUtc,
        revoked = key.Revoked,
        usable = key.IsUsable,
        fromConfiguration = key.FromConfiguration
    };

    internal static object DescribeProvider(AiProviderRecord provider) => new
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
    };

    internal static object DescribeUser(UserRecord user) => new
    {
        id = user.Id,
        username = user.Username,
        displayName = user.DisplayName,
        role = user.Role,
        disabled = user.IsDisabled,
        hasPassword = user.HasPassword,
        mustChangePassword = user.MustChangePassword,
        pendingInvite = user.HasPendingInvite,
        bootstrap = user.IsBootstrap,
        createdUtc = user.CreatedUtc,
        lastLoginUtc = user.LastLoginUtc
    };
}

/// <summary>Body of POST /api/v1/keys.</summary>
public sealed record ApiKeyRequest(string? Name, int? ExpiresInDays);

/// <summary>Body of POST and PUT /api/v1/ai/providers. On PUT, the kind cannot change and an empty key keeps the one it has.</summary>
public sealed record AiProviderRequest(string? Name, string? Kind, string? BaseUrl, string? ApiKey, string? DefaultModel);

/// <summary>Body of POST /api/v1/users. Credential is "link" (a private link, the default) or "password" (a temporary one).</summary>
public sealed record UserCreateRequest(string? Username, string? DisplayName, string? Role, string? Credential);

/// <summary>
/// Body of PUT /api/v1/assistant. No provider turns the assistant off. A daily limit left out keeps
/// the one set; 0 is no limit.
/// </summary>
public sealed record AssistantSettingsRequest(string? ProviderId, string? Model, bool? MembersMayUse, int? MemberMessagesPerDay);

/// <summary>Body of PUT /api/v1/users/{id}. Each field left out is left as it is.</summary>
public sealed record UserUpdateRequest(string? Role, bool? Disabled);
