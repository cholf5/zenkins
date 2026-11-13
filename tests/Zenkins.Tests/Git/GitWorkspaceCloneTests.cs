using Xunit;
using Zenkins.Worker;
using WorkerGit = Zenkins.Worker.Git;

namespace Zenkins.Tests.Git;

/// <summary>
/// TC-001 (card F-013): cloning at a branch, the exact commit that is reported, the honest failure
/// messages, and the shallow-clone knob verified through git's own answer rather than through the flag
/// that was passed.
/// </summary>
[Trait("Category", "Git")]
public sealed class GitWorkspaceCloneTests
{
    [Fact]
    public async Task Clone_at_a_branch_reports_the_exact_commit()
    {
        using var fixture = GitFixtureRepository.Create();
        using var directory = new GitTestDirectory("clone");
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var lines = new List<string>();
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(work, fixture.Environment, lines.Add));

        var result = await workspace.CloneAsync(
            fixture.FileUrl,
            "feature",
            credential: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("feature", result.Branch);
        Assert.Equal(fixture.FeatureTip, result.Commit);
        Assert.Equal(
            GitCli.RevParse(work.RepositoryPath, fixture.Environment, "HEAD"),
            result.Commit);
        Assert.True(File.Exists(Path.Combine(work.RepositoryPath, "feature.txt")));
        Assert.False(result.IsShallow);
        Assert.Empty(result.Warnings);

        // The trace is the redacted argv: a clone at a branch, then the two commit readings and the
        // working-tree sanity check.
        Assert.Contains(workspace.Trace, entry => entry.Arguments.StartsWith("clone ", StringComparison.Ordinal));
        Assert.Contains(workspace.Trace, entry => entry.Arguments == "rev-parse --verify HEAD");
        Assert.Contains(workspace.Trace, entry => entry.Arguments == "status --porcelain");
        Assert.All(workspace.Trace, entry => Assert.Equal(0, entry.ExitCode));

        // The clone really happened into the run's repository directory, and git's own output reached
        // the run log through the redaction filter.
        Assert.Contains(lines, line => line.Contains("Cloning into", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clone_at_a_missing_branch_names_the_branch_and_leaks_nothing()
    {
        using var fixture = GitFixtureRepository.Create();
        using var directory = new GitTestDirectory("clone-missing");
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var lines = new List<string>();
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(work, fixture.Environment, lines.Add));

        var failure = await Assert.ThrowsAsync<WorkerGit.GitException>(() => workspace.CloneAsync(
            fixture.FileUrl,
            "no-such-branch",
            credential: null,
            TestContext.Current.CancellationToken));

        Assert.Equal(WorkerGit.GitFailureKind.BranchNotFound, failure.Kind);
        Assert.Contains("no-such-branch", failure.Message, StringComparison.Ordinal);

        // Nothing about the credential plumbing leaks: not the ask-pass helper's name, not the key
        // file's name, not a URL with embedded user info.
        foreach (var text in lines.Append(failure.Message))
        {
            Assert.DoesNotContain(WorkerGit.GitCredentialFiles.AskPassFileName, text, StringComparison.Ordinal);
            Assert.DoesNotContain("GIT_ASKPASS", text, StringComparison.Ordinal);
            Assert.DoesNotContain("user:", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Clone_refuses_a_target_that_is_not_empty()
    {
        using var fixture = GitFixtureRepository.Create();
        using var directory = new GitTestDirectory("clone-nonempty");
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var leftover = Path.Combine(work.RepositoryPath, "leftover.txt");
        await File.WriteAllTextAsync(leftover, "stale", TestContext.Current.CancellationToken);

        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(work, fixture.Environment));

        var failure = await Assert.ThrowsAsync<WorkerGit.GitException>(() => workspace.CloneAsync(
            fixture.FileUrl,
            "main",
            credential: null,
            TestContext.Current.CancellationToken));

        Assert.Equal(WorkerGit.GitFailureKind.NonEmptyTarget, failure.Kind);
        Assert.Contains(work.RepositoryPath, failure.Message, StringComparison.Ordinal);

        // Nothing was cloned and nothing was deleted: the message is the whole outcome.
        Assert.True(File.Exists(leftover));
        Assert.Empty(workspace.Trace);
    }

    [Fact]
    public async Task Clone_refuses_a_target_that_escapes_the_work_root()
    {
        using var fixture = GitFixtureRepository.Create();
        using var directory = new GitTestDirectory("clone-escape");
        var root = Path.Combine(directory.RootPath, "run");
        Directory.CreateDirectory(root);
        var escaping = new RunWorkDirectory(
            Guid.NewGuid(),
            root,
            Path.Combine(root, "..", "..", "escaped-repository"),
            Path.Combine(root, "artifacts"),
            Path.Combine(root, "temp"));
        Directory.CreateDirectory(escaping.TempPath);

        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(escaping, fixture.Environment));

        var failure = await Assert.ThrowsAsync<WorkerGit.GitException>(() => workspace.CloneAsync(
            fixture.FileUrl,
            "main",
            credential: null,
            TestContext.Current.CancellationToken));

        Assert.Equal(WorkerGit.GitFailureKind.PathEscape, failure.Kind);
        Assert.False(Directory.Exists(Path.GetFullPath(escaping.RepositoryPath)));
        Assert.Empty(workspace.Trace);
    }

    [Fact]
    public async Task Clone_reports_an_unreachable_remote()
    {
        using var directory = new GitTestDirectory("clone-unreachable");
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(
                work,
                GitCli.Environment(directory),
                operationTimeout: TimeSpan.FromSeconds(30)));

        var failure = await Assert.ThrowsAsync<WorkerGit.GitException>(() => workspace.CloneAsync(
            "http://127.0.0.1:1/repo.git",
            "main",
            credential: null,
            TestContext.Current.CancellationToken));

        Assert.Equal(WorkerGit.GitFailureKind.Unreachable, failure.Kind);
        Assert.Contains("127.0.0.1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_depth_knob_produces_a_really_shallow_clone()
    {
        using var fixture = GitFixtureRepository.Create();

        // Depth 0 (the default) is a full clone.
        using (var fullDirectory = new GitTestDirectory("clone-full"))
        {
            var fullWork = GitTestWorkspace.CreateWork(fullDirectory.RootPath);
            var full = new WorkerGit.GitWorkspace(
                GitTestWorkspace.Options(fullWork, fixture.Environment, depth: 0));

            var fullResult = await full.CloneAsync(
                fixture.FileUrl,
                "main",
                credential: null,
                TestContext.Current.CancellationToken);

            Assert.False(fullResult.IsShallow);
            Assert.Equal(
                "false",
                GitCli.MustRun(fullWork.RepositoryPath, fixture.Environment, "rev-parse", "--is-shallow-repository"));
        }

        // Depth 2 over a transport (file://) is genuinely shallow: asserted through git's own answer,
        // because git silently ignores --depth for a plain local path.
        using var shallowDirectory = new GitTestDirectory("clone-shallow");
        var shallowWork = GitTestWorkspace.CreateWork(shallowDirectory.RootPath);
        var shallow = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(shallowWork, fixture.Environment, depth: 2));

        var shallowResult = await shallow.CloneAsync(
            fixture.FileUrl,
            "main",
            credential: null,
            TestContext.Current.CancellationToken);

        Assert.True(shallowResult.IsShallow);
        Assert.Equal(
            "true",
            GitCli.MustRun(shallowWork.RepositoryPath, fixture.Environment, "rev-parse", "--is-shallow-repository"));
        Assert.Equal(
            GitCli.RevParse(shallowWork.RepositoryPath, fixture.Environment, "HEAD"),
            shallowResult.Commit);

        // Two commits visible, the third one truncated - the depth really applied.
        var history = GitCli.MustRun(shallowWork.RepositoryPath, fixture.Environment, "rev-list", "--count", "HEAD");
        Assert.Equal("2", history);
    }

    [Fact]
    public async Task Clone_reports_git_as_unavailable_when_the_executable_cannot_be_started()
    {
        using var directory = new GitTestDirectory("clone-no-git");
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(
                work,
                GitCli.Environment(directory),
                gitExecutable: $"no-such-git-{Guid.NewGuid():N}"));

        var failure = await Assert.ThrowsAsync<WorkerGit.GitException>(() => workspace.CloneAsync(
            "file:///nonexistent/repo.git",
            "main",
            credential: null,
            TestContext.Current.CancellationToken));

        Assert.Equal(WorkerGit.GitFailureKind.GitUnavailable, failure.Kind);
        Assert.Contains("git", failure.Message, StringComparison.Ordinal);
    }
}
