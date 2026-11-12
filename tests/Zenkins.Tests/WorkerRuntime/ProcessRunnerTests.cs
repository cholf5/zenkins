using Xunit;
using Zenkins.Worker;

namespace Zenkins.Tests.WorkerRuntime;

/// <summary>
/// Card F-007's process runner, exercised through real child processes (the ArgvEcho test asset):
/// exact argv fidelity with no shell, per-stream ordered line streaming that starts before the
/// process exits, exit-code capture, a whole-tree kill, dispose-safe reaping, the Zenkins child
/// environment, and per-run work-directory isolation under concurrency.
/// </summary>
[Trait("Category", "WorkerRuntime")]
public sealed class ProcessRunnerTests
{
    // ------------------------------------------------------------------
    // TC-002: argv fidelity
    // ------------------------------------------------------------------

    [Fact]
    public async Task Arguments_reach_the_child_verbatim_with_no_shell_involved()
    {
        var collector = new OutputCollector();
        var trickyArguments = new[]
        {
            "hello world",
            "say \"hi\"",
            "$HOME ${NOT_SET}",
            "semi;colon",
            "and && list",
            "-leading-dash",
            "",
        };
        using var runner = new ProcessRunner(
            new ProcessRunnerOptions
            {
                FileName = ArgvEchoBinary.DotNetPath,
                Arguments = ArgvEchoBinary.Command(["echo", .. trickyArguments]),
            },
            collector.Handler);

        var exitCode = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(trickyArguments, collector.StdOutLines());
        Assert.Empty(collector.StdErrorLines());
    }

