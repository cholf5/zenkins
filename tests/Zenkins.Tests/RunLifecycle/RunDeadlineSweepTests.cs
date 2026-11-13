using System.Net;
using Microsoft.Extensions.Configuration;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;
using Zenkins.Server.Workers;
using Zenkins.Shared.RunCancellation;
using Zenkins.Tests.Runs;
using Zenkins.Tests.Workers;

namespace Zenkins.Tests.RunLifecycle;

/// <summary>
/// F-009's deadline sweeper: the timeout cancel request at the deadline, and the PM ruling that
/// closes F-007's online-but-unreportable gap - after the grace period (deadline + 2 ×
/// <c>Workers:OfflineAfterSeconds</c>) without any terminal report the run is honestly marked
/// <c>Lost</c> (result unknown), never <c>Failed</c>, never retried.
/// </summary>
[Trait("Category", "RunLifecycle")]
public sealed class RunDeadlineSweepTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_deadline_requests_a_timeout_cancel_and_the_grace_period_marks_lost_honestly()
    {
        // A 2 s offline threshold → a 4 s grace period: the whole window is testable without
        // waiting on real time (the deadline itself is backdated directly).
        using var host = await RunLifecycleHost.CreateAsync(
            configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [WorkerMonitorOptions.OfflineAfterSecondsConfigurationKey] = "2",
                    [WorkerMonitorOptions.MonitorIntervalSecondsConfigurationKey] = "3600",
                    [RunOptions.DefaultTimeoutMinutesConfigurationKey] = "30",
                }));
        var (projectId, _) = await host.CreateProjectAsync();
        var (token, workerId) = await host.RegisterWorkerAsync(name: "deadline-agent");
        var runId = await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, token);
        await RunTestHost.AcceptAsync(client, token, runId);

        var accepted = await host.ReadRunAsync(runId);
        Assert.NotNull(accepted.DeadlineAt);
        Assert.Equal(
            accepted.StartedAt!.Value.AddMinutes(30),
            accepted.DeadlineAt!.Value,
            TimeSpan.FromSeconds(2));

        // Before the deadline the sweep changes nothing (the card's "never before the deadline").
        var beforeDeadline = await host.SweepDeadlinesAsync();
        Assert.Equal(0, beforeDeadline.CancelRequested);
        Assert.Equal(0, beforeDeadline.Lost);

        // Deadline passed, within the grace period: the timeout cancel request goes out; the run
        // stays Running and the worker (its heartbeat fresh) stays Online.
        await host.SetDeadlineAsync(runId, DateTimeOffset.UtcNow.AddSeconds(-1));
        var withinGrace = await host.SweepDeadlinesAsync();
        Assert.Equal(1, withinGrace.CancelRequested);
        Assert.Equal(0, withinGrace.Lost);

        var requested = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Running, requested.Status);
        Assert.NotNull(requested.CancelRequestedAt);
        Assert.Equal(RunCancelReasons.Timeout, requested.CancelReason);
        var worker = await host.ReadWorkerAsync(workerId);
        Assert.Equal(WorkerStatus.Online, worker.Status);

        // The grace period passes without any terminal report: Lost, honestly worded - never
        // Failed, and the worker was Online the whole time, so this is the deadline path.
        await host.SetDeadlineAsync(runId, DateTimeOffset.UtcNow.AddSeconds(-10));
        var afterGrace = await host.SweepDeadlinesAsync();
        Assert.Equal(0, afterGrace.CancelRequested);
        Assert.Equal(1, afterGrace.Lost);

        var lost = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Lost, lost.Status);
        Assert.Equal(RunFailureReasons.RunDeadlineExceededResultUnknown, lost.FailureReason);
        Assert.NotNull(lost.EndedAt);
        Assert.Null(lost.ExitCode);
        Assert.NotEqual(BuildRunStatus.Failed, lost.Status);
        Assert.Equal(workerId, lost.WorkerId);

        // Terminal is closed: another sweep changes nothing (no requeue, no retry, no overwrite).
        var repeat = await host.SweepDeadlinesAsync();
        Assert.Equal(0, repeat.Lost);
        var still = await host.ReadRunAsync(runId);
        Assert.Equal(lost.EndedAt, still.EndedAt);
        Assert.Equal(lost.FailureReason, still.FailureReason);
    }

    [Fact]
    public async Task A_user_cancel_request_is_never_overwritten_by_the_timeout_reason()
    {
        using var host = await RunLifecycleHost.CreateAsync(
            configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { [WorkerMonitorOptions.OfflineAfterSecondsConfigurationKey] = "2", [WorkerMonitorOptions.MonitorIntervalSecondsConfigurationKey] = "3600" }));
        var (projectId, owner) = await host.CreateProjectAsync();
        var (token, _) = await host.RegisterWorkerAsync(name: "reason-agent");
        var runId = await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, token);
        await RunTestHost.AcceptAsync(client, token, runId);

        using (var response = await RunLifecycleHost.CancelRunAsync(owner, runId))
        {
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }

        // The deadline passes while the user's cancel request is pending (still inside the 4 s
        // grace window): the sweeper's CAS finds CancelRequestedAt already set and records
        // nothing - the user's reason survives.
        await host.SetDeadlineAsync(runId, DateTimeOffset.UtcNow.AddSeconds(-1));
        await host.SweepDeadlinesAsync();

        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Running, run.Status);
        Assert.StartsWith(RunCancelReasons.CanceledByUser, run.CancelReason, StringComparison.Ordinal);
        Assert.NotEqual(RunCancelReasons.Timeout, run.CancelReason);
    }

    [Fact]
    public async Task A_run_that_reports_within_the_grace_period_is_mapped_by_its_outcome()
    {
        using var host = await RunLifecycleHost.CreateAsync(
            configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [WorkerMonitorOptions.OfflineAfterSecondsConfigurationKey] = "60",
                }));
        var (projectId, _) = await host.CreateProjectAsync();
        var (token, _) = await host.RegisterWorkerAsync(name: "grace-report-agent");
        var runId = await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, token);
        await RunTestHost.AcceptAsync(client, token, runId);

        // The deadline sweeper marked the run cancel-requested (timeout); the worker observed the
        // signal (heartbeat fallback) and killed the process. The report arrives well inside the
        // 120 s grace period: one endpoint, the outcome selects the terminal state.
        await host.SetDeadlineAsync(runId, DateTimeOffset.UtcNow.AddSeconds(-1));
        var sweep = await host.SweepDeadlinesAsync();
        Assert.Equal(1, sweep.CancelRequested);

        using (var timeoutReport = await RunLifecycleHost.ReportCanceledAsync(client, token, runId, RunCanceledOutcomes.Timeout))
        {
            Assert.Equal(HttpStatusCode.OK, timeoutReport.StatusCode);
            var status = await timeoutReport.Content.ReadAsStringAsync(CancellationToken);
            Assert.Contains(BuildRunStatus.Failed, status, StringComparison.Ordinal);
        }

        var failed = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Failed, failed.Status);
        Assert.Equal(RunCancelReasons.Timeout, failed.FailureReason);
        Assert.NotNull(failed.EndedAt);
        Assert.Null(failed.ExitCode);

        // The canceled outcome on a fresh run maps to Canceled with the recorded reason.
        var secondRun = await host.AddRunAsync(projectId);
        await RunTestHost.ClaimAsync(client, token);
        await RunTestHost.AcceptAsync(client, token, secondRun);
        using (var cancel = await RunLifecycleHost.CancelRunAsync(await host.SignInOwnerAsync(), secondRun))
        {
            Assert.Equal(HttpStatusCode.Found, cancel.StatusCode);
        }

        using (var canceledReport = await RunLifecycleHost.ReportCanceledAsync(client, token, secondRun, RunCanceledOutcomes.Canceled))
        {
            Assert.Equal(HttpStatusCode.OK, canceledReport.StatusCode);
        }

        var canceled = await host.ReadRunAsync(secondRun);
        Assert.Equal(BuildRunStatus.Canceled, canceled.Status);
        Assert.StartsWith(RunCancelReasons.CanceledByUser, canceled.FailureReason, StringComparison.Ordinal);
    }
}
