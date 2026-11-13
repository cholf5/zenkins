namespace Zenkins.Worker.Git;

/// <summary>
/// The kind of a <see cref="GitCredentialMaterial"/> (card F-013, PRD §6). Wire and storage value is
/// the string, so the enum-to-text mapping lives here and nowhere else.
/// </summary>
public enum GitCredentialKind
{
    /// <summary>An HTTPS personal access token, delivered to git through <c>GIT_ASKPASS</c>.</summary>
    HttpsToken = 0,

    /// <summary>A private SSH key, written to a 0600 file and pinned through <c>GIT_SSH_COMMAND</c>.</summary>
    SshKey = 1,
}

/// <summary>
/// The decrypted material for one run's Git credential (card F-013). It exists only in the worker
/// process, only for the duration of the run, and it is <b>never</b> rendered: <see cref="ToString"/>
/// is overridden so even an accidental string interpolation writes a placeholder, and the record's
/// generated members cannot leak the value through a log formatter.
/// <para>
/// <b>Two shapes, one carrier.</b> An HTTPS token needs a user name (the token is the password) and
/// travels through <c>GIT_ASKPASS</c>; an SSH key is a PEM/OpenSSH private key and travels through a
/// file plus <c>GIT_SSH_COMMAND -i</c>. The kind selects the plumbing; the value is opaque and is
/// registered with the run's <see cref="GitLogFilter"/> before any git command runs.
/// </para>
/// </summary>
public sealed record GitCredentialMaterial(GitCredentialKind Kind, string Secret, string? UserName = null)
{
    /// <summary>
    /// The user name sent for an HTTPS token when the credential does not name one. Token-auth
    /// servers accept any user name with the token as the password; <c>x-access-token</c> is the
    /// convention GitHub/GitLab document.
    /// </summary>
    public const string DefaultTokenUserName = "x-access-token";

    /// <summary>The user name to answer the ask-pass prompt with.</summary>
    public string EffectiveUserName =>
        string.IsNullOrWhiteSpace(UserName) ? DefaultTokenUserName : UserName;

    /// <summary>The stored/wire text of the kind (<c>HttpsToken</c> or <c>SshKey</c>).</summary>
    public string KindText => Kind switch
    {
        GitCredentialKind.HttpsToken => GitCredentialKinds.HttpsToken,
        GitCredentialKind.SshKey => GitCredentialKinds.SshKey,
        _ => throw new InvalidOperationException($"Unknown Git credential kind '{Kind}'."),
    };

    /// <summary>Redacted on purpose: a credential material value must never be printable.</summary>
    public override string ToString() =>
        $"{nameof(GitCredentialMaterial)}({KindText}, <redacted>)";

    /// <summary>Parses the stored kind text. Unknown values throw rather than defaulting.</summary>
    public static GitCredentialKind ParseKind(string? kindText) => kindText switch
    {
        GitCredentialKinds.HttpsToken => GitCredentialKind.HttpsToken,
        GitCredentialKinds.SshKey => GitCredentialKind.SshKey,
        _ => throw new ArgumentException(
            $"Unknown Git credential kind '{kindText}'.", nameof(kindText)),
    };
}

/// <summary>The kind discriminators, as stored in the database and sent on the wire.</summary>
public static class GitCredentialKinds
{
    public const string HttpsToken = "HttpsToken";
    public const string SshKey = "SshKey";
}
