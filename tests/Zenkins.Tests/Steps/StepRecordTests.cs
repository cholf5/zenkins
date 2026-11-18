using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;
using Zenkins.Shared.Pipelines;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Pipelines;
using Zenkins.Tests.Runs;
using Zenkins.Tests.Workers;

namespace Zenkins.Tests.Steps;

/// <summary>
/// Card F-015's step records (TC-001, TC-002 and AC-2), end to end: a real run through the real step
/// endpoints and the real executor, with assertions against the database rows, the run page and the log
/// segments - so "step 3 was skipped" is a fact about the record and about the working tree, never an
/// inference from the run log.
/// </summary>
[Trait("Category", "Steps")]
public sealed class StepRecordTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------
    // TC-001
    // ------------------------------------------------------------------

    [Fact]
    public async Task TC_001_every_step_records_its_state_timing_and_exit_code_and_a_skipped_step_never_started()
    {
        // Step 1 succeeds, step 2 exits 3 without continue_on_error, step 3 must never run.
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "records",
                MarkerStep("Restore", "restore"),
                PipelineTestData.Step("Build", "exit", "3"),
                MarkerStep("Package", "package")));
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);

        var outcome = await run.ExecuteAsync();

        Assert.Equal(3, outcome.ExitCode);
        Assert.Equal(PipelineFailureReasons.StepFailed, outcome.FailureReason);

        var steps = await run.ReadStepsAsync();
        Assert.Equal(3, steps.Count);

        // Step 1: Success, timing recorded, exit code 0.
        Assert.Equal("Restore", steps[0].Name);
        Assert.Equal(PipelineStepStatus.Success, steps[0].Status);
        Assert.NotNull(steps[0].StartedAt);
        Assert.NotNull(steps[0].EndedAt);
        Assert.True(steps[0].EndedAt >= steps[0].StartedAt);
        Assert.Equal(0, steps[0].ExitCode);
        Assert.Null(steps[0].FailureReason);

        // Step 2: Failed with its own exit code - never an invented one.
        Assert.Equal("Build", steps[1].Name);
        Assert.Equal(PipelineStepStatus.Failed, steps[1].Status);
        Assert.Equal(3, steps[1].ExitCode);
        Assert.NotNull(steps[1].StartedAt);
        Assert.NotNull(steps[1].EndedAt);

        // Step 3: Skipped - and the point of the card's TC-001 detail: it was never Running, so it has no
        // start instant at all, and its process never ran (the marker file proves that from the tree).
        Assert.Equal("Package", steps[2].Name);
        Assert.Equal(PipelineStepStatus.Skipped, steps[2].Status);
        Assert.Null(steps[2].StartedAt);
        Assert.NotNull(steps[2].EndedAt);
        Assert.Null(steps[2].ExitCode);
        Assert.Equal(PipelineStepFailureReasons.PreviousStepFailed, steps[2].FailureReason);

        Assert.DoesNotContain(
            run.Reports,
            report => report.Index == 3 && report.Status == PipelineStepStatus.Running);
        Assert.Equal(
            ["restore"],
            File.ReadAllLines(Path.Combine(run.Work.RepositoryPath, "marker.txt")));

        // The run's terminal state still belongs to the state machine, and it names the failing step.
        var finished = await run.ReadRunAsync();
        Assert.Equal(BuildRunStatus.Failed, finished.Status);
        Assert.Equal(3, finished.ExitCode);
        Assert.Equal(PipelineFailureReasons.StepFailed, finished.FailureReason);
        Assert.Contains(
            run.Lines,
            line => line.Message.Contains("step 2/3 \"Build\" failed with exit code 3", StringComparison.Ordinal));

        // The run page shows all three states for one run.
        var page = await run.ReadRunPageAsync();
        Assert.Contains("run-step-Restore", page, StringComparison.Ordinal);
        Assert.Contains(PipelineStepStatus.Success, page, StringComparison.Ordinal);
        Assert.Contains(PipelineStepStatus.Failed, page, StringComparison.Ordinal);
        Assert.Contains(PipelineStepStatus.Skipped, page, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // TC-002
    // ------------------------------------------------------------------

    [Fact]
    public async Task TC_002_continue_on_error_runs_the_remaining_steps_without_laundering_the_failure()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "continues",
                MarkerStep("Restore", "restore"),
                WithContinueOnError(PipelineTestData.Step("Build", "exit", "5")),
                MarkerStep("Package", "package")));
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);

        var outcome = await run.ExecuteAsync();

        // Each step was announced Running exactly once - not merely "at some point" (a duplicated progress
        // report would make the page show a step twice-over and the ordering checks meaningless).
        Assert.All(
            new[] { 1, 2, 3 },
            index => Assert.Equal(
                1,
                run.Reports.Count(report =>
                    report.Index == index && report.Status == PipelineStepStatus.Running)));

        // All three steps record a terminal state...
        var steps = await run.ReadStepsAsync();
        Assert.Equal(3, steps.Count);
        Assert.Equal(PipelineStepStatus.Success, steps[0].Status);
        Assert.Equal(PipelineStepStatus.Failed, steps[1].Status);
        Assert.Equal(5, steps[1].ExitCode);
        Assert.Equal(PipelineStepStatus.Success, steps[2].Status);

        // ...and the third step really ran (its output and its marker exist).
        Assert.Equal(
            ["restore", "package"],
            File.ReadAllLines(Path.Combine(run.Work.RepositoryPath, "marker.txt")));
        Assert.Contains(
            run.Lines,
            line => line.Message == "appended" && line.StepId == steps[2].Id);

        // The run is Failed (not Success), and the log says which step failed.
        Assert.Equal(5, outcome.ExitCode);
        Assert.Equal(PipelineFailureReasons.StepFailed, outcome.FailureReason);
        var finished = await run.ReadRunAsync();
        Assert.Equal(BuildRunStatus.Failed, finished.Status);
        Assert.Contains(
            run.Lines,
            line => line.Message.Contains("continue_on_error is set", StringComparison.Ordinal));
        Assert.Contains(
            run.Lines,
            line => line.Message.Contains("step 2/3 \"Build\" failed with exit code 5", StringComparison.Ordinal));

        // The run page names the failing step too (not only the run's own reason).
        var page = await run.ReadRunPageAsync();
        Assert.Contains("run-step-Build", page, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // AC-2: a page opened mid-run shows completed, current and pending steps
    // ------------------------------------------------------------------

    [Fact]
    public async Task AC_002_a_page_opened_mid_run_shows_completed_current_and_pending_steps()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "midrun",
                MarkerStep("Restore", "restore"),
                PipelineTestData.Step("Build", "stream", "4000"),
                MarkerStep("Package", "package")));
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);

        var execution = run.ExecuteAsync();
        try
        {
            // Wait until the second step is Running in the database: from that instant the page must show a
            // finished step, a current one and one that has not been reached. (The step's own process is
            // still streaming, so the run has not ended.)
            var steps = await WaitForRunningStepAsync(run, index: 2);

            var page = await run.ReadRunPageAsync();
            Assert.Contains(PipelineStepStatus.Success, page, StringComparison.Ordinal);
            Assert.Contains(PipelineStepStatus.Running, page, StringComparison.Ordinal);
            Assert.Contains(PipelineStepStatus.Pending, page, StringComparison.Ordinal);

            Assert.Equal(
                new[] { PipelineStepStatus.Success, PipelineStepStatus.Running },
                steps.Select(step => step.Status));
            Assert.Null(steps[1].EndedAt);
        }
        finally
        {
            // Let the streamed step finish even if an assertion above failed.
            await execution;
        }

        Assert.Equal(0, run.Outcome.ExitCode);
    }

    // ------------------------------------------------------------------
    // Per-step log segments (AC-8)
    // ------------------------------------------------------------------

    [Fact]
    public async Task AC_008_each_log_line_carries_the_step_it_was_emitted_in_and_null_outside_a_step()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "segments",
                MarkerStep("Restore", "restore"),
                MarkerStep("Build", "build")));
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);

        await run.ExecuteAsync();

        var steps = await run.ReadStepsAsync();
        var restoreId = steps[0].Id;
        var buildId = steps[1].Id;

        // The clone and the definition phase happen before any step: those lines carry no step id.
        Assert.Contains(
            run.Lines,
            line => line.StepId is null && line.Message.Contains("checked out", StringComparison.Ordinal));
        Assert.True(
            run.Lines.Any(line => line.StepId is null),
            "no line was recorded outside a step, so the null case is untested.");

        // Each step's own output is stamped with that step's id, and the synthesized lines around it too.
        Assert.Contains(run.Lines, line => line.StepId == restoreId && line.Message == "appended");
        Assert.Contains(run.Lines, line => line.StepId == buildId && line.Message == "appended");
        Assert.Contains(
            run.Lines,
            line => line.StepId == restoreId
                && line.Message.Contains("step 1/2 \"Restore\" exited with code 0", StringComparison.Ordinal));

        // ...and the persisted rows carry it, so the run page can group by step.
        var rows = await run.ReadLogRowsAsync();
        Assert.Equal(
            restoreId,
            rows.Single(row => row.Message.Contains("step 1/2 \"Restore\" exited", StringComparison.Ordinal)).StepId);
        Assert.Equal(
            buildId,
            rows.Single(row => row.Message.Contains("step 2/2 \"Build\" exited", StringComparison.Ordinal)).StepId);
        Assert.Contains(rows, row => row.StepId is null && row.Message.Contains("checked out", StringComparison.Ordinal));

        // The panel groups by that id: the grouping rule (testable without an interactive circuit) turns the
        // persisted rows into one segment per step, with the header the page shows.
        var headers = steps.ToDictionary(
            step => step.Id,
            step => $"{step.Index}. {step.Name}");
        var segments = Zenkins.Server.Components.Shared.RunLogViewModel.Segment(
            [.. rows.Select(row => new Zenkins.Shared.LogStreaming.LogLineMessage(
                row.Seq, row.Stream, row.Message, row.Timestamp, row.StepId))],
            headers);

        Assert.Contains(segments, entry => entry.Header == "1. Restore");
        Assert.Contains(segments, entry => entry.Header == "2. Build");

        // One header per step segment (each step's lines are contiguous, so it is introduced once) and the
        // lines outside a step - the clone, the definition phase - introduce none.
        Assert.Equal(2, segments.Count(entry => entry.Header is not null));
        Assert.All(
            segments.Where(entry => entry.Header is not null),
            entry => Assert.Equal(headers[entry.Line.StepId!.Value], entry.Header));
    }

    // ------------------------------------------------------------------
    // A refused Running report means nothing starts (the server owns the run)
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_refused_running_report_starts_no_process_and_reports_no_completion()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "refused-step",
                MarkerStep("Restore", "restore"),
                MarkerStep("Build", "build")));
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);

        run.AfterDefinitionAccepted = async () =>
        {
            // The server closes the run between the frozen definition and the first step (a cancel landed, or
            // the deadline sweep marked it Lost). The Running report is then answered 409, and the executor must
            // not start a process for a run nobody owns any more.
            await host.InSystemScopeAsync(async context =>
            {
                var row = (await context.SystemQueryAsync<BuildRun>(candidate => candidate.Id == run.RunId)).Single();
                row.Status = BuildRunStatus.Canceled;
                row.EndedAt = DateTimeOffset.UtcNow;
                await context.SaveChangesAsync();
                return true;
            });
        };

        var outcome = await run.ExecuteAsync();

        Assert.True(outcome.Abandoned);
        Assert.Null(outcome.ExitCode);
        Assert.Null(outcome.FailureReason);

        // Zero processes: the marker file proves it from the working tree, not from the log.
        Assert.False(File.Exists(Path.Combine(run.Work.RepositoryPath, "marker.txt")));
        Assert.Empty(await run.ReadStepsAsync());
        Assert.Contains(run.Lines, line => line.Message.Contains("refused the report for step", StringComparison.Ordinal));

        // And no completion report: the run keeps exactly the state the server gave it.
        var finished = await run.ReadRunAsync();
        Assert.Equal(BuildRunStatus.Canceled, finished.Status);
        Assert.Null(finished.ExitCode);
        Assert.Null(finished.FailureReason);
    }

    // ------------------------------------------------------------------
    // AC-9's second variant: the run's deadline, not a user cancel
    // ------------------------------------------------------------------

    [Fact]
    public async Task AC_009_a_deadline_expiry_fails_the_in_flight_step_as_timed_out_and_skips_the_rest()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "deadline",
                PipelineTestData.Step("Build", "stream", "10000"),
                MarkerStep("Package", "package")));
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);

        // The run's deadline is already in the past, so the deadline sweeper records the timeout cancel
        // request (F-009's real seam) instead of a user cancel.
        await host.InSystemScopeAsync(async context =>
        {
            var row = (await context.SystemQueryAsync<BuildRun>(candidate => candidate.Id == run.RunId)).Single();
            row.DeadlineAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await context.SaveChangesAsync();
            return true;
        });
        var sweep = await host.Host.InScopeAsync(services =>
            services.GetRequiredService<RunService>().SweepDeadlinesAsync(CancellationToken));
        Assert.Equal(1, sweep.CancelRequested);

        using var cancellation = new CancellationTokenSource();
        run.CancellationToken = cancellation.Token;
        // The server's cancel request says "timeout" (F-009), and the harness feeds that reason to the product's
        // own predicate instead of deciding the step's failure reason itself.
        run.CancelReason = Zenkins.Shared.RunCancellation.RunCancelReasons.Timeout;
        run.OnReport = report =>
        {
            if (report.Index == 1 && report.Status == PipelineStepStatus.Running)
            {
                cancellation.Cancel();
            }

            return Task.CompletedTask;
        };

        var outcome = await run.ExecuteAsync();

        Assert.True(outcome.Canceled);
        var steps = await run.ReadStepsAsync();

        // The killed step says it ran out of time - a different value from a user cancel, so the page can tell
        // the two apart without reading the run's own reason.
        Assert.Equal(PipelineStepStatus.Failed, steps[0].Status);
        Assert.Equal(PipelineStepFailureReasons.TimedOut, steps[0].FailureReason);
        Assert.NotEqual(PipelineStepFailureReasons.Canceled, steps[0].FailureReason);
        Assert.Null(steps[0].ExitCode);

        Assert.Equal(PipelineStepStatus.Skipped, steps[1].Status);
        Assert.Equal(PipelineStepFailureReasons.RunCanceled, steps[1].FailureReason);
        Assert.False(File.Exists(Path.Combine(run.Work.RepositoryPath, "marker.txt")));

        // The run itself is Failed with reason `timeout` - the server's mapping of the worker's canceled report.
        var finished = await run.ReadRunAsync();
        Assert.Equal(BuildRunStatus.Failed, finished.Status);
        Assert.Equal(Zenkins.Shared.RunCancellation.RunCancelReasons.Timeout, finished.FailureReason);

        // The page says it in words, not as the raw code: every reason the vocabulary defines has a sentence
        // (card F-015's display rule), and the timeout case is the one a reader is most likely to meet.
        var page = await run.ReadRunPageAsync();

        // (Razor HTML-escapes the apostrophe, so the assertion matches the sentence's tail rather than its exact
        // spelling.)
        Assert.Contains("deadline passed", page, StringComparison.Ordinal);
        Assert.DoesNotContain(">timed-out<", page, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // A step id is attribution the server validates, not something a worker asserts
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_line_claiming_another_runs_step_loses_the_attribution_and_keeps_the_line()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline("mine", MarkerStep("Restore", "restore")));
        using var host = await RunTestHost.CreateAsync();
        using var mine = await StepsRun.StartAsync(host, repository, workerName: "mine-agent");
        using var other = await StepsRun.StartAsync(host, repository, workerName: "other-agent");

        await mine.ExecuteAsync();
        await other.ExecuteAsync();

        var myStep = Assert.Single(await mine.ReadStepsAsync());
        var foreignStep = Assert.Single(await other.ReadStepsAsync());
        var unknownStep = Guid.NewGuid();

        // Three claims: this run's step, another run's real step, and an id nobody ever issued.
        await mine.AppendRawLinesAsync(
            ($"{Zenkins.Shared.LogStreaming.LogStream.StdOut}", "honest-line", myStep.Id),
            ($"{Zenkins.Shared.LogStreaming.LogStream.StdOut}", "foreign-line", foreignStep.Id),
            ($"{Zenkins.Shared.LogStreaming.LogStream.StdOut}", "unknown-line", unknownStep));

        var rows = await mine.ReadLogRowsAsync();

        // The honest attribution survives...
        Assert.Equal(myStep.Id, rows.Single(row => row.Message == "honest-line").StepId);

        // ...and a forged one keeps its text but loses the attribution: the line is evidence about THIS run, but
        // it can never be grouped under a step of another run (or a step that does not exist).
        Assert.Null(rows.Single(row => row.Message == "foreign-line").StepId);
        Assert.Null(rows.Single(row => row.Message == "unknown-line").StepId);

        // The other run's log is untouched by the forged claim.
        Assert.DoesNotContain(await other.ReadLogRowsAsync(), row => row.Message == "foreign-line");
    }

    // ------------------------------------------------------------------
    // The grouping rule itself (the panel needs a circuit to render lines)
    // ------------------------------------------------------------------

    [Fact]
    public void Lines_outside_a_step_introduce_no_header_and_a_step_is_introduced_once()
    {
        var restore = Guid.NewGuid();
        var build = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<Zenkins.Shared.LogStreaming.LogLineMessage> lines =
        [
            new(1, Zenkins.Shared.LogStreaming.LogStream.StdOut, "checked out main", now, null),
            new(2, Zenkins.Shared.LogStreaming.LogStream.StdOut, "step 1/2 \"Restore\" started", now, restore),
            new(3, Zenkins.Shared.LogStreaming.LogStream.StdOut, "appended", now, restore),
            new(4, Zenkins.Shared.LogStreaming.LogStream.StdOut, "step 1/2 \"Restore\" exited with code 0", now, restore),
            new(5, Zenkins.Shared.LogStreaming.LogStream.StdOut, "step 2/2 \"Build\" started", now, build),
        ];

        var entries = Zenkins.Server.Components.Shared.RunLogViewModel.Segment(
            lines,
            new Dictionary<Guid, string> { [restore] = "1. Restore", [build] = "2. Build" });

        Assert.Equal(5, entries.Count);
        Assert.Null(entries[0].Header);
        Assert.Equal("1. Restore", entries[1].Header);
        Assert.Null(entries[2].Header);
        Assert.Null(entries[3].Header);
        Assert.Equal("2. Build", entries[4].Header);
    }

    // ------------------------------------------------------------------
    // The step endpoint's own guards
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_step_report_is_refused_for_another_worker_a_foreign_position_and_a_wrong_name()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "guards",
                MarkerStep("Restore", "restore"),
                MarkerStep("Build", "build")));
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);
        var (foreignToken, _) = await host.RegisterWorkerAsync(name: "foreign-steps-agent");

        // The step reports exist inside a definition: freeze one first (the executor would have reported it
        // before the first step started).
        using (var definition = await RunTestHost.ReportDefinitionAsync(
            run.WorkerClient,
            run.WorkerToken,
            run.RunId,
            PipelineSpecJson.Serialize(new PipelineSpec(
                PipelineSpecSchema.Version,
                "guards",
                [new PipelineStep("Restore", Run: ["echo"]), new PipelineStep("Build", Run: ["echo"])]))))
        {
            Assert.Equal(HttpStatusCode.OK, definition.StatusCode);
        }

        // Not this worker's run.
        using (var foreign = await RunTestHost.ReportStepAsync(
            run.WorkerClient, foreignToken, run.RunId, 1, "Restore", PipelineStepStatus.Running))
        {
            Assert.Equal(HttpStatusCode.Conflict, foreign.StatusCode);
            Assert.Equal(RunErrorCodes.RunNotAssignedToWorker, await ReadErrorPropertyAsync(foreign));
        }

        // A position the frozen spec does not have, and a name that disagrees with it.
        using (var outOfRange = await RunTestHost.ReportStepAsync(
            run.WorkerClient, run.WorkerToken, run.RunId, 3, "Package", PipelineStepStatus.Running))
        {
            Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);
            Assert.Equal(RunErrorCodes.StepUnknown, await ReadErrorPropertyAsync(outOfRange));
        }

        using (var wrongName = await RunTestHost.ReportStepAsync(
            run.WorkerClient, run.WorkerToken, run.RunId, 1, "NotRestore", PipelineStepStatus.Running))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongName.StatusCode);
            Assert.Equal(RunErrorCodes.StepUnknown, await ReadErrorPropertyAsync(wrongName));
        }

        // A skip without a reason, and an exit code a non-exiting state claims.
        using (var reasonless = await RunTestHost.ReportStepAsync(
            run.WorkerClient, run.WorkerToken, run.RunId, 1, "Restore", PipelineStepStatus.Skipped))
        {
            Assert.Equal(HttpStatusCode.BadRequest, reasonless.StatusCode);
            Assert.Equal(RunErrorCodes.FieldInvalid, await ReadErrorPropertyAsync(reasonless));
        }

        using (var runningWithCode = await RunTestHost.ReportStepAsync(
            run.WorkerClient, run.WorkerToken, run.RunId, 1, "Restore", PipelineStepStatus.Running, exitCode: 4))
        {
            Assert.Equal(HttpStatusCode.BadRequest, runningWithCode.StatusCode);
        }

        // Nothing was recorded by any of the refused reports.
        Assert.Empty(await run.ReadStepsAsync());

        // The real transition then works, is idempotent when re-delivered, and a terminal step refuses a
        // further transition.
        Guid stepId;
        using (var running = await RunTestHost.ReportStepAsync(
            run.WorkerClient, run.WorkerToken, run.RunId, 1, "Restore", PipelineStepStatus.Running))
        {
            Assert.Equal(HttpStatusCode.OK, running.StatusCode);
            stepId = await ReadStepIdAsync(running);
        }

        using (var redelivered = await RunTestHost.ReportStepAsync(
            run.WorkerClient, run.WorkerToken, run.RunId, 1, "Restore", PipelineStepStatus.Running))
        {
            Assert.Equal(HttpStatusCode.OK, redelivered.StatusCode);
            Assert.Equal(stepId, await ReadStepIdAsync(redelivered));
        }

        using (var success = await RunTestHost.ReportStepAsync(
            run.WorkerClient, run.WorkerToken, run.RunId, 1, "Restore", PipelineStepStatus.Success, exitCode: 0))
        {
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        }

        using (var backToRunning = await RunTestHost.ReportStepAsync(
            run.WorkerClient, run.WorkerToken, run.RunId, 1, "Restore", PipelineStepStatus.Running))
        {
            Assert.Equal(HttpStatusCode.Conflict, backToRunning.StatusCode);
            Assert.Equal(RunErrorCodes.StepAlreadyFinished, await ReadErrorPropertyAsync(backToRunning));
        }

        // A later position is fine; an earlier one is out of order.
        using (var second = await RunTestHost.ReportStepAsync(
            run.WorkerClient, run.WorkerToken, run.RunId, 2, "Build", PipelineStepStatus.Running))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        using (var backwards = await RunTestHost.ReportStepAsync(
            run.WorkerClient, run.WorkerToken, run.RunId, 1, "Restore", PipelineStepStatus.Failed, exitCode: 1))
        {
            Assert.Equal(HttpStatusCode.Conflict, backwards.StatusCode);
            Assert.Equal(RunErrorCodes.StepAlreadyFinished, await ReadErrorPropertyAsync(backwards));
        }

        var recorded = await run.ReadStepsAsync();
        Assert.Equal(2, recorded.Count);
        Assert.Equal(PipelineStepStatus.Success, recorded[0].Status);
        Assert.Equal(PipelineStepStatus.Running, recorded[1].Status);
    }

    [Fact]
    public async Task AC_009_a_cancellation_fails_the_in_flight_step_and_skips_the_remaining_ones()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "canceled",
                PipelineTestData.Step("Build", "stream", "10000"),
                MarkerStep("Package", "package")));
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);

        using var cancellation = new CancellationTokenSource();
        run.CancellationToken = cancellation.Token;
        run.ReportTerminalState = true;
        run.OnReport = async report =>
        {
            if (report.Index == 1 && report.Status != PipelineStepStatus.Running)
            {
                return;
            }

            // The product order: the server records the cancel request (F-009's button), and only then does
            // the worker kill the tree. Posting the request through the real form is what makes the later
            // canceled report acceptable (it refuses a kill nobody asked for).
            using var requested = await Zenkins.Tests.Organizations.OrganizationTestHost.PostFormAsync(
                run.Owner,
                $"/runs/{run.RunId}",
                $"/runs/{run.RunId}/cancel",
                new Dictionary<string, string>());
            Assert.True(
                (int)requested.StatusCode is 302 or 303,
                $"the cancel request was not accepted ({(int)requested.StatusCode}).");

            cancellation.Cancel();
        };

        var outcome = await run.ExecuteAsync();

        Assert.True(outcome.Canceled);
        var steps = await run.ReadStepsAsync();
        Assert.Equal(PipelineStepStatus.Failed, steps[0].Status);
        Assert.Equal(PipelineStepFailureReasons.Canceled, steps[0].FailureReason);
        Assert.Null(steps[0].ExitCode);
        Assert.Equal(PipelineStepStatus.Skipped, steps[1].Status);
        Assert.Equal(PipelineStepFailureReasons.RunCanceled, steps[1].FailureReason);
        Assert.Null(steps[1].StartedAt);
        Assert.False(File.Exists(Path.Combine(run.Work.RepositoryPath, "marker.txt")));

        // The run itself is Canceled (the server maps the worker's canceled report), not Failed.
        var finished = await run.ReadRunAsync();
        Assert.Equal(BuildRunStatus.Canceled, finished.Status);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// The same rendered step with <c>continue_on_error = true</c> added: the helper emits one closing brace
    /// for the step table, which is where the flag belongs.
    /// </summary>
    private static string WithContinueOnError(string step) =>
        step.TrimEnd()[..^1] + ", continue_on_error = true }";

    /// <summary>A step that appends its name to the marker file (the tree is the proof that it ran).</summary>
    // ------------------------------------------------------------------
    // F-032: the attempt count on the one step row
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_step_record_carries_the_attempt_count_and_the_final_exit_code()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "attempts",
                MarkerStep("Restore", "restore"),
                MarkerStep("Build", "build")));
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);

        using (var definition = await RunTestHost.ReportDefinitionAsync(
            run.WorkerClient,
            run.WorkerToken,
            run.RunId,
            PipelineSpecJson.Serialize(new PipelineSpec(
                PipelineSpecSchema.Version,
                "attempts",
                [new PipelineStep("Restore", Run: ["echo"]), new PipelineStep("Build", Run: ["echo"])]))))
        {
            Assert.Equal(HttpStatusCode.OK, definition.StatusCode);
        }

        // The attempt count is bounded by the schema's own cap, and only a terminal report has one.
        using (var running = await RunTestHost.ReportStepAsync(
            run.WorkerClient, run.WorkerToken, run.RunId, 1, "Restore", PipelineStepStatus.Running))
        {
            Assert.Equal(HttpStatusCode.OK, running.StatusCode);
        }

        using (var runningWithAttempts = await RunTestHost.ReportStepAsync(
            run.WorkerClient, run.WorkerToken, run.RunId, 1, "Restore", PipelineStepStatus.Running,
            attempts: 2))
        {
            Assert.Equal(HttpStatusCode.BadRequest, runningWithAttempts.StatusCode);
            Assert.Equal(RunErrorCodes.FieldInvalid, await ReadErrorPropertyAsync(runningWithAttempts));
        }

        using (var tooMany = await RunTestHost.ReportStepAsync(
            run.WorkerClient, run.WorkerToken, run.RunId, 1, "Restore", PipelineStepStatus.Failed,
            exitCode: 3, attempts: 6))
        {
            Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
            Assert.Equal(RunErrorCodes.FieldInvalid, await ReadErrorPropertyAsync(tooMany));
        }

        using (var zero = await RunTestHost.ReportStepAsync(
            run.WorkerClient, run.WorkerToken, run.RunId, 1, "Restore", PipelineStepStatus.Failed,
            exitCode: 3, attempts: 0))
        {
            Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        }

        // Nothing above was recorded.
        var beforeTerminal = Assert.Single(await run.ReadStepsAsync());
        Assert.Equal(PipelineStepStatus.Running, beforeTerminal.Status);

        // A real retried step: one record, the third attempt's exit code, and the attempt count.
        using (var terminal = await RunTestHost.ReportStepAsync(
            run.WorkerClient, run.WorkerToken, run.RunId, 1, "Restore", PipelineStepStatus.Failed,
            exitCode: 3, attempts: 3))
        {
            Assert.Equal(HttpStatusCode.OK, terminal.StatusCode);
        }

        // And a step that declares nothing stays at the default, one.
        using (var plain = await RunTestHost.ReportStepAsync(
            run.WorkerClient, run.WorkerToken, run.RunId, 2, "Build", PipelineStepStatus.Success,
            exitCode: 0))
        {
            Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        }

        var steps = await run.ReadStepsAsync();
        Assert.Equal(2, steps.Count);
        Assert.Equal(3, steps.Single(step => step.Index == 1).Attempts);
        Assert.Equal(3, steps.Single(step => step.Index == 1).ExitCode);
        Assert.Equal(1, steps.Single(step => step.Index == 2).Attempts);

        // The page renders the count only when there was more than one attempt.
        var page = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(
            run.Owner, $"/runs/{run.RunId}");
        Assert.Contains("attempts 3", page, StringComparison.Ordinal);
        Assert.DoesNotContain("attempts 1", page, StringComparison.Ordinal);

        // AC-5 on the view side (card F-032): the page lists one <li> per DECLARED step - two here, even
        // though one of them made three attempts. A page that rendered one row per attempt (or one per
        // report) would show four here, and every earlier assertion in this fact would still pass.
        Assert.Equal(
            2,
            System.Text.RegularExpressions.Regex.Matches(page, """<li id="run-step-""").Count);
    }

    [Fact]
    public async Task A_retried_step_crosses_the_real_step_endpoint_as_one_accepted_record()
    {
        // Card F-032, AC-5 through the PRODUCT path: the executor's retry loop talks to the real
        // `/api/workers/runs/{id}/steps` endpoint (no offline sink), so the server's own index/name/order
        // rules see both attempts. Two attempts, one row - and the row says both things (Attempts = 2, the
        // final attempt's exit code).
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "server-retry",
                PipelineTestData.StepWith("Unstable", timeoutSeconds: null, retry: 2, "flaky", "attempts.txt", "1"),
                MarkerStep("After", "after")));
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);

        var outcome = await run.ExecuteAsync();

        Assert.Equal(0, outcome.ExitCode);
        Assert.False(outcome.Canceled);
        Assert.False(outcome.Abandoned);

        // The child ran twice (its own counter), the server recorded one row for that step, and the row
        // carries the attempt count plus the final exit code.
        Assert.Equal(
            ["2"],
            File.ReadAllLines(Path.Combine(run.Work.RepositoryPath, "attempts.txt"))
                .Select(line => line.Trim()));

        var steps = await run.ReadStepsAsync();
        Assert.Equal(2, steps.Count);
        var retried = steps.Single(step => step.Index == 1);
        Assert.Equal(PipelineStepStatus.Success, retried.Status);
        Assert.Equal(2, retried.Attempts);
        Assert.Equal(0, retried.ExitCode);
        Assert.Equal(1, steps.Single(step => step.Index == 2).Attempts);

        // Both transitions went through: the harness's sink appends the report it was handed and *then*
        // forwards it, so this count is what the executor asked for, not what the server accepted. The
        // acceptance is what the assertions above already prove - a 400/409 on the second attempt (a stale
        // index, a duplicated name, an out-of-order position) makes the sink answer Refused, the executor
        // give the run up, and `Assert.False(outcome.Abandoned)` plus the one-row assertion fail.
        Assert.Equal(2, run.Reports.Count(report => report.Index == 1));

        // And the page still lists the two declared steps, not the three process runs.
        var page = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(run.Owner, $"/runs/{run.RunId}");
        Assert.Equal(
            2,
            System.Text.RegularExpressions.Regex.Matches(page, """<li id="run-step-""").Count);
        Assert.Contains("attempts 2", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_whose_retries_are_exhausted_ends_one_failed_record_with_the_last_exit_code()
    {
        // Card F-032, the terminal half of AC-5 through the real endpoint: `retry = 2` and a child that fails
        // EVERY time, so the attempt loop ends on the exhausted branch - the one the success fact can never
        // reach. Still one row for the declared step (not one per attempt), Attempts = 2, Failed, and the
        // exit code of the LAST attempt.
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "exhausted",
                PipelineTestData.StepWith("AlwaysFails", timeoutSeconds: null, retry: 2, "flaky", "attempts.txt", "99"),
                MarkerStep("After", "after")));
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);

        var outcome = await run.ExecuteAsync();

        // Both attempts really ran, and the run is a failure that names the exhausted retries.
        Assert.Equal(
            ["2"],
            File.ReadAllLines(Path.Combine(run.Work.RepositoryPath, "attempts.txt"))
                .Select(line => line.Trim()));
        Assert.Equal(PipelineFailureReasons.StepRetryExhausted, outcome.FailureReason);
        Assert.Equal(3, outcome.ExitCode);
        Assert.False(outcome.Abandoned);

        // One record for the step - the same row both attempts' reports addressed.
        var steps = await run.ReadStepsAsync();
        Assert.Equal(2, steps.Count);
        var failed = steps.Single(step => step.Index == 1);
        Assert.Equal(PipelineStepStatus.Failed, failed.Status);
        Assert.Equal(2, failed.Attempts);
        Assert.Equal(3, failed.ExitCode);
        Assert.Null(failed.FailureReason);

        // The step after a failing step without continue_on_error never ran, and its record says so.
        Assert.Equal(PipelineStepStatus.Skipped, steps.Single(step => step.Index == 2).Status);

        // Two reports for that one step (Running + terminal), both addressed at index 1.
        Assert.Equal(2, run.Reports.Count(report => report.Index == 1));

        // And the page still lists the two declared steps, with the attempt count on the failed one.
        var page = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(run.Owner, $"/runs/{run.RunId}");
        Assert.Equal(
            2,
            System.Text.RegularExpressions.Regex.Matches(page, """<li id="run-step-""").Count);
        Assert.Contains("attempts 2", page, StringComparison.Ordinal);
    }

    private static string MarkerStep(string name, string marker) =>
        PipelineTestData.Step(name, "append", "marker.txt", marker);

    /// <summary>Polls the recorded rows until the given step is Running (the mid-run assertion's seam).</summary>
    private static async Task<IReadOnlyList<BuildStep>> WaitForRunningStepAsync(StepsRun run, int index)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var steps = await run.ReadStepsAsync();
            if (steps.Any(step => step.Index == index && step.Status == PipelineStepStatus.Running))
            {
                return steps;
            }

            await Task.Delay(20, CancellationToken);
        }

        Assert.Fail($"step {index} never reached Running within 30 seconds.");
        return [];
    }

    private static async Task<string> ReadErrorPropertyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("error").GetString()
            ?? throw new InvalidOperationException("The error response had no code.");
    }

    private static async Task<Guid> ReadStepIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("stepId").GetGuid();
    }
}
