using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Models;
using StaticSiteHost.Security;

namespace StaticSiteHost.Services;

/// <summary>
/// Seeds the administrator described by the "Bootstrap" configuration section.
/// Runs on every start, but only creates the account once — a password changed in the
/// UI is never reverted unless Bootstrap:ResetPasswordOnStartup is turned on.
/// </summary>
public sealed class BootstrapAdministrator
{
    private readonly UserStore _users;
    private readonly ApiKeyStore _apiKeys;
    private readonly DataPaths _paths;
    private readonly BootstrapOptions _options;
    private readonly ILogger<BootstrapAdministrator> _logger;

    public BootstrapAdministrator(
        UserStore users,
        ApiKeyStore apiKeys,
        DataPaths paths,
        IOptions<BootstrapOptions> options,
        ILogger<BootstrapAdministrator> logger)
    {
        _users = users;
        _apiKeys = apiKeys;
        _paths = paths;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureAsync()
    {
        var username = await EnsureAccountAsync();
        if (username is null) return;

        // After the account, which the key belongs to. Runs even with no key configured, so one
        // that was configured before is revoked.
        var user = await _users.FindByUsernameAsync(username);
        if (user is null) return;

        if (await _apiKeys.EnsureConfiguredAsync(user.Id, _options.ApiKey) is { } keyError)
            _logger.LogError("Bootstrap:ApiKey cannot be used: {Error}. No key was made from it.", keyError);
        else if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            _logger.LogInformation("The API key in Bootstrap:ApiKey works for '{Username}'.", username);
    }

    /// <summary>Seeds the account, and returns its user name, or null when there is none to use.</summary>
    private async Task<string?> EnsureAccountAsync()
    {
        var username = string.IsNullOrWhiteSpace(_options.Username) ? "admin" : _options.Username.Trim();
        if (UserStore.ValidateUsername(username) is { } error)
        {
            _logger.LogError("Bootstrap:Username is not usable ({Error}). No administrator was seeded.", error);
            return null;
        }

        var existing = await _users.FindByUsernameAsync(username);

        if (existing is null)
        {
            var generated = string.IsNullOrEmpty(_options.Password);
            var password = generated ? Tokens.GeneratePassword() : _options.Password!;

            var (user, createError) = await _users.CreateAsync(
                username, _options.DisplayName, Roles.Administrator, password);

            if (user is null)
            {
                _logger.LogError("Could not seed the bootstrap administrator: {Error}", createError);
                return null;
            }

            await _users.UpdateAsync(user.Id, u =>
            {
                u.IsBootstrap = true;
                u.MustChangePassword = generated || _options.MustChangePassword;
            });

            if (generated) await AnnounceGeneratedPasswordAsync(username, password);
            else _logger.LogInformation("Seeded bootstrap administrator '{Username}' from configuration.", username);

            return username;
        }

        // Keep the flag accurate even if the account predates this setting.
        if (!existing.IsBootstrap || !existing.IsAdministrator || existing.IsDisabled)
        {
            await _users.UpdateAsync(existing.Id, u =>
            {
                u.IsBootstrap = true;
                u.Role = Roles.Administrator;
                u.IsDisabled = false;
            });
            _logger.LogInformation("Re-enabled bootstrap administrator '{Username}'.", username);
        }

        if (_options.ResetPasswordOnStartup)
        {
            var generated = string.IsNullOrEmpty(_options.Password);
            var password = generated ? Tokens.GeneratePassword() : _options.Password!;

            await _users.SetPasswordAsync(existing.Id, password, generated || _options.MustChangePassword);

            if (generated) await AnnounceGeneratedPasswordAsync(username, password);
            else _logger.LogWarning(
                "Bootstrap:ResetPasswordOnStartup is on — the password for '{Username}' was reset from configuration.",
                username);
        }

        return username;
    }

    private async Task AnnounceGeneratedPasswordAsync(string username, string password)
    {
        var file = _paths.BootstrapPasswordFile;
        try
        {
            await File.WriteAllTextAsync(file,
                $"username: {username}{Environment.NewLine}password: {password}{Environment.NewLine}");
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not write the generated password to {File}", file);
        }

        _logger.LogWarning(
            """

            ==========================================================================
             A password was generated for the administrator account '{Username}':

                 {Password}

             It must be changed at first sign-in. Also saved to {File}.
             Set Bootstrap__Password to choose your own and avoid this message.
            ==========================================================================
            """,
            username, password, file);
    }
}
