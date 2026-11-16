using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.Concurrency;

/// <summary>
/// F-029's race, and the property B-002's report says a naive per-project limit would miss: two
/// <em>different</em> workers claiming two different runs of one project at the same instant must not both
/// take the second slot of a <c>MaxConcurrentRuns = 1</c> project. The worker row lock cannot serialize
/// them - they lock different workers - so the limit is decided inside the claim's transaction after the
/// project row lock, and this test pins that: worker slots are 1 each (so the worker cap never binds), 2
/// queued runs per round, 8 concurrent authenticated claims released through one barrier, and the verdict
/// read from the rows the server wrote.
/// </summary>
[Trait("Category", "Concurrency")]
public sealed class ConcurrencyProjectCapTests
{
    [Fact(Timeout = 5 * 60 * 1000)]
    public async Task Concurrent_claims_of_two_workers_never_take_a_second_slot_of_a_capped_project()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await RunTestHost.CreateAsync();
        var organizationId = await host.OrganizationIdAsync();
        var owner = await host.Inner.SignInOwnerAsync();

        const int rounds = 3;
        const int claimsPerWorker = 4;
        var worstActive = 0;

        for (var round = 1; round <= rounds; round++)
        {
            // A fresh capped project per round, so a leftover row can never make a later round look
            // correct for the wrong reason.
            var projectId = await host.Inner.Inner.AddProjectAsync(organizationId, $"project-cap-race-{round}");
            Assert.Equal(
                HttpStatusCode.Redirect,
                (await RunTestHost.SaveLimitsAsync(owner, projectId, maxConcurrentRuns: 1)).StatusCode);

            // The two runs are tag-disjoint and each worker covers exactly one of them, so the two
            // claims walk DIFFERENT first candidates. That is the only shape where both can be inside
            // the project's slot decision at the same instant: with a shared candidate order both
            // claims race for the same oldest run, and the loser would re-count before reaching the
            // second one - a shape that hides the check-then-act window instead of testing it.
            var runIds = new[]
            {
                await host.AddRunAsync(projectId, requiredTags: "alpha", organizationId: organizationId),
                await host.AddRunAsync(projectId, requiredTags: "bravo", organizationId: organizationId),
            };

            // Two workers, each structurally one slot deep: only the project's limit can refuse them.
            var (tokenA, _) = await host.RegisterWorkerAsync(
                name: $"cap-race-a-{round}", tags: "alpha", ownerClient: owner);
            var (tokenB, _) = await host.RegisterWorkerAsync(
                name: $"cap-race-b-{round}", tags: "bravo", ownerClient: owner);
            using var client = host.Host.CreateClient();

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var claims = Enumerable.Range(0, claimsPerWorker * 2)
                .Select(index => Task.Run(async () =>
                {
                    var token = index < claimsPerWorker ? tokenA : tokenB;
                    await start.Task.WaitAsync(cancellationToken);
                    using var response = await RunTestHost.ClaimAsync(client, token);
                    return (Index: index, Token: token, Status: response.StatusCode,
                        RunId: response.StatusCode == HttpStatusCode.OK
                            ? await RunTestHost.ReadClaimedRunIdAsync(response)
                            : (Guid?)null);
                }))
                .ToArray();
            start.SetResult();
            var results = await Task.WhenAll(claims);

            // The verdict comes from the rows, not from the responses: a run of this project that carries
            // a worker id is an occupied slot (Queued-but-leased or Running - the same predicate the
            // project's own count uses).
            var rows = await host.ReadRunsAsync(organizationId);
            var active = rows.Count(run =>
                run.ProjectId == projectId
                && run.WorkerId is not null
                && (run.Status == BuildRunStatus.Running || run.Status == BuildRunStatus.Queued));
            worstActive = Math.Max(worstActive, active);
            Assert.True(
                active <= 1,
                $"round {round}: the MaxConcurrentRuns=1 project holds {active} active runs "
                + $"({results.Count(result => result.Status == HttpStatusCode.OK)} of {results.Length} "
                + "concurrent claims returned a run) - the project's cap was decided check-then-act.");

            // Every admitted run came from this project's queue, and each admission has exactly one
            // winner: responses and rows must agree.
            var admitted = results.Where(result => result.RunId is not null).ToArray();
            Assert.All(admitted, result => Assert.Contains(result.RunId!.Value, runIds));
            Assert.Equal(active, admitted.Length);

            // Drain the round so its rows cannot influence the next one: finish the admitted run, then
            // cancel whatever is still queued for the project.
            if (admitted.Length == 1)
            {
                using var workerClient = host.Host.CreateClient();
                var winner = admitted[0];
                Assert.Equal(
                    HttpStatusCode.OK,
                    (await RunTestHost.AcceptAsync(workerClient, winner.Token, winner.RunId!.Value)).StatusCode);
                Assert.Equal(
                    HttpStatusCode.OK,
                    (await RunTestHost.CompleteAsync(workerClient, winner.Token, winner.RunId!.Value, 0)).StatusCode);
            }

            await host.InSystemScopeAsync(async context =>
            {
                var leftover = await context.SystemQueryAsync<BuildRun>(
                    run => run.ProjectId == projectId && run.Status == BuildRunStatus.Queued);
                foreach (var run in leftover)
                {
                    run.Status = BuildRunStatus.Canceled;
                    run.FailureReason = "drained-by-the-test";
                    run.WorkerId = null;
                    run.LeaseExpiresAt = null;
                }

                await context.SaveChangesAsync();
                return true;
            });
        }

        // The ceiling was exercised, not skirted: every round admitted exactly one run.
        Assert.Equal(1, worstActive);
    }
}
