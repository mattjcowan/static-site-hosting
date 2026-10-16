using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Ai;
using StaticSiteHost.Services.Assistant;

namespace StaticSiteHost.Endpoints;

/// <summary>
/// The management assistant's endpoint, for the panel on every management page:
/// <c>POST /assistant/chat</c>, signed in with the UI's own cookie and its antiforgery token. The
/// answer is a stream of JSON lines (see <see cref="AssistantService.RunAsync"/>): text as the
/// model writes it, each tool as it starts and ends, a request to confirm, then <c>done</c>.
/// </summary>
public static class AssistantEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The most sites listed in the context sent with every call.</summary>
    private const int MaxSitesInContext = 60;

    public static IEndpointRouteBuilder MapAssistant(this IEndpointRouteBuilder app)
    {
        app.MapPost("/assistant/chat", ChatAsync).RequireAuthorization().DisableAntiforgery();

        app.MapPost("/assistant/end", async (HttpContext http, IAntiforgery antiforgery, AssistantService assistant, string? conversation) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();
            await assistant.EndAsync(conversation, UserId(http.User));
            return Results.Ok();
        }).RequireAuthorization().DisableAntiforgery();

        return app;
    }

    private static async Task ChatAsync(
        HttpContext http,
        IAntiforgery antiforgery,
        AssistantSettingsStore settingsStore,
        AiProviderStore providers,
        AiChatService chat,
        AssistantService assistant,
        AssistantUsageStore usage,
        SiteStore sites,
        CancellationToken ct)
    {
        // The form pages are protected by the antiforgery middleware; this JSON endpoint checks the
        // same token itself, from the X-CSRF-TOKEN header.
        if (!await antiforgery.IsRequestValidAsync(http))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var isAdministrator = http.User.IsInRole(Roles.Administrator);
        var settings = await settingsStore.ReadAsync();
        var provider = settings.ProviderId is null ? null : providers.Find(settings.ProviderId);

        if (provider is null || !(isAdministrator || settings.MembersMayUse))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new { error = "The assistant is not turned on for you. An administrator sets it up under AI." }, ct);
            return;
        }

        AssistantRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<AssistantRequest>(Json, ct);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (request.Message is { Length: > 8000 })
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = "A message is limited to 8,000 characters." }, ct);
            return;
        }

        http.Response.ContentType = "application/x-ndjson; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        async Task Emit(object item)
        {
            await http.Response.WriteAsync(JsonSerializer.Serialize(item, Json) + "\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }

        // Every message is billed to the provider's key: members get a daily allowance, and
        // everyone's use is counted for the AI page.
        var userId = UserId(http.User);
        var username = http.User.Identity?.Name ?? userId;

        await assistant.RunAsync(
            userId, request,
            Context(http.User, isAdministrator, sites, await http.RequestServices.GetRequiredService<SiteProxyService>().ServerRoutesAsync(), request.Domain),
            new ProviderAssistantModel(chat, provider, settings.Model), http.RequestServices, Emit, ct,
            admit: () => usage.AdmitAsync(userId, username, isAdministrator, settings.MemberMessagesPerDay),
            record: (input, output) => usage.RecordAsync(userId, username, isAdministrator, input, output));
    }

    /// <summary>
    /// What the model is told on every call about where the user is: who they are, the sites on the
    /// server, and the one the page shows, in full. It spares the model a lookup before most tasks.
    /// </summary>
    internal static string Context(
        ClaimsPrincipal user, bool isAdministrator, SiteStore sites, IReadOnlyList<ServerProxyRoute> serverRoutes, string? domain)
    {
        var text = new StringBuilder();
        text.Append("Signed in as ").Append(user.Identity?.Name).Append(isAdministrator ? " (administrator)." : " (member: no functions, proxy routes, AI, users, rename or delete).")
            .Append(" Today is ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd")).AppendLine(".");

        var all = sites.List();
        text.Append("Sites on this server (").Append(all.Count).Append(all.Count == 0 ? "): none yet." : "):").AppendLine();
        foreach (var site in all.Take(MaxSitesInContext)) text.Append("- ").AppendLine(Line(site));
        if (all.Count > MaxSitesInContext) text.AppendLine($"- … and {all.Count - MaxSitesInContext} more (list_sites).");

        if (serverRoutes.Count > 0)
        {
            text.AppendLine("Server-wide proxy routes (get_server_proxy_routes for their ids and settings):");
            foreach (var route in serverRoutes)
                text.Append("- ").Append(route.Path).Append(" → ").Append(route.Target).Append(" for ").Append(route.Sites)
                    .AppendLine(route.Enabled ? "" : " (off)");
        }

        var (normalized, _) = SiteStore.NormalizeDomain(domain);
        if (normalized is not null && sites.TryGet(normalized) is { } current)
        {
            text.Append("The page shows ").Append(current.Domain).Append(". \"This site\" means it.");
            var inherited = ProxySites.ForSite(current.Domain, current.Proxy, serverRoutes, current.DisabledServerRoutes)
                .Select(r => $"{r.Route.Path} → {r.Route.Target} ({r.State})").ToList();
            if (inherited.Count > 0) text.Append(" Server-wide routes for it: ").Append(string.Join("; ", inherited)).Append('.');
            if (current.Proxy.Count > 0)
                text.Append(" Its own proxy routes: ").Append(string.Join("; ", current.Proxy.Select(r =>
                    $"{r.Path} → {r.Target}{(r.Enabled ? "" : " (off)")}{(r.WebSockets ? " (WebSockets)" : "")}{(r.Streaming ? " (streaming)" : "")}{(r.GrpcWeb ? " (gRPC-Web)" : "")}"))).Append('.');
            // The rules themselves when they are short, so changing them needs no get_rules first.
            text.Append(' ').Append(Rules("Header rules", HeaderRuleText.Format(current.Headers)));
            text.Append(' ').Append(Rules("Redirects", RedirectRuleText.Format(current.Redirects)));
            if (current.Variables.Count > 0) text.Append(" Variables set: ").Append(string.Join(", ", current.Variables.Select(v => v.Name))).Append('.');
            text.AppendLine();
        }
        else
        {
            text.AppendLine("The page shows no particular site.");
        }

        return text.ToString();
    }

    /// <summary>The site's own rules of one kind, in their text format, or how many when they are long.</summary>
    private static string Rules(string label, string text)
    {
        if (text.Length == 0) return $"{label} of its own: none.";
        return text.Length <= 1200
            ? $"{label} of its own:\n{text}\n"
            : $"{label} of its own: {text.Split('\n').Count(line => line.StartsWith('/'))} (get_rules shows them).";
    }

    /// <summary>One site in a few words: what it serves.</summary>
    private static string Line(SiteRecord site)
    {
        var parts = new List<string>();
        if (site.Current is { } release) parts.Add($"{release.FileCount} file(s), release {release.Id}");
        if (site.Proxy.Count > 0) parts.Add("proxy " + string.Join(", ", site.Proxy.Select(r => $"{r.Path} → {r.Target}")));
        if (site.CurrentFunctions is not null) parts.Add("functions");
        if (site.IsPasscodeProtected) parts.Add("passcode");
        if (site.Ai is { HasProvider: true }) parts.Add("AI");
        return parts.Count == 0 ? $"{site.Domain}: empty" : $"{site.Domain}: {string.Join("; ", parts)}";
    }

    private static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
}
