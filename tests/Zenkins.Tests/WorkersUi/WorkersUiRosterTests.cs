using System.Net;
using Microsoft.Extensions.Configuration;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Workers;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.WorkersUi;

/// <summary>
/// Card D-005's roster and its TC-001: the per-worker capability fields (OS, architecture,
/// version, CPU count, max concurrency, tags, status, last heartbeat), the current-run count that
/// comes from the same current-runs query the detail page lists (never a denormalised column), and
/// the agreement proof - the page's offline state is exactly the row the offline sweeper (F-010)
/// wrote, aged through the real monitor path, never a recomputation of the page's own.
/// </summary>
[Trait("Category", "WorkersUi")]
public sealed class WorkersUiRosterTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_roster_shows_capability_fields_status_and_current_run_count()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await WorkersUiHost.CreateAsync();
        var (projectId, owner) = await host.Runs.CreateProjectAsync();
        var (tokenA, workerAId) = await host.Runs.RegisterWorkerAsync(
            name: "roster-runner", tags: "dotnet,ci", maxConcurrency: 2);
        var (_, workerBId) = await host.Runs.RegisterWorkerAsync(
            name: "roster-idle", tags: "windows", maxConcurrency: 4);
        var client = host.Host.CreateClient();

        // One current run: A claims and accepts it through the real endpoints, so exactly one
        // worker of the two is Running right now.
        var runId = await host.Runs.AddRunAsync(projectId);
        using (var claimed = await RunTestHost.ClaimAsync(client, tokenA))
        {
            Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(claimed));
        }

        using (var accepted = await RunTestHost.AcceptAsync(client, tokenA, runId))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        var page = await host.ReadRosterAsync(owner);

        // Capability fields, per row (the AC's OS/architecture/version/CPU/max concurrency/tags).
        Assert.Contains("roster-runner", page, StringComparison.Ordinal);
        Assert.Contains("roster-idle", page, StringComparison.Ordinal);
        Assert.Contains("Linux", page, StringComparison.Ordinal);
        Assert.Contains("X64", page, StringComparison.Ordinal);
        Assert.Contains("v1.0.0", page, StringComparison.Ordinal);
        Assert.Contains("8 CPUs", page, StringComparison.Ordinal);
        Assert.Contains("max 2", page, StringComparison.Ordinal);
        Assert.Contains("max 4", page, StringComparison.Ordinal);
        Assert.Contains("dotnet,ci", page, StringComparison.Ordinal);
        Assert.Contains("windows", page, StringComparison.Ordinal);

        // Status and last heartbeat (the registration counts as the first heartbeat).
        Assert.Contains("Online", page, StringComparison.Ordinal);
        Assert.Contains("UTC", page, StringComparison.Ordinal);

        // The current-run count: 1 for the executing worker, 0 for the idle one - the same
        // query the detail page lists, so the roster cannot drift from the detail's answer.
        Assert.Contains("1 running", page, StringComparison.Ordinal);
        Assert.Contains("0 running", page, StringComparison.Ordinal);

        // Every row links to its detail page.
        Assert.Contains($"/workers/{workerAId}", page, StringComparison.Ordinal);
        Assert.Contains($"/workers/{workerBId}", page, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_displayed_offline_state_equals_the_offline_sweepers_judgement()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await WorkersUiHost.CreateAsync(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [WorkerMonitorOptions.OfflineAfterSecondsConfigurationKey] = "2",
                [WorkerMonitorOptions.MonitorIntervalSecondsConfigurationKey] = "3600",
            }));
        var owner = await host.Runs.Inner.SignInOwnerAsync();
        var (_, workerId) = await host.Runs.RegisterWorkerAsync(name: "sweep-agreement-agent");

        // Before the sweep: the row is Online and the page shows Online - one answer, not two.
        Assert.Equal(WorkerStatus.Online, (await host.Runs.ReadWorkerAsync(workerId)).Status);
        var before = await host.ReadWorkerDetailAsync(owner, workerId);
        Assert.DoesNotContain("Offline", before, StringComparison.Ordinal);

        // Age the heartbeat past the threshold, then sweep through the real monitor seam - the
        // page never ages anything itself.
        await host.BackdateHeartbeatAsync(workerId, TimeSpan.FromSeconds(5));
        var sweep = await host.SweepOfflineAsync();
        Assert.Equal(1, sweep.WorkersOfflined);

        // After the sweep: what the page shows IS the row the sweeper wrote.
        var row = await host.Runs.ReadWorkerAsync(workerId);
        Assert.Equal(WorkerStatus.Offline, row.Status);
        var after = await host.ReadWorkerDetailAsync(owner, workerId);
        Assert.Contains("Offline", after, StringComparison.Ordinal);

        // The roster shows the same judgement.
        Assert.Contains("Offline", await host.ReadRosterAsync(owner), StringComparison.Ordinal);
    }
}
