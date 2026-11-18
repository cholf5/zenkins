using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Zenkins.Server.Notifications;

/// <summary>
/// The purpose-bound cipher for notification secrets (card F-039). Same construction as
/// <see cref="Webhooks.WebhookSecretProtector"/>, with its own purpose: Data Protection mixes the purpose into the key
/// derivation, so a notification secret's ciphertext cannot be decrypted by the webhook path, the secrets path or the
/// git-credential path - and vice versa.
/// <para>
/// <b>Only one caller ever sees the plaintext:</b> <see cref="NotificationSender"/>, to compute one request's HMAC.
/// No endpoint, page or log line reaches <see cref="Unprotect"/>.
/// </para>
/// </summary>
public sealed class NotificationSecretProtector
{
    /// <summary>
    /// The single purpose every notification secret is protected with. Changing this string is a data migration
    /// (existing ciphertexts stop decrypting), so it carries a version suffix.
    /// </summary>
    public const string ProtectionPurpose = "Zenkins.Notifications.v1";

    private readonly IDataProtector _protector;

    public NotificationSecretProtector(IDataProtectionProvider dataProtectionProvider)
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
    /// Decrypts a stored ciphertext. Throws <see cref="CryptographicException"/> when the ciphertext was produced
    /// under another purpose or a key ring this process does not hold - never returns a wrong plaintext.
    /// </summary>
    public string Unprotect(string ciphertext)
    {
        ArgumentException.ThrowIfNullOrEmpty(ciphertext);
        return _protector.Unprotect(ciphertext);
    }
}
