using System.Diagnostics;
using System.Text;
using Xunit;
using Xunit.Sdk;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.RunCancellation;
using Zenkins.Worker;
using Zenkins.Worker.Git;
using Zenkins.Worker.Lua;
using Zenkins.Worker.Pipelines;

namespace Zenkins.Tests.Pipelines;

/// <summary>
/// Card F-014's execution phase, end to end and without a server: a real fixture repository is cloned,
/// the definition is read once, and the frozen steps run in order through the real
/// <see cref="ProcessRunner"/> in the run's own <c>repository/</c>.
/// <para>
/// The marker file each case asserts on is written by the steps themselves (ArgvEcho's
/// <c>append</c> mode), so "the third step did not run" is a fact about the working tree, not an
/// inference from the run log.
/// </para>
/// </summary>
[Trait("Category", "Pipeline")]
public sealed class PipelineRunnerTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------
    // TC-001: three steps run in order
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_three_step_pipeline_runs_in_order_and_reports_its_spec_first()
    {
        using var repository = PipelineRepository.Create(PipelineTestData.MarkerPipeline("marker.txt"));
        using var run = new PipelineRunHarness(repository);

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Null(outcome.FailureReason);
        Assert.Equal(repository.MainTip, outcome.Commit);

        // The marker file records the order the steps really ran in.
        Assert.Equal(
            ["restore", "build", "package"],
            File.ReadAllLines(Path.Combine(run.Work.RepositoryPath, "marker.txt")));

        // The definition was reported BEFORE the first step started: no marker existed yet when the
        // server received the step list (which is what lets a run page show the steps of a run that is
        // only just beginning).
        var report = Assert.Single(run.DefinitionReports);
        Assert.False(report.MarkerExistedAtReport);
        Assert.Equal(repository.MainTip, report.GitCommit);
        Assert.Equal(["Restore", "Build", "Package"], report.StepNames);

        // AC-3: the commit was reported on its own, BEFORE the definition phase ran (and therefore before
        // anything could fail), and the pipeline file was read exactly once for the whole run.
        Assert.Equal(repository.MainTip, Assert.Single(run.CommitReports).GitCommit);
        Assert.Equal(["commit", "definition"], run.ReportOrder);
        Assert.Equal(1, run.SourceReads);

        // Every step's exit code and duration is in the log lines this card emits.
        Assert.Contains(run.LogLines, line => line.Contains("step 1/3 \"Restore\" exited with code 0 after", StringComparison.Ordinal));
        Assert.Contains(run.LogLines, line => line.Contains("step 2/3 \"Build\" exited with code 0 after", StringComparison.Ordinal));
        Assert.Contains(run.LogLines, line => line.Contains("step 3/3 \"Package\" exited with code 0 after", StringComparison.Ordinal));
        // And the steps' own output is captured.
        Assert.Contains(run.OutputLines, line => line == "appended");
    }

    // ------------------------------------------------------------------
    // Card F-030: stages are presentation only - execution is unchanged
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_staged_pipeline_executes_the_same_flat_list_in_order_and_freezes_the_names()
    {
        // The same marker pipeline as TC-001, with the steps grouped Build/Build/Test/Package. If the card
        // changed execution in any way, the marker file would show a different order (or a missing step).
        using var repository =
            PipelineRepository.Create(
                PipelineTestData.Pipeline(
                    "staged",
                    PipelineTestData.StepInStage("Build", "Restore", "append", "marker.txt", "restore"),
                    PipelineTestData.StepInStage("Build", "Build", "append", "marker.txt", "build"),
                    PipelineTestData.StepInStage("Test", "Test", "append", "marker.txt", "test"),
                    PipelineTestData.StepInStage("Package", "Package", "append", "marker.txt", "package")));
        using var run = new PipelineRunHarness(repository);

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Null(outcome.FailureReason);
        Assert.Equal(
            ["restore", "build", "test", "package"],
            File.ReadAllLines(Path.Combine(run.Work.RepositoryPath, "marker.txt")));

        // One step report per step, in index order - stages do not add, merge or reorder a step record.
        var report = Assert.Single(run.DefinitionReports);
        Assert.True(PipelineSpecJson.TryRead(report.PipelineSpecJson, out var spec, out var error), error);
        Assert.NotNull(spec);
        Assert.Equal(3, spec!.Version);
        Assert.Equal(["Restore", "Build", "Test", "Package"], spec.Steps.Select(step => step.Name));
        Assert.Equal(["Build", "Build", "Test", "Package"], spec.Steps.Select(step => step.Stage));
    }

    [Fact]
    public async Task A_v2_frozen_spec_still_executes_exactly_as_it_did()
    {
        // A Freestyle spec frozen before F-030: version 2, no stage on its step. The executor reads the same
        // flat list it always read - the field this card added is absent, not empty, and changes nothing.
        var spec = new PipelineSpec(
            2,
            "legacy",
            [
                new PipelineStep(
                    "Only",
                    Run:
                    [
                        PipelineTestData.DotNet,
                        PipelineTestData.ArgvEcho,
                        "append",
                        "marker.txt",
                        "ran",
                    ]),
            ]);
        using var run = new PipelineRunHarness(PipelineSpecJson.Serialize(spec));

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Null(outcome.FailureReason);
        // A frozen-spec run has no checkout: its steps' cwd is the run's workspace (F-020), unchanged by
        // this card - the marker is the proof the one step really ran where it always did.
        Assert.Equal(["ran"], File.ReadAllLines(Path.Combine(run.Work.WorkspacePath, "marker.txt")));
        Assert.Empty(Directory.GetFiles(run.Work.RepositoryPath, "*", SearchOption.AllDirectories));
        Assert.Contains(run.LogLines, line => line.Contains("step 1/1", StringComparison.Ordinal));
    }

    /// <summary>
    /// The v2 <b>document</b>, not a model this build serialised: hand-written JSON with <c>"version": 2</c> and no
    /// <c>stage</c> on any step, four steps long, executed by the real child processes. Each step appends its own
    /// word to the marker file, so an executor that reordered, grouped or dropped steps by a field v2 does not
    /// have would change what the file says - the marker is the order, not an inference from the log.
    /// <para>
    /// <b>Why there is no clone, and why that is the production shape.</b> A frozen document only travels to a
    /// Worker on a Freestyle run: <see cref="RunDefinitionSources.For"/> answers <c>repository</c> for any run with
    /// a branch, and a repository run derives its spec from the checkout (the writer freezes v3 today, so a
    /// repository run never carries a v2 document). Asserting a clone here would therefore assert a shape the
    /// product does not have; the clone-plus-order half is
    /// <c>A_three_step_pipeline_runs_in_order_and_reports_its_spec_first</c>'s job.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_v2_document_literal_executes_its_whole_flat_step_list_in_order()
    {
        var specJson = $$"""
        {
          "version": 2,
          "name": "legacy",
          "steps": [
            { "name": "Restore", "run": ["{{PipelineTestData.DotNet}}", "{{PipelineTestData.ArgvEcho}}", "append", "marker.txt", "restore"] },
            { "name": "Build", "run": ["{{PipelineTestData.DotNet}}", "{{PipelineTestData.ArgvEcho}}", "append", "marker.txt", "build"] },
            { "name": "Test", "run": ["{{PipelineTestData.DotNet}}", "{{PipelineTestData.ArgvEcho}}", "append", "marker.txt", "test"] },
            { "name": "Package", "run": ["{{PipelineTestData.DotNet}}", "{{PipelineTestData.ArgvEcho}}", "append", "marker.txt", "package"] }
          ]
        }
        """;

        // The document really is v2 with no stage anywhere - the executor below cannot be passing because the
        // reader silently upgraded the version or filled the field in.
        Assert.True(PipelineSpecJson.TryRead(specJson, out var frozen, out var readError), readError);
        Assert.Equal(2, frozen!.Version);
        Assert.All(frozen.Steps, step => Assert.Null(step.Stage));

        using var run = new PipelineRunHarness(specJson);

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Null(outcome.FailureReason);

        // The whole list, in declaration order: four markers, in the order the document declared them.
        Assert.Equal(
            ["restore", "build", "test", "package"],
            File.ReadAllLines(Path.Combine(run.Work.WorkspacePath, "marker.txt")));

        // One Running and one terminal report per step, index 1..4 in order - the document's flat list, executed
        // as one list, not grouped into sections of any kind.
        Assert.Equal([1, 1, 2, 2, 3, 3, 4, 4], run.StepReports.Select(report => report.Index));
        Assert.Equal(
            ["Restore", "Build", "Test", "Package"],
            run.StepReports
                .Where(report => report.Status == PipelineStepStatus.Success)
                .Select(report => report.Name));
    }

    // ------------------------------------------------------------------
    // Card F-031: post actions
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_successful_main_list_runs_the_success_set_then_always_and_never_the_failure_set()
    {
        using var repository = PipelineRepository.Create(
            PipelineWithPost(
                PipelineTestData.Step("Main", "append", "marker.txt", "main"),
                success: PipelineTestData.Step("AllGood", "append", "marker.txt", "success"),
                failure: PipelineTestData.Step("OnFail", "append", "marker.txt", "failure"),
                always: PipelineTestData.Step("Cleanup", "append", "marker.txt", "always")));
        using var run = new PipelineRunHarness(repository);

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Null(outcome.FailureReason);

        // The marker file is the order the steps really ran in: success, then always - never the failure set.
        Assert.Equal(["main", "success", "always"], MarkerLines(run));
        Assert.Equal(["Main", "post/AllGood", "post/Cleanup"], TerminalNames(run));

        // The frozen spec carries the prefixed names (so a record and a log line are unambiguous).
        var report = Assert.Single(run.DefinitionReports);
        Assert.True(PipelineSpecJson.TryRead(report.PipelineSpecJson, out var spec, out var error), error);
        Assert.NotNull(spec!.Post);
        Assert.Equal(
            ["post/AllGood", "post/OnFail", "post/Cleanup"],
            spec.Post!.DeclaredSteps.Select(declared => declared.Step.Name));
    }

    [Fact]
    public async Task A_failed_main_list_runs_the_failure_set_then_always_and_never_the_success_set()
    {
        using var repository = PipelineRepository.Create(
            PipelineWithPost(
                PipelineTestData.Step("Main", "exit", "3"),
                success: PipelineTestData.Step("AllGood", "append", "marker.txt", "success"),
                failure: PipelineTestData.Step("OnFail", "append", "marker.txt", "failure"),
                always: PipelineTestData.Step("Cleanup", "append", "marker.txt", "always")));
        using var run = new PipelineRunHarness(repository);

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(3, outcome.ExitCode);
        Assert.Equal(PipelineFailureReasons.StepFailed, outcome.FailureReason);
        Assert.Equal(["failure", "always"], MarkerLines(run));
        Assert.Equal(["Main", "post/OnFail", "post/Cleanup"], TerminalNames(run));
    }

    [Fact]
    public async Task A_failing_post_step_is_recorded_but_does_not_flip_a_successful_run_by_default()
    {
        using var repository = PipelineRepository.Create(
            PipelineWithPost(
                PipelineTestData.Step("Main", "append", "marker.txt", "main"),
                always: PipelineTestData.Step("Broken", "exit", "4")));
        using var run = new PipelineRunHarness(repository);

        var outcome = await run.ExecuteAsync(CancellationToken);

        // The main list decided the result; the broken cleanup is visible as its own failed step.
        Assert.Equal(0, outcome.ExitCode);
        Assert.Null(outcome.FailureReason);

        var broken = Assert.Single(run.StepReports, report =>
            report.Name == "post/Broken" && report.Status == PipelineStepStatus.Failed);
        Assert.Equal(4, broken.ExitCode);
        Assert.Equal(["main"], MarkerLines(run));
    }

    [Fact]
    public async Task The_projects_switch_makes_a_failed_post_step_fail_the_run()
    {
        using var repository = PipelineRepository.Create(
            PipelineWithPost(
                PipelineTestData.Step("Main", "append", "marker.txt", "main"),
                always: PipelineTestData.Step("Broken", "exit", "4")));
        using var run = new PipelineRunHarness(repository) { PostFailureFailsRun = true };

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(4, outcome.ExitCode);
        Assert.Equal(PipelineFailureReasons.PostFailed, outcome.FailureReason);
    }

    [Fact]
    public async Task A_canceled_run_still_runs_always_inside_the_grace_and_stays_canceled()
    {
        using var repository = PipelineRepository.Create(
            PipelineWithPost(
                PipelineTestData.Step("Long", "stream", "5000"),
                success: PipelineTestData.Step("AllGood", "append", "marker.txt", "success"),
                failure: PipelineTestData.Step("OnFail", "append", "marker.txt", "failure"),
                always: PipelineTestData.Step("Cleanup", "append", "marker.txt", "cleanup")));
        using var cancellation = new CancellationTokenSource();
        using var run = new PipelineRunHarness(repository)
        {
            OnStepRunning = _ => cancellation.Cancel(),
        };

        var outcome = await run.ExecuteAsync(cancellation.Token);

        Assert.True(outcome.Canceled);
        // The cleanup really ran after the cancel - that is the point of the grace window.
        Assert.Equal(["cleanup"], MarkerLines(run));
        Assert.Equal(["Long", "post/Cleanup"], TerminalNames(run));

        // ... and the run stays Canceled: a successful cleanup does not resurrect it.
        Assert.Null(outcome.ExitCode);
        Assert.Null(outcome.FailureReason);
    }

    [Fact]
    public async Task A_post_step_that_overruns_the_cancel_grace_is_killed_and_recorded()
    {
        using var repository = PipelineRepository.Create(
            PipelineWithPost(
                PipelineTestData.Step("Long", "stream", "5000"),
                always: PipelineTestData.Step("SlowCleanup", "stream", "5000")));
        using var cancellation = new CancellationTokenSource();
        using var run = new PipelineRunHarness(repository)
        {
            OnStepRunning = _ => cancellation.Cancel(),
            PostGrace = TimeSpan.FromMilliseconds(250),
        };

        var outcome = await run.ExecuteAsync(cancellation.Token);

        Assert.True(outcome.Canceled);
        var cleanup = Assert.Single(run.StepReports, report =>
            report.Name == "post/SlowCleanup" && report.Status == PipelineStepStatus.Failed);
        Assert.Equal(PipelineStepFailureReasons.PostGraceExceeded, cleanup.FailureReason);
    }

    [Fact]
    public async Task A_deadline_cancel_clamps_the_grace_to_the_deadline_and_kills_an_overrunning_post_step()
    {
        using var repository = PipelineRepository.Create(
            PipelineWithPost(
                PipelineTestData.Step("Long", "stream", "5000"),
                always: PipelineTestData.Step("SlowCleanup", "stream", "5000")));

        // A manual clock moves the run to half a second before its deadline at the instant the cancel fires: the
        // definition phase ran with the full 30s, and the post window is clamped to the 0.5s the deadline left -
        // not the 30s an operator cancel would get.
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualClock(now);
        using var cancellation = new CancellationTokenSource();
        using var run = new PipelineRunHarness(repository)
        {
            Clock = clock,
            Deadline = now.AddSeconds(30),
            CancellationFailureReason = () => Zenkins.Shared.RunCancellation.RunCancelReasons.Timeout,
            OnStepRunning = _ =>
            {
                clock.Advance(TimeSpan.FromSeconds(29.5));
                cancellation.Cancel();
            },
        };

        var outcome = await run.ExecuteAsync(cancellation.Token);

        Assert.True(outcome.Canceled);
        var cleanup = Assert.Single(run.StepReports, report =>
            report.Name == "post/SlowCleanup" && report.Status == PipelineStepStatus.Failed);
        // The kill is the run's deadline (not the cancel-grace value): the window was clamped to it.
        Assert.Equal(PipelineStepFailureReasons.TimedOut, cleanup.FailureReason);
    }

    [Fact]
    public async Task A_deadline_with_no_time_left_records_the_post_steps_as_skipped_and_runs_nothing()
    {
        using var repository = PipelineRepository.Create(
            PipelineWithPost(
                PipelineTestData.Step("Long", "stream", "5000"),
                always: PipelineTestData.Step("Cleanup", "append", "marker.txt", "cleanup")));
        // A manual clock lets the deadline pass at the exact moment the cancel arrives: the definition phase
        // ran inside the deadline, and by the time post would start there is no time left to clamp to.
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualClock(now);
        using var cancellation = new CancellationTokenSource();
        using var run = new PipelineRunHarness(repository)
        {
            Clock = clock,
            Deadline = now.AddSeconds(30),
            CancellationFailureReason = () => Zenkins.Shared.RunCancellation.RunCancelReasons.Timeout,
            OnStepRunning = _ =>
            {
                clock.Advance(TimeSpan.FromSeconds(60));
                cancellation.Cancel();
            },
        };

        var outcome = await run.ExecuteAsync(cancellation.Token);

        Assert.True(outcome.Canceled);
        Assert.False(File.Exists(Path.Combine(run.Work.RepositoryPath, "marker.txt")));
        var cleanup = Assert.Single(run.StepReports, report =>
            report.Name == "post/Cleanup" && report.Status == PipelineStepStatus.Skipped);
        Assert.Equal(PipelineStepFailureReasons.RunCanceled, cleanup.FailureReason);
    }

    // ------------------------------------------------------------------
    // TC-002: a failing step stops the run
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_failing_step_stops_the_run_and_records_its_exact_exit_code()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "failing",
                PipelineTestData.Step("Restore", "append", "marker.txt", "restore"),
                PipelineTestData.Step("Build", "exit", "7"),
                PipelineTestData.Step("Package", "append", "marker.txt", "package")));
        using var run = new PipelineRunHarness(repository);

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(7, outcome.ExitCode);
        Assert.Equal(PipelineFailureReasons.StepFailed, outcome.FailureReason);

        // The third step never ran: the marker holds the first step only.
        Assert.Equal(
            ["restore"],
            File.ReadAllLines(Path.Combine(run.Work.RepositoryPath, "marker.txt")));

        Assert.Contains(run.LogLines, line => line.Contains("step 2/3 \"Build\" exited with code 7 after", StringComparison.Ordinal));
        Assert.DoesNotContain(run.LogLines, line => line.Contains("step 3/3", StringComparison.Ordinal));

        // The log says what happened and does not invent a cause for the exit code.
        Assert.Contains(run.LogLines, line => line.Contains("exited with code 7", StringComparison.Ordinal));
        Assert.DoesNotContain(run.LogLines, line => line.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(run.LogLines, line => line.Contains("crash", StringComparison.OrdinalIgnoreCase));

        // The frozen spec was still reported before the first step: the step list exists even though
        // the run failed.
        Assert.Equal(
            ["Restore", "Build", "Package"],
            Assert.Single(run.DefinitionReports).StepNames);
    }

    // ------------------------------------------------------------------
    // TC-004: the spec is frozen, never re-derived
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_executed_spec_is_the_frozen_one_even_if_both_copies_of_the_pipeline_change_while_step_one_runs()
    {
        // The definition the run must execute: three steps, one marker line each.
        var frozen = PipelineTestData.Pipeline(
            "frozen",
            PipelineTestData.Step("First", "append", "marker.txt", "first"),
            PipelineTestData.Step("Second", "append", "marker.txt", "second"),
            PipelineTestData.Step("Third", "append", "marker.txt", "third"));

        // The definition somebody writes DURING the run: a fourth step that would leave its own marker.
        var rewritten = PipelineTestData.Pipeline(
            "rewritten",
            PipelineTestData.Step("First", "append", "marker.txt", "first"),
            PipelineTestData.Step("Second", "append", "marker.txt", "second"),
            PipelineTestData.Step("Third", "append", "marker.txt", "third"),
            PipelineTestData.Step("Fourth", "append", "fourth.txt", "fourth"));

        using var repository = PipelineRepository.Create(
            // Step one rewrites the working tree's own pipeline.lua while it runs (ArgvEcho's
            // `probe` mode writes a file relative to the step's cwd, which is the checkout).
            PipelineTestData.Pipeline(
                "frozen",
                PipelineTestData.Step("First", "probe", "pipeline.lua", rewritten),
                PipelineTestData.Step("Second", "append", "marker.txt", "second"),
                PipelineTestData.Step("Third", "append", "marker.txt", "third")));
        using var run = new PipelineRunHarness(repository)
        {
            // Between the definition report and the first step, the branch moves too: a new commit
            // with the four-step definition, pushed to the origin the run cloned from.
            BeforeFirstStep = () =>
            {
                var commit = repository.CommitPipeline(rewritten);
                Assert.Equal(repository.MainTip, commit);
            },
        };

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(0, outcome.ExitCode);

        // Both copies now declare a fourth step...
        var localPipeline = await File.ReadAllTextAsync(
            Path.Combine(run.Work.RepositoryPath, "pipeline.lua"), CancellationToken);
        Assert.Contains("Fourth", localPipeline, StringComparison.Ordinal);
        Assert.Contains("Fourth", repository.ShowMainFile("pipeline.lua"), StringComparison.Ordinal);

        // ...and the run still executed exactly the three frozen steps: the fourth marker is absent and
        // the marker file holds one line per frozen step, in order.
        Assert.False(
            File.Exists(Path.Combine(run.Work.RepositoryPath, "fourth.txt")),
            "the fourth step ran: the executor re-read pipeline.lua instead of using the frozen spec.");
        Assert.Equal(
            ["second", "third"],
            File.ReadAllLines(Path.Combine(run.Work.RepositoryPath, "marker.txt")));

        // The run's own record is the frozen step list, not the branch's new one - and the definition
        // file was read exactly once, which is *why* the rewritten copies cannot matter.
        Assert.Equal(1, run.SourceReads);
        var report = Assert.Single(run.DefinitionReports);
        Assert.Equal(["First", "Second", "Third"], report.StepNames);
        Assert.True(PipelineSpecJson.TryRead(report.PipelineSpecJson, out var frozenSpec, out var specError), specError);
        Assert.Equal(3, frozenSpec!.Steps.Length);
        Assert.DoesNotContain(frozenSpec.Steps, step => step.Name == "Fourth");
    }

    // ------------------------------------------------------------------
    // Definition failures execute nothing
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_missing_pipeline_file_fails_as_pipeline_missing_and_spawns_nothing()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline("Echo", PipelineTestData.Step("Echo", "append", "marker.txt", "ran")));
        using var run = new PipelineRunHarness(repository, pipelinePath: "ci/pipeline.lua");

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(PipelineFailureReasons.PipelineMissing, outcome.FailureReason);
        Assert.Null(outcome.ExitCode);
        Assert.Empty(run.DefinitionReports);

        // AC-3: the commit was reported right after the clone, so a run whose definition is missing
        // still records what it checked out - and the definition report (which carries the spec) was
        // never sent, because there was no spec to send.
        Assert.Equal(repository.MainTip, Assert.Single(run.CommitReports).GitCommit);
        Assert.Equal(["commit"], run.ReportOrder);
        Assert.Equal(repository.MainTip, outcome.Commit);

        // m3: nothing executed - not only "no captured output" but "the step's marker file is absent".
        Assert.Empty(run.OutputLines);
        Assert.False(File.Exists(Path.Combine(run.Work.RepositoryPath, "marker.txt")));
        Assert.False(File.Exists(Path.Combine(run.Work.RepositoryPath, "ok.txt")));
    }

    [Fact]
    public async Task A_syntax_error_fails_as_pipeline_syntax_with_a_position_and_executes_nothing()
    {
        using var repository = PipelineRepository.Create("local x = = 1");
        using var run = new PipelineRunHarness(repository);

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(PipelineFailureReasons.PipelineSyntax, outcome.FailureReason);
        Assert.Empty(run.DefinitionReports);
        Assert.Empty(run.OutputLines);
        Assert.Equal(repository.MainTip, Assert.Single(run.CommitReports).GitCommit);
        Assert.False(File.Exists(Path.Combine(run.Work.RepositoryPath, "marker.txt")));
        Assert.Contains(run.LogLines, line => line.Contains("pipeline.lua:(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_invalid_spec_fails_as_pipeline_invalid_with_the_field_path_and_executes_nothing()
    {
        using var repository = PipelineRepository.Create(
            "pipeline { name = \"bad\", steps = { { name = \"a\", run = { \"x\" }, runs = { \"y\" } } } }");
        using var run = new PipelineRunHarness(repository);

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(PipelineFailureReasons.PipelineInvalid, outcome.FailureReason);
        Assert.Empty(run.DefinitionReports);
        Assert.Empty(run.OutputLines);
        Assert.Equal(repository.MainTip, Assert.Single(run.CommitReports).GitCommit);
        Assert.False(File.Exists(Path.Combine(run.Work.RepositoryPath, "marker.txt")));
        Assert.Contains(run.LogLines, line => line.Contains("pipeline.steps[1] has an unknown key 'runs'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_runaway_definition_fails_as_pipeline_timeout_and_executes_nothing()
    {
        using var repository = PipelineRepository.Create("while true do end");
        using var run = new PipelineRunHarness(repository)
        {
            // The same deadline bounds the clone, so it must be long enough for a local clone to
            // finish: what this asserts is that the runaway script is abandoned at that deadline.
            Deadline = DateTimeOffset.UtcNow.AddSeconds(3),
        };

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(PipelineFailureReasons.PipelineTimeout, outcome.FailureReason);
        Assert.Empty(run.DefinitionReports);
        Assert.Empty(run.OutputLines);
        Assert.Equal(repository.MainTip, Assert.Single(run.CommitReports).GitCommit);
        Assert.False(File.Exists(Path.Combine(run.Work.RepositoryPath, "marker.txt")));
    }

    // ------------------------------------------------------------------
    // Branch selection and the git layer's failures
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_run_executes_the_definition_of_the_branch_it_was_triggered_on()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline("main-pipeline", PipelineTestData.Step("Main", "append", "main.txt", "main")));
        repository.CommitPipelineOnBranch(
            "release",
            PipelineTestData.Pipeline("release-pipeline", PipelineTestData.Step("Release", "append", "release.txt", "release")));

        using var run = new PipelineRunHarness(repository, branch: "release");

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(["Release"], Assert.Single(run.DefinitionReports).StepNames);
        Assert.True(File.Exists(Path.Combine(run.Work.RepositoryPath, "release.txt")));
        Assert.False(File.Exists(Path.Combine(run.Work.RepositoryPath, "main.txt")));

        // The pipeline path is the project's; the default was used here.
        Assert.Equal("pipeline.lua", run.PipelinePath);
    }

    [Fact]
    public async Task A_worker_without_git_fails_with_a_reason_that_names_git()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline("Echo", PipelineTestData.Step("Echo", "append", "marker.txt", "ran")));
        using var run = new PipelineRunHarness(repository)
        {
            // The production path A: git is not resolvable at all. A name that cannot be started is
            // the deterministic stand-in for "the worker host has no git binary" - the start fails, it
            // does not hang (the same seam F-013's own availability test uses).
            GitExecutable = $"no-such-git-{Guid.NewGuid():N}",
        };

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(PipelineFailureReasons.GitUnavailable, outcome.FailureReason);
        Assert.Null(outcome.ExitCode);
        Assert.Empty(run.DefinitionReports);
        Assert.Contains(run.LogLines, line => line.Contains("git", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------
    // M2: a deterministically refused report stops the run
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_definitively_refused_definition_report_aborts_before_any_step_runs()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "refused",
                PipelineTestData.Step("First", "append", "marker.txt", "first"),
                PipelineTestData.Step("Second", "append", "marker.txt", "second")));
        using var run = new PipelineRunHarness(repository)
        {
            // The server answers 409/410: another worker owns the run, it was canceled, or it is already
            // terminal. Running the steps now would produce real side effects for a run nobody owns.
            RejectDefinitionReport = true,
        };

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.True(outcome.Abandoned);
        Assert.Null(outcome.ExitCode);
        Assert.Null(outcome.FailureReason);
        Assert.Empty(run.OutputLines);
        Assert.False(File.Exists(Path.Combine(run.Work.RepositoryPath, "marker.txt")));
        Assert.Contains(run.LogLines, line => line.Contains("refused this run's pipeline definition", StringComparison.Ordinal));

        // The commit report itself was accepted, so the abort is specifically about the refused spec.
        Assert.Single(run.CommitReports);
    }

    [Fact]
    public async Task A_definitively_refused_commit_report_aborts_before_the_definition_runs()
    {
        using var repository = PipelineRepository.Create(
            PipelineTestData.MarkerPipeline("marker.txt"));
        using var run = new PipelineRunHarness(repository) { RejectCommitReport = true };

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.True(outcome.Abandoned);
        Assert.Single(run.CommitReports); // attempted, refused
        Assert.Empty(run.DefinitionReports);
        Assert.Equal(0, run.SourceReads); // not even the definition phase ran
        Assert.False(File.Exists(Path.Combine(run.Work.RepositoryPath, "marker.txt")));
    }

    // ------------------------------------------------------------------
    // The Freestyle script step (card F-020; the facts are card Q-012's)
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_script_step_lands_in_temp_as_step_N_in_the_platform_encoding()
    {
        // Two script steps, so the name is pinned by position and not by "there is one file".
        var spec = ScriptSpec("echo café", "echo second");
        using var run = new PipelineRunHarness(PipelineSpecJson.Serialize(spec));

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(0, outcome.ExitCode);

        // temp/step-N.{cmd|sh}: the run's own scratch directory, the step's 1-based position, and the
        // platform's extension - never the other platform's.
        var extension = OperatingSystem.IsWindows() ? "cmd" : "sh";
        var otherPlatform = OperatingSystem.IsWindows() ? "sh" : "cmd";
        var first = Path.Combine(run.Work.TempPath, $"step-1.{extension}");
        var second = Path.Combine(run.Work.TempPath, $"step-2.{extension}");
        Assert.True(File.Exists(first), $"the first script step's file is missing: {first}");
        Assert.True(File.Exists(second), $"the second script step's file is missing: {second}");
        Assert.Empty(Directory.GetFiles(run.Work.TempPath, $"step-*.{otherPlatform}"));

        // The bytes are the platform's contract (card B-016): Unix gets UTF-8 without a BOM; Windows
        // gets the code page cmd.exe reads, through the same encoder the runner uses.
        var written = File.ReadAllBytes(first);
        var expectedFirst = OperatingSystem.IsWindows()
            ? WindowsBatchScript.Encode("echo café", WindowsCodePages.Oem!).Bytes
            : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes("echo café");
        Assert.Equal(expectedFirst, written);
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(
                written.Length >= 3 && written[0] == 0xEF && written[1] == 0xBB && written[2] == 0xBF,
                "the script file starts with a UTF-8 BOM");
        }

        // And the file is what actually ran: the non-ASCII text came back through the child's stdout.
        var expectedSecond = OperatingSystem.IsWindows()
            ? WindowsBatchScript.Encode("echo second", WindowsCodePages.Oem!).Bytes
            : new UTF8Encoding(false).GetBytes("echo second");
        Assert.Equal(expectedSecond, File.ReadAllBytes(second));
        Assert.Contains("café", run.OutputLines);
        Assert.Contains("second", run.OutputLines);
    }

    [Fact]
    public async Task A_script_step_runs_under_sh_minus_e_so_a_failing_line_ends_the_script()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip(
                "cmd /c has no -e: a failing line does not end a command file, so the Unix `sh -e` "
                + "pin has no Windows counterpart to execute. The Windows half of the invocation is "
                + "pinned at the source level by the next fact (recorded reason: no Windows host was "
                + "in the reviewing loop).");
        }

        // Two lines: the first fails and the second would print. Without `-e` the shell would run the
        // second line and the step would exit 0 - so the outcome is what pins the flag, not a comment.
        var script = "false\necho SURVIVED";
        using var run = new PipelineRunHarness(PipelineSpecJson.Serialize(ScriptSpec(script)));

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal(PipelineFailureReasons.StepFailed, outcome.FailureReason);
        Assert.DoesNotContain("SURVIVED", run.OutputLines);
        Assert.Contains(run.LogLines, line => line.Contains("exited with code 1", StringComparison.Ordinal));

        // The file really holds both lines: the script stopped at the failure, it was not truncated.
        var extension = OperatingSystem.IsWindows() ? "cmd" : "sh";
        Assert.Equal(
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(script),
            File.ReadAllBytes(Path.Combine(run.Work.TempPath, $"step-1.{extension}")));
    }

    [Fact]
    public void The_windows_half_of_the_script_invocation_is_pinned_at_the_source_level()
    {
        // Recorded reason (card Q-012): the facts above were executed on macOS, with no Windows host in
        // the loop, so the `cmd` branch cannot be run here. This is the code-level pin the card allows:
        // the two literals that make a script step work on Windows at all - the extension the file is
        // written with and the `/d /s /c` invocation (`/s` makes a path with spaces safe).
        var source = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Worker", "Pipelines", "PipelineRunner.cs"));

        Assert.Contains("OperatingSystem.IsWindows() ? \"cmd\" : \"sh\"", source, StringComparison.Ordinal);
        Assert.Contains("[\"cmd.exe\", \"/d\", \"/s\", \"/c\", scriptFile]", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// <summary>
    /// A definition with a post table (card F-031): the main step plus whichever outcome sets the fact needs.
    /// Post steps are authored with plain names; the reader freezes them with the `post/` prefix.
    /// </summary>
    private static string PipelineWithPost(
        string mainStep,
        string? success = null,
        string? failure = null,
        string? always = null)
    {
        var lists = new List<string>();
        if (success is not null)
        {
            lists.Add($"success = {{ {success} }},");
        }

        if (failure is not null)
        {
            lists.Add($"failure = {{ {failure} }},");
        }

        if (always is not null)
        {
            lists.Add($"always = {{ {always} }},");
        }

        return $$"""
            pipeline {
              name = "post",
              steps = { {{mainStep}} },
              post = {
            {{string.Join("\n", lists.Select(list => "    " + list))}}
              },
            }
            """;
    }

    /// <summary>The marker file's lines, in the order the steps appended them (a repository run's cwd).</summary>
    private static string[] MarkerLines(PipelineRunHarness run) =>
        File.ReadAllLines(Path.Combine(run.Work.RepositoryPath, "marker.txt"));

    /// <summary>The names of a run's terminal step reports, in order - what a page lists as its steps.</summary>
    private static string[] TerminalNames(PipelineRunHarness run) =>
        [.. run.StepReports
            .Where(report => PipelineStepStatus.IsTerminal(report.Status))
            .Select(report => report.Name)];

    /// A Freestyle spec over one script per entry (card F-020): built and serialized by the product's
    /// own contract, so the shape the runner reads is the shape the Server freezes.
    /// </summary>
    private static PipelineSpec ScriptSpec(params string[] scripts) => new(
        PipelineSpecSchema.Version,
        "script-shape",
        [.. scripts.Select((script, index) => new PipelineStep($"Step {index + 1}", Script: script))]);

    private static string RepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (File.Exists(Path.Combine(directory, "Zenkins.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory)!;
        }

        Assert.Fail("the repository root (Zenkins.slnx) was not found above the test output.");
        return string.Empty;
    }

    /// <summary>
    /// One pipeline run with the real Git layer, the real definition phase and the real process runner,
    /// in a throwaway work root. The only stand-ins are the server hop (the definition report) and the
    /// credential fetch (there is no server here, so there is no credential).
    /// </summary>
    // ------------------------------------------------------------------
    // F-022: an inline script is described like a repository file
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_inline_script_runs_its_steps_in_the_workspace_and_leaves_repository_empty()
    {
        var script = PipelineTestData.MarkerPipeline("marker.txt");
        using var run = new PipelineRunHarness(script, inline: true);

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Null(outcome.FailureReason);
        Assert.Null(outcome.Commit);

        // TC-001, read against the card's own §3.4.6 trio: Prepare always creates repository/, so the
        // honest assertion is that it stays EMPTY - the steps' cwd is workspace/, where the marker lands.
        Assert.True(Directory.Exists(run.Work.RepositoryPath));
        Assert.Empty(Directory.GetFiles(run.Work.RepositoryPath, "*", SearchOption.AllDirectories));
        Assert.Equal(
            ["restore", "build", "package"],
            File.ReadAllLines(Path.Combine(run.Work.WorkspacePath, "marker.txt")));

        // The script really became temp/pipeline.lua, byte for byte (UTF-8 without a BOM), and the
        // definition's chunk name is pipeline.lua - not the absolute path, not empty.
        var materialized = Path.Combine(run.Work.TempPath, PipelineScriptLimits.InlineFileName);
        Assert.True(File.Exists(materialized));
        Assert.Equal(script, await File.ReadAllTextAsync(materialized, CancellationToken));
        Assert.Equal(1, run.SourceReads);

        var report = Assert.Single(run.DefinitionReports);
        Assert.Null(report.GitCommit);
        Assert.Equal(["Restore", "Build", "Package"], report.StepNames);
        Assert.Empty(run.CommitReports);
        Assert.Equal(["definition"], run.ReportOrder);
        Assert.Contains(run.LogLines, line => line.Contains("inline pipeline run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_inline_definition_carries_its_triggers_pipeline_env_and_secret_references()
    {
        // The step runs through `dotnet <ArgvEcho>` like every other step in this file (PipelineTestData
        // .Step's own rendering): a hand-written `echo` is not an executable on Windows, where the step
        // would then fail with step-failed before the definition facts below could be read.
        var script = $$"""
            local token = secret("TOKEN")
            pipeline {
              name = "inline-definition",
              env = { REGION = "eu" },
              triggers = { branches = { "main", "release/*" } },
              steps = { {{PipelineTestData.Step("Only", "echo", "hi")}} },
            }
            """;
        using var run = new PipelineRunHarness(script, inline: true);

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Null(outcome.FailureReason);
        var report = Assert.Single(run.DefinitionReports);
        Assert.True(PipelineSpecJson.TryRead(report.PipelineSpecJson, out var spec, out var error), error);
        Assert.Equal("inline-definition", spec!.Name);
        Assert.Equal("eu", spec.Env!["REGION"]);
        Assert.Equal(["main", "release/*"], spec.Triggers!.Branches!);
        Assert.Equal(["TOKEN"], spec.SecretReferences!);
    }

    [Fact]
    public async Task An_inline_definition_that_never_returns_times_out_instead_of_holding_the_run()
    {
        using var run = new PipelineRunHarness(
            "while true do end\npipeline { name = \"spin\", steps = { { name = \"Never\", run = { \"echo\" } } } }",
            inline: true)
        {
            Deadline = DateTimeOffset.UtcNow.AddSeconds(1),
        };

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(PipelineFailureReasons.PipelineTimeout, outcome.FailureReason);
        Assert.Empty(run.DefinitionReports);
        Assert.Contains(run.LogLines, line => line.Contains("did not finish within the run's deadline", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_syntax_error_carries_the_same_reason_and_the_same_position_for_both_sources(bool inline)
    {
        // One fact, two sources: AC-2's "behaves exactly as a repository pipeline" would be false if the
        // origin of the bytes changed the failure taxonomy or the diagnostic's name.
        const string broken = "pipeline {";
        using var run = inline
            ? new PipelineRunHarness(broken, inline: true)
            : new PipelineRunHarness(PipelineRepository.Create(broken));

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(PipelineFailureReasons.PipelineSyntax, outcome.FailureReason);
        Assert.Null(outcome.ExitCode);
        Assert.Empty(run.DefinitionReports);

        var line = Assert.Single(run.LogLines, line => line.Contains("pipeline.lua:(", StringComparison.Ordinal));
        // Anchored on purpose: an absolute path would still contain "pipeline.lua:(", so a loose
        // Matches/DoesNotContain let a mutation that leaks the temp path stay green (F-022 review F1).
        Assert.StartsWith("zenkins: pipeline.lua:(", line, StringComparison.Ordinal);
        Assert.Matches(@"^zenkins: pipeline\.lua:\(1,\d+\)", line);
    }
    // ------------------------------------------------------------------
    // F-032: step-level timeout and retry
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_hanging_step_is_killed_at_its_own_timeout_with_its_process_tree()
    {
        // TC-001. The step declares two seconds while the run has two minutes, so the step's own limit is
        // the earlier one - and the child it spawned must die with it (the same tree kill a cancel uses).
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "hanging",
                PipelineTestData.StepWith("Sleeps", timeoutSeconds: 2, retry: null, "spawn", "30000"),
                PipelineTestData.Step("After", "append", "marker.txt", "after")));
        using var run = new PipelineRunHarness(repository);

        var clock = Stopwatch.StartNew();
        var outcome = await run.ExecuteAsync(CancellationToken);
        clock.Stop();

        // The typed reason is the step's own limit, not the run's deadline - and the run recorded no exit
        // code, because nothing exited: the tree was killed.
        Assert.Equal(PipelineFailureReasons.StepTimeout, outcome.FailureReason);
        Assert.Null(outcome.ExitCode);
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(40),
            $"the step was not stopped by its own limit: the run took {clock.Elapsed.TotalSeconds:0.#}s");

        // The step after it never ran (default stop), and the run failed on the step's own limit.
        Assert.False(File.Exists(Path.Combine(run.Work.RepositoryPath, "marker.txt")));
        var killed = Terminal(run, 1);
        Assert.Equal(PipelineStepStatus.Failed, killed.Status);
        Assert.Equal(PipelineStepFailureReasons.StepTimeout, killed.FailureReason);
        Assert.Null(killed.ExitCode);
        Assert.Equal(1, killed.Attempts);
        Assert.Equal(PipelineStepStatus.Skipped, Terminal(run, 2).Status);

        // The log says which limit fired, and nothing claims the run's deadline did.
        Assert.Contains(run.LogLines, line => line.Contains(
            "the earlier limit is the step's own", StringComparison.Ordinal));
        Assert.Contains(run.LogLines, line => line.Contains(
            "hit its own 2s timeout and was killed", StringComparison.Ordinal));
        Assert.DoesNotContain(run.LogLines, line => line.Contains(
            "killed by the run's cancellation", StringComparison.Ordinal));

        // The process tree, not just the child: the grandchild the step spawned is gone too.
        var grandchildLine = Assert.Single(run.OutputLines, line => line.StartsWith("grandchild-pid ", StringComparison.Ordinal));
        var grandchildPid = int.Parse(
            grandchildLine["grandchild-pid ".Length..],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(
            await WaitForProcessExitAsync(grandchildPid, TimeSpan.FromSeconds(15)),
            "the step's grandchild survived the step timeout (orphan)");
    }

    [Fact]
    public async Task A_step_timeout_with_continue_on_error_is_a_normal_failure_and_the_run_goes_on()
    {
        // AC-2's other half: the kill is this step's failure, not the run's cancellation. With
        // continue_on_error the next step still runs - and the run still ends Failed, naming the limit that
        // fired (a failure is never laundered into a success).
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "continue-after-timeout",
                PipelineTestData.StepWithContinueOnError("Slow", timeoutSeconds: 2, retry: null, "stream", "30000"),
                PipelineTestData.Step("After", "append", "marker.txt", "after")));
        using var run = new PipelineRunHarness(repository);

        var outcome = await run.ExecuteAsync(CancellationToken);

        // The step after the killed one really ran (its own marker is the proof).
        Assert.Equal(["after"], File.ReadAllLines(Path.Combine(run.Work.RepositoryPath, "marker.txt")));
        Assert.Equal(PipelineStepStatus.Failed, Terminal(run, 1).Status);
        Assert.Equal(PipelineStepFailureReasons.StepTimeout, Terminal(run, 1).FailureReason);
        Assert.Equal(PipelineStepStatus.Success, Terminal(run, 2).Status);

        // The run failed on the step's own limit, with no invented exit code.
        Assert.Equal(PipelineFailureReasons.StepTimeout, outcome.FailureReason);
        Assert.Null(outcome.ExitCode);
    }

    [Fact]
    public async Task A_step_without_a_timeout_gets_no_implicit_one()
    {
        // AC-7(b): a step that declares no limit must have none. Six and a half seconds of real work is
        // the assertion - a default of "?? 5s" (or any other invented limit) kills it, and the suite before
        // this fact could not tell the difference because no step ran longer than a second.
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "no-limit",
                PipelineTestData.StepWith("Slow", timeoutSeconds: null, retry: null, "stream", "6500")));
        using var run = new PipelineRunHarness(repository);

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Null(outcome.FailureReason);
        var step = Terminal(run, 1);
        Assert.Equal(PipelineStepStatus.Success, step.Status);
        Assert.Equal(0, step.ExitCode);

        // And nothing in the log claims a limit (there is none to name).
        Assert.DoesNotContain(run.LogLines, line => line.Contains("declares its own", StringComparison.Ordinal));
        Assert.DoesNotContain(run.LogLines, line => line.Contains("declares a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_user_cancel_beats_a_long_step_timeout_because_the_attempt_is_linked_to_the_run()
    {
        // AC-3's link: the attempt's timer is a source linked to the run's token. The step's own limit must
        // be the EARLIER one (300 s against a 10-minute run), otherwise the clamp installs no timer at all
        // and this fact would pass without ever building the linked source. With the timer installed and
        // cancellation requested, the step must die at once - an attempt that watched only its own limit
        // would sit for the child's whole 30-second sleep.
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "cancel-with-limit",
                PipelineTestData.StepWith("Long", timeoutSeconds: 300, retry: 3, "stream", "30000")));
        using var run = new PipelineRunHarness(repository) { Deadline = DateTimeOffset.UtcNow.AddMinutes(10) };
        using var cancellation = new CancellationTokenSource();

        var clock = Stopwatch.StartNew();
        var execution = run.ExecuteAsync(cancellation.Token);

        await WaitForConditionAsync(
            () => run.StepReports.Any(report => report.Index == 1 && report.Status == PipelineStepStatus.Running),
            TimeSpan.FromSeconds(15));
        cancellation.Cancel();

        var outcome = await execution.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken);
        clock.Stop();

        Assert.True(outcome.Canceled);
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(15),
            $"the cancel took {clock.Elapsed.TotalSeconds:0.#}s: the attempt did not observe the run's token");

        var killed = Terminal(run, 1);
        Assert.Equal(PipelineStepFailureReasons.Canceled, killed.FailureReason);
        Assert.NotEqual(PipelineStepFailureReasons.StepTimeout, killed.FailureReason);
        Assert.Equal(1, killed.Attempts);
        Assert.DoesNotContain(run.LogLines, line => line.Contains("--- attempt 2/3 ---", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_flaky_step_is_retried_and_still_succeeds_in_one_step_record()
    {
        // TC-002: the first attempt exits 3, the second one succeeds. One record, Attempts = 2, exit 0 - and
        // the attempt sections are delimited in the step's log.
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "flaky",
                PipelineTestData.StepWith("Unstable", timeoutSeconds: null, retry: 2, "flaky", "attempts.txt", "1"),
                PipelineTestData.Step("After", "append", "marker.txt", "after")));
        using var run = new PipelineRunHarness(repository);

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Null(outcome.FailureReason);

        // The child really ran twice (its own counter file says so), and the run continued past the step.
        Assert.Equal(
            ["2"],
            File.ReadAllLines(Path.Combine(run.Work.RepositoryPath, "attempts.txt")).Select(line => line.Trim()));
        Assert.True(File.Exists(Path.Combine(run.Work.RepositoryPath, "marker.txt")));

        // One record for the step, carrying the final exit code and the attempt count.
        var reports = run.StepReports.Where(report => report.Index == 1).ToArray();
        Assert.Equal(2, reports.Length); // one Running + one terminal: not one per attempt
        var terminal = Assert.Single(reports, report => report.Status != PipelineStepStatus.Running);
        Assert.Equal(PipelineStepStatus.Success, terminal.Status);
        Assert.Equal(0, terminal.ExitCode);
        Assert.Equal(2, terminal.Attempts);

        // Two delimited attempt sections in this step's log: the first attempt's failure, then the delimiter.
        Assert.Contains(run.LogLines, line => line.Contains("attempt 1/2 exited with code 3; retrying", StringComparison.Ordinal));
        Assert.Contains(run.LogLines, line => line.Contains("--- attempt 2/2 ---", StringComparison.Ordinal));
        Assert.Equal(
            2,
            run.LogLines.Count(line => line.Contains("\"Unstable\" started:", StringComparison.Ordinal)));

        // The next step declared neither knob: one attempt, no delimiter, unchanged behaviour.
        var after = Assert.Single(run.StepReports, report => report.Index == 2 && report.Status == PipelineStepStatus.Success);
        Assert.Equal(1, after.Attempts);
        Assert.DoesNotContain(run.LogLines, line => line.Contains("attempt 1/1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancellation_is_never_retried_even_when_the_step_declares_attempts()
    {
        // TC-003: retry = 3, cancelled during the first attempt. No second attempt starts.
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "cancelled",
                PipelineTestData.StepWith("Long", timeoutSeconds: null, retry: 3, "stream", "30000")));
        using var run = new PipelineRunHarness(repository);
        using var cancellation = new CancellationTokenSource();

        var execution = run.ExecuteAsync(cancellation.Token);

        // Cancel once the step is really running (the executor reported Running), so the attempt exists.
        await WaitForConditionAsync(
            () => run.StepReports.Any(report => report.Index == 1 && report.Status == PipelineStepStatus.Running),
            TimeSpan.FromSeconds(15));
        cancellation.Cancel();

        var outcome = await execution.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken);

        Assert.True(outcome.Canceled);
        var killed = Terminal(run, 1);
        Assert.Equal(PipelineStepStatus.Failed, killed.Status);
        Assert.Equal(PipelineStepFailureReasons.Canceled, killed.FailureReason);
        Assert.Equal(1, killed.Attempts);

        // No retry happened: no delimiter, and no second "started" line.
        Assert.DoesNotContain(run.LogLines, line => line.Contains("--- attempt 2/3 ---", StringComparison.Ordinal));
        Assert.Single(run.LogLines, line => line.Contains("\"Long\" started:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_step_limit_is_clamped_by_the_runs_remaining_deadline_and_the_log_says_which_fired()
    {
        // AC-3, both directions. The earlier of the two limits governs, and which one it was is readable
        // from the log and from the two stored reasons (`step-timeout` vs `timed-out`).

        // (a) The run's deadline is earlier: a two-hour step limit cannot fire first, so no step timer is
        // installed, the run's own cancellation stops the step, and the record says the run's deadline did it.
        //
        // Card Q-023 (second pass): the fixture owns both halves - the clock and the cancel - and nothing here
        // waits on real time. The frozen clock makes "the run has an hour left" a fact about the fixture, and the
        // cancel is raised the moment the step reports Running. The deadline is an hour out rather than two
        // seconds away, because it is an *absolute* instant and the frozen clock only feeds this executor's
        // comparison: git's per-command budget and the Lua definition phase measure that instant against the real
        // clock. A two-second deadline was therefore a real two-second budget for the clone, and a loaded host
        // spent it there - the run answered `git-timeout` before the step it is about had even reported Running.
        var deadlineClock = new ManualClock(DateTimeOffset.UtcNow);
        using var cancellation = new CancellationTokenSource();
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "deadline-governs",
                PipelineTestData.StepWith("Long", timeoutSeconds: 7200, retry: null, "stream", "30000")));
        using (var run = new PipelineRunHarness(repository)
               {
                   Deadline = deadlineClock.Now.AddHours(1),
                   Clock = deadlineClock,
                   OnStepRunning = _ => cancellation.Cancel(),
                   CancellationFailureReason = () => StepCancellationReason.For(RunCancelReasons.Timeout),
               })
        {
            var outcome = await run.ExecuteAsync(cancellation.Token);

            Assert.True(
                outcome.Canceled,
                $"the run answered {outcome.FailureReason ?? outcome.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "success"} instead of a cancellation.");
            var step = Terminal(run, 1);
            Assert.Equal(PipelineStepStatus.Failed, step.Status);
            Assert.Equal(PipelineStepFailureReasons.TimedOut, step.FailureReason);
            Assert.NotEqual(PipelineStepFailureReasons.StepTimeout, step.FailureReason);

            Assert.Contains(run.LogLines, line => line.Contains(
                "the earlier limit is the run's deadline, and it governs", StringComparison.Ordinal));
            Assert.DoesNotContain(run.LogLines, line => line.Contains(
                "hit its own", StringComparison.Ordinal));
        }

        // (b) The step's limit is earlier: it fires, and the log says so - the reverse sentence.
        var stepClock = new ManualClock(DateTimeOffset.UtcNow);
        using (var stepRepository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "step-governs",
                PipelineTestData.StepWith("Short", timeoutSeconds: 2, retry: null, "stream", "30000"))))
        using (var run = new PipelineRunHarness(stepRepository)
               {
                   Deadline = stepClock.Now.AddMinutes(2),
                   Clock = stepClock,
               })
        {
            var outcome = await run.ExecuteAsync(CancellationToken);

            Assert.Equal(PipelineFailureReasons.StepTimeout, outcome.FailureReason);
            Assert.Contains(run.LogLines, line => line.Contains(
                "the earlier limit is the step's own", StringComparison.Ordinal));
            Assert.DoesNotContain(run.LogLines, line => line.Contains(
                "the earlier limit is the run's deadline", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_retry_does_not_resolve_the_steps_secrets_a_second_time()
    {
        // AC-6: secrets are resolved once per step, before the first attempt - a retry re-runs the process
        // with the values it already has. Two independent assertions, because "resolved again" has two
        // shapes: the source is asked once (a real second fetch would be visible), AND the value the second
        // attempt received is the FIRST attempt's value - the source hands out a different value per fetch
        // (v1, v2, ...), so a re-resolution that produced a fresh value shows up in the child's own file.
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "secret-retry",
                PipelineTestData.StepWithSecretEnv(
                    "UsesSecret", retry: 2, "TOKEN", "flaky-env", "attempts.txt", "1", "TOKEN", "attempt-env.txt")));
        var secrets = new CountingSecretSource();
        using var run = new PipelineRunHarness(repository) { Secrets = secrets };

        var outcome = await run.ExecuteAsync(CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(2, Terminal(run, 1).Attempts);

        // One fetch, for one name.
        Assert.Equal(1, secrets.Fetches);
        Assert.Equal(["TOKEN"], secrets.Names);

        // Both attempts saw "v1": the text is written by the child itself, into a file (the log would have
        // been redacted), so this is the value the second process really ran with.
        Assert.Equal(
            ["1 TOKEN=v1", "2 TOKEN=v1"],
            File.ReadAllLines(Path.Combine(run.Work.RepositoryPath, "attempt-env.txt"))
                .Select(line => line.Trim()));
    }

    /// <summary>
    /// A secret source that counts how often it is asked and by which names (card F-032's "resolved once
    /// per step" fact). The value is registered with the run's redactor by the resolver, exactly as a real
    /// fetch's would be.
    /// </summary>
    private sealed class CountingSecretSource : IRunSecretSource
    {
        public int Fetches { get; private set; }

        public List<string> Names { get; } = [];

        public Task<RunSecretResult> FetchAsync(string name, CancellationToken cancellationToken)
        {
            Fetches++;
            Names.Add(name);

            // A different value per fetch: "v1", "v2", ... A step that resolved twice would then visibly run
            // the second attempt with a different value, not merely with the same one twice.
            return Task.FromResult(RunSecretResult.Found($"v{Fetches}"));
        }
    }

    /// <summary>The terminal report of one step position (the record the run's step row would become).</summary>
    private static PipelineStepReport Terminal(PipelineRunHarness run, int index) =>
        Assert.Single(
            run.StepReports,
            report => report.Index == index && report.Status != PipelineStepStatus.Running);

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50, CancellationToken);
        }

        Assert.Fail("the condition was not met within the timeout.");
    }

    private static async Task<bool> WaitForProcessExitAsync(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsProcessGone(pid))
            {
                return true;
            }

            await Task.Delay(100, CancellationToken);
        }

        return IsProcessGone(pid);
    }

    private static bool IsProcessGone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            // No such process: already gone (and reaped by whoever owns it).
            return true;
        }
    }

    private sealed class PipelineRunHarness : IDisposable
    {
        private readonly string _workRoot = Path.Combine(
            Path.GetTempPath(), $"zenkins-f014-run-{Guid.NewGuid():N}");

        private readonly PipelineRepository? _repository;
        private readonly List<string> _logLines = [];
        private readonly List<string> _outputLines = [];

        public PipelineRunHarness(
            PipelineRepository repository,
            string branch = "main",
            string pipelinePath = "pipeline.lua")
        {
            _repository = repository;
            Branch = branch;
            PipelinePath = pipelinePath;
            Directory.CreateDirectory(_workRoot);
            Work = new WorkDirectoryService(_workRoot).Prepare(Guid.NewGuid());
        }

        /// <summary>
        /// A Freestyle run (card F-020): no repository, no clone, no definition phase - the frozen spec
        /// below is what executes, exactly as the claim hands it to the worker.
        /// </summary>
        public PipelineRunHarness(string frozenSpecJson)
        {
            _repository = null;
            FrozenSpecJson = frozenSpecJson;
            Branch = string.Empty;
            PipelinePath = string.Empty;
            Directory.CreateDirectory(_workRoot);
            Work = new WorkDirectoryService(_workRoot).Prepare(Guid.NewGuid());
        }

        /// <summary>
        /// An inline Pipeline run (card F-022): no repository, the script is snapshotted on the run, and
        /// the definition phase describes it after writing it to <c>temp/pipeline.lua</c>.
        /// </summary>
        public PipelineRunHarness(string inlineScript, bool inline)
        {
            Assert.True(inline);
            _repository = null;
            InlineScript = inlineScript;
            Branch = string.Empty;
            PipelinePath = "pipeline.lua";
            Directory.CreateDirectory(_workRoot);
            Work = new WorkDirectoryService(_workRoot).Prepare(Guid.NewGuid());
        }

        /// <summary>The Freestyle run's frozen spec; null for a Pipeline or inline run.</summary>
        public string? FrozenSpecJson { get; }

        /// <summary>The inline run's script snapshot; null for a repository or Freestyle run.</summary>
        public string? InlineScript { get; }

        public RunWorkDirectory Work { get; }

        public string Branch { get; }

        public string PipelinePath { get; }

        public DateTimeOffset Deadline { get; init; } = DateTimeOffset.UtcNow.AddMinutes(2);

        /// <summary>
        /// Card Q-023: the clock the runner reads when it decides which limit is earlier. Real by default; a
        /// fact that must not depend on scheduling passes <see cref="ManualClock"/>.
        /// </summary>
        public TimeProvider Clock { get; init; } = TimeProvider.System;

        /// <summary>
        /// Card F-031: how long a canceled run's post steps may run. The product default is 30s; a fact that
        /// proves the kill shortens it.
        /// </summary>
        public TimeSpan PostGrace { get; init; } =
            TimeSpan.FromSeconds(PipelineSpecSchema.PostGraceSeconds);

        /// <summary>Card F-031: the project's explicit "a post failure fails the run" switch.</summary>
        public bool PostFailureFailsRun { get; init; }

        /// <summary>
        /// Card Q-023: called when a step reports that it is <b>running</b>. A fact that cancels "two seconds
        /// into the step" arms its timer here instead of at construction, so a slow machine cannot deliver the
        /// cancel before the step ever started (which used to turn "the step timed out" into "the run was
        /// canceled").
        /// </summary>
        public Action<int>? OnStepRunning { get; init; }

        public IReadOnlyDictionary<string, string?> GitEnvironment { get; init; } =
            new Dictionary<string, string?>();

        /// <summary>argv[0] of every git invocation; a bogus name simulates a host without git.</summary>
        public string GitExecutable { get; init; } = "git";

        /// <summary>Runs between the definition report and the first step (TC-004's "the branch moved").</summary>
        public Action? BeforeFirstStep { get; init; }

        /// <summary>
        /// Card F-032: the run's secret source. The default has none, so a <c>secret()</c> reference fails
        /// the step; the retry fact passes a counting one to prove a retry does not fetch again.
        /// </summary>
        public IRunSecretSource Secrets { get; init; } = EmptyRunSecretSource.Instance;

        /// <summary>
        /// Card F-032: why the run's cancellation fired, asked when a step is killed. The clamp fact sets
        /// it to the deadline's answer, exactly as the worker loop does in production.
        /// </summary>
        public Func<string?>? CancellationFailureReason { get; init; }

        /// <summary>The step reports the executor sent, in order (one Running and one terminal per step).</summary>
        public IReadOnlyList<PipelineStepReport> StepReports => StepSink.Reports;

        private RecordingStepSink StepSink { get; } = new();

        /// <summary>Answers the commit report with a definitive refusal (409/410 in production).</summary>
        public bool RejectCommitReport { get; init; }

        /// <summary>Answers the spec report with a definitive refusal (the run is already terminal).</summary>
        public bool RejectDefinitionReport { get; init; }

        /// <summary>Counts how often the pipeline definition file was read (m4's "read once" pin).</summary>
        public int SourceReads { get; private set; }

        public string[] LogLines => [.. _logLines];

        public string[] OutputLines => [.. _outputLines];

        /// <summary>The commit-only reports (sent right after the clone).</summary>
        public List<CommitReport> CommitReports { get; } = [];

        /// <summary>The order the server received the reports in ("commit" / "definition").</summary>
        public List<string> ReportOrder { get; } = [];

        /// <summary>The full reports (commit + frozen spec, sent before the first step).</summary>
        public List<DefinitionReport> DefinitionReports { get; } = [];

        public async Task<PipelineRunOutcome> ExecuteAsync(CancellationToken cancellationToken)
        {
            var secretReferences = new SecretReferenceCapture();
            var definitions = new PipelineDefinitionRunner(
                PipelineDefinitionRunner.CreateRuntime(PipelineRunner.CreateLogSink(Emit), secretReferences),
                secretReferences)
            {
                // F-014's "the pipeline file is read exactly once per run" as an assertion, not a claim.
                ReadSource = path =>
                {
                    SourceReads++;
                    return File.ReadAllText(path);
                },
            };

            var runner = new PipelineRunner(
                new PipelineRunOptions
                {
                    Work = Work,
                    RepositoryUrl = _repository?.FileUrl,
                    PipelineSpecJson = FrozenSpecJson,
                    PipelineScriptText = InlineScript,
                    DefinitionSource = _repository is not null
                        ? Zenkins.Shared.Pipelines.RunDefinitionSources.Repository
                        : FrozenSpecJson is not null
                            ? Zenkins.Shared.Pipelines.RunDefinitionSources.Freestyle
                            : Zenkins.Shared.Pipelines.RunDefinitionSources.Inline,
                    Branch = Branch,
                    PipelinePath = PipelinePath,
                    Deadline = Deadline,
                    PostGrace = PostGrace,
                    PostFailureFailsRun = PostFailureFailsRun,
                    GitExecutable = GitExecutable,
                    GitEnvironment = GitEnvironment,
                    Log = Emit,
                    OnProcessOutput = line => _outputLines.Add(line.Line),
                    Secrets = Secrets,
                    CancellationFailureReason = CancellationFailureReason,
                    StepSink = StepSink,
                },
                definitions,
                _ => Task.FromResult<GitCredentialMaterial?>(null),
                (commit, specJson, _) =>
                {
                    var markerPath = Path.Combine(Work.RepositoryPath, "marker.txt");

                    // AC-3: the commit is reported on its own, right after the clone - before anything
                    // can fail - so the assertion can also be made on runs whose definition never
                    // produced a spec.
                    if (specJson is null)
                    {
                        CommitReports.Add(new CommitReport(commit));
                        ReportOrder.Add("commit");
                        return Task.FromResult(!RejectCommitReport);
                    }

                    Assert.True(
                        PipelineSpecJson.TryRead(specJson, out var spec, out var error),
                        error ?? "the reported spec did not parse");

                    // Captured NOW, at report time: the point is whether any step had already written
                    // its marker when the server received the step list.
                    DefinitionReports.Add(new DefinitionReport(
                        commit,
                        specJson,
                        [.. spec!.Steps.Select(step => step.Name)],
                        MarkerExisted: File.Exists(markerPath)));
                    ReportOrder.Add("definition");

                    // The last hook before the first step: whatever the caller wants to change in the
                    // world happens here, and the report records the state it was in.
                    BeforeFirstStep?.Invoke();
                    return Task.FromResult(!RejectDefinitionReport);
                },
                // Card Q-023: the clock the deadline comparison reads. Real unless a fact passes a manual
                // one, so production and every other test are unchanged.
                Clock);

            StepSink.OnRunning = index => OnStepRunning?.Invoke(index);
            return await runner.RunAsync(cancellationToken);
        }

        private void Emit(string line) => _logLines.Add(line);

        public void Dispose()
        {
            // Q-011: a failed delete used to be swallowed (IOException-only) and the root was left in
            // the system temp forever; TempRootCleanup retries the transient failures instead.
            TempRootCleanup.DeleteDirectory(_workRoot);
        }
    }

    /// <summary>
    /// A step sink that records every report (card F-032: the attempt count travels on the terminal one) and
    /// mints one id per position, the way the server's row does.
    /// </summary>
    private sealed class RecordingStepSink : IPipelineStepSink
    {
        private readonly Dictionary<int, Guid> _ids = [];

        public List<PipelineStepReport> Reports { get; } = [];

        /// <summary>Invoked when a report says the step is running (card Q-023's arming point).</summary>
        public Action<int>? OnRunning { get; set; }

        public Task<PipelineStepReceipt> ReportAsync(
            PipelineStepReport report,
            CancellationToken cancellationToken)
        {
            if (!_ids.TryGetValue(report.Index, out var id))
            {
                id = Guid.NewGuid();
                _ids[report.Index] = id;
            }

            Reports.Add(report);
            if (string.Equals(report.Status, PipelineStepStatus.Running, StringComparison.Ordinal))
            {
                OnRunning?.Invoke(report.Index);
            }

            return Task.FromResult(PipelineStepReceipt.Recorded(id));
        }
    }

    /// <summary>The commit-only report the runner sends right after the clone (card F-014, AC-3).</summary>
    private sealed record CommitReport(string? GitCommit);

    /// <summary>What the server received from one run, plus the state of the checkout at that instant.</summary>
    private sealed record DefinitionReport(
        string? GitCommit,
        string PipelineSpecJson,
        string[] StepNames,
        bool MarkerExisted)
    {
        /// <summary>Whether any step had already written its marker when the report was sent.</summary>
        public bool MarkerExistedAtReport => MarkerExisted;
    }
}

/// <summary>
/// Card Q-023: a clock that does not move. A fact that asks "which limit is earlier" passes this one when the
/// answer must not depend on scheduling - the comparison reads a known instant instead of however much wall
/// clock the machine spent before reaching it.
/// </summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    /// <summary>The instant the runner reads; <see cref="Advance"/> moves it without a real wait.</summary>
    public DateTimeOffset Now { get; private set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>Moves the clock forward - card F-031's "the deadline has passed by the time post runs".</summary>
    public void Advance(TimeSpan by) => Now += by;
}
