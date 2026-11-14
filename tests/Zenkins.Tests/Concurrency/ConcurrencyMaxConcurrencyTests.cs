using Xunit;
using Zenkins.Tests.Milestone;
using Zenkins.Tests.Recovery;

namespace Zenkins.Tests.Concurrency;

/// <summary>
/// Q-005's concurrency-ceiling AC: a worker whose two execution loops each claim their own run is
/// sampled, under real load, for as long as runs are in flight - and never holds more running (or
/// leased) runs than the <c>maxConcurrency</c> it registered and refreshes on every heartbeat. The
/// single-loop workers in the same ring are held to one. The ceiling being <i>exercised</i>, not just
/// trivially satisfied, is asserted too: the two-loop worker really held two runs at once during the
/// window.
/// <para>
/// The claim slot check counts the worker's running runs <i>and</i> its claimed-but-not-yet-accepted
/// leases, so the sampled ceiling is the honest one: between a claim's lease and its accept a run is
/// still <c>Queued</c>, and it occupies a slot exactly then.
/// </para>
/// </summary>
[Trait("Category", "Concurrency")]
public sealed class ConcurrencyMaxConcurrencyTests
{
    [Fact(Timeout = 5 * 60 * 1000)]
    public async Task A_worker_never_holds_more_runs_than_its_declared_maxConcurrency()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await ConcurrencyHost.StartAsync();

        var project = await host.CreateProjectAsync("concurrency-ceiling");
        using var repository = MilestoneRepository.Create(ConcurrencyPipelines.HoldsFor(4_000));
        await host.PointProjectAtAsync(project, repository.FileUrl);

        var duo = await host.StartWorkerAsync("capacity-two", "recovery,ci", 2);
        var single = await host.StartWorkerAsync("capacity-one", "recovery,ci", 1);
        var singleB = await host.StartWorkerAsync("capacity-one-b", "recovery,ci", 1);
        Assert.Equal(3, await host.CountOnlineWorkersAsync());

        var duoRow = await host.ReadConcurrencyWorkerRowAsync(duo.Name);
        Assert.Equal(2, duoRow.MaxConcurrency);
        var singleRow = await host.ReadConcurrencyWorkerRowAsync(single.Name);
        Assert.Equal(1, singleRow.MaxConcurrency);
        var singleBRow = await host.ReadConcurrencyWorkerRowAsync(singleB.Name);
        Assert.Equal(1, singleBRow.MaxConcurrency);

        // Eight runs queued over the ring at once; the workers pick them up while the test samples.
        var runIds = new List<Guid>(8);
        for (var index = 0; index < 8; index++)
        {
            runIds.Add(await host.TriggerTaggedRunAsync(project, requiredTags: string.Empty));
        }

        var remaining = runIds.ToHashSet();
        var maxDuoRunning = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (remaining.Count > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the eight runs never all reached a terminal state.");

            // The ceiling, sampled at every tick: running runs AND claimed-but-unaccepted leases.
            var running = (await host.ReadRunningCountsAsync()).ToDictionary(pair => pair.WorkerId, pair => pair.Count);
            var assigned = (await host.ReadAssignedCountsAsync()).ToDictionary(pair => pair.WorkerId, pair => pair.Count);
            running.TryGetValue(duoRow.Id, out var duoRunning);
            assigned.TryGetValue(duoRow.Id, out var duoAssigned);
            Assert.True(
                duoRunning <= 2,
                $"worker '{duo.Name}' held {duoRunning} running runs - more than its declared maxConcurrency of 2.");
            Assert.True(
                duoAssigned <= 2,
                $"worker '{duo.Name}' holds {duoAssigned} assigned runs (running + leases) - more than 2.");
            maxDuoRunning = Math.Max(maxDuoRunning, duoRunning);

            foreach (var (rowId, rowName) in new[] { (singleRow.Id, single.Name), (singleBRow.Id, singleB.Name) })
            {
                running.TryGetValue(rowId, out var singleRunning);
                assigned.TryGetValue(rowId, out var singleAssigned);
                Assert.True(
                    singleRunning <= 1,
                    $"worker '{rowName}' held {singleRunning} running runs - more than its declared maxConcurrency of 1.");
                Assert.True(
                    singleAssigned <= 1,
                    $"worker '{rowName}' holds {singleAssigned} assigned runs (running + leases) - more than 1.");
            }

            var statuses = await host.ReadRunStatusesAsync(runIds);
            foreach (var (id, status) in statuses)
            {
                if (status is not ("Queued" or "Running"))
                {
                    remaining.Remove(id);
                }
            }

            await Task.Delay(100, cancellationToken);
        }

        // The ceiling was exercised, not skirted: the two-loop worker really held two runs at once.
        Assert.Equal(2, maxDuoRunning);

        // Every run finished in the state its worker reported, and the ring's heartbeats kept
        // reporting the same declared concurrency throughout.
        foreach (var runId in runIds)
        {
            var run = await host.ReadRunAsync(runId);
            Assert.Equal("Success", run.Status);
            Assert.Equal(0, run.ExitCode);
            Assert.Null(run.FailureReason);
            Assert.NotNull(run.WorkerId);
        }

        Assert.Equal(2, (await host.ReadConcurrencyWorkerRowAsync(duo.Name)).MaxConcurrency);
    }
}
