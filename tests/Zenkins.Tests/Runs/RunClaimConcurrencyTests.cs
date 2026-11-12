using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;

namespace Zenkins.Tests.Runs;

/// <summary>
/// The double-claim proof (card F-006 TC-002): concurrent HTTP claims over the real TestServer
/// pipeline against one SQLite file. The atomicity mechanism is
/// <see cref="Zenkins.Server.Data.ZenkinsDbContext.TryLeaseBuildRunAsync"/> - a single conditional
/// UPDATE per candidate (compare-and-set) - so a run is leased exactly once no matter how many
/// claims race for it. The red proof on the card was produced by stripping the conditions from
/// that WHERE (an unconditional UPDATE): both tests here go red, then green again after restore.
/// </summary>
[Trait("Category", "Runs")]
public sealed class RunClaimConcurrencyTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Concurrent_claims_never_return_the_same_run()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();

        const int workerCount = 3;
        const int runCount = 6;
        const int claimsPerWorker = 4;

        var workers = new List<(HttpClient Client, string Token)>();
        for (var index = 0; index < workerCount; index++)
        {
            var (token, _) = await host.RegisterWorkerAsync(
                name: $"agent-{index}",
                tags: "ci",
                maxConcurrency: runCount);
            workers.Add((host.Host.CreateClient(), token));
        }

        var runIds = new HashSet<Guid>();
        for (var index = 0; index < runCount; index++)
        {
            runIds.Add(await host.AddRunAsync(projectId, organizationId: organizationId));
        }

        // Real concurrency: all claims released through one barrier over the live pipeline.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimTasks = workers
            .SelectMany(worker => Enumerable.Range(0, claimsPerWorker)
                .Select(_ => (worker.Client, worker.Token)))
            .Select(pair => Task.Run(async () =>
            {
                await start.Task.WaitAsync(CancellationToken);
                using var response = await RunTestHost.ClaimAsync(pair.Client, pair.Token);
                return response.StatusCode == HttpStatusCode.OK
                    ? await RunTestHost.ReadClaimedRunIdAsync(response)
                    : (Guid?)null;
            }))
            .ToArray();
        start.SetResult();
        var claimResults = await Task.WhenAll(claimTasks);

        var handedOut = claimResults.OfType<Guid>().ToList();

        // Every response either handed out a run (200) or honestly reported an empty queue (204):
        // no status in between, and no run appears twice.
        Assert.All(claimResults, result =>
            Assert.True(result is null || runIds.Contains(result.Value)));
        Assert.Equal(handedOut.Count, handedOut.Distinct().Count());

        // Drain: every run can still be claimed exactly once, and the queue really empties.
        var progress = true;
        while (progress)
        {
            progress = false;
            foreach (var (client, token) in workers)
            {
                using var response = await RunTestHost.ClaimAsync(client, token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    handedOut.Add(await RunTestHost.ReadClaimedRunIdAsync(response));
                    progress = true;
                }
                else
                {
                    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                }
            }
        }

        Assert.Equal(runIds.Count, handedOut.Distinct().Count());

        // And every run's row shows exactly one lease - the winner of the race for it.
        var stored = await host.ReadRunsAsync(organizationId);
        Assert.Equal(runCount, stored.Count);
        Assert.All(stored, run =>
        {
            Assert.Contains(run.Id, handedOut);
            Assert.NotNull(run.WorkerId);
            Assert.Equal(BuildRunStatus.Queued, run.Status);
        });
    }

    [Fact]
    public async Task Two_concurrent_claims_for_a_single_run_return_it_exactly_once()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var (firstToken, firstWorkerId) = await host.RegisterWorkerAsync(name: "agent-a", maxConcurrency: 4);
        var (secondToken, secondWorkerId) = await host.RegisterWorkerAsync(name: "agent-b", maxConcurrency: 4);
        var runId = await host.AddRunAsync(projectId, organizationId: organizationId);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstClient = host.Host.CreateClient();
        using var secondClient = host.Host.CreateClient();
        var claims = new[]
            {
                (Client: firstClient, Token: firstToken, WorkerId: firstWorkerId),
                (Client: secondClient, Token: secondToken, WorkerId: secondWorkerId),
            }
            .Select(pair => Task.Run(async () =>
            {
                await start.Task.WaitAsync(CancellationToken);
                using var response = await RunTestHost.ClaimAsync(pair.Client, pair.Token);
                return (pair.WorkerId,
                    StatusCode: response.StatusCode,
                    RunId: response.StatusCode == HttpStatusCode.OK
                        ? await RunTestHost.ReadClaimedRunIdAsync(response)
                        : (Guid?)null);
            }))
            .ToArray();
        start.SetResult();
        var results = await Task.WhenAll(claims);

        // Exactly one 200 with the run, one 204 without it - never two winners.
        Assert.Equal(1, results.Count(result => result.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, results.Count(result => result.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(runId, results.Single(result => result.StatusCode == HttpStatusCode.OK).RunId);

        var stored = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Queued, stored.Status);
        Assert.Equal(
            results.Single(result => result.StatusCode == HttpStatusCode.OK).WorkerId,
            stored.WorkerId);
        Assert.NotNull(stored.LeaseExpiresAt);
    }
}
