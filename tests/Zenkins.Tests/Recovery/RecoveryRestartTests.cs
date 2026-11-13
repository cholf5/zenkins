using Xunit;
using Zenkins.Tests.Milestone;

namespace Zenkins.Tests.Recovery;

/// <summary>
/// Q-004 TC-001: <b>the queue outlives the server</b>. A run triggered with no worker connected sits
/// in <c>Queued</c>; the server process is stopped; a new server process is started over the <b>same
/// database</b>. The run must still be <c>Queued</c> - the queue lives in the rows, not in the
/// process - and then execute <b>exactly once</b> when a worker finally connects: no duplicate run, no
/// lost run, and nothing re-queued or re-executed afterwards.
/// <para>
/// Every claim is observable: the run row is read straight out of the SQLite file while the server is
/// down, the restarted server answers through the same Owner session (the Data Protection key ring is
/// shared state that survives the restart), and "exactly once" is one run row plus one
/// <c>step 1/2 started</c> line in the persisted log (a second execution would write a second one).
/// </para>
/// </summary>
[Trait("Category", "Recovery")]
public sealed class RecoveryRestartTests
{
    [Fact(Timeout = 6 * 60 * 1000)]
    public async Task A_queued_run_survives_a_server_restart_and_executes_exactly_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await RecoveryHost.StartAsync();

        var projectId = await host.CreateProjectAsync("recovery-restart");
        var markerPath = Path.Combine(host.RootPath, "restart-marker.txt");
        using var repository = MilestoneRepository.Create(
            RecoveryPipelines.HoldsInFirstStep(markerPath, milliseconds: 400));
        await host.PointProjectAtAsync(projectId, repository.FileUrl);

        // Deliberately no worker yet: the trigger has nothing that could claim the run.
        Assert.Equal(0, await host.CountOnlineWorkersAsync());
        var runId = await host.TriggerRunAsync(projectId);

        var queued = await host.WaitForAnyRunAsync(projectId);
        Assert.Equal("Queued", queued.Status);
        Assert.Null(queued.WorkerId);
        Assert.Null(queued.ExitCode);
        Assert.Equal(0, await host.CountOnlineWorkersAsync());

        // It really waits: the status never leaves Queued while no worker exists.
        await host.AssertRunHoldsStatusAsync(runId, "Queued", TimeSpan.FromSeconds(1.5), cancellationToken);

        // ----- The server stops. Its pid must really leave the process table. -----
        var serverPid = host.ServerProcessId;
        host.StopServer();
        await RecoveryHost.WaitForProcessGoneAsync(serverPid);

        // While the server is down, the queue is already on disk: the row is read straight out of the
        // SQLite file this host owns (no process holds it), and it is still Queued and unclaimed.
        var downRow = await host.ReadRunAsync(runId);
        Assert.Equal("Queued", downRow.Status);
        Assert.Null(downRow.WorkerId);

        // ----- A new server process over the SAME database. -----
        await host.StartServerAgainAsync();

        var afterRestart = await host.ReadRunAsync(runId);
        Assert.Equal("Queued", afterRestart.Status);
        Assert.Null(afterRestart.WorkerId);
        Assert.Null(afterRestart.ExitCode);
        Assert.Null(afterRestart.FailureReason);
        Assert.Single(await host.ReadProjectRunsAsync(projectId));

        // ----- A worker connects; the queued run executes, exactly once. -----
        var worker = await host.StartWorkerAsync();
        var workerRow = await host.SampleWorkerDuringRunAsync(worker, runId);

        var terminal = await host.WaitForTerminalAsync(runId);
        Assert.Equal("Success", terminal.Status);
        Assert.Equal(workerRow.Id, terminal.WorkerId);
        Assert.Equal(0, terminal.ExitCode);
        Assert.Null(terminal.FailureReason);
        Assert.NotNull(terminal.EndedAt);

        var steps = await host.ReadStepsAsync(runId);
        Assert.Equal(new[] { "Step1", "Step2", "Step3" }, steps.Select(step => step.Name));
        Assert.All(steps, step =>
        {
            Assert.Equal("Success", step.Status);
            Assert.Equal(0, step.ExitCode);
        });

        // Exactly one execution: one run row for the project, one "step 1/3 started" line in the
        // persisted log - a second execution would write a second one.
        Assert.Single(await host.ReadProjectRunsAsync(projectId));
        var log = await host.ReadLogLinesAsync(runId);
        Assert.Single(log, line => line.Message.StartsWith(
            "zenkins: step 1/3 \"Step1\" started",
            StringComparison.Ordinal));

        // The fast step held only 400 ms, so the marker step really ran; the marker is the file-system
        // counterpart of the rows.
        Assert.Equal(new[] { "Step2", "Step3" }, File.ReadAllLines(markerPath));

        // Settle window: nothing re-queues the finished run and nothing duplicates it.
        await host.AssertRunSettlesInAsync(runId, cancellationToken, "Success");
        await host.AssertHoldsAsync(
            async () => Assert.Single(await host.ReadProjectRunsAsync(projectId)),
            window: null,
            cancellationToken);
    }
}
