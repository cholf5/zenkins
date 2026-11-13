using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;
using Zenkins.Server.Workers;
using Zenkins.Shared.RunCancellation;
using Zenkins.Tests.Runs;
using Zenkins.Tests.Workers;

namespace Zenkins.Tests.RunLifecycle;

/// <summary>
/// F-010's heartbeat-staleness monitor: a worker past the threshold goes Offline exactly once and
/// its Running runs become Lost exactly once (reason <c>worker-offline</c>, result unknown); a
/// single missed heartbeat does not flap; Lost runs are never resurrected or retried; Offline
/// workers get no claims; a claimed-but-unaccepted lease is the lease sweeper's business, never a
/// Lost marking.
/// </summary>
[Trait("Category", "RunLifecycle")]
public sealed class RunOfflineLostTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_worker_past_the_offline_threshold_is_marked_offline_and_its_running_runs_lost_exactly_once()
    {
        using var host = await RunLifecycleHost.CreateAsync(
            configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { [WorkerMonitorOptions.OfflineAfterSecondsConfigurationKey] = "2", [WorkerMonitorOptions.MonitorIntervalSecondsConfigurationKey] = "3600" }));
        var (projectId, _) = await host.CreateProjectAsync();
        var (token, workerId) = await host.RegisterWorkerAsync(name: "stale-agent", maxConcurrency: 4);
        var runId = await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, token);
        await RunTestHost.AcceptAsync(client, token, runId);

        // Inside the threshold: nothing happens (no flapping on one missed beat).
        await host.BackdateHeartbeatAsync(workerId, TimeSpan.FromSeconds(1));
        var early = await host.SweepOfflineAsync();
        Assert.Equal(0, early.WorkersOfflined);
        Assert.Equal(0, early.RunsLost);
        Assert.Equal(WorkerStatus.Online, (await host.ReadWorkerAsync(workerId)).Status);

        // Past the threshold: two monitor ticks race - exactly one may win the worker's
        // Online → Offline flip, and exactly one Lost transition may follow.
        await host.BackdateHeartbeatAsync(workerId, TimeSpan.FromSeconds(5));
        var sweeps = await Task.WhenAll(host.SweepOfflineAsync(), host.SweepOfflineAsync());
        Assert.Equal(1, sweeps.Sum(sweep => sweep.WorkersOfflined));
        Assert.Equal(1, sweeps.Sum(sweep => sweep.RunsLost));

        var worker = await host.ReadWorkerAsync(workerId);
        Assert.Equal(WorkerStatus.Offline, worker.Status);

        var lost = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Lost, lost.Status);
        Assert.Equal(RunFailureReasons.WorkerOffline, lost.FailureReason);
        Assert.NotNull(lost.EndedAt);
        Assert.Null(lost.ExitCode);

        // A late sequential tick must not re-mark, overwrite or resurrect anything.
        await Task.Delay(50, CancellationToken);
        var late = await host.SweepOfflineAsync();
        Assert.Equal(0, late.WorkersOfflined);
        Assert.Equal(0, late.RunsLost);
        var afterLate = await host.ReadRunAsync(runId);
        Assert.Equal(lost.EndedAt, afterLate.EndedAt);
        Assert.Equal(lost.FailureReason, afterLate.FailureReason);
    }

    /// <summary>The stale-snapshot pin (and the documented red-proof vehicle): the offline flip's
    /// compare-and-set demands the heartbeat its caller observed still be the row's current one.
    /// A real heartbeat that lands between the sweep's snapshot read and the flip must leave zero
    /// rows - the pre-fix WHERE (<c>Status == Online</c> only) flipped the worker Offline (and its
    /// runs Lost) on evidence that was no longer true.</summary>
    [Fact]
    public async Task A_fresh_heartbeat_after_the_stale_snapshot_refuses_the_offline_flip()
    {
        using var host = await RunLifecycleHost.CreateAsync(
            configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { [WorkerMonitorOptions.OfflineAfterSecondsConfigurationKey] = "2", [WorkerMonitorOptions.MonitorIntervalSecondsConfigurationKey] = "3600" }));
        var (projectId, _) = await host.CreateProjectAsync();
        var (token, workerId) = await host.RegisterWorkerAsync(name: "fresh-beat-agent");
        var runId = await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, token);
        await RunTestHost.AcceptAsync(client, token, runId);

        // The sweep's snapshot view: a heartbeat backdated past the threshold.
        var staleHeartbeat = await host.BackdateHeartbeatAsync(workerId, TimeSpan.FromSeconds(5));

        // The race's second half: a real heartbeat lands before the sweep's compare-and-set.
        await WorkerTestHost.HeartbeatAsync(client, token, new
        {
            os = "Linux",
            architecture = "X64",
            version = "1.0.0",
            tags = "ci",
            cpuCount = 8,
            maxConcurrency = 2,
        });
        var currentHeartbeat = (await host.ReadWorkerAsync(workerId)).LastHeartbeatAt!.Value;
        Assert.NotEqual(staleHeartbeat, currentHeartbeat);

        // The flip with the stale observation must refuse the row: the worker stays Online and
        // its run stays untouched (this tick skips it; the next tick re-evaluates).
        var (refused, workerAfterRefusal, runAfterRefusal) = await host.InSystemScopeAsync(async context =>
        {
            var rows = await context.TryMarkWorkerOfflineAsync(
                workerId,
                staleHeartbeat,
                DateTimeOffset.UtcNow);
            var worker = await context.Workers.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(candidate => candidate.Id == workerId);
            var run = await context.BuildRuns.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(candidate => candidate.Id == runId);
            return (rows, worker.Status, run.Status);
        });
        Assert.Equal(0, refused);
        Assert.Equal(WorkerStatus.Online, workerAfterRefusal);
        Assert.Equal(BuildRunStatus.Running, runAfterRefusal);

        // The flip with the current observation still wins (the sweep's normal path) - exactly
        // once, and only then.
        var (flipped, workerAfterFlip) = await host.InSystemScopeAsync(async context =>
        {
            var rows = await context.TryMarkWorkerOfflineAsync(
                workerId,
                currentHeartbeat,
                DateTimeOffset.UtcNow);
            var worker = await context.Workers.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(candidate => candidate.Id == workerId);
            return (rows, worker.Status);
        });
        Assert.Equal(1, flipped);
        Assert.Equal(WorkerStatus.Offline, workerAfterFlip);
    }

    [Fact]
    public async Task A_heartbeat_before_the_sweep_keeps_the_worker_online_while_a_silent_one_still_flips()
    {
        // The honest path around the snapshot race, through the real sweep seam: the worker whose
        // beat landed after the backdating (so before the sweep read it) stays Online and its run
        // keeps running; the silent worker beside it flips and loses its run exactly as before.
        using var host = await RunLifecycleHost.CreateAsync(
            configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { [WorkerMonitorOptions.OfflineAfterSecondsConfigurationKey] = "2", [WorkerMonitorOptions.MonitorIntervalSecondsConfigurationKey] = "3600" }));
        var (projectId, _) = await host.CreateProjectAsync();
        var (tokenA, workerAId) = await host.RegisterWorkerAsync(name: "beating-agent");
        var (tokenB, workerBId) = await host.RegisterWorkerAsync(name: "silent-agent");
        await host.AddRunAsync(projectId);
        await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        Guid runAId, runBId;
        using (var claimA = await RunTestHost.ClaimAsync(client, tokenA))
        {
            runAId = await RunTestHost.ReadClaimedRunIdAsync(claimA);
        }

        await RunTestHost.AcceptAsync(client, tokenA, runAId);
        using (var claimB = await RunTestHost.ClaimAsync(client, tokenB))
        {
            runBId = await RunTestHost.ReadClaimedRunIdAsync(claimB);
        }

        await RunTestHost.AcceptAsync(client, tokenB, runBId);

        // Both stale in what a pre-beat snapshot would hold; only A sends a real heartbeat before
        // the sweep runs.
        await host.BackdateHeartbeatAsync(workerAId, TimeSpan.FromSeconds(5));
        await host.BackdateHeartbeatAsync(workerBId, TimeSpan.FromSeconds(5));
        await WorkerTestHost.HeartbeatAsync(client, tokenA, new
        {
            os = "Linux",
            architecture = "X64",
            version = "1.0.0",
            tags = "ci",
            cpuCount = 8,
            maxConcurrency = 2,
        });

        var sweep = await host.SweepOfflineAsync();
        Assert.Equal(1, sweep.WorkersOfflined);
        Assert.Equal(1, sweep.RunsLost);

        // The beating worker: still Online, its run still Running - nothing fabricated.
        Assert.Equal(WorkerStatus.Online, (await host.ReadWorkerAsync(workerAId)).Status);
        Assert.Equal(BuildRunStatus.Running, (await host.ReadRunAsync(runAId)).Status);

        // The silent one: the ordinary Offline + Lost(worker-offline) transition, unchanged.
        Assert.Equal(WorkerStatus.Offline, (await host.ReadWorkerAsync(workerBId)).Status);
        var lost = await host.ReadRunAsync(runBId);
        Assert.Equal(BuildRunStatus.Lost, lost.Status);
        Assert.Equal(RunFailureReasons.WorkerOffline, lost.FailureReason);
        Assert.NotNull(lost.EndedAt);
    }

    /// <summary>The exactly-once pin (and the documented red-proof vehicle): the termination
    /// compare-and-set refuses a run that already left Running. Stripping the
    /// <c>Status == Running</c> condition from <c>TryTerminateRunningBuildRunAsync</c> turns this
    /// red (the second call re-terminates the Lost run and moves its EndedAt).</summary>
    [Fact]
    public async Task The_termination_compare_and_set_refuses_a_run_that_already_left_running()
    {
        using var host = await RunLifecycleHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync();
        var (token, _) = await host.RegisterWorkerAsync(name: "terminate-pin-agent");
        var runId = await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, token);
        await RunTestHost.AcceptAsync(client, token, runId);

        var (first, second, endedAt) = await host.TerminateRunningViaCasTwiceAsync(
            runId,
            BuildRunStatus.Lost,
            RunFailureReasons.WorkerOffline);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(BuildRunStatus.Lost, (await host.ReadRunAsync(runId)).Status);
        Assert.NotNull(endedAt);
    }

    [Fact]
    public async Task A_single_missed_heartbeat_does_not_flip_the_worker_offline()
    {
        // Production threshold: 60 s against a 15 s heartbeat interval - one missed beat (last
        // beat ~30 s old) must stay Online. Shortened but proportionally the same here.
        using var host = await RunLifecycleHost.CreateAsync(
            configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { [WorkerMonitorOptions.OfflineAfterSecondsConfigurationKey] = "60" }));
        var (projectId, _) = await host.CreateProjectAsync();
        var (token, workerId) = await host.RegisterWorkerAsync(name: "flap-agent");
        var runId = await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, token);
        await RunTestHost.AcceptAsync(client, token, runId);

        // Two beats missed (30 s of a 60 s threshold), then a beat inside the threshold.
        await host.BackdateHeartbeatAsync(workerId, TimeSpan.FromSeconds(30));
        Assert.Equal(0, (await host.SweepOfflineAsync()).WorkersOfflined);
        await host.BackdateHeartbeatAsync(workerId, TimeSpan.FromSeconds(1));
        Assert.Equal(0, (await host.SweepOfflineAsync()).WorkersOfflined);

        Assert.Equal(WorkerStatus.Online, (await host.ReadWorkerAsync(workerId)).Status);
        Assert.Equal(BuildRunStatus.Running, (await host.ReadRunAsync(runId)).Status);
    }

    [Fact]
    public async Task Lost_runs_are_never_resurrected_or_retried()
    {
        using var host = await RunLifecycleHost.CreateAsync(
            configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { [WorkerMonitorOptions.OfflineAfterSecondsConfigurationKey] = "2", [WorkerMonitorOptions.MonitorIntervalSecondsConfigurationKey] = "3600" }));
        var (projectId, _) = await host.CreateProjectAsync();
        var (token, workerId) = await host.RegisterWorkerAsync(name: "resurrect-agent");
        var runId = await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, token);
        await RunTestHost.AcceptAsync(client, token, runId);

        await host.BackdateHeartbeatAsync(workerId, TimeSpan.FromSeconds(5));
        Assert.Equal(1, (await host.SweepOfflineAsync()).RunsLost);
        var lost = await host.ReadRunAsync(runId);

        // The worker returns: its heartbeat flips it back Online. The Lost run is untouched -
        // same status, same reason, same end time, still attributed to its worker.
        await WorkerTestHost.HeartbeatAsync(client, token, new
        {
            os = "Linux",
            architecture = "X64",
            version = "1.0.0",
            tags = "ci",
            cpuCount = 8,
            maxConcurrency = 2,
        });
        Assert.Equal(WorkerStatus.Online, (await host.ReadWorkerAsync(workerId)).Status);

        using var claim = await RunTestHost.ClaimAsync(client, token);
        Assert.Equal(HttpStatusCode.NoContent, claim.StatusCode);

        var after = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Lost, after.Status);
        Assert.Equal(lost.FailureReason, after.FailureReason);
        Assert.Equal(lost.EndedAt, after.EndedAt);
        Assert.Equal(workerId, after.WorkerId);
    }

    [Fact]
    public async Task Offline_workers_are_excluded_from_claims_until_they_recover()
    {
        using var host = await RunLifecycleHost.CreateAsync(
            configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { [WorkerMonitorOptions.OfflineAfterSecondsConfigurationKey] = "2", [WorkerMonitorOptions.MonitorIntervalSecondsConfigurationKey] = "3600" }));
        var (projectId, _) = await host.CreateProjectAsync();
        var (token, workerId) = await host.RegisterWorkerAsync(name: "claim-gate-agent");
        var runId = await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        await host.BackdateHeartbeatAsync(workerId, TimeSpan.FromSeconds(5));
        Assert.Equal(1, (await host.SweepOfflineAsync()).WorkersOfflined);

        // Offline: the claim's eligibility check returns nothing (204) - the run stays queued.
        using (var claim = await RunTestHost.ClaimAsync(client, token))
        {
            Assert.Equal(HttpStatusCode.NoContent, claim.StatusCode);
        }

        Assert.Equal(BuildRunStatus.Queued, (await host.ReadRunAsync(runId)).Status);

        // Recovery: a real heartbeat flips the worker Online and the same run is handed out.
        await WorkerTestHost.HeartbeatAsync(client, token, new
        {
            os = "Linux",
            architecture = "X64",
            version = "1.0.0",
            tags = "ci",
            cpuCount = 8,
            maxConcurrency = 2,
        });
        using (var recovered = await RunTestHost.ClaimAsync(client, token))
        {
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(recovered));
        }
    }

    [Fact]
    public async Task The_lease_of_an_offline_worker_is_requeued_and_never_lost()
    {
        using var host = await RunLifecycleHost.CreateAsync(
            configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { [WorkerMonitorOptions.OfflineAfterSecondsConfigurationKey] = "2", [WorkerMonitorOptions.MonitorIntervalSecondsConfigurationKey] = "3600" }));
        var (projectId, _) = await host.CreateProjectAsync();
        var (tokenA, workerAId) = await host.RegisterWorkerAsync(name: "lease-agent-a");
        var (tokenB, _) = await host.RegisterWorkerAsync(name: "lease-agent-b");
        var runId = await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, tokenA); // leased, never accepted

        // The worker goes silent while its lease is live: the offline sweep must not touch the
        // run (claimed-but-unaccepted is the lease sweeper's business - it never executed).
        await host.BackdateHeartbeatAsync(workerAId, TimeSpan.FromSeconds(5));
        var sweep = await host.SweepOfflineAsync();
        Assert.Equal(1, sweep.WorkersOfflined);
        Assert.Equal(0, sweep.RunsLost);

        var leased = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Queued, leased.Status);
        Assert.Equal(workerAId, leased.WorkerId);
        Assert.NotNull(leased.LeaseExpiresAt);

        // The lease sweeper (its single expiry authority) returns it to the queue once the lease
        // window passed, and another worker claims it.
        await host.Inner.ExpireLeaseAsync(runId);
        Assert.Equal(1, await host.Inner.SweepAsync());
        var requeued = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Queued, requeued.Status);
        Assert.Null(requeued.WorkerId);
        Assert.Null(requeued.LeaseExpiresAt);

        using var claim = await RunTestHost.ClaimAsync(client, tokenB);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(claim));
    }
}
