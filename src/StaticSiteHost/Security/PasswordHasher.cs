using System.Security.Cryptography;

namespace StaticSiteHost.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256 hashing with a per-password salt. Encoded as
/// <c>pbkdf2$sha256$&lt;iterations&gt;$&lt;salt-b64&gt;$&lt;hash-b64&gt;</c> so the iteration
/// count can be raised later without invalidating existing hashes.
/// </summary>
public static class PasswordHasher
{
    private const int DefaultIterations = 210_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const string Prefix = "pbkdf2$sha256$";

    /// <summary>
    /// A valid hash of a random value nobody will ever supply. Verifying against it costs
    /// exactly what a real check costs, so sign-in can spend the same work whether or not
    /// the account exists — otherwise an unknown username returns noticeably faster and
    /// becomes a way to enumerate who has an account here.
    /// </summary>
    public static string PlaceholderHash { get; } = Hash(Tokens.New());

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, DefaultIterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Prefix}{DefaultIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string? encoded)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(encoded)) return false;
        if (!encoded.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        var parts = encoded.Split('$');
        if (parts.Length != 5) return false;
        if (!int.TryParse(parts[2], out var iterations) || iterations is < 1000 or > 10_000_000) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0) return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
