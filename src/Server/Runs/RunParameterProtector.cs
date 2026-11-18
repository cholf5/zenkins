using Microsoft.AspNetCore.DataProtection;

namespace Zenkins.Server.Runs;

/// <summary>
/// The purpose-bound cipher for a run's <b>sensitive</b> parameter values (card F-041).
/// <para>
/// <b>Its own purpose, and that is the point.</b> Data Protection mixes the purpose into the key derivation, so
/// a ciphertext minted here cannot be decrypted as an organization Secret (or as any other purpose's blob) and
/// vice versa: a password parameter and a stored Secret are different kinds of thing, and swapping one blob for
/// the other must fail rather than silently produce a value.
/// </para>
/// <para>
/// <b>Why encrypt at all.</b> The value has to survive until a worker claims the run - it is frozen with the
/// run, not typed again at execution time - so it must be stored. Encrypting it is what lets the run's plain
/// snapshot carry the mask (<see cref="Zenkins.Shared.Projects.ProjectParameterTypes.MaskedValue"/>) while a
/// database dump alone cannot answer "what was the deploy password": the key ring is a separate, backed-up file,
/// exactly as for <c>Secrets</c>.
/// </para>
/// </summary>
public sealed class RunParameterProtector
{
    /// <summary>
    /// The single purpose every protected parameter value is sealed with. Changing this string is a data
    /// migration (a queued run's password would stop decrypting), so it carries a version suffix.
    /// </summary>
    public const string ProtectionPurpose = "Zenkins.RunParameters.v1";

    private readonly IDataProtector _protector;

    public RunParameterProtector(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(ProtectionPurpose);
    }

    /// <summary>Seals one plaintext value. The result is the only form that is stored.</summary>
    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return _protector.Protect(plaintext);
    }

    /// <summary>
    /// Opens a stored ciphertext. Throws <see cref="System.Security.Cryptography.CryptographicException"/> when
    /// the blob was produced under another purpose or a key ring this process does not hold - never returns a
    /// wrong plaintext.
    /// </summary>
    public string Unprotect(string ciphertext)
    {
        ArgumentException.ThrowIfNullOrEmpty(ciphertext);
        return _protector.Unprotect(ciphertext);
    }
}
