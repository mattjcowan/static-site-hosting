using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace StaticSiteHost.Security;

public static class Tokens
{
    private const string AlphanumericAlphabet =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>URL-safe random token. 32 bytes ≈ 256 bits of entropy.</summary>
    public static string New(int byteCount = 32) =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(byteCount));

    /// <summary>
    /// Random letters and digits only. Used where the value is embedded in a delimited
    /// string (API keys are <c>prefix_id_secret</c>), so it must not contain '_' or '-'.
    /// 43 characters ≈ 256 bits of entropy.
    /// </summary>
    public static string NewAlphanumeric(int length = 43) =>
        RandomNumberGenerator.GetString(AlphanumericAlphabet, length);

    /// <summary>Lowercase hex SHA-256, used to store invitation tokens and API key secrets at rest.</summary>
    public static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>A readable, reasonably strong generated password for bootstrap/temporary use.</summary>
    public static string GeneratePassword(int length = 20)
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789-_";
        return RandomNumberGenerator.GetString(alphabet, length);
    }
}
