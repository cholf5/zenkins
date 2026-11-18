using System.Security.Cryptography;
using System.Text;

namespace Zenkins.Shared.Notifications;

/// <summary>
/// The outbound notification's signature (card F-039), deliberately the same scheme card F-017's inbound deliveries
/// use: <c>sha256=&lt;lower-case hex&gt;</c>, HMAC-SHA256 over the <b>exact bytes</b> of the request body with the
/// endpoint's shared secret.
/// <para>
/// <b>Why the same scheme.</b> One receiver-side verifier and one sentence in the docs ("HMAC-SHA256 over the raw
/// body, hex, <c>sha256=</c> prefix") covers both directions; the header name differs only because the direction
/// does.
/// </para>
/// <para>
/// <b>Sign the bytes that are sent.</b> The body is serialized once and both signed and transmitted from the same
/// array, so a re-serialization (key order, escaping) can never make a valid delivery look forged.
/// </para>
/// <para>
/// <b>Verification is constant-time.</b> <see cref="Verify"/> hashes both sides and compares fixed-size digests with
/// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>, so a wrong secret,
/// a wrong length and a missing header take the same path. It exists for the receiver and for this product's own
/// tests; the Server never needs to verify its own signature.
/// </para>
/// </summary>
public static class NotificationSignature
{
    /// <summary>The header carrying <c>sha256=&lt;hex&gt;</c>.</summary>
    public const string HeaderName = "X-Zenkins-Signature";

    /// <summary>The header carrying the stable delivery id (the value a receiver deduplicates on).</summary>
    public const string DeliveryHeaderName = "X-Zenkins-Delivery";

    /// <summary>The header carrying the event name, so a receiver can route without parsing the body.</summary>
    public const string EventHeaderName = "X-Zenkins-Event";

    /// <summary>The header carrying the attempt number (1 = first send).</summary>
    public const string AttemptHeaderName = "X-Zenkins-Attempt";

    /// <summary>The algorithm prefix, identical to F-017's inbound header.</summary>
    public const string AlgorithmPrefix = "sha256=";

    /// <summary>The signature header value for <paramref name="body"/> under <paramref name="secret"/>.</summary>
    public static string Compute(string secret, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return AlgorithmPrefix + Convert.ToHexStringLower(mac);
    }

    /// <summary>
    /// Whether <paramref name="providedHeader"/> proves knowledge of <paramref name="secret"/> for these bytes. A
    /// missing or malformed header is simply "not verified" - never an error with a detail.
    /// </summary>
    public static bool Verify(string? providedHeader, ReadOnlySpan<byte> body, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var expected = Compute(secret, body);
        var normalized = providedHeader?.Trim().ToLowerInvariant() ?? string.Empty;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
