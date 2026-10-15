namespace StaticSiteHost.Configuration;

/// <summary>
/// The first administrator, seeded from configuration on startup.
/// Bound from the "Bootstrap" configuration section.
/// </summary>
public sealed class BootstrapOptions
{
    public const string SectionName = "Bootstrap";

    public string Username { get; set; } = "admin";

    /// <summary>
    /// Password for the bootstrap administrator. When left empty a random one is
    /// generated on first run, written to the log and to config/bootstrap-password.txt,
    /// and must be changed at first login.
    /// </summary>
    public string? Password { get; set; }

    public string DisplayName { get; set; } = "Administrator";

    /// <summary>Force a password change at first login even when a password was configured.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>
    /// An API key for the bootstrap administrator, usable from the first start, so a new server
    /// can be set up by a script with no one signing in first. Written as
    /// <c>sshost_&lt;id&gt;_&lt;secret&gt;</c>: an id of 8 to 64 letters and digits, and a secret of at
    /// least 32. Changing it revokes the old key; removing it revokes the key; a value that cannot
    /// be read revokes the old key too. Revoking the key in the UI sticks until a new one is set.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Recovery switch: re-apply the configured password on every startup. Leave off
    /// in normal operation so a password changed in the UI is not reverted by a restart.
    /// </summary>
    public bool ResetPasswordOnStartup { get; set; }
}
