using Microsoft.AspNetCore.DataProtection;
using Zenkins.Server.Secrets;

namespace Zenkins.Server.Git;

/// <summary>
/// The purpose-bound cipher for Git credential values (card F-013), the sibling of
/// <see cref="SecretProtector"/> with a <b>different purpose</b>.
/// <para>
/// <b>Why two purposes instead of one.</b> Data Protection mixes the purpose string into the key
/// derivation, so a ciphertext minted for <c>Zenkins.Secrets.v1</c> cannot be decrypted here and vice
/// versa. That is what keeps the two classes of ciphertext from being interchangeable: copying a
/// secret blob into the credentials table (or the reverse) fails with
/// <see cref="System.Security.Cryptography.CryptographicException"/> instead of silently delivering a
/// different secret to a git process. It also bounds the blast radius of a future purpose change - the
/// secrets purpose can be rotated without orphaning Git credentials, and the other way round.
/// </para>
/// <para>
/// <b>The key ring is shared.</b> Both protectors use the persisted ring configured in
/// <c>Program</c> (<c>App_Data/keys</c>); losing it means every credential has to be re-entered, not
/// restored. Nothing outside <c>GitCredentialService.FetchMaterialForRunAsync</c> calls
/// <see cref="Unprotect"/>, and no endpoint returns the result to a user.
/// </para>
/// </summary>
public sealed class GitCredentialProtector
{
    /// <summary>
    /// The purpose Git credential ciphertexts are bound to. Changing this string is a data migration.
    /// Deliberately not <see cref="SecretProtector.ProtectionPurpose"/>.
    /// </summary>
    public const string ProtectionPurpose = "Zenkins.GitCredentials.v1";

    private readonly IDataProtector _protector;

    public GitCredentialProtector(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(ProtectionPurpose);
    }

    /// <summary>Encrypts a credential value for storage. The result is the only form that is stored.</summary>
    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        return _protector.Protect(plaintext);
    }

    /// <summary>
    /// Decrypts a stored ciphertext. Throws <see cref="System.Security.Cryptography.CryptographicException"/>
    /// for a ciphertext produced under another purpose (including a secret's), another application
    /// name, or a key ring this process does not hold - never returns a wrong plaintext.
    /// </summary>
    public string Unprotect(string ciphertext)
    {
        ArgumentException.ThrowIfNullOrEmpty(ciphertext);
        return _protector.Unprotect(ciphertext);
    }
}
