using System.Diagnostics;
using System.Text;
using Xunit;
using Xunit.Sdk;
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

    // ------------------------------------------------------------------
    // The Freestyle script step (card F-020; the facts are card Q-012's)
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_script_step_lands_in_temp_as_step_N_as_utf8_without_a_bom()
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

        // UTF-8 without a BOM, byte for byte: a BOM's characters would become the first command on
        // Windows and a stray token on Unix, which is why the encoding is written explicitly.
        var written = File.ReadAllBytes(first);
        Assert.Equal(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes("echo café"), written);
        Assert.False(
            written.Length >= 3 && written[0] == 0xEF && written[1] == 0xBB && written[2] == 0xBF,
            "the script file starts with a UTF-8 BOM");

        // And the file is what actually ran: the non-ASCII text came back through the child's stdout.
        Assert.Equal(new UTF8Encoding(false).GetBytes("echo second"), File.ReadAllBytes(second));
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
        var script = """
            local token = secret("TOKEN")
            pipeline {
              name = "inline-definition",
              env = { REGION = "eu" },
              triggers = { branches = { "main", "release/*" } },
              steps = { { name = "Only", run = { "echo", "hi" } } },
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
