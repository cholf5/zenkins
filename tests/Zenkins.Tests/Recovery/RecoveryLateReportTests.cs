using System.Net;
using System.Text.Json;
using Xunit;
using Zenkins.Shared.Pipelines;
using Zenkins.Tests.Milestone;

namespace Zenkins.Tests.Recovery;

/// <summary>
/// Q-004's late-report scenario: the killed worker's process cannot produce a report, so the test
/// <b>replays the same terminal report its completion path would have sent</b> - the
/// <c>POST /api/workers/runs/&#123;id&#125;/completed</c> call with <c>&#123;exitCode, failureReason&#125;</c>,
/// authenticated with the very credential the dead worker persisted (read back from the token file it
/// wrote, so the replay is indistinguishable from the worker's own retry).
/// <para>
/// The server must refuse it with the <b>existing conflict code</b> - 409
/// <c>invalid-transition</c>, because <c>Lost</c> is terminal and has no outgoing edges - and change
/// <b>nothing</b>: the row keeps the offline facts (status, reason, null exit code, the same
/// <c>EndedAt</c>), the step rows keep their recorded states, and a re-delivered replay is refused
/// again. A Lost run never becomes Success, Failed or Queued afterwards (asserted over a settle
/// window, not a single read).
/// </para>
/// </summary>
[Trait("Category", "Recovery")]
public sealed class RecoveryLateReportTests
{
    [Fact(Timeout = 6 * 60 * 1000)]
    public async Task A_killed_workers_late_report_is_refused_and_changes_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await RecoveryHost.StartAsync();

        var projectId = await host.CreateProjectAsync("recovery-late-report");
        var markerPath = Path.Combine(host.RootPath, "late-report-marker.txt");
        using var repository = MilestoneRepository.Create(
            RecoveryPipelines.HoldsInFirstStep(markerPath, milliseconds: 30_000));
        await host.PointProjectAtAsync(projectId, repository.FileUrl);

        var worker = await host.StartWorkerAsync();

        var runId = await host.TriggerRunAsync(projectId);
        await host.WaitForStepStatusAsync(runId, 1, "Running");
        await host.AssertWorkerRunsAsItsOwnLiveProcessAsync(worker, runId);

        // The credential the dead worker would have reported with - read from the token file it
        // persisted under its own data directory.
        var workerToken = host.ReadPersistedWorkerToken(worker);

        // ----- Kill mid-step; the real monitor marks the run Lost. -----
        host.KillWorker(worker);
        await RecoveryHost.WaitForProcessGoneAsync(worker.ProcessId);
        await host.AssertRunHoldsStatusAsync(runId, "Running", null, cancellationToken);
        await host.WaitForRunStatusAsync(runId, "Lost");

        var lost = await host.ReadRunAsync(runId);
        Assert.Equal("Lost", lost.Status);
        Assert.Equal("worker-offline", lost.FailureReason);
        Assert.Null(lost.ExitCode);
        var stepsBefore = StepSignature(await host.ReadStepsAsync(runId));

        // ----- The late report: exactly the payload the worker's completion path would have sent. -----
        using (var late = await host.ReportCompletedAsync(
            workerToken, runId, exitCode: 1, failureReason: PipelineFailureReasons.StepFailed))
        {
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
            Assert.Equal("invalid-transition", await ReadErrorAsync(late));
        }

        // A re-delivery of the same replay is refused the same way.
        using (var again = await host.ReportCompletedAsync(
            workerToken, runId, exitCode: 1, failureReason: PipelineFailureReasons.StepFailed))
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal("invalid-transition", await ReadErrorAsync(again));
        }

        // ----- Nothing changed: the run keeps the offline facts, the steps keep their states. -----
        var after = await host.ReadRunAsync(runId);
        Assert.Equal("Lost", after.Status);
        Assert.Equal("worker-offline", after.FailureReason);
        Assert.Null(after.ExitCode);
        Assert.Equal(lost.EndedAt, after.EndedAt);
        Assert.Equal(stepsBefore, StepSignature(await host.ReadStepsAsync(runId)));
        Assert.False(File.Exists(markerPath), "the late report resurrected an execution.");

        // Settle window: the Lost run never becomes Success, Failed or Queued afterwards, and no
        // retry ever appears.
        await host.AssertRunSettlesInAsync(runId, cancellationToken, "Lost");
        await host.AssertHoldsAsync(
            async () => Assert.Single(await host.ReadProjectRunsAsync(projectId)),
            window: null,
            cancellationToken);
    }

    /// <summary>The steps as a stable signature (index, name, state, exit code, reason) - the same
    /// comparison the milestone suite uses to prove no row moved.</summary>
    private static string[] StepSignature(IReadOnlyList<MilestoneStep> steps) =>
        [.. steps.Select(step => $"{step.Index}|{step.Name}|{step.Status}|{step.ExitCode}|{step.FailureReason}")];

    /// <summary>Reads the worker API's refusal body: <c>&#123;"error": "&lt;code&gt;"&#125;</c>.</summary>
    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("error").GetString()
            ?? throw new InvalidOperationException($"the refusal body carried no error code: {body}");
    }
}
