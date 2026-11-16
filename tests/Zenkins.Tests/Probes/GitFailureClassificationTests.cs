using Xunit;
using Zenkins.Worker.Git;

namespace Zenkins.Tests.Probes;

/// <summary>
/// Pins card F-013's clone-failure diagnosis - the shared table card I-005 extracted so the probe could
/// classify with the same rules. Three behaviours a refactor can silently lose, and did once:
/// <list type="bullet">
/// <item><b>The branch answer wins.</b> git prints several families in one stderr (a wrong branch of a
/// private repository mentions both the ref and a rejected credential); the branch names a setting the
/// operator can fix, so it must be read first.</item>
/// <item><b>Auth and unreachable keep their kinds</b> through the shared table.</item>
/// <item><b>A silent git still yields a readable message.</b> With no stderr the message must say
/// "no stderr output" - never end in a bare <c>: )</c>.</item>
/// </list>
/// </summary>
public sealed class GitFailureClassificationTests
{
    [Fact]
    public void A_missing_branch_wins_over_an_auth_sounding_stderr()
    {
        var result = Failed(
            exitCode: 128,
            stderr: "remote: Invalid username or password\n"
                + "fatal: Remote branch release not found in upstream origin");

        var failure = GitFailureClassification.ClassifyCloneFailure(
            result,
            "https://zenkins.example.com/team/app.git",
            "release");

        Assert.Equal(GitFailureKind.BranchNotFound, failure.Kind);
        Assert.Contains("release", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_shared_families_keep_their_kinds()
    {
        var auth = GitFailureClassification.ClassifyCloneFailure(
            Failed(128, "fatal: Authentication failed for 'https://zenkins.example.com/team/app.git/'"),
            "https://zenkins.example.com/team/app.git",
            "main");
        Assert.Equal(GitFailureKind.AuthenticationRejected, auth.Kind);

        var unreachable = GitFailureClassification.ClassifyCloneFailure(
            Failed(128, "fatal: unable to access 'https://zenkins.example.com/team/app.git/': Connection refused"),
            "https://zenkins.example.com/team/app.git",
            "main");
        Assert.Equal(GitFailureKind.Unreachable, unreachable.Kind);
    }

    [Fact]
    public void A_silent_git_still_produces_a_readable_message()
    {
        // No stderr at all: the message must not end in the empty parenthetical the extraction once produced.
        var failure = GitFailureClassification.ClassifyCloneFailure(
            Failed(1, stderr: string.Empty),
            "https://zenkins.example.com/team/app.git",
            "main");

        Assert.Equal(GitFailureKind.Failed, failure.Kind);
        Assert.EndsWith("no stderr output).", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(": )", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failed invocation shaped like <see cref="GitCommandRunner"/>'s own result: every stderr line is also
    /// a captured line (the runner appends both streams to <c>Lines</c>), which is what the classification
    /// reads, plus the bounded stderr tail the message quotes.
    /// </summary>
    private static GitCommandResult Failed(int exitCode, string stderr) =>
        new(
            exitCode,
            stderr.Length == 0 ? [] : stderr.Split('\n'),
            stderr);
}
