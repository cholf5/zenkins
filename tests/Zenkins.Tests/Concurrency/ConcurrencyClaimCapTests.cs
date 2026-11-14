using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.Concurrency;

/// <summary>
/// B-002's regression: the server's <c>maxConcurrency</c> slot check used to be check-then-act - it
/// counted the worker's assigned runs in one statement and leased the run in another (the
/// compare-and-set), so one hostile-but-authenticated claimer holding a single valid worker token
/// could release a burst of concurrent claims that all passed the count before any of their leases
/// committed, and walk away holding more runs than the cap it reported. The claim now takes the
/// count and the compare-and-set in one transaction, serialized per worker on the worker's own row,
/// and this test pins that: one worker registered with <c>maxConcurrency=1</c> through the real
/// register endpoint, a barrier release of concurrent authenticated claims (the F-006 race harness
/// shape, <see cref="RunClaimConcurrencyTests"/>), and the verdict read from the rows the server
/// wrote - never more than one assigned run, no matter how the responses interleave.
/// <para>
/// The probe claimer never accepts what it leases, so every admitted run stays <c>Queued</c> with
/// the worker's id - exactly the state the claim's own slot count reads. The ceiling being
/// <i>exercised</i>, not vacuous, is asserted too: the race really admitted one run per round.
/// </para>
/// </summary>
[Trait("Category", "Concurrency")]
public sealed class ConcurrencyClaimCapTests
{
    [Fact(Timeout = 5 * 60 * 1000)]
    public async Task Concurrent_claims_of_one_maxConcurrency_one_worker_never_admit_more_than_one_run()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await RunTestHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync("cap-race");
        var organizationId = await host.OrganizationIdAsync();

        const int rounds = 5;
        const int claimsPerRound = 8;
        const int queuedPerRound = 4;
        var worstAssigned = 0;
        var allQueuedRunIds = new HashSet<Guid>();

        for (var round = 1; round <= rounds; round++)
        {
            // A fresh probe worker per round, pinned to one slot at the real register endpoint; the
            // plaintext token is the hostile claimer's valid credential.
            var (token, workerId) = await host.RegisterWorkerAsync(
                name: $"cap-race-{round}",
                tags: "ci",
                maxConcurrency: 1);

            var runIds = new HashSet<Guid>();
            for (var index = 0; index < queuedPerRound; index++)
            {
                runIds.Add(await host.AddRunAsync(projectId, organizationId: organizationId));
            }

            // The queue is shared: runs a round's burst did not admit stay Queued and are FIFO-first
            // for the next round's claims (the pull model's ordinary behaviour), so the sanity check
            // below runs against everything queued so far, not this round's additions alone.
            allQueuedRunIds.UnionWith(runIds);

            // One valid token, eight concurrent claim requests released through one barrier - the
            // racing claim responses the seam allowed. Everything answers honestly (200 with a run
            // or the 204 nothing-eligible): the race must never produce a third kind of answer.
            using var client = host.Host.CreateClient();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var claims = Enumerable.Range(0, claimsPerRound)
                .Select(_ => Task.Run(async () =>
                {
                    await start.Task.WaitAsync(cancellationToken);
                    using var response = await RunTestHost.ClaimAsync(client, token);
                    return response.StatusCode == HttpStatusCode.OK
                        ? await RunTestHost.ReadClaimedRunIdAsync(response)
                        : (Guid?)null;
                }))
                .ToArray();
            start.SetResult();
            var results = await Task.WhenAll(claims);

            Assert.All(results, result => Assert.True(
                result is null || allQueuedRunIds.Contains(result.Value),
                $"round {round}: a claim returned a run that was never queued."));

            // The verdict comes from the rows, not from the responses: how many runs the worker was
            // assigned (running plus still-leased queued - the same state the claim's slot count
            // reads). Two racing admissions both winning the count shows up here as 2 or more.
            // ReadRunsAsync crosses tenants through the product's system scope (a bare query here
            // would run behind an empty tenant filter and see nothing).
            var assigned = (await host.ReadRunsAsync(organizationId)).Count(run =>
                run.WorkerId == workerId
                && (run.Status == BuildRunStatus.Running || run.Status == BuildRunStatus.Queued));
            worstAssigned = Math.Max(worstAssigned, assigned);
            Assert.True(
                assigned <= 1,
                $"round {round}: the maxConcurrency=1 worker holds {assigned} assigned runs "
                + $"({results.Count(result => result is not null)} of {claimsPerRound} concurrent "
                + "claims returned a run) - the check-then-act window admitted work past the "
                + "reported cap.");
        }

        // The ceiling was exercised, not skirted: the race really admitted one run every round.
        Assert.Equal(1, worstAssigned);
    }
}
