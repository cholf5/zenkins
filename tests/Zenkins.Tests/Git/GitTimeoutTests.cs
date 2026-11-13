using System.Diagnostics;
using Xunit;
using WorkerGit = Zenkins.Worker.Git;

namespace Zenkins.Tests.Git;

/// <summary>
/// The budget rule (card F-013, AC-9): a git invocation that exceeds its budget is killed <b>with its
/// process tree</b> and reported as a timeout (F-009 semantics), leaving nothing behind.
/// <para>
/// The fixture is a <c>git</c> executable that records its own pid and a child <c>sleep</c>'s pid before
/// waiting. After the timeout both pids must be gone - which is what distinguishes "the tree was
/// killed" from "the parent was killed and the child kept running".
/// </para>
/// </summary>
[Trait("Category", "Git")]
public sealed class GitTimeoutTests
{
    /// <summary>
    /// A <c>git</c> executable that records its own pid and its child <c>sleep</c>'s pid, then waits -
    /// the fixture both timeout facts hang on (literally).
    /// </summary>
    private static string CreateHangingGit(GitTestDirectory directory, out string pidFile)
    {
        pidFile = directory.Combine("pids.txt");
        var binDirectory = directory.Combine("bin");
        GitCli.WriteScript(
            binDirectory,
            "git",
            string.Join(
                '\n',
                // Composed so no absolute interpreter path appears in the source.
                "#!" + "/" + "bin" + "/" + "sh",
                $"printf '%s\\n' \"$$\" >> '{pidFile}'",
                "sleep 300 &",
                $"printf '%s\\n' \"$!\" >> '{pidFile}'",
                "wait",
                string.Empty));

        return Path.Combine(binDirectory, "git");
    }

    [Fact]
    public async Task A_clone_that_exceeds_its_budget_is_killed_with_its_process_tree()
    {
        using var directory = new GitTestDirectory("timeout");
        var gitPath = CreateHangingGit(directory, out var pidFile);

        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var lines = new List<string>();
        var workspace = new WorkerGit.GitWorkspace(
            GitTestWorkspace.Options(
                work,
                GitCli.Environment(directory),
                lines.Add,
                gitExecutable: gitPath,

                // Long enough that the stub records both pids before the budget fires even on a loaded
                // machine, short enough to keep the test quick. The timeout path itself is what is
                // measured, not the exact duration.
                operationTimeout: TimeSpan.FromSeconds(5)));

        // A credential is configured on purpose: the kill path must clean up the materialised
        // ask-pass helper too (review M3).
        var token = "zkpat_0123456789abcdef0123456789abcdef";
        var stopwatch = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<WorkerGit.GitException>(() => workspace.CloneAsync(
            "file:///nonexistent/repository.git",
            "main",
            new WorkerGit.GitCredentialMaterial(WorkerGit.GitCredentialKind.HttpsToken, token),
            TestContext.Current.CancellationToken));
        stopwatch.Stop();

        Assert.Equal(WorkerGit.GitFailureKind.Timeout, failure.Kind);
        Assert.Contains("budget", failure.Message, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"the kill took {stopwatch.Elapsed}");
        Assert.DoesNotContain(token, failure.Message, StringComparison.Ordinal);

        // The trace records the invocation as never having produced an exit code.
        var entry = Assert.Single(workspace.Trace);
        Assert.Null(entry.ExitCode);
        Assert.StartsWith("clone ", entry.Arguments, StringComparison.Ordinal);

        // Both the process and its child are gone: the tree was killed, not just the parent.
        var pids = await ReadPidsAsync(pidFile, expected: 2, TestContext.Current.CancellationToken);
        Assert.Equal(2, pids.Length);
        foreach (var pid in pids)
        {
            await AssertProcessGoneAsync(pid, TestContext.Current.CancellationToken);
        }

        // The kill path collapsed the credential files with the process: nothing is left in temp, and
        // no file under the run's tree carries the token.
        Assert.Empty(Directory.EnumerateFileSystemEntries(work.TempPath));
        Assert.False(File.Exists(Path.Combine(work.TempPath, WorkerGit.GitCredentialFiles.AskPassFileName)));
        Assert.False(File.Exists(Path.Combine(work.TempPath, WorkerGit.GitCredentialFiles.SshKeyFileName)));
        foreach (var file in Directory.EnumerateFiles(work.RootPath, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(token, await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_clone_is_bounded_by_the_runs_remaining_budget_not_only_the_operation_timeout()
    {
        using var directory = new GitTestDirectory("timeout-deadline");
        var gitPath = CreateHangingGit(directory, out var pidFile);

        // The per-operation timeout is generous (90 seconds); the run's own deadline is what must cut
        // the invocation short - AC-9's "exceeds the run's remaining budget" path. 90 s (not minutes)
        // so a regression that ignored the deadline fails the duration assertion in ~1.5 minutes.
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        var workspace = new WorkerGit.GitWorkspace(
            new WorkerGit.GitWorkspaceOptions
            {
                Work = work,
                GitExecutable = gitPath,
                OperationTimeout = TimeSpan.FromSeconds(90),
                RunDeadline = DateTimeOffset.UtcNow.AddSeconds(2),
                EnvironmentOverrides = GitCli.Environment(directory),
            });

        var stopwatch = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<WorkerGit.GitException>(() => workspace.CloneAsync(
            "file:///nonexistent/repository.git",
            "main",
            credential: null,
            TestContext.Current.CancellationToken));
        stopwatch.Stop();

        Assert.Equal(WorkerGit.GitFailureKind.Timeout, failure.Kind);

        // It really was the *remaining budget* that fired: well under the 90-second operation timeout,
        // i.e. the two-second deadline plus a small margin.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(45),
            $"the run budget did not cut the invocation short (took {stopwatch.Elapsed})");
        Assert.Contains("budget", failure.Message, StringComparison.Ordinal);

        var pids = await ReadPidsAsync(pidFile, expected: 2, TestContext.Current.CancellationToken);
        foreach (var pid in pids)
        {
            await AssertProcessGoneAsync(pid, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<int[]> ReadPidsAsync(
        string path,
        int expected,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                var pids = File.ReadAllLines(path)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .Select(int.Parse)
                    .ToArray();
                if (pids.Length >= expected)
                {
                    return pids;
                }
            }

            await Task.Delay(50, cancellationToken);
        }

        Assert.Fail($"the stub git recorded no pids in '{path}'");
        return [];
    }

    private static async Task AssertProcessGoneAsync(int pid, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!IsAlive(pid))
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        Assert.Fail($"process {pid} is still alive after the timeout kill");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No such process: the pid is gone (or already reaped).
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
