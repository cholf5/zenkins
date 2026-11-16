using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Runs;

/// <summary>
/// Card F-029: the three per-project queue controls - the concurrency limit, the quiet period and the
/// queue cap - driven through the product's own forms and claim endpoint, with the verdicts read from the
/// rows the server wrote.
/// <para>
/// The claim race itself (two workers, one project, one slot) has its own test in
/// <c>Zenkins.Tests.Concurrency</c>, because it needs the barrier harness and repeated rounds; this file
/// covers the sequential facts the card states plus the defaults.
/// </para>
/// </summary>
[Trait("Category", "Runs")]
public sealed class QueueControlTests
{
    [Fact]
    public async Task A_full_queue_refuses_the_next_trigger_before_any_row_exists()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("queue-cap");
        var organizationId = await host.OrganizationIdAsync();

        var saved = await RunTestHost.SaveLimitsAsync(owner, projectId, maxQueuedRuns: 2);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Contains("notice=limits-saved", saved.Headers.Location!.OriginalString, StringComparison.Ordinal);

        await RunTestHost.TriggerRunAsync(owner, projectId);
        await RunTestHost.TriggerRunAsync(owner, projectId);

        var refused = await RunTestHost.StartRunAsync(owner, projectId);
        Assert.True((int)refused.StatusCode is 302 or 303);
        var location = refused.Headers.Location!.OriginalString;
        Assert.Equal($"/projects/{projectId}?runError=queue-full", location);

        // No orphan queued row: the cap was checked before the insert, not cleaned up after it.
        var runs = await host.ReadRunsAsync(organizationId);
        Assert.Equal(2, runs.Count);
        Assert.All(runs, run => Assert.Equal(BuildRunStatus.Queued, run.Status));

        // The refusal quotes the observed numbers, read from the same count the gate used.
        var page = await AuthTestHost.GetStringAsync(owner, location);
        Assert.Contains("Queue full: 2 queued runs, cap 2", page, StringComparison.Ordinal);

        // Draining one queued run makes room for exactly one more trigger.
        await host.InSystemScopeAsync(async context =>
        {
            var oldest = (await context.SystemQueryAsync<BuildRun>(
                run => run.ProjectId == projectId && run.Status == BuildRunStatus.Queued))
                .OrderBy(run => run.CreatedAt)
                .First();
            oldest.Status = BuildRunStatus.Canceled;
            oldest.FailureReason = "drained-by-the-test";
            await context.SaveChangesAsync();
            return true;
        });

