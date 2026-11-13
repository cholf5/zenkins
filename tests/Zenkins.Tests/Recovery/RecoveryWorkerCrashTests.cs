using Xunit;
using Zenkins.Tests.Milestone;

namespace Zenkins.Tests.Recovery;

/// <summary>
/// Q-004 TC-002 (with the restarted-worker half of the card's pinned semantics): a worker process is
/// <b>killed mid-step</b>. The run stays <c>Running</c> until the real offline monitor's threshold
/// expires - the grace period is configuration, compressed to seconds, never faked - and then the
/// monitor marks it <c>Lost</c> with the <c>worker-offline</c> reason, not a step failure. No retry
/// happens; no second execution starts.
/// <para>
/// A worker that comes back (a new process, same name and same persisted credential - the product's
/// real load-persisted-token reconnect path, F-005) does <b>not</b> resume the abandoned run: it
/// claims <b>new</b> work and executes it once, while the Lost run keeps exactly the record it had at
/// the moment it was given up on.
/// </para>
/// <para>
/// Recorded semantics found in the code, pinned here as the contract: the Lost transition touches the
/// <b>run row only</b> (a compare-and-set on <c>Status == Running</c>). The step the worker was in
/// keeps the state the worker last reported - <c>Running</c> - with no exit code: the server never
/// fabricates a Failed/Canceled step for a run it did not see end. The abandoned run's
/// <c>ExitCode</c> stays null, its <c>FailureReason</c> is the offline reason, and its step count
/// never grows.
/// </para>
/// </summary>
[Trait("Category", "Recovery")]
public sealed class RecoveryWorkerCrashTests
{
    [Fact(Timeout = 6 * 60 * 1000)]
    public async Task A_killed_worker_is_lost_after_the_grace_and_a_restarted_worker_claims_new_work()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await RecoveryHost.StartAsync();

        // Two projects, two repositories: the abandoned run's pipeline streams for 30 seconds (and
        // owns its marker file); the control run's pipeline finishes in seconds and touches nothing
        // the abandoned run would leave behind.
        var projectId = await host.CreateProjectAsync("recovery-crash");
        var markerPath = Path.Combine(host.RootPath, "crash-marker.txt");
        using var repository = MilestoneRepository.Create(
            RecoveryPipelines.HoldsInFirstStep(markerPath, milliseconds: 30_000));
        await host.PointProjectAtAsync(projectId, repository.FileUrl);

        var controlProject = await host.CreateProjectAsync("recovery-crash-control");
        using var controlRepository = MilestoneRepository.Create(RecoveryPipelines.Fast);
        await host.PointProjectAtAsync(controlProject, controlRepository.FileUrl);

        var worker = await host.StartWorkerAsync();

        // ----- The run starts and really reaches its first step. -----
        var runId = await host.TriggerRunAsync(projectId);
        await host.WaitForStepStatusAsync(runId, 1, "Running");

        // The card's central claim holds for this worker while it executes: a real, live process of
        // its own, registered as the row this run is assigned to.
        var workerRow = await host.AssertWorkerRunsAsItsOwnLiveProcessAsync(worker, runId);

        // ----- Kill the worker's whole process tree, mid-step. -----
        host.KillWorker(worker);
        await RecoveryHost.WaitForProcessGoneAsync(worker.ProcessId);
        Assert.False(File.Exists(markerPath), "a step ran past the kill: steps 2/3 must never execute.");

        // ----- The grace period: Running does not flip the instant the process died. -----
        await host.AssertRunHoldsStatusAsync(runId, "Running", null, cancellationToken);

        // ----- The real monitor flips the stale worker Offline and marks the run Lost. -----
        await host.WaitForRunStatusAsync(runId, "Lost");
        var lost = await host.ReadRunAsync(runId);
        Assert.Equal("Lost", lost.Status);
        Assert.Equal("worker-offline", lost.FailureReason);
        Assert.Null(lost.ExitCode);
        Assert.Equal(workerRow.Id, lost.WorkerId);
        Assert.NotNull(lost.EndedAt);
        await host.WaitForWorkerStatusAsync(worker.Name, "Offline", cancellationToken);

        // The step the worker was in: exactly what the worker last reported, nothing fabricated.
        var steps = await host.ReadStepsAsync(runId);
        var inFlight = Assert.Single(steps);
        Assert.Equal(1, inFlight.Index);
        Assert.Equal("Step1", inFlight.Name);
        Assert.Equal("Running", inFlight.Status);
        Assert.Null(inFlight.ExitCode);

        // ----- No retry, no second execution, no file-system residue. -----
        await host.AssertRunSettlesInAsync(runId, cancellationToken, "Lost");
        await host.AssertHoldsAsync(async () =>
        {
            Assert.Single(await host.ReadProjectRunsAsync(projectId));
            Assert.Single(await host.ReadStepsAsync(runId));
            Assert.False(File.Exists(markerPath), "a retried or resumed run wrote a step marker.");
        }, window: null, cancellationToken);

        // ----- The worker comes back: same name, same persisted credential, new process. -----
        var restarted = await host.StartWorkerAsync(worker.Name);
        Assert.Contains(
            restarted.Output,
            line => line.Contains("using the persisted worker token", StringComparison.Ordinal));
        var restartedRow = await host.AssertWorkerRunsAsItsOwnLiveProcessAsync(restarted);

        // The restart reused the server's worker row (a reconnect, not a new worker) and its
        // heartbeats flipped the Offline row back Online.
        Assert.Equal(workerRow.Id, restartedRow.Id);
        Assert.Equal("Online", restartedRow.Status);

        // ----- It claims NEW work; the abandoned run is not resumed. -----
        var secondRunId = await host.TriggerRunAsync(controlProject);
        var second = await host.WaitForTerminalAsync(secondRunId);
        Assert.Equal("Success", second.Status);
        Assert.Equal(workerRow.Id, second.WorkerId);
        Assert.All(await host.ReadStepsAsync(secondRunId), step => Assert.Equal("Success", step.Status));

        var abandoned = await host.ReadRunAsync(runId);
        Assert.Equal("Lost", abandoned.Status);
        Assert.Equal("worker-offline", abandoned.FailureReason);
        Assert.Null(abandoned.ExitCode);
        Assert.Single(await host.ReadStepsAsync(runId));
        Assert.False(File.Exists(markerPath), "the restarted worker resumed the abandoned run.");

        // Settle: one run per project is the whole story, and the Lost one stays Lost.
        await host.AssertHoldsAsync(async () =>
        {
            Assert.Single(await host.ReadProjectRunsAsync(projectId));
            Assert.Single(await host.ReadProjectRunsAsync(controlProject));
        }, window: null, cancellationToken);
        await host.AssertRunSettlesInAsync(runId, cancellationToken, "Lost");
    }
}
