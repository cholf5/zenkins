using System.Net;
using Xunit;
using Zenkins.Server.Auth;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Runs;
using Zenkins.Tests.Workers;

// The worker project's namespace shadows these names; the aliases keep them readable (same as the
// other hosts' files).
using WorkerTokenCrypto = Zenkins.Server.Workers.WorkerTokenCrypto;

namespace Zenkins.Tests.WorkersUi;

/// <summary>
/// Card D-005's controls, driven through the real forms and the real worker API:
/// <list type="bullet">
/// <item>TC-002 - disabling stops claiming (the real claim endpoint answers the not-assigned 204)
/// while the run already in flight continues to completion; re-enabling restores claiming and the
/// queued run executes exactly once.</item>
/// <item>Revoking a registration token blocks a new registration with it immediately and leaves an
/// already-registered worker's heartbeats untouched.</item>
/// <item>TC-003 - a worker with run history is refused deletion with a message naming the runs; a
/// worker without history is deleted; no run loses its worker reference.</item>
/// <item>Permissions - the UI offers a Member nothing, and a Member's forged POST is a 403 that
/// changes nothing.</item>
/// </list>
/// </summary>
[Trait("Category", "WorkersUi")]
public sealed class WorkersUiControlsTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task Disabling_stops_claims_while_the_in_flight_run_finishes_and_enable_restores()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await WorkersUiHost.CreateAsync();
        var (projectId, owner) = await host.Runs.CreateProjectAsync();
        var (token, workerId) = await host.Runs.RegisterWorkerAsync(name: "disable-agent", maxConcurrency: 2);
        var client = host.Host.CreateClient();

        // Start the long run: claimed and accepted (Running).
        var firstRunId = await host.Runs.AddRunAsync(projectId);
        using (var claim = await RunTestHost.ClaimAsync(client, token))
        {
            Assert.Equal(firstRunId, await RunTestHost.ReadClaimedRunIdAsync(claim));
        }

        using (var accept = await RunTestHost.AcceptAsync(client, token, firstRunId))
        {
            Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        }

        // Disable through the real Owner form; the answer is a PRG redirect to the detail page,
        // and the row carries the flag the claim query reads.
        MembershipAuthorizationProbes.AssertRedirect(
            await host.PostWorkerActionAsync(owner, workerId, "disable"),
            $"/workers/{workerId}?notice=worker-disabled");
        Assert.True((await host.Runs.ReadWorkerAsync(workerId)).IsDisabled);
        var disabledPage = await host.ReadWorkerDetailAsync(owner, workerId);
        Assert.Contains("Disabled", disabledPage, StringComparison.Ordinal);
        Assert.Contains("Enable worker", disabledPage, StringComparison.Ordinal);

        // The disabled worker's next claim gets the not-assigned answer - even though a
        // concurrency slot is free - and the second run stays queued.
        var secondRunId = await host.Runs.AddRunAsync(projectId);
        using (var refused = await RunTestHost.ClaimAsync(client, token))
        {
            Assert.Equal(HttpStatusCode.NoContent, refused.StatusCode);
        }

        Assert.Equal(BuildRunStatus.Queued, (await host.Runs.ReadRunAsync(secondRunId)).Status);

        // Disable means stop claiming, not stop running: the in-flight run reports completion
        // through the real endpoint and finishes normally.
        using (var completed = await RunTestHost.CompleteAsync(client, token, firstRunId, 0))
        {
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        }

        Assert.Equal(BuildRunStatus.Success, (await host.Runs.ReadRunAsync(firstRunId)).Status);

        // Still disabled: still no claims.
        using (var stillRefused = await RunTestHost.ClaimAsync(client, token))
        {
            Assert.Equal(HttpStatusCode.NoContent, stillRefused.StatusCode);
        }

        // Re-enable through the real form; the same queued run is claimed and executed exactly once.
        MembershipAuthorizationProbes.AssertRedirect(
            await host.PostWorkerActionAsync(owner, workerId, "enable"),
            $"/workers/{workerId}?notice=worker-enabled");
        Assert.False((await host.Runs.ReadWorkerAsync(workerId)).IsDisabled);

        Guid claimedSecondRunId;
        using (var restored = await RunTestHost.ClaimAsync(client, token))
        {
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            claimedSecondRunId = await RunTestHost.ReadClaimedRunIdAsync(restored);
        }

        Assert.Equal(secondRunId, claimedSecondRunId);
        using (var accept = await RunTestHost.AcceptAsync(client, token, secondRunId))
        {
            Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        }

        using (var complete = await RunTestHost.CompleteAsync(client, token, secondRunId, 0))
        {
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        }

        Assert.Equal(BuildRunStatus.Success, (await host.Runs.ReadRunAsync(secondRunId)).Status);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task Revoking_a_registration_token_blocks_new_registration_but_not_existing_heartbeats()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await WorkersUiHost.CreateAsync();
        var owner = await host.Runs.Inner.SignInOwnerAsync();
        var organizationId = await host.Runs.Inner.Inner.ReadOrganizationIdAsync(
            BootstrapService.StarterOrganizationName);
        var api = host.Host.CreateClient();

        // An already-registered worker with its own worker token, heartbeating once.
        var (existingToken, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        using var registered = await WorkerTestHost.RegisterAsync(
            owner,
            existingToken,
            WorkerTestHost.RegistrationPayload(name: "heartbeating-agent"));
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var workerId = Guid.Parse(await WorkerTestHost.ReadStringPropertyAsync(registered, "workerId"));
        var workerToken = await WorkerTestHost.ReadStringPropertyAsync(registered, "workerToken");
        using (var beat = await WorkerTestHost.HeartbeatAsync(api, workerToken, WorkerTestHost.HeartbeatPayload()))
        {
            Assert.Equal(HttpStatusCode.OK, beat.StatusCode);
        }

        var heartbeatBefore = (await host.Runs.ReadWorkerAsync(workerId)).LastHeartbeatAt;
        Assert.NotNull(heartbeatBefore);

        // Mint a fresh registration token and revoke it through the real roster form.
        var (pendingToken, _) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        var pendingRow = (await host.Runs.Inner.ReadWorkerTokensAsync(organizationId))
            .Single(row => row.TokenHash == WorkerTokenCrypto.Hash(pendingToken));
        MembershipAuthorizationProbes.AssertRedirect(
            await OrganizationTestHost.PostFormAsync(
                owner,
                "/workers",
                $"/workers/registration-tokens/{pendingRow.Id}/revoke",
                new Dictionary<string, string>()),
            "/workers?notice=token-revoked");

        // A new registration with the revoked token is refused immediately - and creates nothing.
        using var refusedRegistration = await WorkerTestHost.RegisterAsync(
            owner,
            pendingToken,
            WorkerTestHost.RegistrationPayload(name: "late-arrival"));
        Assert.Equal(HttpStatusCode.Unauthorized, refusedRegistration.StatusCode);
        Assert.Single(await host.Runs.Inner.ReadWorkersAsync(organizationId), worker => worker.Id == workerId);

        // The already-registered worker's heartbeats are unaffected: the next beat lands.
        using (var beat = await WorkerTestHost.HeartbeatAsync(api, workerToken, WorkerTestHost.HeartbeatPayload()))
        {
            Assert.Equal(HttpStatusCode.OK, beat.StatusCode);
        }

        var heartbeatAfter = (await host.Runs.ReadWorkerAsync(workerId)).LastHeartbeatAt;
        Assert.NotNull(heartbeatAfter);
        Assert.True(heartbeatAfter > heartbeatBefore, "the heartbeat did not refresh after the token revoke.");
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_worker_with_run_history_is_not_deletable_and_one_without_runs_is()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await WorkersUiHost.CreateAsync();
        var (projectId, owner) = await host.Runs.CreateProjectAsync();
        var (token, workerId) = await host.Runs.RegisterWorkerAsync(name: "history-agent");
        var (_, idleWorkerId) = await host.Runs.RegisterWorkerAsync(name: "fresh-agent");
        var client = host.Host.CreateClient();
        var organizationId = await host.Runs.Inner.Inner.ReadOrganizationIdAsync(
            BootstrapService.StarterOrganizationName);

        // The history worker completes one run through the real endpoints.
        var runId = await host.Runs.AddRunAsync(projectId);
        using (var claim = await RunTestHost.ClaimAsync(client, token))
        {
            Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(claim));
        }

        using (var accept = await RunTestHost.AcceptAsync(client, token, runId))
        {
            Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        }

        using (var complete = await RunTestHost.CompleteAsync(client, token, runId, 0))
        {
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        }

        // Deleting it is refused with a PRG answer that names the runs.
        MembershipAuthorizationProbes.AssertRedirect(
            await host.PostWorkerActionAsync(owner, workerId, "delete"),
            $"/workers/{workerId}?error=worker-has-runs&runs=1");
        Assert.Single(
            await host.Runs.Inner.ReadWorkersAsync(organizationId),
            worker => worker.Id == workerId);

        // The page the redirect lands on (the browser follows the Location above) names the runs.
        var refusalPage = await AuthTestHost.GetStringAsync(
            owner,
            $"/workers/{workerId}?error=worker-has-runs&runs=1");
        Assert.Contains("executed 1 run", refusalPage, StringComparison.Ordinal);
        Assert.Contains("keep their worker reference", refusalPage, StringComparison.Ordinal);

        // The worker without run history deletes fine.
        MembershipAuthorizationProbes.AssertRedirect(
            await host.PostWorkerActionAsync(owner, idleWorkerId, "delete"),
            "/workers?notice=worker-deleted");
        Assert.DoesNotContain(
            idleWorkerId.ToString(),
            await host.ReadRosterAsync(owner),
            StringComparison.Ordinal);
        Assert.Single(
            await host.Runs.Inner.ReadWorkersAsync(organizationId),
            worker => worker.Id == workerId);

        // No run lost its worker reference in either case.
        Assert.Equal(workerId, (await host.Runs.ReadRunAsync(runId)).WorkerId);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_members_forged_post_is_403_and_changes_nothing()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await WorkersUiHost.CreateAsync();
        var owner = await host.Runs.Inner.SignInOwnerAsync();
        var (_, workerId) = await host.Runs.RegisterWorkerAsync(name: "member-gate-agent");

        // F-002 invites existing accounts: register dave, then invite him as a Member.
        const string memberEmail = "dave@example.test";
        var member = await host.Runs.Inner.Inner.RegisterUserAsync(memberEmail);
        MembershipAuthorizationProbes.AssertRedirect(
            await OrganizationTestHost.PostFormAsync(
                owner,
                "/organizations/members",
                "/organizations/members/invite",
                new Dictionary<string, string> { ["Email"] = memberEmail }),
            "/organizations/members?notice=invited");

        // The Member reads the detail page but is offered no controls - the UI does not offer
        // what the server would refuse.
        var memberPage = await host.ReadWorkerDetailAsync(member, workerId);
        Assert.Contains("member-gate-agent", memberPage, StringComparison.Ordinal);
        Assert.DoesNotContain("Disable worker", memberPage, StringComparison.Ordinal);
        Assert.DoesNotContain("Delete worker", memberPage, StringComparison.Ordinal);
        Assert.DoesNotContain($"/workers/{workerId}/disable", memberPage, StringComparison.Ordinal);
        Assert.DoesNotContain($"/workers/{workerId}/delete", memberPage, StringComparison.Ordinal);

        // Forged POSTs: 403 on every action, and the worker row is unchanged.
        foreach (var action in new[] { "disable", "enable", "delete" })
        {
            using var forged = await host.PostWorkerActionAsync(member, workerId, action);
            Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        }

        Assert.False((await host.Runs.ReadWorkerAsync(workerId)).IsDisabled);
    }
}
