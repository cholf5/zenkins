using Xunit;
using Zenkins.Shared.Pipelines;
using Zenkins.Worker;
using Zenkins.Worker.Git;
using Zenkins.Worker.Pipelines;
using Zenkins.Tests.Pipelines;
using Zenkins.Tests.Webhooks;

namespace Zenkins.Tests.Git;

/// <summary>
/// Card F-037's worker half: what "the same inputs" means once the claim reaches the executor.
/// <para>
/// A rebuild is only honest if the commit the original run pinned is the commit that gets built - the
/// branch may have moved (two pushes to one branch seconds apart are allowed, F-017), so trusting the
/// branch tip would rebuild <em>different code under the same name</em>. These two facts pin both halves
/// of that promise at the layer where it is decided:
/// </para>
/// <list type="bullet">
/// <item><b>TC-001/TC-003:</b> a claim that names a commit builds that commit even after the branch moved,
/// while the same repository and moment produce the <em>new</em> tree for a claim that names no commit
/// (the fresh-trigger shape).</item>
/// <item><b>TC-003:</b> a pinned commit the remote no longer has fails the run with a <c>git-*</c> reason
/// naming it, and the branch head is never built in its place.</item>
/// </list>
/// <para>
/// The server half - the rebuild's claim really carries the original's commit - is
/// <c>Zenkins.Tests.Runs.RebuildReplayTests</c>. The old <c>zenkins: the branch moved; checked out the
/// pushed commit</c> line keeps its wording: it is the same mechanism F-017 introduced, now used by a
/// second intent.
/// </para>
/// </summary>
[Trait("Category", "Git")]
public sealed class RebuildPinTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pinned_commit_is_built_after_the_branch_moved_while_an_unpinned_run_takes_the_tip()
    {
        using var repository = GitFixtureRepository.Create("rebuild-pin");
        var environment = repository.Environment;
        GitCli.MustRun(repository.SourcePath, environment, "remote", "add", "origin", repository.BarePath);

        // Run #1's commit: the pipeline, pushed.
        var pinnedCommit = GitFixtureRepository.CommitFile(
            repository.SourcePath,
            environment,
            "pipeline.lua",
            PipelineTestData.MarkerPipeline("marker.txt"),
            "pipeline");
        GitCli.MustRun(repository.SourcePath, environment, "push", "origin", "main");

        // ...and then the branch moves on. A fresh trigger at this moment builds the tip; a rebuild of #1
        // must still build the pinned commit.
        var movedTip = GitFixtureRepository.CommitFile(
            repository.SourcePath,
            environment,
            "later.txt",
            "this file must not exist in a rebuilt tree\n",
            "the branch moved");
        GitCli.MustRun(repository.SourcePath, environment, "push", "origin", "main");
        Assert.NotEqual(pinnedCommit, movedTip);

        using var rebuildDirectory = new GitTestDirectory("rebuild-pin-run");
        var rebuiltWork = GitTestWorkspace.CreateWork(rebuildDirectory.RootPath);
        var rebuilt = await RunAsync(
            repository.FileUrl,
            rebuiltWork,
            environment,
            expectedCommit: pinnedCommit);

        Assert.Equal(0, rebuilt.ExitCode);
        Assert.Null(rebuilt.FailureReason);
        Assert.Equal(pinnedCommit, rebuilt.Commit);
        Assert.Equal(pinnedCommit, GitCli.RevParse(rebuiltWork.RepositoryPath, environment, "HEAD"));
        Assert.False(File.Exists(Path.Combine(rebuiltWork.RepositoryPath, "later.txt")));

        // The steps really ran on the pinned tree (their own marker records the order).
        Assert.Equal(
            ["restore", "build", "package"],
            File.ReadAllLines(Path.Combine(rebuiltWork.RepositoryPath, "marker.txt")));

        // The control: a fresh trigger names no commit, so the worker resolves the branch - the moved tip.
        using var freshDirectory = new GitTestDirectory("rebuild-fresh-run");
        var freshWork = GitTestWorkspace.CreateWork(freshDirectory.RootPath);
        var fresh = await RunAsync(
            repository.FileUrl,
            freshWork,
            environment,
            expectedCommit: null);

        Assert.Equal(0, fresh.ExitCode);
        Assert.Equal(movedTip, fresh.Commit);
        Assert.Equal(movedTip, GitCli.RevParse(freshWork.RepositoryPath, environment, "HEAD"));
        Assert.True(File.Exists(Path.Combine(freshWork.RepositoryPath, "later.txt")));
    }

    [Fact]
    public async Task A_vanished_commit_fails_with_a_git_reason_and_the_branch_head_is_not_built()
    {
        using var repository = GitFixtureRepository.Create("rebuild-vanished");
        var environment = repository.Environment;
        GitCli.MustRun(repository.SourcePath, environment, "remote", "add", "origin", repository.BarePath);

        // A commit that is *not in the repository* stands in for one that used to be. The two are the same
        // code path on purpose - `checkout()` can only answer "the object is not here", whether it was
        // rewritten away and garbage-collected or never existed - and this approximation keeps the test
        // deterministic and fast (a real rewrite would need a force-push plus a `git gc --prune=now` on the
        // fixture's bare repository). What the fact pins is the behaviour a vanished commit produces: a
        // `git-*` failure, the commit named in the log, and no fallback to the branch head.
        var vanished = new string('9', 40);
        GitFixtureRepository.CommitFile(
            repository.SourcePath,
            environment,
            "pipeline.lua",
            PipelineTestData.MarkerPipeline("marker.txt"),
            "pipeline");
        GitCli.MustRun(repository.SourcePath, environment, "push", "origin", "main");

        // The commit a rebuild pins was rewritten away: the clone succeeds (the branch is there), the pin
        // does not. The run must fail honestly instead of quietly building the branch head.
        using var directory = new GitTestDirectory("rebuild-vanished-run");
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var outcome = await RunAsync(
            repository.FileUrl,
            work,
            environment,
            expectedCommit: vanished,
            lines: out var lines);

        Assert.Null(outcome.ExitCode);
        Assert.NotNull(outcome.FailureReason);
        Assert.StartsWith("git-", outcome.FailureReason!, StringComparison.Ordinal);
        Assert.Equal(PipelineFailureReasons.GitFailed, outcome.FailureReason);

        // The reason is the closed code; the log is where the commit is named, so an operator can act.
        Assert.Contains(
            lines,
            line => line.Contains(vanished, StringComparison.Ordinal) && line.Contains("could not be prepared", StringComparison.Ordinal));

        // Nothing was built: no step ran, so the pipeline's own marker file never appeared.
        Assert.False(File.Exists(Path.Combine(work.RepositoryPath, "marker.txt")));
    }

    private static async Task<PipelineRunOutcome> RunAsync(
        string repositoryUrl,
        RunWorkDirectory work,
        IReadOnlyDictionary<string, string?> environment,
        string? expectedCommit) =>
        await RunAsync(repositoryUrl, work, environment, expectedCommit, out _);

    private static Task<PipelineRunOutcome> RunAsync(
        string repositoryUrl,
        RunWorkDirectory work,
        IReadOnlyDictionary<string, string?> environment,
        string? expectedCommit,
        out List<string> lines)
    {
        var log = new List<string>();
        lines = log;
        var runner = new PipelineRunner(
            new PipelineRunOptions
            {
                Work = work,
                RepositoryUrl = repositoryUrl,
                Branch = "main",
                PipelinePath = "pipeline.lua",
                Deadline = DateTimeOffset.UtcNow.AddMinutes(5),
                GitExecutable = GitCli.ResolveGitPath(),
                GitEnvironment = environment,

                // F-037: the commit the original run pinned (a rebuild), or null (a fresh trigger).
                ExpectedCommit = expectedCommit,
                Log = line =>
                {
                    lock (log)
                    {
                        log.Add(line);
                    }
                },
            },
            new PipelineDefinitionRunner(
                PipelineDefinitionRunner.CreateRuntime(PipelineRunner.CreateLogSink(line =>
                {
                    lock (log)
                    {
                        log.Add(line);
                    }
                }))),
            _ => Task.FromResult<GitCredentialMaterial?>(null),
            (_, _, _) => Task.FromResult(true));

        return runner.RunAsync(CancellationToken);
    }
}
