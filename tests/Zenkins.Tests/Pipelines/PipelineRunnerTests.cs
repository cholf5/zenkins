using System.Diagnostics;
using Xunit;
using Zenkins.Shared.Pipelines;
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

    /// <summary>
    /// One pipeline run with the real Git layer, the real definition phase and the real process runner,
    /// in a throwaway work root. The only stand-ins are the server hop (the definition report) and the
    /// credential fetch (there is no server here, so there is no credential).
    /// </summary>
    private sealed class PipelineRunHarness : IDisposable
    {
        private readonly string _workRoot = Path.Combine(
            Path.GetTempPath(), $"zenkins-f014-run-{Guid.NewGuid():N}");

        private readonly PipelineRepository _repository;
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

        public RunWorkDirectory Work { get; }

        public string Branch { get; }

        public string PipelinePath { get; }

        public DateTimeOffset Deadline { get; init; } = DateTimeOffset.UtcNow.AddMinutes(2);

        public IReadOnlyDictionary<string, string?> GitEnvironment { get; init; } =
            new Dictionary<string, string?>();

        /// <summary>argv[0] of every git invocation; a bogus name simulates a host without git.</summary>
        public string GitExecutable { get; init; } = "git";

        /// <summary>Runs between the definition report and the first step (TC-004's "the branch moved").</summary>
        public Action? BeforeFirstStep { get; init; }

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
            var definitions = new PipelineDefinitionRunner(
                PipelineDefinitionRunner.CreateRuntime(PipelineRunner.CreateLogSink(Emit)))
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
                    RepositoryUrl = _repository.FileUrl,
                    Branch = Branch,
                    PipelinePath = PipelinePath,
                    Deadline = Deadline,
                    GitExecutable = GitExecutable,
                    GitEnvironment = GitEnvironment,
                    Log = Emit,
                    OnProcessOutput = line => _outputLines.Add(line.Line),
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
                });

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
