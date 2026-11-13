using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Zenkins.Server.Webhooks;

/// <summary>
/// The purpose-bound cipher for webhook secrets (card F-017). Same construction as
/// <see cref="Secrets.SecretProtector"/>, with its own purpose: Data Protection mixes the purpose into
/// the key derivation, so an organization secret's ciphertext cannot be decrypted here and a webhook
/// secret's cannot be decrypted by the secrets path.
/// <para>
/// <b>Unlike an organization secret, this one is shown once.</b> A webhook secret has to be pasted into
/// the git host, so <see cref="WebhookService"/> returns the plaintext in the create/rotate response and
/// nowhere else. <see cref="Unprotect"/> exists for exactly one caller (delivery signature
/// verification) and no endpoint or page reaches it.
/// </para>
/// <para>
/// <b>The key ring is shared with the rest of the product</b> (<c>App_Data/keys</c>, backed up with the
/// database): losing it means every webhook has to be rotated, not restored.
/// </para>
/// </summary>
public sealed class WebhookSecretProtector
{
    /// <summary>
    /// The single purpose every webhook secret is protected with. Changing this string is a data
    /// migration (existing ciphertexts stop decrypting), so it carries a version suffix.
    /// </summary>
    public const string ProtectionPurpose = "Zenkins.Webhooks.v1";

    /// <summary>
    /// Prefix of a generated secret. A visible marker like the worker tokens' <c>zwrk_</c>, so a secret
    /// pasted into the wrong field is recognisable - and so a test can find it in a page.
    /// </summary>
    public const string SecretPrefix = "whsec_";

    /// <summary>Entropy of a generated secret: 32 bytes, base64url-encoded.</summary>
    private const int SecretByteCount = 32;

    private readonly IDataProtector _protector;

    public WebhookSecretProtector(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(ProtectionPurpose);
    }

    /// <summary>Encrypts a plaintext secret for storage. The result is the only form that is stored.</summary>
    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        return _protector.Protect(plaintext);
    }

    /// <summary>
    /// Decrypts a stored ciphertext. Throws <see cref="CryptographicException"/> when the ciphertext was
    /// produced under another purpose, another application name, or a key ring this process does not
    /// hold - never returns a wrong plaintext.
    /// </summary>
    public string Unprotect(string ciphertext)
    {
        ArgumentException.ThrowIfNullOrEmpty(ciphertext);
        return _protector.Unprotect(ciphertext);
    }

    /// <summary>
    /// Mints a fresh secret: 32 cryptographically random bytes, base64url, with the
    /// <see cref="SecretPrefix"/>. Generated rather than typed, so a weak shared secret is not a
    /// configuration mistake the product has to defend against.
    /// </summary>
    public static string Generate() =>
        SecretPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(SecretByteCount));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
