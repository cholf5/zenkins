using Xunit;
using Zenkins.Tests.Pipelines;
using Zenkins.Tests.Runs;
using Zenkins.Worker;

namespace Zenkins.Tests.WorkerRuntime;

/// <summary>
/// The worker's claim→execute→report loop (cards F-007 + F-014) against the real F-006 server pipeline
/// (in-process HTTP host): a completed run is followed by the next claim, a pipeline executes its steps
/// in order, a non-zero step exit maps to Failed with that code, and temp cleanup follows the
/// configuration while repository/ and artifacts/ stay.
/// <para>
/// Since F-014 every run is a pipeline run, so each case seeds a project pointing at a real repository
/// fixture whose <c>pipeline.lua</c> declares the steps - the exact shape a user's project has.
/// </para>
/// </summary>
[Trait("Category", "WorkerRuntime")]
public sealed class WorkerRunLoopTests : IDisposable
{
    private static readonly TimeSpan RunCompletionTimeout = TimeSpan.FromSeconds(90);

    private readonly string _workRoot = Path.Combine(
        Path.GetTempPath(), $"zenkins-f007-loop-{Guid.NewGuid():N}");

    [Fact]
    public async Task The_claim_loop_keeps_claiming_after_a_completed_run()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline("loop", PipelineTestData.Step("Echo", "echo", "ok")));
        using var host = await RunTestHost.CreateAsync();
        var projectId = await host.SeedProjectAsync($"loop-{Guid.NewGuid():N}", repository.FileUrl);
        var (token, workerId) = await host.RegisterWorkerAsync(name: $"loop-agent-{Guid.NewGuid():N}", maxConcurrency: 4);
        var run1 = await host.AddRunAsync(projectId);
        var run2 = await host.AddRunAsync(projectId);

        var (loop, stop) = CreateLoop(host, token, cleanupTemp: true);
        var loopTask = loop.RunAsync(stop.Token);
        try
        {
            var finished1 = await WaitUntilTerminalAsync(host, run1);
            var finished2 = await WaitUntilTerminalAsync(host, run2);

            // Both runs were claimed, cloned, defined, executed and reported - the loop kept claiming
            // after the first completed instead of stopping or re-running the same run.
            Assert.Equal("Success", finished1.Status);
            Assert.Equal(0, finished1.ExitCode);
            Assert.Equal(workerId, finished1.WorkerId);
            Assert.NotNull(finished1.StartedAt);
            Assert.NotNull(finished1.EndedAt);
            Assert.NotNull(finished1.GitCommit);
            Assert.NotNull(finished1.PipelineSpecJson);
            Assert.Equal("Success", finished2.Status);
            Assert.Equal(0, finished2.ExitCode);
            Assert.NotEqual(finished1.Id, finished2.Id);

            // The work directory trio existed per run and temp was cleaned (default configuration).
            // Asserted structurally - calling Prepare here would recreate temp and prove nothing.
            foreach (var runId in new[] { run1, run2 })
            {
                var workPath = Path.Combine(_workRoot, runId.ToString("D"));
                Assert.True(Directory.Exists(Path.Combine(workPath, "repository")), $"repository/ missing for {runId}");
                Assert.True(Directory.Exists(Path.Combine(workPath, "artifacts")), $"artifacts/ missing for {runId}");
                Assert.False(Directory.Exists(Path.Combine(workPath, "temp")), $"temp/ survived cleanup for {runId}");
            }
        }
        finally
        {
            await stop.CancelAsync();
            await loopTask.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_non_zero_exit_code_maps_to_Failed_and_the_loop_continues()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "failing",
                PipelineTestData.Step("Restore", "echo", "restore"),
                PipelineTestData.Step("Build", "exit", "3")));
        using var host = await RunTestHost.CreateAsync();
        var projectId = await host.SeedProjectAsync($"loop-fail-{Guid.NewGuid():N}", repository.FileUrl);
        // The trailing run needs a pipeline that succeeds: it proves the loop kept claiming, not that
        // the same failing definition fails twice.
        using var passingRepository = PipelineRepository.Create(
            PipelineTestData.Pipeline("loop-ok", PipelineTestData.Step("Echo", "echo", "ok")));
        var trailingProjectId = await host.SeedProjectAsync(
            $"loop-ok-{Guid.NewGuid():N}", passingRepository.FileUrl);
        var (token, _) = await host.RegisterWorkerAsync(name: $"loop-fail-{Guid.NewGuid():N}", maxConcurrency: 4);
        var runId = await host.AddRunAsync(projectId);
        var trailing = await host.AddRunAsync(trailingProjectId);

        var (loop, stop) = CreateLoop(host, token, cleanupTemp: true);
        var loopTask = loop.RunAsync(stop.Token);
        try
        {
            var failed = await WaitUntilTerminalAsync(host, runId);
            Assert.Equal("Failed", failed.Status);
            Assert.Equal(3, failed.ExitCode);
            Assert.Equal("step-failed", failed.FailureReason);

            // The loop picks the next run up even after a failing one.
            var trailingRun = await WaitUntilTerminalAsync(host, trailing);
            Assert.Equal("Success", trailingRun.Status);
        }
        finally
        {
            await stop.CancelAsync();
            await loopTask.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_run_whose_definition_is_missing_still_records_the_commit_it_checked_out()
    {
        // AC-3, end to end through the real loop: the commit is reported right after the clone, before
        // the definition can fail - so a pipeline-missing run is not left with "Git commit: -".
        using var repository = PipelineRepository.Create(
            PipelineTestData.MarkerPipeline("marker.txt"));
        using var host = await RunTestHost.CreateAsync();
        var projectId = await host.SeedProjectAsync(
            $"loop-missing-{Guid.NewGuid():N}", repository.FileUrl, pipelinePath: "ci/pipeline.lua");
        var (token, _) = await host.RegisterWorkerAsync(name: $"loop-missing-{Guid.NewGuid():N}");
        var runId = await host.AddRunAsync(projectId, pipelinePath: "ci/pipeline.lua");

        var (loop, stop) = CreateLoop(host, token, cleanupTemp: true);
        var loopTask = loop.RunAsync(stop.Token);
        try
        {
            var finished = await WaitUntilTerminalAsync(host, runId);
            Assert.Equal("Failed", finished.Status);
            Assert.Equal("pipeline-missing", finished.FailureReason);
            Assert.Equal(repository.MainTip, finished.GitCommit);
            Assert.Null(finished.PipelineSpecJson);
            Assert.Null(finished.ExitCode);

            // Nothing ran: the marker file never appeared in the run's checkout.
            var marker = Path.Combine(_workRoot, runId.ToString("D"), "repository", "marker.txt");
            Assert.False(File.Exists(marker), "a step ran although the pipeline was missing.");
        }
        finally
        {
            await stop.CancelAsync();
            await loopTask.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Temp_is_kept_when_cleanup_is_disabled()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline("keep", PipelineTestData.Step("Echo", "echo", "kept")));
        using var host = await RunTestHost.CreateAsync();
        var projectId = await host.SeedProjectAsync($"loop-keep-{Guid.NewGuid():N}", repository.FileUrl);
        var (token, _) = await host.RegisterWorkerAsync(name: $"loop-keep-{Guid.NewGuid():N}", maxConcurrency: 4);
        var runId = await host.AddRunAsync(projectId);

        var (loop, stop) = CreateLoop(host, token, cleanupTemp: false);
        var loopTask = loop.RunAsync(stop.Token);
        try
        {
            var finished = await WaitUntilTerminalAsync(host, runId);
            Assert.Equal("Success", finished.Status);

            var work = new WorkDirectoryService(_workRoot).Prepare(runId);
            var stdOutLog = Path.Combine(work.TempPath, WorkerRunLoop.StdOutLogFileName);
            var stdErrorLog = Path.Combine(work.TempPath, WorkerRunLoop.StdErrorLogFileName);
            Assert.True(Directory.Exists(work.TempPath), "temp/ was removed although cleanup is disabled.");
            Assert.True(File.Exists(stdOutLog), "stdout never landed in the temp log file.");
            Assert.True(File.Exists(stdErrorLog), "the stderr log file was not created.");

            // F-014: the run's own step lines are in the captured stdout (the step record this card
            // emits), and so is the step's own output.
            var captured = await File.ReadAllTextAsync(stdOutLog, TestContext.Current.CancellationToken);
            Assert.Contains("zenkins: step 1/1 \"Echo\" started", captured, StringComparison.Ordinal);
            Assert.Contains("zenkins: step 1/1 \"Echo\" exited with code 0", captured, StringComparison.Ordinal);
            Assert.Contains("kept", captured, StringComparison.Ordinal);
        }
        finally
        {
            await stop.CancelAsync();
            await loopTask.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
    }

    // ------------------------------------------------------------------
    // Plumbing
    // ------------------------------------------------------------------

    private (WorkerRunLoop Loop, CancellationTokenSource Stop) CreateLoop(
        RunTestHost host,
        string workerToken,
        bool cleanupTemp)
    {
        Directory.CreateDirectory(_workRoot);
        var configuration = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://localhost", // unused: the client's base address wins
            [WorkerConfiguration.WorkerNameVariable] = "loop-test-agent",
            [WorkerConfiguration.WorkRootVariable] = _workRoot,
            [WorkerConfiguration.CleanupTempVariable] = cleanupTemp ? "true" : "false",
            [WorkerConfiguration.ClaimSecondsVariable] = "0.2",
        }).Configuration
            ?? throw new InvalidOperationException("The loop test configuration did not parse.");

        // A plain HttpClient on the in-memory server pipeline: the loop talks the real HTTP
        // contract with its bearer token, exactly as the process-level worker does. The log
        // stream client is never started here (HasEverConnected stays false), so the loop's
        // drain step cannot wait on a connection this test never opens.
        var api = new WorkerApiClient(host.Inner.Inner.Host.CreateClient());
        var logStreams = new LogStreamClient(
            configuration, workerToken, BackoffScheduler.Default(), Console.Out, Console.Error);
        var loop = new WorkerRunLoop(
            configuration,
            api,
            workerToken,
            new WorkDirectoryService(configuration.WorkRoot),
            logStreams,
            new BackoffScheduler(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2)),
            Console.Out,
            Console.Error);
        return (loop, new CancellationTokenSource());
    }

    private static async Task<Zenkins.Server.Data.Entities.BuildRun> WaitUntilTerminalAsync(
        RunTestHost host,
        Guid runId)
    {
        var deadline = DateTime.UtcNow + RunCompletionTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var run = await host.ReadRunAsync(runId);
            if (run.Status is Zenkins.Server.Data.Entities.BuildRunStatus.Success
                or Zenkins.Server.Data.Entities.BuildRunStatus.Failed)
            {
                return run;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Run {runId} never reached a terminal state.");
    }

    public void Dispose()
    {
        // Q-011's cleanup contract via TestCleanup: the tree holds a cloned repository with
        // read-only pack files (a Windows delete reports UnauthorizedAccessException for them).
        TestCleanup.DeleteTree(_workRoot);
    }
}
