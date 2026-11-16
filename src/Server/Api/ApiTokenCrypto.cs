using System.Security.Cryptography;
using System.Text;

namespace Zenkins.Server.Api;

/// <summary>
/// Plaintext token material for the organization API (card F-027), the same discipline as the worker
/// tokens (F-004): a <c>zapi_…</c> prefixed base64url string, shown exactly once, and only its SHA-256
/// hash ever stored - so the plaintext of a leaked database cannot authenticate anything.
/// </summary>
public static class ApiTokenCrypto
{
    /// <summary>Prefix of every organization API token (recognizable in a log or a leak report).</summary>
    public const string Prefix = "zapi_";

    private const int SecretBytes = 32;

    /// <summary>Creates a new plaintext token. Shown exactly once, never stored.</summary>
    public static string Generate()
    {
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        return Prefix + Base64UrlEncode(secret);
    }

    /// <summary>Lowercase hex SHA-256 of the plaintext token (the only stored form).</summary>
    public static string Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        // Tokens are ASCII by construction (prefix + base64url), so ASCII and UTF-8 bytes are
        // identical here; the helper only names the intent of the hashing input.
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    }

    /// <summary>
    /// The prefix logged when a request is rejected - enough to correlate two log lines about the same
    /// token, never enough to reconstruct it (the same length the worker tokens log).
    /// </summary>
    public const int LogHashPrefixLength = 8;

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
