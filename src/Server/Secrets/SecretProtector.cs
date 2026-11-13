using Microsoft.AspNetCore.DataProtection;

namespace Zenkins.Server.Secrets;

/// <summary>
/// The purpose-bound cipher for secret values (card F-012, PRD §3.8).
/// <para>
/// <b>One purpose, one key scope.</b> Every ciphertext is produced by a protector created for
/// <see cref="ProtectionPurpose"/>; Data Protection mixes the purpose into the key derivation, so a
/// ciphertext minted for another purpose (the run-log subscription ticket, a different application
/// name) cannot be decrypted here and vice versa. That is what makes "the value is unrecoverable
/// through the API" a cryptographic property rather than a convention: swapping a stored ciphertext
/// for a ticket or another purpose's blob fails with
/// <see cref="System.Security.Cryptography.CryptographicException"/>.
/// </para>
/// <para>
/// <b>The key ring is data, not a machine default.</b> The keys live in the persisted ring configured
/// in <c>Program</c> (<c>App_Data/keys</c>, <c>DataProtection:KeysDirectory</c>) and must be backed up
/// with the database - losing the ring means every secret has to be re-entered, not restored.
/// </para>
/// <para>
/// <see cref="Unprotect"/> is only reachable from <see cref="SecretService.TryGetValueAsync"/> (an
/// internal member used by F-013/F-015); no endpoint or page calls this class's decrypt path.
/// </para>
/// </summary>
public sealed class SecretProtector
{
    /// <summary>
    /// The single purpose every organization secret is protected with. Changing this string is a
    /// data migration (existing ciphertexts stop decrypting), so it carries a version suffix.
    /// </summary>
    public const string ProtectionPurpose = "Zenkins.Secrets.v1";

    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(ProtectionPurpose);
    }

    /// <summary>Encrypts a plaintext value for storage. The result is the only form that is stored.</summary>
    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        return _protector.Protect(plaintext);
    }

    /// <summary>
    /// Decrypts a stored ciphertext. Throws <see cref="System.Security.Cryptography.CryptographicException"/>
    /// when the ciphertext was produced under another purpose, another application name, or a key ring
    /// this process does not hold - never returns a wrong plaintext.
    /// </summary>
    public string Unprotect(string ciphertext)
    {
        ArgumentException.ThrowIfNullOrEmpty(ciphertext);
        return _protector.Unprotect(ciphertext);
    }
}
