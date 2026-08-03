using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using StaticSiteHost.Services;

namespace StaticSiteHost.Security;

/// <summary>
/// Authenticates API requests from an <c>X-Api-Key</c> header or an
/// <c>Authorization: Bearer &lt;key&gt;</c> header. The principal carries the owning
/// user's role, so an API key can do exactly what its owner can do.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string ApiKeyIdClaim = "ssh:api_key_id";

    private readonly ApiKeyStore _apiKeys;
    private readonly UserStore _users;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiKeyStore apiKeys,
        UserStore users)
        : base(options, logger, encoder)
    {
        _apiKeys = apiKeys;
        _users = users;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = ReadKey(Request);
        if (presented is null) return AuthenticateResult.NoResult();

        var key = await _apiKeys.ValidateAsync(presented);
        if (key is null) return AuthenticateResult.Fail("Unknown or expired API key.");

        var user = await _users.FindByIdAsync(key.UserId);
        if (user is null || user.IsDisabled) return AuthenticateResult.Fail("The owner of this API key is not active.");

        var principal = UserStore.CreatePrincipal(user, SchemeName);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ApiKeyIdClaim, key.Id));

        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"Bearer realm=\"static-site-host\", charset=\"UTF-8\"";
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private static string? ReadKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue(HeaderName, out var header) && !string.IsNullOrWhiteSpace(header))
            return header.ToString().Trim();

        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var value = authorization["Bearer ".Length..].Trim();
            if (value.Length > 0) return value;
        }

        return null;
    }
}
