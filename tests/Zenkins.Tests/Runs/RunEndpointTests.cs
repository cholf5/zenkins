using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Runs;

/// <summary>
/// Card F-006's run tests, driven through the real pipeline: the authenticated trigger form
/// (cookies + antiforgery + PRG, F-003's shape) and the token-authenticated worker claim/report
/// API (F-004's shape). Every rejection is verified against the database, not against the absence
/// of a message. The double-claim concurrency proof lives in
/// <see cref="RunClaimConcurrencyTests"/>.
/// </summary>
[Trait("Category", "Runs")]
public sealed class RunEndpointTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------
    // TC-001: happy path
    // ------------------------------------------------------------------

    [Fact]
    public async Task Trigger_to_claim_to_accept_to_complete_records_the_full_lifecycle()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var (workerToken, workerId) = await host.RegisterWorkerAsync(
            name: "build-agent-1",
            tags: "ci",
            maxConcurrency: 2);
        var ownerUserId = (await host.Inner.Inner.ReadUserAsync(OrganizationTestHost.OwnerEmail)).Id;

        // 1. Trigger: a Queued run with the exact argv, no shell interpretation anywhere.
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);

        var queued = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Queued, queued.Status);
        Assert.Equal(organizationId, queued.OrganizationId);
        Assert.Equal(projectId, queued.ProjectId);
        Assert.Equal(BuildRunTriggerType.Manual, queued.TriggerType);
        Assert.Equal(ownerUserId, queued.TriggerUserId);
        // F-014: a run is a pipeline execution - the branch and pipeline path are snapshots of the
        // project's own values, taken at trigger time.
        Assert.Equal("main", queued.GitBranch);
        Assert.Equal("pipeline.lua", queued.PipelinePath);
        Assert.Null(queued.PipelineSpecJson);
        Assert.Equal(string.Empty, queued.RequiredTags);
        Assert.Null(queued.WorkerId);
        Assert.Null(queued.LeaseExpiresAt);
        Assert.Null(queued.StartedAt);
        Assert.Null(queued.EndedAt);
        Assert.Null(queued.ExitCode);

        // 2. Claim: the run is leased (WorkerId + lease), but a lease is not a transition.
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(claimed));
        var leased = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Queued, leased.Status);
        Assert.Equal(workerId, leased.WorkerId);
        Assert.NotNull(leased.LeaseExpiresAt);

        // 3. Accepted: the single Queued → Running edge; start time set, lease gone.
        using var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, runId);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(BuildRunStatus.Running, await RunTestHost.ReadReportStatusAsync(accepted));
        var running = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Running, running.Status);
        Assert.NotNull(running.StartedAt);
        Assert.Null(running.LeaseExpiresAt);

        // 4. Completed with exit 0: Success, end time and exit code persisted.
        using var completed = await RunTestHost.CompleteAsync(workerClient, workerToken, runId, exitCode: 0);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(BuildRunStatus.Success, await RunTestHost.ReadReportStatusAsync(completed));
        var finished = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Success, finished.Status);
        Assert.Equal(0, finished.ExitCode);
        Assert.NotNull(finished.EndedAt);
        Assert.True(finished.EndedAt!.Value >= finished.StartedAt!.Value);
        Assert.Equal(workerId, finished.WorkerId);

        // 5. The pages show the recorded data (tenant-scoped reads through the services).
        var list = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/runs");
        Assert.Contains("build-agent-1", list, StringComparison.Ordinal);
        Assert.Contains("Success", list, StringComparison.Ordinal);
        Assert.Contains("alice@example.test", list, StringComparison.Ordinal);

        var detail = await AuthTestHost.GetStringAsync(owner, $"/runs/{runId}");
        Assert.Contains("build-agent-1", detail, StringComparison.Ordinal);
        Assert.Contains("Success", detail, StringComparison.Ordinal);
        Assert.Contains("id=\"run-branch\"", detail, StringComparison.Ordinal);
        Assert.Contains(">main</dd>", detail, StringComparison.Ordinal);
        Assert.Contains("id=\"run-pipeline-path\"", detail, StringComparison.Ordinal);
        Assert.Contains(">pipeline.lua</dd>", detail, StringComparison.Ordinal);
        Assert.Contains("id=\"run-steps-pending\"", detail, StringComparison.Ordinal);
        Assert.Contains("alice@example.test", detail, StringComparison.Ordinal);
        Assert.Contains("id=\"run-exit-code\">0</dd>", detail, StringComparison.Ordinal);
        Assert.Contains("id=\"run-started\"", detail, StringComparison.Ordinal);
        Assert.Contains("id=\"run-ended\"", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"run-duration\">-", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_trigger_snapshots_the_branch_and_pipeline_path_from_the_project()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();

        // The form left the branch blank: the run takes the project's default branch, and the pipeline
        // path the project declared - both as copies, so editing the project later cannot move it.
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId, branch: "release/1.0");

        var stored = await host.ReadRunAsync(runId);
        Assert.Equal("release/1.0", stored.GitBranch);
        Assert.Equal("pipeline.lua", stored.PipelinePath);
    }

    // ------------------------------------------------------------------
    // TC-003: eligibility
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_worker_without_the_required_tags_is_never_handed_the_run()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var (linuxToken, _) = await host.RegisterWorkerAsync(name: "linux-agent", tags: "linux");

        // A run requiring "windows": seeded directly because the form refuses tags no worker has
        // (the form-side guard has its own test below).
        var runId = await host.AddRunAsync(projectId, requiredTags: "windows", organizationId: organizationId);

        using var workerClient = host.Host.CreateClient();
        using var response = await RunTestHost.ClaimAsync(workerClient, linuxToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Queued, run.Status);
        Assert.Null(run.WorkerId);
        Assert.Null(run.LeaseExpiresAt);
    }

    [Fact]
    public async Task An_offline_worker_is_never_handed_a_run_until_it_is_online_again()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var (workerToken, workerId) = await host.RegisterWorkerAsync(name: "sleepy-agent");
        var runId = await host.AddRunAsync(projectId, organizationId: organizationId);

        await host.SetWorkerStatusAsync(workerId, WorkerStatus.Offline);

        using var workerClient = host.Host.CreateClient();
        using var offline = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.NoContent, offline.StatusCode);
        Assert.Null((await host.ReadRunAsync(runId)).WorkerId);

        // The status is what blocked: back Online, the same worker gets the run.
        await host.SetWorkerStatusAsync(workerId, WorkerStatus.Online);
        using var online = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.OK, online.StatusCode);
        Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(online));
    }

    [Fact]
    public async Task A_worker_at_full_concurrency_is_never_handed_a_run()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "single-slot", maxConcurrency: 1);

        var firstRunId = await RunTestHost.TriggerRunAsync(owner, projectId);
        using var workerClient = host.Host.CreateClient();
        using var firstClaim = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.OK, firstClaim.StatusCode);
        using var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, firstRunId);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        // The worker's single slot is busy (Running): the queued run stays queued.
        var secondRunId = await RunTestHost.TriggerRunAsync(owner, projectId, branch: "dev");
        using var busy = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.NoContent, busy.StatusCode);
        Assert.Null((await host.ReadRunAsync(secondRunId)).WorkerId);

        // Completing the run frees the slot; the next claim takes the second run.
        using var completed = await RunTestHost.CompleteAsync(workerClient, workerToken, firstRunId, exitCode: 0);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        using var freed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.OK, freed.StatusCode);
        Assert.Equal(secondRunId, await RunTestHost.ReadClaimedRunIdAsync(freed));
    }

    [Fact]
    public async Task A_queued_but_leased_run_occupies_the_workers_concurrency_slot()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "single-slot", maxConcurrency: 1);

        // Claim without accepting: the run stays Queued but leased, and the lease must occupy the
        // worker's only slot - a claim storm must not oversubscribe MaxConcurrency through leases
        // (the assigned count covers Running *and* queued-but-leased runs).
        var firstRunId = await RunTestHost.TriggerRunAsync(owner, projectId);
        using var workerClient = host.Host.CreateClient();
        using var firstClaim = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.OK, firstClaim.StatusCode);
        Assert.Equal(firstRunId, await RunTestHost.ReadClaimedRunIdAsync(firstClaim));
        var leased = await host.ReadRunAsync(firstRunId);
        Assert.Equal(BuildRunStatus.Queued, leased.Status);
        Assert.NotNull(leased.LeaseExpiresAt);

        // The next claim is a 204 and the second run keeps no worker: no second lease.
        var secondRunId = await RunTestHost.TriggerRunAsync(owner, projectId, branch: "dev");
        using var secondClaim = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.NoContent, secondClaim.StatusCode);
        var secondRun = await host.ReadRunAsync(secondRunId);
        Assert.Equal(BuildRunStatus.Queued, secondRun.Status);
        Assert.Null(secondRun.WorkerId);
        Assert.Null(secondRun.LeaseExpiresAt);
    }

    // ------------------------------------------------------------------
    // TC-004: lease expiry (short window from configuration, deterministic clock)
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_expired_unaccepted_lease_returns_to_the_queue_and_is_claimable_again()
    {
        using var host = await RunTestHost.CreateAsync(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runs:LeaseSeconds"] = "1",
            }));
        var (projectId, _) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var (firstToken, firstWorkerId) = await host.RegisterWorkerAsync(name: "agent-a");
        var (secondToken, secondWorkerId) = await host.RegisterWorkerAsync(name: "agent-b");
        var runId = await host.AddRunAsync(projectId, organizationId: organizationId);

        using var client = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(client, firstToken);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        var leaseExpiresAt = (await host.ReadRunAsync(runId)).LeaseExpiresAt;
        Assert.NotNull(leaseExpiresAt);
        Assert.True(leaseExpiresAt!.Value > DateTimeOffset.UtcNow.AddMilliseconds(-100));

        // The worker never reports accepted; the lease expires and the sweeper requeues.
        await host.ExpireLeaseAsync(runId);
        Assert.Equal(1, await host.SweepAsync());
        var requeued = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Queued, requeued.Status);
        Assert.Null(requeued.WorkerId);
        Assert.Null(requeued.LeaseExpiresAt);

        // Claimable again - by another worker, exactly once.
        using var reclaimed = await RunTestHost.ClaimAsync(client, secondToken);
        Assert.Equal(HttpStatusCode.OK, reclaimed.StatusCode);
        Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(reclaimed));
        Assert.Equal(secondWorkerId, (await host.ReadRunAsync(runId)).WorkerId);
        Assert.NotEqual(firstWorkerId, (await host.ReadRunAsync(runId)).WorkerId);
    }

    [Fact]
    public async Task The_sweeper_never_touches_an_accepted_running_run()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "agent", maxConcurrency: 2);
        var runningRunId = await RunTestHost.TriggerRunAsync(owner, projectId);
        var leasedRunId = await host.AddRunAsync(projectId, organizationId: organizationId);

        using var client = host.Host.CreateClient();

        // The worker takes both runs (capacity 2); the first is accepted, the second stays leased.
        using var firstClaim = await RunTestHost.ClaimAsync(client, workerToken);
        Assert.Equal(runningRunId, await RunTestHost.ReadClaimedRunIdAsync(firstClaim)); // FIFO
        using var secondClaim = await RunTestHost.ClaimAsync(client, workerToken);
        Assert.Equal(leasedRunId, await RunTestHost.ReadClaimedRunIdAsync(secondClaim));
        using var accepted = await RunTestHost.AcceptAsync(client, workerToken, runningRunId);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        await host.ExpireLeaseAsync(leasedRunId);
        Assert.Equal(1, await host.SweepAsync());

        var running = await host.ReadRunAsync(runningRunId);
        Assert.Equal(BuildRunStatus.Running, running.Status);
        Assert.NotNull(running.StartedAt);
        Assert.NotNull(running.WorkerId);
        Assert.Null(running.LeaseExpiresAt);

        var leased = await host.ReadRunAsync(leasedRunId);
        Assert.Equal(BuildRunStatus.Queued, leased.Status);
        Assert.Null(leased.WorkerId);
        Assert.Null(leased.LeaseExpiresAt);
    }

    // ------------------------------------------------------------------
    // TC-005: transition and ownership guards
    // ------------------------------------------------------------------

    [Fact]
    public async Task Completing_before_accepting_is_a_409_and_changes_nothing()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "agent");
        var runId = await host.AddRunAsync(projectId, organizationId: organizationId);

        using var client = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(client, workerToken);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

        using var completed = await RunTestHost.CompleteAsync(client, workerToken, runId, exitCode: 0);
        Assert.Equal(HttpStatusCode.Conflict, completed.StatusCode);
        Assert.Equal(
            RunErrorCodes.InvalidTransition,
            await ReadErrorPropertyAsync(completed));

        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Queued, run.Status);
        Assert.Null(run.ExitCode);
        Assert.Null(run.EndedAt);
        Assert.NotNull(run.WorkerId); // the lease itself is untouched
    }

    [Fact]
    public async Task Completing_an_unclaimed_run_is_a_409()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "agent");
        var runId = await host.AddRunAsync(projectId, organizationId: organizationId);

        using var client = host.Host.CreateClient();
        using var completed = await RunTestHost.CompleteAsync(client, workerToken, runId, exitCode: 0);

        Assert.Equal(HttpStatusCode.Conflict, completed.StatusCode);
        Assert.Equal(
            RunErrorCodes.RunNotAssignedToWorker,
            await ReadErrorPropertyAsync(completed));
        Assert.Equal(BuildRunStatus.Queued, (await host.ReadRunAsync(runId)).Status);
    }

    [Fact]
    public async Task Accepting_twice_is_a_409()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "agent");
        var runId = await host.AddRunAsync(projectId, organizationId: organizationId);

        using var client = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(client, workerToken);
        using var accepted = await RunTestHost.AcceptAsync(client, workerToken, runId);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using var second = await RunTestHost.AcceptAsync(client, workerToken, runId);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(RunErrorCodes.InvalidTransition, await ReadErrorPropertyAsync(second));
        Assert.Equal(BuildRunStatus.Running, (await host.ReadRunAsync(runId)).Status);
    }

    [Fact]
    public async Task Accepting_after_the_lease_window_is_a_409()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "agent");
        var runId = await host.AddRunAsync(projectId, organizationId: organizationId);

        using var client = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(client, workerToken);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

        await host.ExpireLeaseAsync(runId);
        using var accepted = await RunTestHost.AcceptAsync(client, workerToken, runId);
        Assert.Equal(HttpStatusCode.Conflict, accepted.StatusCode);
        Assert.Equal(RunErrorCodes.LeaseExpired, await ReadErrorPropertyAsync(accepted));
        Assert.Equal(BuildRunStatus.Queued, (await host.ReadRunAsync(runId)).Status);
        Assert.Null((await host.ReadRunAsync(runId)).StartedAt);
    }

    [Fact]
    public async Task Completing_with_exit_3_is_Failed()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "agent");
        var runId = await host.AddRunAsync(projectId, organizationId: organizationId);

        using var client = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(client, workerToken);
        using var accepted = await RunTestHost.AcceptAsync(client, workerToken, runId);
        using var completed = await RunTestHost.CompleteAsync(client, workerToken, runId, exitCode: 3);

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(BuildRunStatus.Failed, await RunTestHost.ReadReportStatusAsync(completed));
        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Failed, run.Status);
        Assert.Equal(3, run.ExitCode);
        Assert.NotNull(run.EndedAt);
        Assert.Null(run.FailureReason);
    }

    [Fact]
    public async Task Another_worker_of_the_same_organization_cannot_report_a_foreign_run()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var (ownerToken, _) = await host.RegisterWorkerAsync(name: "agent-a");
        var (foreignToken, _) = await host.RegisterWorkerAsync(name: "agent-b");
        var runId = await host.AddRunAsync(projectId, organizationId: organizationId);

        using var client = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(client, ownerToken);
        using var accepted = await RunTestHost.AcceptAsync(client, ownerToken, runId);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using var foreignAccepted = await RunTestHost.AcceptAsync(client, foreignToken, runId);
        Assert.Equal(HttpStatusCode.Conflict, foreignAccepted.StatusCode);
        Assert.Equal(
            RunErrorCodes.RunNotAssignedToWorker,
            await ReadErrorPropertyAsync(foreignAccepted));
        using var foreignCompleted = await RunTestHost.CompleteAsync(client, foreignToken, runId, exitCode: 0);
        Assert.Equal(HttpStatusCode.Conflict, foreignCompleted.StatusCode);
        Assert.Equal(
            RunErrorCodes.RunNotAssignedToWorker,
            await ReadErrorPropertyAsync(foreignCompleted));

        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Running, run.Status);
        Assert.Null(run.ExitCode);
        Assert.Null(run.EndedAt);
    }

    // ------------------------------------------------------------------
    // Tenant isolation
    // ------------------------------------------------------------------

    [Fact]
    public async Task Another_organizations_worker_never_receives_a_queued_run_or_a_foreign_run_id()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var runId = await host.AddRunAsync(projectId, organizationId: organizationId);

        // Organization B: a real second organization with its own worker token.
        var bravoId = await host.Inner.Inner.AddOrganizationAsync(
            "Bravo",
            (OrganizationTestHost.OwnerEmail, OrganizationRole.Owner));
        var bravoToken = await host.CreateWorkerTokenDirectlyAsync(bravoId, name: "bravo-agent");

        using var client = host.Host.CreateClient();

        // B's claim sees only B's queue: 204, and A's run is untouched.
        using var bravoClaim = await RunTestHost.ClaimAsync(client, bravoToken);
        Assert.Equal(HttpStatusCode.NoContent, bravoClaim.StatusCode);
        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Queued, run.Status);
        Assert.Null(run.WorkerId);
        Assert.Null(run.LeaseExpiresAt);

        // B's worker token against A's run id: 404, no data.
        using var bravoAccepted = await RunTestHost.AcceptAsync(client, bravoToken, runId);
        Assert.Equal(HttpStatusCode.NotFound, bravoAccepted.StatusCode);
        using var bravoCompleted = await RunTestHost.CompleteAsync(client, bravoToken, runId, exitCode: 0);
        Assert.Equal(HttpStatusCode.NotFound, bravoCompleted.StatusCode);
        Assert.Equal(BuildRunStatus.Queued, (await host.ReadRunAsync(runId)).Status);

        // A's own worker still claims exactly that run afterwards.
        var (ownToken, _) = await host.RegisterWorkerAsync(name: "starter-agent");
        using var ownClaim = await RunTestHost.ClaimAsync(client, ownToken);
        Assert.Equal(HttpStatusCode.OK, ownClaim.StatusCode);
        Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(ownClaim));
    }

    [Fact]
    public async Task A_bad_or_missing_worker_token_is_401_and_claims_nothing()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var runId = await host.AddRunAsync(projectId, organizationId: organizationId);

        using var client = host.Host.CreateClient();
        using var bad = await RunTestHost.ClaimAsync(client, "zwrk_totally-unknown-token-value");
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        using var missing = await RunTestHost.ClaimAsync(client, null);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        using var badAccepted = await RunTestHost.AcceptAsync(client, "zwrk_totally-unknown-token-value", runId);
        Assert.Equal(HttpStatusCode.Unauthorized, badAccepted.StatusCode);
        using var badCompleted = await RunTestHost.CompleteAsync(client, "zwrk_totally-unknown-token-value", runId, exitCode: 0);
        Assert.Equal(HttpStatusCode.Unauthorized, badCompleted.StatusCode);

        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Queued, run.Status);
        Assert.Null(run.WorkerId);
        Assert.Null(run.LeaseExpiresAt);
    }

    [Fact]
    public async Task Run_pages_of_another_organization_are_404()
    {
        using var host = await RunTestHost.CreateAsync();
        var (_, owner) = await host.CreateProjectAsync("starter-app");
        var bravoId = await host.Inner.Inner.AddOrganizationAsync(
            "Bravo",
            (OrganizationTestHost.OwnerEmail, OrganizationRole.Owner));
        var bravoProjectId = await host.Inner.Inner.AddProjectAsync(bravoId, "beta-app");
        var bravoRunId = await host.AddRunAsync(bravoProjectId, organizationId: bravoId);

        // HTML navigation (the F-003 B1 hazard: /not-found must not be re-executed here).
        using var htmlRequest = new HttpRequestMessage(HttpMethod.Get, $"/runs/{bravoRunId}");
        htmlRequest.Headers.Accept.ParseAdd("text/html");
        using var htmlResponse = await owner.SendAsync(htmlRequest, CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, htmlResponse.StatusCode);
        Assert.DoesNotContain("beta-app", await htmlResponse.Content.ReadAsStringAsync(CancellationToken), StringComparison.Ordinal);

        using var runsResponse = await owner.GetAsync($"/projects/{bravoProjectId}/runs", CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, runsResponse.StatusCode);

        // The trigger form POSTed against a foreign project is 404 and stores nothing new. The
        // token comes from the owner's own project page (the foreign page itself is a 404, so
        // PostFormAsync cannot fetch a token there).
        var ownProjectId = (await host.Inner.Inner.ReadProjectsAsync(
            await host.OrganizationIdAsync())).Single().Id;
        var ownPage = await AuthTestHost.GetStringAsync(owner, $"/projects/{ownProjectId}");
        var token = OrganizationTestHost.ExtractAntiforgeryMetaToken(ownPage);
        using var startResponse = await owner.PostAsync(
            $"/projects/{bravoProjectId}/runs/start",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Branch"] = "main",
                ["__RequestVerificationToken"] = token,
            }),
            CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, startResponse.StatusCode);
        Assert.Single(await host.ReadRunsAsync(bravoId));
    }

    // ------------------------------------------------------------------
    // Trigger form
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_anonymous_start_run_post_is_redirected_to_sign_in()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();

        // A client with no cookie jar: authorization challenges before anything else happens
        // (no antiforgery token needed - the challenge never reaches the endpoint).
        using var anonymous = host.Host.CreateClient();
        using var response = await anonymous.PostAsync(
            $"/projects/{projectId}/runs/start",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Branch"] = "main",
            }),
            CancellationToken);

        Assert.True((int)response.StatusCode is 302 or 303);
        var location = response.Headers.Location!.OriginalString;
        Assert.Contains("/account/login", location, StringComparison.Ordinal);

        // Same for the runs pages.
        using var listPage = await anonymous.GetAsync($"/projects/{projectId}/runs", CancellationToken);
        Assert.True((int)listPage.StatusCode is 302 or 303);
        Assert.Contains("/account/login", listPage.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invalid_branch_is_a_field_error_and_creates_no_run()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();

        var response = await RunTestHost.StartRunAsync(owner, projectId, branch: "-not-a-branch");

        Assert.True((int)response.StatusCode is 302 or 303);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith($"/projects/{projectId}?", location, StringComparison.Ordinal);
        Assert.Contains("runError=branch-invalid", location, StringComparison.Ordinal);
        var page = await AuthTestHost.GetStringAsync(owner, location);
        Assert.Contains("id=\"run-branch-error\"", page, StringComparison.Ordinal);
        Assert.Empty(await host.ReadRunsAsync(organizationId));
    }

    [Fact]
    public async Task A_required_tag_no_worker_has_yet_is_queued_and_records_what_it_waits_on()
    {
        // Card F-033 revised this contract (it used to refuse with tag-unknown). A tag nobody carries is
        // ordinary operation - the matching Worker may register in ten minutes - and a refusal here would
        // turn every poll/webhook/schedule trigger into a silent drop, because their caller has nobody to
        // read an error. The run is created, keeps `windows` as its normalized requirement, and stays
        // Queued until a worker carrying it claims it.
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "agent", tags: "linux");

        var response = await RunTestHost.StartRunAsync(owner, projectId, requiredTags: "windows");

        var run = Assert.Single(await host.ReadRunsAsync(organizationId));
        Assert.Equal(BuildRunStatus.Queued, run.Status);
        Assert.Equal("windows", run.RequiredTags);
        Assert.Equal("windows", run.RequiredRequirement);

        // ...and the linux-only worker cannot claim it, which is the point of keeping it queued rather than
        // building on the wrong machine.
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.NoContent, claimed.StatusCode);
        Assert.Contains("/runs/", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_required_tag_over_64_characters_is_a_field_error_and_creates_no_run()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        await host.RegisterWorkerAsync(name: "agent", tags: "linux");

        // A single over-long tag must not vanish into "no required tags" (a run any worker could
        // claim): it is a field-level tag-invalid error against the form's 64-character copy.
        var response = await RunTestHost.StartRunAsync(
            owner,
            projectId,
            requiredTags: new string('x', RunValidation.MaxTagLength + 1));

        var location = response.Headers.Location!.OriginalString;
        Assert.Contains("runError=tag-invalid", location, StringComparison.Ordinal);
        var page = await AuthTestHost.GetStringAsync(owner, location);
        Assert.Contains("id=\"run-required-tags-error\"", page, StringComparison.Ordinal);
        Assert.Contains("at most 64 characters per tag", page, StringComparison.Ordinal);
        Assert.Empty(await host.ReadRunsAsync(organizationId));
    }

    [Fact]
    public async Task A_member_can_trigger_a_run_and_required_tags_are_persisted()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        const string memberEmail = "bob@example.test";
        using var memberClient = await host.Inner.Inner.RegisterUserAsync(memberEmail);
        var invite = await OrganizationTestHost.PostFormAsync(
            owner,
            "/organizations/members",
            "/organizations/members/invite",
            new Dictionary<string, string> { ["Email"] = memberEmail });
        Assert.True((int)invite.StatusCode is 302 or 303);
        await host.RegisterWorkerAsync(name: "agent", tags: "linux");

        // A Member (not an Owner) triggers with required tags that exist.
        var runId = await RunTestHost.TriggerRunAsync(
            memberClient,
            projectId,
            requiredTags: "linux");

        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Queued, run.Status);
        Assert.Equal("linux", run.RequiredTags);
        Assert.Equal(
            (await host.Inner.Inner.ReadUserAsync(memberEmail)).Id,
            run.TriggerUserId);

        // The run is visible on the project's runs list.
        var list = await AuthTestHost.GetStringAsync(memberClient, $"/projects/{projectId}/runs");
        Assert.Contains("bob@example.test", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_runs_list_of_a_project_without_runs_is_empty_but_200()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();

        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/runs");

        Assert.Contains("No runs yet", page, StringComparison.Ordinal);
        Assert.Contains("Start run", page, StringComparison.Ordinal);
    }

    private static async Task<string> ReadErrorPropertyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("error").GetString()
            ?? throw new InvalidOperationException("The error response had no code.");
    }
}
