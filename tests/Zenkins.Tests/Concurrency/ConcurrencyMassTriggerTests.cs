using Xunit;
using Zenkins.Tests.Milestone;
using Zenkins.Tests.Recovery;

namespace Zenkins.Tests.Concurrency;

/// <summary>
/// Q-005 TC-001 (AC: mass trigger, claim exactly once, terminal states, attributable rows): three
/// real worker processes are online at the same time, twelve runs are triggered over one queue, and
/// every run is claimed exactly once - executed by exactly one worker, ending in the terminal state
/// its worker reported. No run is ever executed twice, and that is proven from records, not from the
/// absence of errors: each run's row names one worker, that worker's own process output claims the
/// execution, no other worker's output ever mentions the run, and the run's persisted log carries
/// exactly its executing worker's identity (the pipeline prints <c>ZENKINS_WORKER_NAME</c>, which the
/// worker injects into every step's environment).
/// <para>
/// The step records close AC's attribution clause: each run holds one uncorrupted step sequence -
/// the declared positions, one row each, in order - and every log line is attributed to a step row of
/// its own run, so concurrent workers never interleave into each other's step sequence.
/// </para>
/// </summary>
[Trait("Category", "Concurrency")]
public sealed class ConcurrencyMassTriggerTests
{
    [Fact(Timeout = 6 * 60 * 1000)]
    public async Task Twelve_runs_across_three_workers_are_claimed_and_executed_exactly_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await ConcurrencyHost.StartAsync();

        var project = await host.CreateProjectAsync("concurrency-mass");
        using var repository = MilestoneRepository.Create(ConcurrencyPipelines.Identity);
        await host.PointProjectAtAsync(project, repository.FileUrl);

        // Three real worker processes in the ring at the same time (each its own pid, not the test's,
        // not the server's; each row Online with the tags it registered).
        var workers = new List<RecoveryWorker>
        {
            await host.StartWorkerAsync("concurrency-a", "recovery,ci", 1),
            await host.StartWorkerAsync("concurrency-b", "recovery,ci", 1),
            await host.StartWorkerAsync("concurrency-c", "recovery,ci", 1),
        };
        Assert.Equal(3, await host.CountOnlineWorkersAsync());
        foreach (var worker in workers)
        {
            await host.AssertWorkerRunsAsItsOwnLiveProcessAsync(worker);
        }

        // Twelve runs triggered over the one shared queue, while all three workers poll it.
        var runIds = new List<Guid>(12);
        for (var index = 0; index < 12; index++)
        {
            runIds.Add(await host.TriggerTaggedRunAsync(project, requiredTags: string.Empty));
        }

        Assert.Equal(12, runIds.Distinct().Count());
        var runs = new List<MilestoneRun>(12);
        foreach (var runId in runIds)
        {
            runs.Add(await host.WaitForTerminalAsync(runId));
        }

        // Every run ends in the terminal state its worker reported: exit 0, no failure reason,
        // and every run was claimed (a WorkerId stamped) - no run lost in the queue. A failure names
        // the runs that did not and attaches the ring's own output (Q-020: `Lost` alone was not
        // diagnosable from the CI log).
        var unexpected = runs
            .Where(run => run.Status != "Success"
                || run.ExitCode != 0
                || run.FailureReason is not null
                || run.WorkerId is null)
            .ToArray();
        Assert.True(
            unexpected.Length == 0,
            $"{unexpected.Length} of {runs.Count} runs did not end in the state their worker reported: "
            + string.Join(
                "; ",
                unexpected.Select(run => $"{run.Id:D} ended {run.Status} (exit {run.ExitCode}, "
                    + $"reason '{run.FailureReason}', worker {run.WorkerId?.ToString("D") ?? "none"})"))
            + Environment.NewLine
            + await host.FormatWorkerTailsAsync(workers));

        // Exactly once, per run, provable from records: row, process output and persisted log.
        var rows = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var worker in workers)
        {
            rows[worker.Name] = (await host.ReadConcurrencyWorkerRowAsync(worker.Name)).Id;
        }

        foreach (var run in runs)
        {
            var owner = workers.Single(worker => rows[worker.Name] == run.WorkerId);
            var others = workers.Where(worker => !ReferenceEquals(worker, owner)).ToArray();
            await host.AssertRunExecutedByExactlyOneWorkerAsync(run, owner, others);
            await host.AssertRunStepsAreOneUncorruptedSequenceAsync(run.Id, ConcurrencyPipelines.IdentitySteps);
        }

        // The ring really shared the work: each of the three processes executed at least one run
        // (twelve runs at once across three hungry workers - nobody can lose every race).
        Assert.All(workers, worker => Assert.Contains(
            runs,
            run => run.WorkerId == rows[worker.Name]));
    }
}
