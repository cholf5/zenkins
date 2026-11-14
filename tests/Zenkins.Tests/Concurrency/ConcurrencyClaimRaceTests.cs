using Xunit;
using Zenkins.Tests.Milestone;
using Zenkins.Tests.Recovery;

namespace Zenkins.Tests.Concurrency;

/// <summary>
/// Q-005 TC-003 (AC: the claim race never double-executes): exactly one run is queued while two real
/// worker processes poll the same queue every second. One worker receives it and executes it; the
/// other's claims return no work - its process output never mentions the run. The executed-once
/// property is provable from records: the run holds exactly one step sequence (one row per declared
/// position, no duplicate) and exactly one terminal report, and the run's persisted log names exactly
/// one worker's identity.
/// <para>
/// The poll cycles of two independently started processes are not synchronized, so the race happens
/// wherever their polls land: a poll that reads the run before the winner's lease loses the
/// compare-and-set (204), a poll after it finds nothing queued - the product's answer is no work
/// either way, which is what the test asserts.
/// </para>
/// </summary>
[Trait("Category", "Concurrency")]
public sealed class ConcurrencyClaimRaceTests
{
    [Fact(Timeout = 5 * 60 * 1000)]
    public async Task Two_workers_racing_one_queued_run_execute_it_exactly_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await ConcurrencyHost.StartAsync();

        var project = await host.CreateProjectAsync("concurrency-race");
        using var repository = MilestoneRepository.Create(ConcurrencyPipelines.Identity);
        await host.PointProjectAtAsync(project, repository.FileUrl);

        var first = await host.StartWorkerAsync("race-a", "recovery,ci", 1);
        var second = await host.StartWorkerAsync("race-b", "recovery,ci", 1);
        var workers = new[] { first, second };

        // Both processes were really in the ring, polling the same queue, when the run appeared.
        Assert.Equal(2, await host.CountOnlineWorkersAsync());
        foreach (var worker in workers)
        {
            await host.AssertWorkerRunsAsItsOwnLiveProcessAsync(worker);
        }

        // Exactly one queued run.
        var runId = await host.TriggerTaggedRunAsync(project, requiredTags: string.Empty);
        var run = await host.WaitForTerminalAsync(runId);

        Assert.Equal("Success", run.Status);
        Assert.Equal(0, run.ExitCode);
        Assert.Null(run.FailureReason);

        // Exactly one worker received it; the other's claim got nothing.
        Assert.NotNull(run.WorkerId);
        var rows = new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            [first.Name] = (await host.ReadConcurrencyWorkerRowAsync(first.Name)).Id,
            [second.Name] = (await host.ReadConcurrencyWorkerRowAsync(second.Name)).Id,
        };
        var winner = workers.Single(worker => rows[worker.Name] == run.WorkerId);
        var loser = workers.Single(worker => !ReferenceEquals(worker, winner));
        await host.AssertRunExecutedByExactlyOneWorkerAsync(run, winner, [loser]);

        // The executed-once proof from step records: exactly one step sequence and one terminal
        // report - a second execution would have needed a second lease, and the row shows one.
        await host.AssertRunStepsAreOneUncorruptedSequenceAsync(runId, ConcurrencyPipelines.IdentitySteps);

        // The loser kept polling: its row is still Online, and the queue it now sees is empty -
        // a probe's direct claim reads the endpoint's own 204.
        var loserRow = await host.ReadConcurrencyWorkerRowAsync(loser.Name);
        Assert.Equal("Online", loserRow.Status);
        var probeToken = await host.RegisterTaggedProbeWorkerAsync("race-probe", "recovery,probe");
        using (var probeClaim = await host.ProbeClaimAsync(probeToken))
        {
            RecoveryHost.AssertClaimGotNothing(probeClaim);
        }

        // And the outcome holds over a settle window: the terminal run never leaves its state.
        await host.AssertRunSettlesInAsync(runId, cancellationToken, "Success");
    }
}
