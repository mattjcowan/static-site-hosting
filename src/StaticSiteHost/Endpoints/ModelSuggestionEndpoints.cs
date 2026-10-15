using Microsoft.AspNetCore.Antiforgery;
using StaticSiteHost.Models;
using StaticSiteHost.Services.Ai;

namespace StaticSiteHost.Endpoints;

/// <summary>
/// Model names for the management UI's model fields to suggest (wwwroot/js/model-suggestions.js):
/// <c>POST /ui/ai/models</c>, signed in as an administrator with the UI's cookie and antiforgery
/// token. Either a saved provider's models, or those of one being added, from what is typed into
/// the form, so a default model can be picked before anything is saved. Scripts use
/// <c>GET /api/v1/ai/providers/{id}/models</c> instead.
/// </summary>
public static class ModelSuggestionEndpoints
{
    public static IEndpointRouteBuilder MapModelSuggestions(this IEndpointRouteBuilder app)
    {
        app.MapPost("/ui/ai/models", async (
            ModelSuggestionRequest? body, HttpContext http, IAntiforgery antiforgery,
            AiProviderStore providers, AiModelCatalog catalog, CancellationToken ct) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest();

            AiModelList list;
            if (!string.IsNullOrWhiteSpace(body?.ProviderId))
            {
                var provider = await providers.FindAsync(body.ProviderId);
                if (provider is null) return Results.NotFound(new { error = "That provider no longer exists." });
                list = await catalog.ListAsync(provider, ct);
            }
            else
            {
                list = await catalog.ProbeAsync(body?.Kind, body?.BaseUrl, body?.ApiKey, ct);
            }

            return Results.Ok(new { models = list.Models, error = list.Error });
        })
        .RequireAuthorization(policy => policy.RequireRole(Roles.Administrator))
        .DisableAntiforgery();

        return app;
    }
}

/// <summary>A saved provider's id, or the kind, base URL and key typed into the form that adds one.</summary>
public sealed record ModelSuggestionRequest(string? ProviderId, string? Kind, string? BaseUrl, string? ApiKey);
