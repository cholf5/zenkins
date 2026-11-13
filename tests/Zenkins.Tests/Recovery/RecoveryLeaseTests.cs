using Xunit;
using Zenkins.Tests.Milestone;

namespace Zenkins.Tests.Recovery;

/// <summary>
/// Q-004 TC-003: <b>a live hold is respected, an expired one is not re-queued</b>. Worker A executes a
/// run and keeps it alive (fresh heartbeats - the hold is "live"); worker B (a real second process)
/// and a probe worker's direct claims get <b>nothing</b> for as long as A holds the run. Then A's
/// process is killed and the hold expires: the real offline monitor marks the run <c>Lost</c> - never
/// re-queued, because re-running someone's build silently would be a lie about what happened - and
/// claims <b>still</b> return nothing.
/// <para>
/// <b>What "lease" means here, and why.</b> F-006's claim lease exists only between a claim and its
/// accepted report (the accept clears it), and its sweeper deliberately requeues a claimed-but-never-
/// accepted run - nothing ever executed, so a requeue is not a retry. The hold whose expiry produces
/// <c>Lost</c> is the worker's liveness (F-010): the sweeper that marks the run Lost is the real
/// <c>WorkerOfflineMonitor</c> over the real <c>Workers:OfflineAfterSeconds</c> threshold, driven by
/// configuration (five seconds here), never by a forced state change. Both halves of the card's
/// sentence hold: not claimable while live, Lost and still not claimable after expiry.
/// </para>
/// <para>
/// The control at the end matters: once new work exists, B really does claim and finish it - B got
/// nothing earlier because nothing was claimable, not because B was broken. The Lost run's settle
/// window is asserted alongside, and the probe's direct 204 answers make "the claim got nothing"
/// first-hand evidence rather than an inference from B's silence.
/// </para>
/// </summary>
[Trait("Category", "Recovery")]
public sealed class RecoveryLeaseTests
{
    [Fact(Timeout = 6 * 60 * 1000)]
    public async Task A_live_hold_blocks_other_claims_and_an_expired_one_is_lost_never_requeued()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await RecoveryHost.StartAsync();

        // Two projects, two repositories: the held run's pipeline streams for 30 seconds; the control
        // run's pipeline finishes in seconds.
        var heldProject = await host.CreateProjectAsync("recovery-lease-held");
        var heldMarker = Path.Combine(host.RootPath, "lease-held-marker.txt");
        using var heldRepository = MilestoneRepository.Create(
            RecoveryPipelines.HoldsInFirstStep(heldMarker, milliseconds: 30_000));
        await host.PointProjectAtAsync(heldProject, heldRepository.FileUrl);

        var controlProject = await host.CreateProjectAsync("recovery-lease-control");
        using var controlRepository = MilestoneRepository.Create(RecoveryPipelines.Fast);
        await host.PointProjectAtAsync(controlProject, controlRepository.FileUrl);

        // Worker A takes the run and holds it, alive.
        var workerA = await host.StartWorkerAsync();
        var heldRunId = await host.TriggerRunAsync(heldProject);
        await host.WaitForStepStatusAsync(heldRunId, 1, "Running");
        var rowA = await host.AssertWorkerRunsAsItsOwnLiveProcessAsync(workerA, heldRunId);

        // A second real worker and a probe worker poll for work. While A holds the run, neither gets
        // anything: B never executes, every direct claim is the endpoint's own 204.
        var workerB = await host.StartWorkerAsync();
        var probeToken = await host.RegisterProbeWorkerAsync("recovery-probe");

        await host.AssertHoldsAsync(async () =>
        {
            var run = await host.ReadRunAsync(heldRunId);
            Assert.Equal("Running", run.Status);
            Assert.Equal(rowA.Id, run.WorkerId);
            using var claim = await host.ProbeClaimAsync(probeToken);
            RecoveryHost.AssertClaimGotNothing(claim);
            Assert.DoesNotContain(
                workerB.Output,
                line => line.Contains("executing in", StringComparison.Ordinal));
            Assert.Single(await host.ReadProjectRunsAsync(heldProject));
        }, TimeSpan.FromSeconds(2.5), cancellationToken);

        // ----- The hold expires: kill A's process and let the real threshold do its work. -----
        host.KillWorker(workerA);
        await RecoveryHost.WaitForProcessGoneAsync(workerA.ProcessId);
        await host.AssertRunHoldsStatusAsync(heldRunId, "Running", null, cancellationToken);
        await host.WaitForRunStatusAsync(heldRunId, "Lost");

        var lost = await host.ReadRunAsync(heldRunId);
        Assert.Equal("Lost", lost.Status);
        Assert.Equal("worker-offline", lost.FailureReason);
        Assert.Null(lost.ExitCode);
        await host.WaitForWorkerStatusAsync(workerA.Name, "Offline", cancellationToken);

        // After expiry: claims still return nothing, and the run is never re-queued.
        await host.AssertHoldsAsync(async () =>
        {
            using var claim = await host.ProbeClaimAsync(probeToken);
            RecoveryHost.AssertClaimGotNothing(claim);
            Assert.DoesNotContain(
                workerB.Output,
                line => line.Contains("executing in", StringComparison.Ordinal));
            var run = await host.ReadRunAsync(heldRunId);
            Assert.Equal("Lost", run.Status);
            Assert.Single(await host.ReadProjectRunsAsync(heldProject));
        }, window: null, cancellationToken);

        // ----- Control: work exists again, and B really can claim it. -----
        var controlRunId = await host.TriggerRunAsync(controlProject);
        var control = await host.WaitForTerminalAsync(controlRunId);
        Assert.Equal("Success", control.Status);
        var rowB = await host.ReadWorkerRowAsync(workerB.Name);
        Assert.Equal("Online", rowB.Status);
        Assert.Equal(rowB.Id, control.WorkerId);
        Assert.Contains(
            workerB.Output,
            line => line.Contains("executing in", StringComparison.Ordinal));

        // With nothing queued, the probe's claim is empty again (and B's row is the one the control
        // run named - the probe never took it).
        using (var probeClaim = await host.ProbeClaimAsync(probeToken))
        {
            RecoveryHost.AssertClaimGotNothing(probeClaim);
        }

        // Settle: the Lost run stays Lost over the window, and each project still holds exactly one run.
        await host.AssertRunSettlesInAsync(heldRunId, cancellationToken, "Lost");
        await host.AssertHoldsAsync(async () =>
        {
            Assert.Single(await host.ReadProjectRunsAsync(heldProject));
            Assert.Single(await host.ReadProjectRunsAsync(controlProject));
        }, window: null, cancellationToken);
    }
}
