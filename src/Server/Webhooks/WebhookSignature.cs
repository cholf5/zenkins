using System.Security.Cryptography;
using System.Text;

namespace Zenkins.Server.Webhooks;

/// <summary>
/// Verification of a delivery's signature (card F-017): GitHub/Gitea's
/// <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c> (HMAC-SHA256 over the <b>raw</b> request body) and
/// GitLab's <c>X-Gitlab-Token</c> shared secret, accepted as an alternative.
/// <para>
/// <b>The body is the bytes that arrived.</b> The caller passes the raw payload, before any
/// deserialisation: re-serialising a parsed model and signing that is the classic way to make this
/// feature look green in a test and fail against a real git host (key order, whitespace, unicode
/// escaping), so the code is shaped so it cannot happen - there is no model to serialise here.
/// </para>
/// <para>
/// <b>Every comparison is constant-time.</b> Both sides of each comparison are hashed first and the
/// fixed-size digests are compared with <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>,
/// so a wrong secret, a wrong length and an absent header all take the same path: no early exit, no
/// length-dependent branch, nothing for a remote prober to time. Both schemes are always evaluated and
/// combined with a non-short-circuiting OR, so the response never reveals which one was tried.
/// </para>
/// </summary>
public static class WebhookSignature
{
    /// <summary>GitHub's and Gitea's HMAC header.</summary>
    public const string HubSignatureHeader = "X-Hub-Signature-256";

    /// <summary>GitLab's shared-secret header.</summary>
    public const string GitLabTokenHeader = "X-Gitlab-Token";

    /// <summary>The algorithm prefix of <see cref="HubSignatureHeader"/>.</summary>
    public const string HubSignaturePrefix = "sha256=";

    /// <summary>
    /// Whether the request proves knowledge of <paramref name="secret"/>. A missing or malformed header
    /// is simply "not verified" - never an error with a detail.
    /// </summary>
    public static bool IsValid(
        string? hubSignature,
        string? gitLabToken,
        ReadOnlySpan<byte> body,
        string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        var expectedSignature = HubSignaturePrefix + Convert.ToHexStringLower(mac);

        // Both lines always run (no &&/|| between them): the answer must not depend on which scheme
        // matched first, or an attacker could tell them apart by timing.
        var hubMatches = FixedTimeTextEquals(expectedSignature, Normalize(hubSignature));
        var tokenMatches = FixedTimeTextEquals(secret, gitLabToken?.Trim() ?? string.Empty);

        return hubMatches | tokenMatches;
    }

    /// <summary>
    /// Compares two texts in constant time with respect to their contents <em>and</em> their lengths:
    /// both are hashed first, so the comparison itself is always 32 bytes wide. Trimming/lower-casing
    /// the header happens before this call and touches no secret-dependent data.
    /// </summary>
    private static bool FixedTimeTextEquals(string expected, string provided) =>
        CryptographicOperations.FixedTimeEquals(Digest(expected), Digest(provided));

    private static byte[] Digest(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Canonical form of a signature header: hex digests are case-insensitive and hosts may pad, so
    /// trim and lower-case. Applied to both sides, so it cannot weaken the comparison.
    /// </summary>
    private static string Normalize(string? header) => header?.Trim().ToLowerInvariant() ?? string.Empty;
}