    // ------------------------------------------------------------------
    // Exit codes
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_exit_code_is_captured_verbatim()
    {
        using var runner = new ProcessRunner(
            new ProcessRunnerOptions
            {
                FileName = ArgvEchoBinary.DotNetPath,
                Arguments = ArgvEchoBinary.Command("exit", "3"),
            });

        var exitCode = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, exitCode);
    }

    [Fact]
    public async Task A_command_that_cannot_start_surfaces_the_failure()
    {
        using var runner = new ProcessRunner(
            new ProcessRunnerOptions
            {
                FileName = $"no-such-zenkins-binary-{Guid.NewGuid():N}",
            });

        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(
            () => runner.RunAsync(TestContext.Current.CancellationToken));
    }

    // ------------------------------------------------------------------
    // TC-003: streaming before exit, per-stream order
    // ------------------------------------------------------------------

    [Fact]
    public async Task Output_lines_arrive_before_the_process_exits_and_in_per_stream_order()
    {
        var collector = new OutputCollector();
        var firstStdOutObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runner = new ProcessRunner(
            new ProcessRunnerOptions
            {
                FileName = ArgvEchoBinary.DotNetPath,
                Arguments = ArgvEchoBinary.Command("write-slow", "2500", "the-stderr-line"),
            },
            line =>
            {
                collector.Handler(line);
                if (line.Stream == ProcessOutputStream.StdOut && line.Line == "stream-first")
                {
                    firstStdOutObserved.TrySetResult();
                }
                else if (line.Stream == ProcessOutputStream.StdError)
                {
                    stderrObserved.TrySetResult();
                }
            });

        var run = runner.RunAsync(TestContext.Current.CancellationToken);

        // The first line must be delivered while the process is still running - the runner
        // streams, it does not wait for exit to hand over output.
        await firstStdOutObserved.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted, "the run completed before the first line was observed");
        Assert.False(runner.HasExited, "the process exited before the first line was observed");

        // stderr arrives independently (its own stream, its own order).
        await stderrObserved.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.Equal(0, await run);
        Assert.Equal(["stream-first", "stream-second"], collector.StdOutLines());
        Assert.Equal(["the-stderr-line"], collector.StdErrorLines());
    }

    // ------------------------------------------------------------------
    // TC-004: kill-tree
    // ------------------------------------------------------------------

    [Fact]
    public async Task Cancelling_the_run_kills_the_whole_process_tree()
    {
        var collector = new OutputCollector();
        using var runner = new ProcessRunner(
            new ProcessRunnerOptions
            {
                FileName = ArgvEchoBinary.DotNetPath,
                Arguments = ArgvEchoBinary.Command("spawn", "60000"),
            },
            collector.Handler);
        using var cancellation = new CancellationTokenSource();

        var run = runner.RunAsync(cancellation.Token);
        var grandchildPid = ParseGrandchildPid(
            (await collector.WaitForFirstLineAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)).Line);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(runner.HasExited, "the child survived the cancellation");
        Assert.True(
            await WaitForProcessExitAsync(grandchildPid, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken),
            "the grandchild survived the tree kill (orphan)");
    }

    [Fact]
    public async Task KillTree_directly_also_takes_down_the_tree()
    {
        var collector = new OutputCollector();
        using var runner = new ProcessRunner(
            new ProcessRunnerOptions
            {
                FileName = ArgvEchoBinary.DotNetPath,
                Arguments = ArgvEchoBinary.Command("spawn", "60000"),
            },
            collector.Handler);
        using var cancellation = new CancellationTokenSource();

        var run = runner.RunAsync(cancellation.Token);
        var grandchildPid = ParseGrandchildPid(
            (await collector.WaitForFirstLineAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)).Line);

        runner.KillTree();

        // The killed child's exit code is the platform's kill code (e.g. 137 on POSIX); the run
        // must simply complete - what matters is that the whole tree is gone.
        await run.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.True(
            await WaitForProcessExitAsync(grandchildPid, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken),
            "the grandchild survived the direct tree kill (orphan)");
    }

    // ------------------------------------------------------------------
    // Dispose safety
    // ------------------------------------------------------------------

    [Fact]
    public async Task Disposing_an_abandoned_runner_kills_and_reaps_the_process()
    {
        var collector = new OutputCollector();
        var firstLineObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ProcessRunner(
            new ProcessRunnerOptions
            {
                FileName = ArgvEchoBinary.DotNetPath,
                Arguments = ArgvEchoBinary.Command("stream", "60000"),
            },
            line =>
            {
                collector.Handler(line);
                if (line.Stream == ProcessOutputStream.StdOut && line.Line == "stream-first")
                {
                    firstLineObserved.TrySetResult();
                }
            });

        // Deliberately not awaited to completion: the consumer abandons the run after it starts.
        _ = runner.RunAsync(CancellationToken.None);
        await firstLineObserved.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.False(runner.HasExited, "the child should still be running before the dispose");

        runner.Dispose();

        Assert.True(
            await WaitForConditionAsync(() => runner.HasExited, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken),
            "the abandoned process survived Dispose (zombie)");
    }

    // ------------------------------------------------------------------
    // Child environment
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_child_receives_the_zenkins_environment_and_inherits_the_rest()
    {
        var collector = new OutputCollector();
        using var runner = new ProcessRunner(
            new ProcessRunnerOptions
            {
                FileName = ArgvEchoBinary.DotNetPath,
                Arguments = ArgvEchoBinary.Command(
                    "env",
                    "ZENKINS_RUN_ID",
                    "ZENKINS_WORKER_NAME",
                    "ZENKINS_PROJECT_ID",
                    "PATH"),
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["ZENKINS_RUN_ID"] = "0197c0de-0000-0000-0000-000000000001",
                    ["ZENKINS_WORKER_NAME"] = "env-agent",
                    ["ZENKINS_PROJECT_ID"] = "0197c0de-0000-0000-0000-000000000002",
                },
            },
            collector.Handler);

        var exitCode = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        var lines = collector.StdOutLines();
        Assert.Equal(4, lines.Length);
        Assert.Equal("0197c0de-0000-0000-0000-000000000001", lines[0]);
        Assert.Equal("env-agent", lines[1]);
        Assert.Equal("0197c0de-0000-0000-0000-000000000002", lines[2]);
        // The rest of the environment is inherited: the child sees the parent's PATH.
        Assert.False(string.IsNullOrEmpty(lines[3]), "the child did not inherit the parent's PATH");
    }

    // ------------------------------------------------------------------
    // TC-001: directory isolation under concurrency
    // ------------------------------------------------------------------

    [Fact]
    public async Task Two_concurrent_runs_in_distinct_work_directories_never_share_writable_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"zenkins-f007-isolation-{Guid.NewGuid():N}");
        var workDirectories = new WorkDirectoryService(root);
        try
        {
            var runA = Guid.NewGuid();
            var runB = Guid.NewGuid();
            var workA = workDirectories.Prepare(runA);
            var workB = workDirectories.Prepare(runB);

            // Two runs execute concurrently, each writing a probe file named by its run id into
            // its own repository/ cwd (the command's working directory).
            var exitA = RunProbeAsync(workA, runA);
            var exitB = RunProbeAsync(workB, runB);
            Assert.Equal(0, await exitA);
            Assert.Equal(0, await exitB);

            // Neither run observes the other's files: each repository/ contains exactly its own probe.
            var probeA = $"probe-{runA:N}.txt";
            var probeB = $"probe-{runB:N}.txt";
            Assert.Equal([probeA], Directory.GetFiles(workA.RepositoryPath).Select(Path.GetFileName).ToArray());
            Assert.Equal([probeB], Directory.GetFiles(workB.RepositoryPath).Select(Path.GetFileName).ToArray());
            Assert.Equal($"probe content {runA:N}", File.ReadAllText(Path.Combine(workA.RepositoryPath, probeA)).TrimEnd());
            Assert.Equal($"probe content {runB:N}", File.ReadAllText(Path.Combine(workB.RepositoryPath, probeB)).TrimEnd());
            Assert.NotEqual(Path.GetFullPath(workA.RootPath), Path.GetFullPath(workB.RootPath));
        }
        finally
        {
            TryDelete(root);
        }

        async Task<int> RunProbeAsync(RunWorkDirectory work, Guid runId)
        {
            using var runner = new ProcessRunner(
                new ProcessRunnerOptions
                {
                    FileName = ArgvEchoBinary.DotNetPath,
                    Arguments = ArgvEchoBinary.Command("probe", $"probe-{runId:N}.txt", $"probe content {runId:N}"),
                    WorkingDirectory = work.RepositoryPath,
                });
            return await runner.RunAsync(TestContext.Current.CancellationToken);
        }
    }

    // ------------------------------------------------------------------
    // Plumbing
    // ------------------------------------------------------------------

    private static int ParseGrandchildPid(string line)
    {
        Assert.StartsWith("grandchild-pid ", line, StringComparison.Ordinal);
        return int.Parse(line["grandchild-pid ".Length..], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> WaitForProcessExitAsync(int pid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return await WaitForConditionAsync(
            () =>
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(pid);
                    return process.HasExited;
                }
                catch (ArgumentException)
                {
                    // No such process: already gone (and reaped by whoever owns it).
                    return true;
                }
            },
            timeout,
            cancellationToken);
    }

    private static async Task<bool> WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100, cancellationToken);
        }

        return condition();
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
