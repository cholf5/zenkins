namespace Zenkins.Worker.Git;

/// <summary>Why a Git operation failed. The value drives the message and the tests' assertions.</summary>
public enum GitFailureKind
{
    /// <summary>The <c>git</c> executable itself could not be started or answered "not a git command".</summary>
    GitUnavailable = 0,

    /// <summary>The target repository directory already holds something; a clone never overwrites.</summary>
    NonEmptyTarget = 1,

    /// <summary>The target path would leave the run's work directory.</summary>
    PathEscape = 2,

    /// <summary>The repository URL is malformed or embeds credentials (which must not reach argv).</summary>
    InvalidUrl = 3,

    /// <summary>The requested branch does not exist on the remote.</summary>
    BranchNotFound = 4,

    /// <summary>The remote refused the credential.</summary>
    AuthenticationRejected = 5,

    /// <summary>The remote could not be reached (DNS, connection, TLS).</summary>
    Unreachable = 6,

    /// <summary>The operation exceeded its budget and its process tree was killed (F-009 semantics).</summary>
    Timeout = 7,

    /// <summary>The operation was canceled by the caller.</summary>
    Canceled = 8,

    /// <summary>The credential material itself is unusable (e.g. a token with a line break).</summary>
    Credential = 9,

    /// <summary>Any other git failure; the message carries the filtered stderr tail.</summary>
    Failed = 10,
}

/// <summary>
/// A refused or failed Git operation (card F-013). The message is safe to log verbatim: it is built
/// from the sanitised URL (no user info), the branch name and the <see cref="GitLogFilter"/>-redacted
/// stderr tail - never from argv that could carry a credential.
/// </summary>
public sealed class GitException(string message, GitFailureKind kind, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>The classification the caller (and the run log) can branch on.</summary>
    public GitFailureKind Kind { get; } = kind;
}

/// <summary>Syntax the <see cref="GitWorkspace"/> accepts for a pinned commit (abbreviated or full).</summary>
public static class GitCommitSyntax
{
    /// <summary>
    /// <c>^[0-9a-f]{7,64}$</c>: a lower-case hex object name. An abbreviated SHA-1/SHA-256 is accepted
    /// (git resolves it), anything else - a branch, <c>HEAD~1</c>, a rev expression - is refused, so
    /// the reported commit is always an object name and never a moving reference.
    /// </summary>
    public static bool IsValid(string? commit) =>
        commit is { Length: >= 7 and <= 64 }
        && commit.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