        await RunTestHost.TriggerRunAsync(owner, projectId);
        Assert.Equal(3, (await host.ReadRunsAsync(organizationId)).Count);
    }

    [Fact]
    public async Task A_quiet_period_delays_claimability_and_zero_means_immediately()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("quiet-period");
        var (defaultProjectId, _) = await host.CreateProjectAsync("quiet-period-default");
        using var workerClient = host.Host.CreateClient();
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "quiet-agent");

        Assert.Equal(
            HttpStatusCode.Redirect,
            (await RunTestHost.SaveLimitsAsync(owner, projectId, quietPeriodSeconds: 5)).StatusCode);

        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);

        // Inside the period the run is queued but not claimable: the claim answers its ordinary 204.
        using var insidePeriod = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.NoContent, insidePeriod.StatusCode);

        // Deterministic stand-in for waiting: age the run past its quiet period (the alternative is a
        // six-second sleep in the suite for a five-second assertion).
        await host.AgeRunAsync(runId, TimeSpan.FromSeconds(6));

        var agedProject = await host.ReadProjectAsync(projectId);
        Assert.Equal(5, agedProject.QuietPeriodSeconds);
        var agedRun = await host.ReadRunAsync(runId);
        Assert.True(
            agedRun.CreatedAt.AddSeconds(5) < DateTimeOffset.UtcNow,
            $"the aged run is still inside the quiet period: CreatedAt={agedRun.CreatedAt:o} now={DateTimeOffset.UtcNow:o}");
        Assert.Equal(BuildRunStatus.Queued, agedRun.Status);
        Assert.Null(agedRun.LeaseExpiresAt);

        using var afterPeriod = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.OK, afterPeriod.StatusCode);
        Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(afterPeriod));

        // The default (0) is claimable immediately - the limit, not the harness, is what blocked above.
        // A second worker, because the first one's one slot is still held by the quiet-period run it just
        // leased (the worker-level cap and the project-level one are independent decisions).
        var (secondWorkerToken, _) = await host.RegisterWorkerAsync(name: "quiet-agent-2");
        var immediateRunId = await RunTestHost.TriggerRunAsync(owner, defaultProjectId);
        using var immediate = await RunTestHost.ClaimAsync(workerClient, secondWorkerToken);
        Assert.Equal(HttpStatusCode.OK, immediate.StatusCode);
        Assert.Equal(immediateRunId, await RunTestHost.ReadClaimedRunIdAsync(immediate));
    }

    [Fact]
    public async Task A_concurrency_limit_of_one_serializes_the_project_until_the_run_ends()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("project-cap");
        var organizationId = await host.OrganizationIdAsync();

        Assert.Equal(
            HttpStatusCode.Redirect,
            (await RunTestHost.SaveLimitsAsync(owner, projectId, maxConcurrentRuns: 1)).StatusCode);

        var firstRunId = await host.AddRunAsync(projectId, organizationId: organizationId);
        var secondRunId = await host.AddRunAsync(projectId, organizationId: organizationId);

        using var workerA = host.Host.CreateClient();
        using var workerB = host.Host.CreateClient();
        var (tokenA, _) = await host.RegisterWorkerAsync(name: "cap-agent-a");
        var (tokenB, _) = await host.RegisterWorkerAsync(name: "cap-agent-b");

        using var firstClaim = await RunTestHost.ClaimAsync(workerA, tokenA);
        Assert.Equal(HttpStatusCode.OK, firstClaim.StatusCode);
        var claimedId = await RunTestHost.ReadClaimedRunIdAsync(firstClaim);

        // The claimed-but-not-yet-accepted run already occupies the project's only slot: a second worker
        // with a free slot of its own gets nothing.
        using var blocked = await RunTestHost.ClaimAsync(workerB, tokenB);
        Assert.Equal(HttpStatusCode.NoContent, blocked.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await RunTestHost.AcceptAsync(workerA, tokenA, claimedId)).StatusCode);
        using var stillBlocked = await RunTestHost.ClaimAsync(workerB, tokenB);
        Assert.Equal(HttpStatusCode.NoContent, stillBlocked.StatusCode);

        // Once the first run ends, the second is claimable by the other worker.
        Assert.Equal(HttpStatusCode.OK, (await RunTestHost.CompleteAsync(workerA, tokenA, claimedId, 0)).StatusCode);
        using var secondClaim = await RunTestHost.ClaimAsync(workerB, tokenB);
        Assert.Equal(HttpStatusCode.OK, secondClaim.StatusCode);
        var nextId = await RunTestHost.ReadClaimedRunIdAsync(secondClaim);
        Assert.NotEqual(claimedId, nextId);
        Assert.Contains(nextId, new[] { firstRunId, secondRunId });
    }

    [Fact]
    public async Task Existing_projects_keep_the_unlimited_defaults()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("unlimited-defaults");

        var project = await host.ReadProjectAsync(projectId);
        Assert.Null(project.MaxConcurrentRuns);
        Assert.Equal(0, project.QuietPeriodSeconds);
        Assert.Null(project.MaxQueuedRuns);

        // The settings tab states the defaults as blanks, not as zeros somebody has to interpret.
        var settings = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/settings");
        Assert.Contains("id=\"limits-max-concurrent\"", settings, StringComparison.Ordinal);
        Assert.Contains("id=\"limits-quiet-period\"", settings, StringComparison.Ordinal);
        Assert.Contains("id=\"limits-max-queued\"", settings, StringComparison.Ordinal);

        // Unlimited means unlimited: three triggers all queue, and nothing is claimed away.
        await RunTestHost.TriggerRunAsync(owner, projectId);
        await RunTestHost.TriggerRunAsync(owner, projectId);
        await RunTestHost.TriggerRunAsync(owner, projectId);
        Assert.Equal(3, (await host.ReadRunsAsync(await host.OrganizationIdAsync())).Count);
    }

    [Theory]
    [InlineData("0", "", "", "error=max-concurrent-runs-invalid")]
    [InlineData("1", "-1", "", "error=quiet-period-invalid")]
    [InlineData("1", "", "0", "error=max-queued-runs-invalid")]
    [InlineData("one", "", "", "error=max-concurrent-runs-invalid")]
    public async Task Illegal_limits_are_refused_and_change_nothing(
        string maxConcurrentRuns,
        string quietPeriodSeconds,
        string maxQueuedRuns,
        string expectedLocation)
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync($"limits-invalid-{maxConcurrentRuns}-{quietPeriodSeconds}-{maxQueuedRuns}");

        var response = await Zenkins.Tests.Organizations.OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/settings",
            $"/projects/{projectId}/limits",
            new Dictionary<string, string>
            {
                ["MaxConcurrentRuns"] = maxConcurrentRuns,
                ["QuietPeriodSeconds"] = quietPeriodSeconds,
                ["MaxQueuedRuns"] = maxQueuedRuns,
            });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(expectedLocation, response.Headers.Location!.OriginalString, StringComparison.Ordinal);

        var project = await host.ReadProjectAsync(projectId);
        Assert.Null(project.MaxConcurrentRuns);
        Assert.Equal(0, project.QuietPeriodSeconds);
        Assert.Null(project.MaxQueuedRuns);

        // The page renders the code's message rather than looking like a no-op.
        var page = await AuthTestHost.GetStringAsync(owner, response.Headers.Location!.OriginalString);
        Assert.Contains("settings-error", page, StringComparison.Ordinal);
    }
}
