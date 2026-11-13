using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;
using Zenkins.Shared.RunCancellation;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Runs;
using Zenkins.Tests.WorkerRuntime;

namespace Zenkins.Tests.RunLifecycle;

/// <summary>
/// F-009's process-killing integrations: a real <see cref="Zenkins.Worker.WorkerRunner"/> (real
/// registration, heartbeat loop, run loop) executes a real child process; the server's cancel
/// request (user cancel through the real form, or the deadline sweeper's timeout) reaches the
/// worker, the process tree is killed, and the run reaches its terminal state through the canceled
/// report. The two tests pin the two delivery channels separately.
/// <para>
/// The user-cancel test rides the connected SignalR channel (the primary path, routed through the
/// TestServer), where the partial log is preserved upstream and asserted on the server. The
/// timeout test runs its worker with a deliberately unreachable upstream - the fallback scenario
/// the card describes ("a dropped SignalR connection cannot strand a cancel"): the kill arrives
/// only through the heartbeat response's <c>cancelRequestedRuns</c>, end to end. Without an
/// upstream no log line can reach the server, so that test asserts the kill and the terminal
/// mapping (Failed(timeout), end time, no exit code, temp cleaned) and deliberately not the
/// partial log; the SignalR group delivery itself is pinned in <see cref="RunCancellationTests"/>.
/// </para>
/// </summary>
[Trait("Category", "RunLifecycle")]
public sealed class RunWorkerCancellationTests
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(60);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact(Timeout = 3 * 60 * 1000)]
    public async Task Canceling_a_running_run_kills_the_process_and_preserves_the_partial_log()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await RunLifecycleHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var (memberClient, memberEmail) = await AddMemberAsync(host);
        var runId = await RunTestHost.TriggerRunAsync(
            owner,
            projectId,
            $"dotnet\n{ArgvEchoBinary.DllPath}\nstream\n120000");

        await using var worker = await InProcessWorker.StartAsync(host, $"cancel-agent-{Guid.NewGuid():N}");
        var running = await WaitUntilStatusAsync(host, runId, BuildRunStatus.Running);
        Assert.NotNull(running.DeadlineAt);
        Assert.Equal(BuildRunStatus.Running, running.Status);

        // The partial output is already persisted while the child sleeps (F-008's ingest flushes
        // continuously); the child would only print its second line after the full 120 s.
        await WaitForLogLineAsync(host, runId, "stream-first");
        Assert.DoesNotContain(
            await host.ReadLogRowsAsync(runId),
            row => row.Message == "stream-second");

        // Cancel from the UI as a Member (TC-002): PRG with the cancel-requested notice.
        using (var response = await RunLifecycleHost.CancelRunAsync(memberClient, runId))
        {
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.EndsWith(
                "?notice=cancel-requested",
                response.Headers.Location!.OriginalString,
                StringComparison.Ordinal);
        }

        var canceled = await WaitUntilStatusAsync(host, runId, BuildRunStatus.Canceled);
        Assert.StartsWith(RunCancelReasons.CanceledByUser, canceled.FailureReason, StringComparison.Ordinal);
        // TC-002's actor: a Member. The reason names the user who pressed the button.
        Assert.Contains(memberEmail, canceled.FailureReason, StringComparison.Ordinal);
        Assert.NotNull(canceled.EndedAt);
        Assert.Null(canceled.ExitCode);

        // The partial log survived the kill (the drain precedes the canceled report) and the
        // child never got to its second line: the process tree was really killed.
        var rows = await host.ReadLogRowsAsync(runId);
        Assert.Contains(rows, row => row.Message == "stream-first");
        Assert.DoesNotContain(rows, row => row.Message == "stream-second");

        // The worker's temp was cleaned before the report (the completed path's discipline).
        var work = Path.Combine(worker.WorkRoot, runId.ToString("D"));
        Assert.False(Directory.Exists(Path.Combine(work, "temp")), "temp/ survived the canceled run.");
    }

    [Fact(Timeout = 3 * 60 * 1000)]
    public async Task A_run_past_its_deadline_is_killed_and_marked_failed_with_the_timeout_reason()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Sub-minute default timeout (the card's "config allows sub-minute test values"): the
        // deadline lands three seconds after accept, and the child sleeps far beyond it.
        using var host = await RunLifecycleHost.CreateAsync(
            configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [RunOptions.DefaultTimeoutMinutesConfigurationKey] = "0.05",
                }));
        var (projectId, owner) = await host.CreateProjectAsync();
        var runId = await RunTestHost.TriggerRunAsync(
            owner,
            projectId,
            $"dotnet\n{ArgvEchoBinary.DllPath}\nstream\n120000");

        // The fallback E2E: the worker's upstream is deliberately unreachable, so the SignalR
        // cancel channel is down for the whole run. The kill can only arrive through the
        // heartbeat response's cancelRequestedRuns (deadline sweep → cancel request → heartbeat
        // payload → WorkerRunner raises it on the bus → the run loop's kill token).
        await using var worker = await InProcessWorker.StartAsync(
            host,
            $"timeout-agent-{Guid.NewGuid():N}",
            connectUpstream: false);
        var running = await WaitUntilStatusAsync(host, runId, BuildRunStatus.Running);
        Assert.Equal(
            running.StartedAt!.Value.AddSeconds(3),
            running.DeadlineAt!.Value,
            TimeSpan.FromSeconds(2));

        // The child is executing. Without an upstream its output never reaches the server, so
        // the partial log cannot be asserted here (the connected E2E above owns that assertion).
        var work = Path.Combine(worker.WorkRoot, runId.ToString("D"));
        await WaitForTempDirectoryAsync(work);

        // Poll with the real sweeper seam: as soon as the deadline passes, the tick marks the
        // cancel request (timeout) and the heartbeat (1 s period) delivers it to the worker.
        var deadline = DateTime.UtcNow + StatusTimeout;
        var requested = false;
        while (DateTime.UtcNow < deadline)
        {
            await host.SweepDeadlinesAsync();
            var run = await host.ReadRunAsync(runId);
            if (run.CancelRequestedAt is not null)
            {
                Assert.Equal(RunCancelReasons.Timeout, run.CancelReason);
                requested = true;
                break;
            }

            await Task.Delay(250, CancellationToken);
        }

        Assert.True(requested, "The deadline sweep never recorded the timeout cancel request.");

        var failed = await WaitUntilStatusAsync(host, runId, BuildRunStatus.Failed);
        Assert.Equal(RunCancelReasons.Timeout, failed.FailureReason);
        Assert.NotNull(failed.EndedAt);
        Assert.Null(failed.ExitCode);

        // The run was Failed (timeout), not Canceled: the outcome mapping the server owns.
        Assert.NotEqual(BuildRunStatus.Canceled, failed.Status);

        // Upstream-independent evidence of the kill path: the canceled report only follows the
        // process-tree kill and the cleanup-before-report discipline, so temp/ is already gone
        // when the terminal state becomes visible.
        Assert.False(Directory.Exists(Path.Combine(work, "temp")), "temp/ survived the killed run.");
    }

    /// <summary>Registers a second account, seeds a Member membership in the starter organization,
    /// and returns a signed-in client (the trigger right the cancel shares, TC-002's actor).</summary>
    private static async Task<(HttpClient Client, string Email)> AddMemberAsync(RunLifecycleHost host)
    {
        var email = $"member-cancel-{Guid.NewGuid():N}@example.test";
        await host.RegisterUserAsync(email);
        var organizationId = await host.OrganizationIdAsync();
        await host.InSystemScopeAsync(async context =>
        {
            var user = await context.Users.SingleAsync(candidate => candidate.Email == email);
            context.OrganizationMembers.Add(new Zenkins.Server.Data.Entities.OrganizationMember
            {
                OrganizationId = organizationId,
                UserId = user.Id,
                Role = OrganizationRole.Member,
            });
            await context.SaveChangesAsync();
            return true;
        });

        return (await host.SignInAsync(email), email);
    }

    private static async Task<BuildRun> WaitUntilStatusAsync(
        RunLifecycleHost host,
        Guid runId,
        string status)
    {
        var deadline = DateTime.UtcNow + StatusTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var run = await host.ReadRunAsync(runId);
            if (run.Status == status)
            {
                return run;
            }

            if (run.Status is BuildRunStatus.Success or BuildRunStatus.Failed
                or BuildRunStatus.Canceled or BuildRunStatus.Lost)
            {
                // Terminal but not the expected state: a real failure, fail fast with the row.
                Assert.Fail(
                    $"Run {runId} reached {run.Status} (reason: {run.FailureReason}) instead of {status}.");
            }

            await Task.Delay(200, CancellationToken);
        }

        throw new TimeoutException($"Run {runId} never reached {status} within {StatusTimeout}.");
    }

    private static async Task WaitForLogLineAsync(RunLifecycleHost host, Guid runId, string message)
    {
        var deadline = DateTime.UtcNow + StatusTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if ((await host.ReadLogRowsAsync(runId)).Any(row => row.Message == message))
            {
                return;
            }

            await Task.Delay(200, CancellationToken);
        }

        throw new TimeoutException($"Run {runId} never streamed the line '{message}'.");
    }

    /// <summary>Waits until the run's worker-side temp directory exists - proof the claimed run's
    /// process is actually executing (the fallback E2E cannot use the server-side log as that
    /// proof: without an upstream no line is delivered).</summary>
    private static async Task WaitForTempDirectoryAsync(string work)
    {
        var deadline = DateTime.UtcNow + StatusTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(Path.Combine(work, "temp")))
            {
                return;
            }

            await Task.Delay(200, CancellationToken);
        }

        throw new TimeoutException($"The run's temp directory under {work} never appeared.");
    }
}
