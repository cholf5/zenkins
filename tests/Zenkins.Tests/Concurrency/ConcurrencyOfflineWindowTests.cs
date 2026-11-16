using Xunit;
using Zenkins.Tests.Milestone;
using Zenkins.Tests.Recovery;

namespace Zenkins.Tests.Concurrency;

/// <summary>
/// Q-020: the ring's offline window has margin over a stalled heartbeat, and the margin is
/// <b>proved</b> rather than assumed.
/// <para>
/// The failure this replaces was on Windows CI: three of twelve runs went <c>Lost</c> with
/// <c>worker-offline</c> while every worker process stayed alive and heartbeating again afterwards.
/// Load alone could not reproduce it (the card records four attempts, up to a saturated two-core
/// machine, all green); a heartbeat that <i>stops arriving</i> reproduced it every time. So the state
/// this fact drives is that one: the worker keeps running (and keeps its run), while a
/// <see cref="HeartbeatGate"/> in front of the server holds its traffic long enough to outlive the
/// 5 s window Q-005 picked and not long enough to outlive the 15 s one the ring now runs under
/// (<see cref="ConcurrencyHost.OfflineWindow"/>). Nothing is lost, and the run the gated worker was
/// holding finishes in the state that worker reports.
/// </para>
/// <para>
/// The window was genuinely too tight, and the card records the arithmetic: five skipped beats, a
/// backoff ladder of 1 s / 2 s / 4 s (±25 % jitter), and a request that may stall for its 10 s timeout
/// before the heartbeat loop even learns it failed - which is precisely the stall the gate produces,
/// with a worker that never died.
/// </para>
/// </summary>
[Trait("Category", "Concurrency")]
public sealed class ConcurrencyOfflineWindowTests
{
    /// <summary>Q-005's window, the one this fact has to outlive to mean anything.</summary>
    private const int ReplacedWindowSeconds = 5;

    [Fact(Timeout = 8 * 60 * 1000)]
    public async Task A_stalled_worker_inside_the_window_loses_no_run()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await ConcurrencyHost.StartAsync();
        await using var gate = HeartbeatGate.Start(new Uri(host.ServerUrl));

        var project = await host.CreateProjectAsync("concurrency-stall");

        // Every run really occupies its worker for a while, so the gated worker is one that is
        // holding work - the shape CI hit. A stall of an idle worker would prove nothing.
        using var repository = MilestoneRepository.Create(ConcurrencyPipelines.IdentityWithHold(12_000));
        await host.PointProjectAtAsync(project, repository.FileUrl);

        // Two workers talk to the server directly; the third sits behind the gate, so only its own
        // heartbeat can be stalled - the server, the other workers and the test's own HTTP never are.
        var directA = await host.StartWorkerAsync("stall-a", "recovery,ci", 1);
        var directB = await host.StartWorkerAsync("stall-b", "recovery,ci", 1);
        var gated = await host.StartWorkerAsync("stall-c", "recovery,ci", 1, serverUrl: gate.BaseUrl);
        var workers = new List<RecoveryWorker> { directA, directB, gated };
        Assert.Equal(3, await host.CountOnlineWorkersAsync());

        var runIds = new List<Guid>(12);
        for (var index = 0; index < 12; index++)
        {
            runIds.Add(await host.TriggerTaggedRunAsync(project, requiredTags: string.Empty));
        }

        // Wait for the gated worker to be the one put to work, so the stalled heartbeat threatens a
        // run that really belongs to it.
        var rows = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var worker in workers)
        {
            rows[worker.Name] = (await host.ReadConcurrencyWorkerRowAsync(worker.Name)).Id;
        }

        var held = await host.WaitForAnyRunningAsync(
            runIds,
            TimeSpan.FromSeconds(120),
            workerId: rows[gated.Name]);

        // Shut the gate until the server's own record says this worker's heartbeat has been silent for
        // longer than the window Q-020 replaced - and no longer than that. The age below is measured
        // from the row, not assumed from a sleep: 5 s of silence used to be enough for Lost, and the
        // run the gated worker holds must survive it now.
        TimeSpan silent;
        DateTimeOffset? stalledHeartbeat;
        gate.Close();
        try
        {
            silent = await host.WaitForHeartbeatAgeAsync(
                gated.Name,
                TimeSpan.FromSeconds(ReplacedWindowSeconds + 1),
                TimeSpan.FromSeconds(30));

            var row = await host.ReadConcurrencyWorkerRowAsync(gated.Name);
            stalledHeartbeat = row.LastHeartbeatAt;
            Assert.True(
                row.Status == "Online",
                $"worker '{gated.Name}' was already {row.Status} after {silent.TotalSeconds:0.#}s without a "
                + $"heartbeat, inside the {ConcurrencyHost.OfflineWindow}s window: the window has no margin."
                + Environment.NewLine + await host.FormatWorkerTailsAsync(workers));
            Assert.True(
                silent < TimeSpan.FromSeconds(ConcurrencyHost.OfflineWindow),
                $"the stall measured {silent.TotalSeconds:0.#}s, which is not inside the "
                + $"{ConcurrencyHost.OfflineWindow}s window - the fact would prove nothing.");

            // The gated worker still holds its run: the server has no report from it and no reason to
            // take the run away, because the worker has not gone offline. Its own process is untouched
            // and still running.
            Assert.Equal("Running", (await host.ReadRunAsync(held.Id)).Status);
            Assert.False(gated.Process.HasExited, $"worker '{gated.Name}' died during the stall.");
        }
        finally
        {
            gate.Open();
        }

        // The gate really opened: the worker's stored heartbeat moves forward again (a worker that had
        // died would leave the value frozen, and its run would be Lost).
        var resumeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var row = await host.ReadConcurrencyWorkerRowAsync(gated.Name);
            if (row.LastHeartbeatAt is { } refreshed && refreshed > stalledHeartbeat)
            {
                break;
            }

            Assert.True(
                DateTime.UtcNow < resumeDeadline,
                $"worker '{gated.Name}' never heartbeated again after its {silent.TotalSeconds:0.#}s stall."
                + Environment.NewLine + await host.FormatWorkerTailsAsync(workers));
            await Task.Delay(200, cancellationToken);
        }

        // Twelve runs, none lost: the silence cost wall-clock, not work.
        var runs = new List<MilestoneRun>(12);
        foreach (var runId in runIds)
        {
            runs.Add(await host.WaitForTerminalAsync(runId));
        }

        var unexpected = runs
            .Where(run => run.Status != "Success" || run.ExitCode != 0 || run.FailureReason is not null)
            .ToArray();
        Assert.True(
            unexpected.Length == 0,
            $"{unexpected.Length} of {runs.Count} runs did not survive a {silent.TotalSeconds:0.#}s heartbeat "
            + $"stall inside the {ConcurrencyHost.OfflineWindow}s window: "
            + string.Join(
                "; ",
                unexpected.Select(run => $"{run.Id:D} ended {run.Status} (exit {run.ExitCode}, "
                    + $"reason '{run.FailureReason}', worker {run.WorkerId?.ToString("D") ?? "none"})"))
            + Environment.NewLine + await host.FormatWorkerTailsAsync(workers));
    }
}
