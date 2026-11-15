using Xunit;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.RunCancellation;

namespace Zenkins.Tests.Milestone;

/// <summary>
/// Q-002 TC-003: the <b>failure taxonomy</b> at the product boundary. Five real runs - each one a real
/// clone, a real definition phase and real child processes where applicable - must end in five
/// distinguishable, terminal outcomes, and each must have run <em>nothing beyond</em> what it should
/// have.
/// <para>
/// "Nothing beyond" is a marker file in this suite's temporary root: every step that runs appends its own
/// name, so a missing marker is evidence that a process never started, not an inference from a status
/// code. The host's default deadline is 15s (the product reads it as
/// <c>Runs:DefaultTimeoutMinutes</c>) and the sweep interval is pushed out of the window, so the
/// definition runaway is abandoned by the worker's own clock - the path that must produce
/// <c>pipeline-timeout</c> rather than a server-requested cancel.
/// </para>
/// </summary>
[Trait("Category", "Milestone")]
public sealed class MilestoneFailureTaxonomyTests
{
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromSeconds(3);

    /// <summary>How long the host's default run deadline is; the runaway definition must hit it.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Fact(Timeout = 10 * 60 * 1000)]
    public async Task Five_real_failures_are_terminal_distinguishable_and_never_run_extra_steps()
    {
        using var host = await MilestoneHost.StartAsync(new MilestoneHostOptions(
            DefaultTimeoutMinutes: Deadline.TotalMinutes,
            SweepIntervalSeconds: 600));

        var missingMarker = MarkerPath(host, "missing");
        var syntaxMarker = MarkerPath(host, "syntax");
        var failingMarker = MarkerPath(host, "failing");
        var runawayMarker = MarkerPath(host, "runaway");
        var cancelMarker = MarkerPath(host, "cancel");

        using var repository = MilestoneRepository.Create(MilestonePipelines.FailingAt(missingMarker, 2, 7));
        var projectId = await host.CreateProjectAsync("milestone-failures");
        await host.PointProjectAtAsync(projectId, repository.FileUrl);
        await host.StartWorkerAsync();

        // ----- 1. No pipeline file at all: `pipeline-missing`, nothing ran. -----
        repository.Delete("pipeline.lua");
        var missing = await RunToTerminalAsync(host, projectId);
        Assert.Equal("Failed", missing.Status);
        Assert.Null(missing.ExitCode);
        Assert.Equal(PipelineFailureReasons.PipelineMissing, missing.FailureReason);
        Assert.Equal(repository.MainTip, missing.GitCommit);
        Assert.NotNull(missing.EndedAt);
        Assert.Empty(await host.ReadStepsAsync(missing.Id));
        // F-008's drain lands the log lines around the terminal report ("on their way"), so the
        // lines are waited for, not read once - a loaded runner legitimately saw an empty log for
        // these green runs (recorded red on the ubuntu runner).
        Assert.Contains(
            await host.WaitForLogLinesAsync(
                missing.Id,
                lines => lines.Any(line => line.Message.Contains("not found", StringComparison.Ordinal))),
            line => line.Message.Contains("not found", StringComparison.Ordinal));
        Assert.False(File.Exists(missingMarker), "a step process ran for a run whose repository has no pipeline file.");

        // ----- 2. A definition that does not compile: `pipeline-syntax`, nothing ran. -----
        repository.CommitPipeline(MilestonePipelines.SyntaxError(syntaxMarker));
        var syntax = await RunToTerminalAsync(host, projectId);
        Assert.Equal("Failed", syntax.Status);
        Assert.Null(syntax.ExitCode);
        Assert.Equal(PipelineFailureReasons.PipelineSyntax, syntax.FailureReason);
        Assert.Equal(repository.MainTip, syntax.GitCommit);
        Assert.NotNull(syntax.EndedAt);
        Assert.Empty(await host.ReadStepsAsync(syntax.Id));
        Assert.Contains(
            await host.WaitForLogLinesAsync(
                syntax.Id,
                lines => lines.Any(line => line.Message.Contains("pipeline.lua", StringComparison.Ordinal))),
            line => line.Message.Contains("pipeline.lua", StringComparison.Ordinal));
        Assert.False(File.Exists(syntaxMarker), "a step process ran for a definition that never compiled.");

        // ----- 3. The second step exits 7: `step-failed`, and step 3 never ran. -----
        repository.CommitPipeline(MilestonePipelines.FailingAt(failingMarker, failingIndex: 2, exitCode: 7));
        var failing = await RunToTerminalAsync(host, projectId);
        Assert.Equal("Failed", failing.Status);
        Assert.Equal(7, failing.ExitCode);
        Assert.Equal(PipelineFailureReasons.StepFailed, failing.FailureReason);
        Assert.Equal(repository.MainTip, failing.GitCommit);
        Assert.NotNull(failing.EndedAt);

        var failingSteps = await host.ReadStepsAsync(failing.Id);
        Assert.Equal(3, failingSteps.Count);
        Assert.Equal(PipelineStepStatus.Success, failingSteps[0].Status);
        Assert.Equal(PipelineStepStatus.Failed, failingSteps[1].Status);
        Assert.Equal(7, failingSteps[1].ExitCode);
        Assert.Equal(PipelineStepStatus.Skipped, failingSteps[2].Status);
        Assert.Equal(PipelineStepFailureReasons.PreviousStepFailed, failingSteps[2].FailureReason);

        // The marker proves what the rows say: step 1 ran, steps 2 and 3 did not.
        Assert.Equal(new[] { "Step1" }, ReadMarkerLines(failingMarker));

        // ----- 4. A definition that never returns: `pipeline-timeout`, nothing ran. -----
        repository.CommitPipeline(MilestonePipelines.RunawayDefinition(runawayMarker));
        var startedAt = DateTime.UtcNow;
        var runaway = await RunToTerminalAsync(host, projectId);
        var elapsed = DateTime.UtcNow - startedAt;

        Assert.Equal("Failed", runaway.Status);
        Assert.Null(runaway.ExitCode);
        Assert.Equal(PipelineFailureReasons.PipelineTimeout, runaway.FailureReason);
        Assert.Equal(repository.MainTip, runaway.GitCommit);
        Assert.NotNull(runaway.EndedAt);
        Assert.Empty(await host.ReadStepsAsync(runaway.Id));
        Assert.Contains(
            await host.WaitForLogLinesAsync(
                runaway.Id,
                lines => lines.Any(line => line.Message.Contains("deadline", StringComparison.Ordinal))),
            line => line.Message.Contains("deadline", StringComparison.Ordinal));
        Assert.False(File.Exists(runawayMarker), "a step process ran for a definition that never returned.");
        Assert.True(
            elapsed < Deadline + TimeSpan.FromSeconds(60),
            $"the runaway definition took {elapsed.TotalSeconds:0.0}s - the run's deadline did not bound it.");

        // ----- 5. A user cancel during step 1: `Canceled`, and step 2 never ran. -----
        repository.CommitPipeline(MilestonePipelines.CancelDuringFirstStep(cancelMarker, milliseconds: 30_000));
        var cancelRunId = await host.TriggerRunAsync(projectId);
        await host.WaitForStepStatusAsync(cancelRunId, index: 1, PipelineStepStatus.Running);

        using (var cancelResponse = await host.CancelRunAsync(cancelRunId))
        {
            Assert.True((int)cancelResponse.StatusCode is 302 or 303, "the cancel form did not accept the request.");
        }

        var canceledRun = await host.WaitForTerminalAsync(cancelRunId);
        Assert.Equal("Canceled", canceledRun.Status);
        Assert.NotNull(canceledRun.EndedAt);
        Assert.Null(canceledRun.ExitCode);
        Assert.NotNull(canceledRun.FailureReason);
        Assert.StartsWith(RunCancelReasons.CanceledByUser, canceledRun.FailureReason, StringComparison.Ordinal);

        var canceledSteps = await host.ReadStepsAsync(cancelRunId);
        Assert.Equal(3, canceledSteps.Count);
        Assert.Equal(PipelineStepStatus.Failed, canceledSteps[0].Status);
        Assert.Equal(PipelineStepFailureReasons.Canceled, canceledSteps[0].FailureReason);
        Assert.Equal(PipelineStepStatus.Skipped, canceledSteps[1].Status);
        Assert.Equal(PipelineStepFailureReasons.RunCanceled, canceledSteps[1].FailureReason);
        Assert.Equal(PipelineStepStatus.Skipped, canceledSteps[2].Status);
        Assert.False(File.Exists(cancelMarker), "step 2 ran although the cancel was requested during step 1.");

        // ----- None of the five was retried: one run row each, and no late arrivals. -----
        var runs = await host.ReadProjectRunsAsync(projectId);
        Assert.Equal(new[] { missing.Id, syntax.Id, failing.Id, runaway.Id, cancelRunId }, runs.Select(run => run.Id));

        // Everything that must not move after the fact: the run rows, the step rows and the marker files
        // (a late retry, a straggling step or a "nothing ran" step that suddenly ran would move one of them).
        var settled = runs.ToDictionary(run => run.Id, run => (run.Status, run.FailureReason, run.ExitCode));
        var markers = new[] { missingMarker, syntaxMarker, failingMarker, runawayMarker, cancelMarker };
        var settledSteps = new Dictionary<Guid, string[]>();
        foreach (var run in runs)
        {
            settledSteps[run.Id] = await StepSignatureAsync(host, run.Id);
        }

        var settledMarkerLines = markers.ToDictionary(
            marker => marker,
            marker => MarkerLineCount(marker));

        var deadline = DateTime.UtcNow + ObservationWindow;
        while (DateTime.UtcNow < deadline)
        {
            var observed = await host.ReadProjectRunsAsync(projectId);
            Assert.Equal(5, observed.Count);
            foreach (var run in observed)
            {
                var before = settled[run.Id];
                Assert.Equal(before.Status, run.Status);
                Assert.Equal(before.FailureReason, run.FailureReason);
                Assert.Equal(before.ExitCode, run.ExitCode);

                // The recorded steps are the same rows in the same states: no step appeared, moved or was
                // re-reported after the run was terminal.
                Assert.Equal(settledSteps[run.Id], await StepSignatureAsync(host, run.Id));
            }

            // ... and no step process wrote anything more into any marker file.
            foreach (var marker in markers)
            {
                Assert.Equal(settledMarkerLines[marker], MarkerLineCount(marker));
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        // The worker survived all five: a fresh, healthy run still works on the same worker.
        repository.CommitPipeline(MilestonePipelines.Happy);
        await host.CreateSecretAsync(MilestonePipelines.SecretName, "after-the-taxonomy");
        var recovery = await RunToTerminalAsync(host, projectId);
        Assert.Equal("Success", recovery.Status);
        Assert.All(await host.ReadStepsAsync(recovery.Id), step => Assert.Equal("Success", step.Status));
    }

    private static async Task<MilestoneRun> RunToTerminalAsync(MilestoneHost host, Guid projectId)
    {
        var runId = await host.TriggerRunAsync(projectId);
        return await host.WaitForTerminalAsync(runId);
    }

    /// <summary>A marker path inside the host's temporary root (deleted with the host, visible to the test).</summary>
    private static string MarkerPath(MilestoneHost host, string name) =>
        Path.Combine(host.RootPath, $"step-marker-{name}.txt");

    /// <summary>One run's steps as a stable signature (index, name, state, exit code, reason).</summary>
    private static async Task<string[]> StepSignatureAsync(MilestoneHost host, Guid runId) =>
        [.. (await host.ReadStepsAsync(runId))
            .Select(step => $"{step.Index}|{step.Name}|{step.Status}|{step.ExitCode}|{step.FailureReason}")];

    /// <summary>How many lines a marker file holds (0 when no step ever wrote it).</summary>
    private static int MarkerLineCount(string path) => File.Exists(path) ? File.ReadAllLines(path).Length : 0;

    /// <summary>The lines a marker file holds, or an empty list when nothing wrote it.</summary>
    private static string[] ReadMarkerLines(string path) => File.Exists(path)
        ? [.. File.ReadAllLines(path).Where(line => line.Length > 0)]
        : [];
}
