using System.Security.Cryptography;

namespace Zenkins.Server.Workers;

/// <summary>
/// Plaintext token material for the worker API (F-004). Tokens are `zreg_…`/`zwrk_…` prefixed
/// base64url strings; only their SHA-256 hash is ever stored, so the plaintext of a leaked
/// database cannot authenticate anything.
/// </summary>
public static class WorkerTokenCrypto
{
    /// <summary>Prefix of registration tokens minted by an Owner.</summary>
    public const string RegistrationPrefix = "zreg_";

    /// <summary>Prefix of the per-worker tokens returned at registration.</summary>
    public const string WorkerPrefix = "zwrk_";

    private const int SecretBytes = 32;

    /// <summary>Creates a new plaintext token with the given prefix. Shown exactly once, never stored.</summary>
    public static string Generate(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        return prefix + Base64UrlEncode(secret);
    }

    /// <summary>Lowercase hex SHA-256 of the plaintext token (the only stored form).</summary>
    public static string Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return Convert.ToHexStringLower(SHA256.HashData(AuthenticationBytes(token)));
    }

    // Tokens are ASCII by construction (prefix + base64url), so ASCII and UTF-8 bytes are
    // identical here; the helper only names the intent of the hashing input.
    private static byte[] AuthenticationBytes(string value) => System.Text.Encoding.ASCII.GetBytes(value);

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
