using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;
using Zenkins.Server.Workers;
using Zenkins.Shared.RunCancellation;
using Zenkins.Tests.LogStreaming;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.RunLifecycle;

/// <summary>
/// F-009's cancellation surface, server side: the queued cancel's CAS discipline under a real
/// claim storm, the cancel-request marking, the heartbeat fallback payload, the SignalR
/// <c>cancelRun</c> delivery, the canceled report's guards and outcome mapping, and the no-op /
/// authorization behaviour the card's decisions pin. The process-killing integration cases live
/// in <see cref="RunWorkerCancellationTests"/>, the deadline/grace behaviour in
/// <see cref="RunDeadlineSweepTests"/>.
/// </summary>
[Trait("Category", "RunLifecycle")]
public sealed class RunCancellationTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_canceled_queued_run_is_never_handed_out_by_a_concurrent_claim_storm()
    {
        using var host = await RunLifecycleHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var workers = new List<(HttpClient Client, string Token)>();
        for (var index = 0; index < 3; index++)
        {
            var (token, _) = await host.RegisterWorkerAsync(name: "storm-agent", maxConcurrency: 8);
            workers.Add((host.Host.CreateClient(), token));
        }

        var runId = await host.AddRunAsync(projectId, organizationId: organizationId);

        // The cancel commits FIRST - then the storm. Nothing about "the claim must never obtain a
        // canceled run" is left to timing: every claim's compare-and-set re-evaluates
        // Status == 'Queued' against a row that is already Canceled.
        var cancelResponse = await RunLifecycleHost.CancelRunAsync(owner, runId);
        Assert.Equal(HttpStatusCode.Found, cancelResponse.StatusCode);
        Assert.EndsWith("?notice=canceled", cancelResponse.Headers.Location!.OriginalString, StringComparison.Ordinal);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimTasks = workers
            .SelectMany(worker => Enumerable.Range(0, 8).Select(_ => worker))
            .Select(worker => Task.Run(async () =>
            {
                await start.Task.WaitAsync(CancellationToken);
                using var response = await RunTestHost.ClaimAsync(worker.Client, worker.Token);
                return response.StatusCode;
            }))
            .ToArray();
        start.SetResult();
        var statuses = await Task.WhenAll(claimTasks);

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.NoContent, status));

        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Canceled, run.Status);
        Assert.StartsWith(RunCancelReasons.CanceledByUser, run.FailureReason, StringComparison.Ordinal);
        Assert.Contains(OrganizationTestHost.OwnerEmail, run.FailureReason, StringComparison.Ordinal);
        Assert.Null(run.WorkerId);
        Assert.Null(run.LeaseExpiresAt);
        Assert.NotNull(run.EndedAt);
    }

    [Fact]
    public async Task Cancel_racing_a_claim_storm_never_yields_two_claimants_or_an_executed_canceled_run()
    {
        using var host = await RunLifecycleHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var workers = new List<(HttpClient Client, string Token)>();
        for (var index = 0; index < 3; index++)
        {
            var (token, _) = await host.RegisterWorkerAsync(name: "race-agent", maxConcurrency: 8);
            workers.Add((host.Host.CreateClient(), token));
        }

        var runId = await host.AddRunAsync(projectId, organizationId: organizationId);

        // True concurrency: one cancel and nine claims released through one barrier. Every
        // outcome the race can produce is pinned - the invariants, not a single interleaving.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimTasks = workers
            .SelectMany(worker => Enumerable.Range(0, 3).Select(_ => worker))
            .Select(worker => Task.Run(async () =>
            {
                await start.Task.WaitAsync(CancellationToken);
                using var claim = await RunTestHost.ClaimAsync(worker.Client, worker.Token);
                if (claim.StatusCode != HttpStatusCode.OK)
                {
                    Assert.Equal(HttpStatusCode.NoContent, claim.StatusCode);
                    return (HandedOut: false, AcceptStatus: (HttpStatusCode?)null);
                }

                // A claim that did hand the run out immediately tries the accepted edge - the
                // race-window semantic the card pins: cancel-before-accept must make this a 409.
                var claimedRunId = await RunTestHost.ReadClaimedRunIdAsync(claim);
                Assert.Equal(runId, claimedRunId);
                using var accept = await RunTestHost.AcceptAsync(worker.Client, worker.Token, runId);
                return (HandedOut: true, AcceptStatus: (HttpStatusCode?)accept.StatusCode);
            }))
            .ToArray();
        var cancelTask = Task.Run(async () =>
        {
            await start.Task.WaitAsync(CancellationToken);
            using var response = await RunLifecycleHost.CancelRunAsync(owner, runId);
            return response.StatusCode;
        });
        start.SetResult();
        var claims = await Task.WhenAll(claimTasks);
        var cancelStatus = await cancelTask;

        Assert.Equal(HttpStatusCode.Found, cancelStatus);
        var handedOut = claims.Count(claim => claim.HandedOut);
        Assert.True(handedOut <= 1, $"The run was handed out {handedOut} times by one storm.");

        var run = await host.ReadRunAsync(runId);
        if (run.Status == BuildRunStatus.Canceled)
        {
            // The cancel won before any accepted edge: nobody may have executed the run.
            Assert.StartsWith(RunCancelReasons.CanceledByUser, run.FailureReason, StringComparison.Ordinal);
            Assert.Null(run.WorkerId);
            Assert.DoesNotContain(claims, claim => claim.AcceptStatus == HttpStatusCode.OK);
        }
        else
        {
            // The accepted edge won the race window: the run stays Running with a recorded cancel
            // request (the Running cancel path) - never Canceled behind an executing worker.
            Assert.Equal(BuildRunStatus.Running, run.Status);
            Assert.NotNull(run.CancelRequestedAt);
            Assert.StartsWith(RunCancelReasons.CanceledByUser, run.CancelReason, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, claims.Single(claim => claim.HandedOut).AcceptStatus);
        }

        // And the queue is closed for this run either way: further claims never return it again.
        var drain = new List<HttpStatusCode>();
        foreach (var (client, token) in workers)
        {
            using var response = await RunTestHost.ClaimAsync(client, token);
            drain.Add(response.StatusCode);
        }

        Assert.DoesNotContain(HttpStatusCode.OK, drain);
    }

    /// <summary>The CAS-discipline pin (and the documented red-proof vehicle): the queued-cancel
    /// compare-and-set refuses a run that is no longer Queued. Stripping the
    /// <c>Status == Queued</c> condition from <c>TryCancelQueuedBuildRunAsync</c> turns this red
    /// (rows 1 instead of 0, a Running run flipped Canceled without any cancel request).</summary>
    [Fact]
    public async Task The_queued_cancel_compare_and_set_only_touches_a_queued_run()
    {
        using var host = await RunLifecycleHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync();
        var (token, _) = await host.RegisterWorkerAsync(name: "cas-pin-agent");
        var runId = await host.AddRunAsync(projectId);

        await RunTestHost.ClaimAsync(host.Host.CreateClient(), token);
        await RunTestHost.AcceptAsync(host.Host.CreateClient(), token, runId);

        var (rows, status) = await host.CancelQueuedViaCasAsync(
            runId,
            RunCancelReasons.ForUser(OrganizationTestHost.OwnerEmail));

        Assert.Equal(0, rows);
        Assert.Equal(BuildRunStatus.Running, status);

        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Running, run.Status);
        Assert.Null(run.CancelRequestedAt);
        Assert.Null(run.FailureReason);
    }

    [Fact]
    public async Task The_heartbeat_response_carries_the_workers_pending_cancel_requests()
    {
        using var host = await RunLifecycleHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var (tokenA, workerAId) = await host.RegisterWorkerAsync(name: "beat-agent-a");
        var (tokenB, _) = await host.RegisterWorkerAsync(name: "beat-agent-b");
        var runId = await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, tokenA);
        await RunTestHost.AcceptAsync(client, tokenA, runId);

        // Before the cancel: no pending requests on either worker's heartbeat.
        using (var before = await RunLifecycleHost.HeartbeatAsync(client, tokenA))
        {
            Assert.Empty(before.RootElement.GetProperty("cancelRequestedRuns").EnumerateArray());
        }

        using (var response = await RunLifecycleHost.CancelRunAsync(owner, runId))
        {
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }

        // The executing worker's heartbeat carries the run and the recorded reason; the other
        // worker's heartbeat carries nothing of it.
        using (var heartbeat = await RunLifecycleHost.HeartbeatAsync(client, tokenA))
        {
            var requests = heartbeat.RootElement.GetProperty("cancelRequestedRuns").EnumerateArray().ToArray();
            var entry = Assert.Single(requests);
            Assert.Equal(runId, entry.GetProperty("runId").GetGuid());
            Assert.StartsWith(RunCancelReasons.CanceledByUser, entry.GetProperty("reason").GetString(), StringComparison.Ordinal);
        }

        using (var otherHeartbeat = await RunLifecycleHost.HeartbeatAsync(client, tokenB))
        {
            Assert.Empty(otherHeartbeat.RootElement.GetProperty("cancelRequestedRuns").EnumerateArray());
        }

        // After the canceled report the run is terminal: it leaves the heartbeat payload.
        using (var report = await RunLifecycleHost.ReportCanceledAsync(client, tokenA, runId, RunCanceledOutcomes.Canceled))
        {
            Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        }

        using (var after = await RunLifecycleHost.HeartbeatAsync(client, tokenA))
        {
            Assert.Empty(after.RootElement.GetProperty("cancelRequestedRuns").EnumerateArray());
        }

        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Canceled, run.Status);
        Assert.StartsWith(RunCancelReasons.CanceledByUser, run.FailureReason, StringComparison.Ordinal);
        Assert.Null(run.ExitCode);
        Assert.NotNull(run.EndedAt);
        Assert.Equal(workerAId, run.WorkerId);
    }

    [Fact]
    public async Task The_cancel_run_signal_is_delivered_over_the_worker_hub_group()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await LogStreamingHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var (token, _) = await host.RegisterWorkerAsync(name: "hub-cancel-agent");
        var runId = await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, token);
        await RunTestHost.AcceptAsync(client, token, runId);

        var signal = new TaskCompletionSource<(Guid RunId, string? Reason)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = host.BuildWorkerHubConnection(token);
        connection.On<Guid, string?>("cancelRun", (run, reason) => signal.TrySetResult((run, reason)));
        await connection.StartAsync(cancellationToken);

        using var cancelResponse = await RunLifecycleHost.CancelRunAsync(owner, runId);
        Assert.Equal(HttpStatusCode.Found, cancelResponse.StatusCode);

        var delivered = await signal.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        Assert.Equal(runId, delivered.RunId);
        Assert.StartsWith(RunCancelReasons.CanceledByUser, delivered.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canceling_a_terminal_run_is_a_friendly_no_op_and_double_cancel_is_idempotent()
    {
        using var host = await RunLifecycleHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var (token, _) = await host.RegisterWorkerAsync(name: "noop-agent");
        var runId = await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, token);
        await RunTestHost.AcceptAsync(client, token, runId);
        await RunTestHost.CompleteAsync(client, token, runId, 0);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await RunLifecycleHost.CancelRunAsync(owner, runId);
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.EndsWith(
                "?notice=already-finished",
                response.Headers.Location!.OriginalString,
                StringComparison.Ordinal);
        }

        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Success, run.Status);
        Assert.Null(run.FailureReason);
        Assert.Null(run.CancelRequestedAt);

        // A canceled run: the same friendly no-op, and the recorded reason never changes.
        using (var cancel = await RunLifecycleHost.CancelRunAsync(owner, runId))
        {
            Assert.Equal(HttpStatusCode.Found, cancel.StatusCode);
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var again = await RunLifecycleHost.CancelRunAsync(owner, runId);
            Assert.EndsWith(
                "?notice=already-finished",
                again.Headers.Location!.OriginalString,
                StringComparison.Ordinal);
        }

        var canceled = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Success, canceled.Status);
    }

    [Fact]
    public async Task A_foreign_organizations_member_and_a_non_member_cannot_cancel()
    {
        using var host = await RunLifecycleHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync();
        var runId = await host.AddRunAsync(projectId);
        var original = await host.ReadRunAsync(runId);

        // Organization B: a registered member of another organization gets the same 404 a missing
        // id would give - no existence oracle (the form endpoint's hard-rejection rule).
        var foreignOrgId = await host.AddOrganizationAsync("cancel-foreign-org");
        Assert.NotEqual(await host.OrganizationIdAsync(), foreignOrgId);
        var foreignEmail = "foreign-cancel@example.test";
        await host.RegisterUserAsync(foreignEmail);
        await host.InSystemScopeAsync(async context =>
        {
            var user = await context.Users.SingleAsync(candidate => candidate.Email == foreignEmail);
            context.OrganizationMembers.Add(new OrganizationMember
            {
                OrganizationId = foreignOrgId,
                UserId = user.Id,
                Role = OrganizationRole.Member,
            });
            await context.SaveChangesAsync();
            return true;
        });
        var foreignMember = await host.SignInAsync(foreignEmail);
        using (var foreignResponse = await RunLifecycleHost.CancelRunFromPageAsync(
                   foreignMember, runId, "/"))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
        }

        // A memberless account: the service's membership check answers 403 (hard rejection).
        var memberlessEmail = "memberless-cancel@example.test";
        var memberless = await host.RegisterUserAsync(memberlessEmail);
        using (var forbidden = await RunLifecycleHost.CancelRunFromPageAsync(
                   memberless, runId, "/"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        // An unauthenticated (or worker-token) caller never reaches the service: authorization
        // challenges before anything else happens (no antiforgery token needed - the challenge
        // never reaches the endpoint, the same shape as F-006's anonymous trigger probe).
        using (var anonymous = host.Host.CreateClient())
        {
            using var challenge = await anonymous.PostAsync(
                $"/runs/{runId}/cancel",
                new FormUrlEncodedContent(new Dictionary<string, string>()),
                CancellationToken);
            Assert.True(
                challenge.StatusCode is HttpStatusCode.Found or HttpStatusCode.Unauthorized,
                $"Expected a sign-in challenge, got {(int)challenge.StatusCode}.");
            if (challenge.Headers.Location is { } location)
            {
                Assert.Contains("/account/login", location.OriginalString, StringComparison.Ordinal);
            }
        }

        var unchanged = await host.ReadRunAsync(runId);
        Assert.Equal(original.Status, unchanged.Status);
        Assert.Equal(original.CancelRequestedAt, unchanged.CancelRequestedAt);
        Assert.Equal(original.FailureReason, unchanged.FailureReason);
    }

    [Fact]
    public async Task The_canceled_report_requires_a_pending_cancel_request_and_the_owning_worker()
    {
        using var host = await RunLifecycleHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var (tokenA, _) = await host.RegisterWorkerAsync(name: "report-agent-a");
        var (tokenB, _) = await host.RegisterWorkerAsync(name: "report-agent-b");
        var foreignOrganizationId = await host.AddOrganizationAsync("report-foreign-org");
        var foreignToken = await host.Inner.CreateWorkerTokenDirectlyAsync(
            foreignOrganizationId, name: "report-foreign-agent");
        var runId = await host.AddRunAsync(projectId, organizationId: organizationId);

        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, tokenA);
        await RunTestHost.AcceptAsync(client, tokenA, runId);

        // No pending cancel request: the worker may not kill what the server never asked it to.
        using (var unrequested = await RunLifecycleHost.ReportCanceledAsync(client, tokenA, runId, RunCanceledOutcomes.Canceled))
        {
            Assert.Equal(HttpStatusCode.Conflict, unrequested.StatusCode);
            var error = await unrequested.Content.ReadAsStringAsync(CancellationToken);
            Assert.Contains(RunErrorCodes.CancelNotRequested, error, StringComparison.Ordinal);
        }

        // A foreign organization's worker token selects its own tenant: the run is
        // indistinguishable from a missing one (404, no existence oracle).
        using (var foreign = await RunLifecycleHost.ReportCanceledAsync(client, foreignToken, runId, RunCanceledOutcomes.Canceled))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }

        // The deadline sweeper's timeout request allows the outcome mapping to Failed - but only
        // with a pending request; here the user cancel is pending instead, so the timeout outcome
        // is still a legal report (the server decides the mapping from the outcome).
        using (var cancel = await RunLifecycleHost.CancelRunAsync(await host.SignInOwnerAsync(), runId))
        {
            Assert.Equal(HttpStatusCode.Found, cancel.StatusCode);
        }

        // Another worker of the same organization: not its run.
        using (var notMine = await RunLifecycleHost.ReportCanceledAsync(client, tokenB, runId, RunCanceledOutcomes.Canceled))
        {
            Assert.Equal(HttpStatusCode.Conflict, notMine.StatusCode);
            var error = await notMine.Content.ReadAsStringAsync(CancellationToken);
            Assert.Contains(RunErrorCodes.RunNotAssignedToWorker, error, StringComparison.Ordinal);
        }

        // An outcome the contract does not know is a field error, even for the owning worker.
        using (var invalid = await RunLifecycleHost.ReportCanceledAsync(client, tokenA, runId, "exploded"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        using (var report = await RunLifecycleHost.ReportCanceledAsync(client, tokenA, runId, RunCanceledOutcomes.Canceled))
        {
            Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        }

        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Canceled, run.Status);
    }

    [Fact]
    public async Task A_canceled_report_racing_a_lost_transition_answers_409_and_never_overwrites()
    {
        using var host = await RunLifecycleHost.CreateAsync(
            configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { [WorkerMonitorOptions.OfflineAfterSecondsConfigurationKey] = "2", [WorkerMonitorOptions.MonitorIntervalSecondsConfigurationKey] = "3600" }));
        var (projectId, _) = await host.CreateProjectAsync();
        var (token, workerId) = await host.RegisterWorkerAsync(name: "lost-race-agent");
        var runId = await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, token);
        await RunTestHost.AcceptAsync(client, token, runId);
        await RunLifecycleHost.CancelRunAsync(await host.SignInOwnerAsync(), runId);

        // The worker goes silent; the offline sweep marks the run Lost while its cancel request
        // is still pending. The report must lose the race honestly: 409, and the Lost transition
        // is never overwritten into Canceled.
        await host.BackdateHeartbeatAsync(workerId, TimeSpan.FromSeconds(5));
        var sweep = await host.SweepOfflineAsync();
        Assert.Equal(1, sweep.RunsLost);

        using (var report = await RunLifecycleHost.ReportCanceledAsync(client, token, runId, RunCanceledOutcomes.Canceled))
        {
            Assert.Equal(HttpStatusCode.Conflict, report.StatusCode);
        }

        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Lost, run.Status);
        Assert.Equal(RunFailureReasons.WorkerOffline, run.FailureReason);
    }

    [Fact]
    public async Task An_out_of_range_timeout_override_is_a_field_error_and_a_valid_one_is_stored()
    {
        using var host = await RunLifecycleHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();

        foreach (var invalid in new[] { "0", "-5", "10081", "abc", "1.5" })
        {
            using var response = await RunTestHost.StartRunAsync(
                owner,
                projectId,
                "dotnet\n--version",
                requiredTags: null,
                timeoutMinutes: invalid);
            var location = response.Headers.Location!.OriginalString;
            Assert.Contains("runError=timeout-invalid", location, StringComparison.Ordinal);
        }

        var created = await RunTestHost.TriggerRunAsync(
            owner,
            projectId,
            "dotnet\n--version",
            timeoutMinutes: "45");
        var run = await host.ReadRunAsync(created);
        Assert.Equal(45 * 60, run.TimeoutSeconds);

        // Blank means the server default: no override stored, the deadline derives from
        // Runs:DefaultTimeoutMinutes at accept time.
        var defaultRun = await RunTestHost.TriggerRunAsync(owner, projectId, "dotnet\n--version");
        var stored = await host.ReadRunAsync(defaultRun);
        Assert.Null(stored.TimeoutSeconds);
        Assert.Null(stored.DeadlineAt);
    }
}
