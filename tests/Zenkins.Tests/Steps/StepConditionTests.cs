using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Shared.Pipelines;
using Zenkins.Tests.Pipelines;
using Zenkins.Tests.Runs;
using Zenkins.Worker.Pipelines;

namespace Zenkins.Tests.Steps;

/// <summary>
/// Card F-015's execution-time conditions and environment composition (TC-003), end to end: a real run whose
/// steps declare <c>when</c> and whose environment is set at every level, so both the child process and the
/// deferred <c>env()</c> reference are read back from the same run.
/// </summary>
[Trait("Category", "Steps")]
public sealed class StepConditionTests
{
    /// <summary>The non-main branch the condition test builds (its own name, not a parent of another).</summary>
    private const string BranchUnderTest = "steps/conditions";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------
    // TC-003, first half: conditions
    // ------------------------------------------------------------------

    [Fact]
    public async Task TC_003_a_when_condition_skips_a_step_without_spawning_it_and_records_the_reason()
    {
        var pipeline = """
            pipeline {
              name = "conditions",
              steps = {
                { name = "MainOnly", run = { "{{dotnet}}", "{{argv}}", "append", "marker.txt", "main" },
                  when = { branch = "main" } },
                { name = "LinuxOnly", run = { "{{dotnet}}", "{{argv}}", "append", "marker.txt", "linux" },
                  when = { platform = "linux" } },
                { name = "ThisPlatform", run = { "{{dotnet}}", "{{argv}}", "append", "marker.txt", "this" },
                  when = { platform = "{{platform}}" } },
                { name = "EnvMatches", run = { "{{dotnet}}", "{{argv}}", "append", "marker.txt", "env" },
                  env = { STAGE = "prod" },
                  when = { env = { STAGE = "prod" } } },
                { name = "EnvMissing", run = { "{{dotnet}}", "{{argv}}", "append", "marker.txt", "never" },
                  when = { env = { STAGE = "prod" } } },
              },
            }
            """
            .Replace("{{dotnet}}", PipelineTestData.DotNet, StringComparison.Ordinal)
            .Replace("{{argv}}", PipelineTestData.ArgvEcho, StringComparison.Ordinal)
            .Replace("{{platform}}", StepConditionFacts.CurrentPlatform, StringComparison.Ordinal);

        // The branch under test carries the conditions pipeline; main carries a different one, so the two
        // clones are distinguishable.
        using var repository = PipelineRepository.Create(
            PipelineTestData.Pipeline(
                "main-baseline",
                PipelineTestData.Step("Baseline", "append", "marker.txt", "baseline")));
        repository.CommitPipelineOnBranch(BranchUnderTest, pipeline);
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository, branch: BranchUnderTest);

        var outcome = await run.ExecuteAsync();

        // A skipped step does not fail the run.
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(BuildRunStatus.Success, (await run.ReadRunAsync()).Status);

        var steps = await run.ReadStepsAsync();
        Assert.Equal(
            new[]
            {
                "MainOnly", "LinuxOnly", "ThisPlatform", "EnvMatches", "EnvMissing",
            },
            steps.Select(step => step.Name));

        // Branch mismatch: the run built steps/conditions, so a main-only step is skipped - never started.
        Assert.Equal(PipelineStepStatus.Skipped, steps[0].Status);
        Assert.Equal(PipelineStepFailureReasons.WhenNotMatched, steps[0].FailureReason);
        Assert.Null(steps[0].StartedAt);
        Assert.Null(steps[0].ExitCode);
        Assert.DoesNotContain(
            run.Reports,
            report => report.Index == 1 && report.Status == PipelineStepStatus.Running);

        // The platform clauses are evaluated against the worker's own OS.
        Assert.Equal(
            StepConditionFacts.CurrentPlatform == "linux" ? PipelineStepStatus.Success : PipelineStepStatus.Skipped,
            steps[1].Status);
        Assert.Equal(PipelineStepStatus.Success, steps[2].Status);

        // A step-level env value is part of the effective environment the condition sees.
        Assert.Equal(PipelineStepStatus.Success, steps[3].Status);

        // ...and a clause nothing sets is skipped, not quietly passed.
        Assert.Equal(PipelineStepStatus.Skipped, steps[4].Status);
        Assert.Equal(PipelineStepFailureReasons.WhenNotMatched, steps[4].FailureReason);

        // The marker file records exactly the steps that ran - the tree, not the log, is the proof.
        var markers = File.ReadAllLines(Path.Combine(run.Work.RepositoryPath, "marker.txt"));
        Assert.Contains("this", markers);
        Assert.Contains("env", markers);
        Assert.DoesNotContain("main", markers);
        Assert.DoesNotContain("never", markers);

        // The reason is in the run log, naming the clause and both values.
        Assert.Contains(
            run.Lines,
            line => line.Message.Contains(
                "skipped: when.branch \"main\" does not match the run's branch \"steps/conditions\"",
                StringComparison.Ordinal));
        Assert.Contains(
            run.Lines,
            line => line.Message.Contains("skipped: when.env STAGE is not set (expected \"prod\")", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // TC-003, second half: the environment precedence chain
    // ------------------------------------------------------------------

    [Fact]
    public async Task TC_003_the_step_environment_wins_and_env_reads_what_the_child_sees()
    {
        // CONFIG is set at every level (inherited, pipeline, step): the step's value must win, and both the
        // child process and the deferred env() reference must report it. The server-provided run environment
        // wins over a step's attempt to set it (F-015's documented order).
        var pipeline = """
            pipeline {
              name = "env-precedence",
              env = { CONFIG = "pipeline", PIPELINE_ONLY = "from-pipeline" },
              steps = {
                {
                  name = "Probe",
                  run = { "{{dotnet}}", "{{argv}}", "env", "CONFIG", "CHECKED", "PIPELINE_ONLY", "INHERITED_ONLY" },
                  env = { CONFIG = "step", CHECKED = env("CONFIG") },
                },
                {
                  name = "ViaArgv",
                  run = { "{{dotnet}}", "{{argv}}", "echo", env("CONFIG") },
                  env = { CONFIG = "step" },
                },
                {
                  name = "RunEnvironmentWins",
                  run = { "{{dotnet}}", "{{argv}}", "env", "ZENKINS_RUN_ID" },
                  env = { ZENKINS_RUN_ID = "a-step-cannot-set-this" },
                },
              },
            }
            """
            .Replace("{{dotnet}}", PipelineTestData.DotNet, StringComparison.Ordinal)
            .Replace("{{argv}}", PipelineTestData.ArgvEcho, StringComparison.Ordinal);

        using var repository = PipelineRepository.Create(pipeline);
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);
        run.InheritedEnvironment = new Dictionary<string, string?>
        {
            ["CONFIG"] = "inherited",
            ["INHERITED_ONLY"] = "from-inherited",
        };

        var outcome = await run.ExecuteAsync();
        Assert.Equal(0, outcome.ExitCode);

        var steps = await run.ReadStepsAsync();

        // The probe's captured lines, in the order ArgvEcho printed them.
        // Only what the child printed: the worker's own lines (the step header) share the step id and the
        // stdout stream, and they are not the child's environment report.
        var probe = run.Lines
            .Where(line => line.StepId == steps[0].Id
                && line.Stream == Zenkins.Shared.LogStreaming.LogStream.StdOut
                && !line.Message.StartsWith("zenkins: ", StringComparison.Ordinal))
            .Select(line => line.Message)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "step", // the child's own CONFIG: the step layer beat the pipeline and the inherited values
                "step", // env("CONFIG"), resolved at the step boundary - the same value the child got
                "from-pipeline", // the pipeline layer reaches the child
                "from-inherited", // and so does the inherited one
            },
            probe);

        // The argv surface resolves against that step's own composition too: the same name, the same value
        // the child of that step sees (the third reader of one composition).
        Assert.Contains(
            run.Lines,
            line => line.StepId == steps[1].Id && line.Message == "step");

        // The server-provided run environment is the last layer before a secret: a step cannot override it.
        var runIdLine = run.Lines
            .Single(line => line.StepId == steps[2].Id
                && line.Stream == Zenkins.Shared.LogStreaming.LogStream.StdOut
                && !line.Message.StartsWith("zenkins: ", StringComparison.Ordinal));
        Assert.Equal(run.RunId.ToString(), runIdLine.Message);
    }

    // ------------------------------------------------------------------
    // A condition is validated at definition time
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_key_in_when_is_a_definition_error_naming_the_field()
    {
        // A typo must not silently never match: the definition phase refuses the field by path.
        using var repository = PipelineRepository.Create("""
            pipeline {
              name = "bad-condition",
              steps = {
                { name = "Only", run = { "dotnet", "--info" }, when = { brnach = "main" } },
              },
            }
            """);
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);

        var outcome = await run.ExecuteAsync();

        Assert.Equal(PipelineFailureReasons.PipelineInvalid, outcome.FailureReason);
        Assert.Contains(
            run.Lines,
            line => line.Message.Contains(
                "pipeline.steps[1].when has an unknown key 'brnach'",
                StringComparison.Ordinal));

        // Nothing was recorded and nothing ran.
        Assert.Empty(await run.ReadStepsAsync());
        Assert.False(File.Exists(Path.Combine(run.Work.RepositoryPath, "marker.txt")));
    }

    [Fact]
    public async Task A_platform_the_executor_does_not_know_is_a_definition_error()
    {
        using var repository = PipelineRepository.Create("""
            pipeline {
              name = "bad-platform",
              steps = {
                { name = "Only", run = { "dotnet", "--info" }, when = { platform = "beos" } },
              },
            }
            """);
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);

        var outcome = await run.ExecuteAsync();

        Assert.Equal(PipelineFailureReasons.PipelineInvalid, outcome.FailureReason);
        Assert.Contains(
            run.Lines,
            line => line.Message.Contains("pipeline.steps[1].when.platform must be one of linux, macos, windows", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_when_is_a_definition_error_rather_than_always_running()
    {
        using var repository = PipelineRepository.Create("""
            pipeline {
              name = "empty-condition",
              steps = {
                { name = "Only", run = { "dotnet", "--info" }, when = { } },
              },
            }
            """);
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(host, repository);

        var outcome = await run.ExecuteAsync();

        Assert.Equal(PipelineFailureReasons.PipelineInvalid, outcome.FailureReason);
        Assert.Contains(
            run.Lines,
            line => line.Message.Contains("must declare at least one of branch, platform or env", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // The composition rule, in isolation
    // ------------------------------------------------------------------

    [Fact]
    public void The_composition_applies_the_layers_in_the_documented_order()
    {
        var composed = StepEnvironment.Compose(
            new Dictionary<string, string?> { ["A"] = "inherited", ["B"] = "inherited", ["C"] = "inherited" },
            new SortedDictionary<string, string>(StringComparer.Ordinal) { ["B"] = "pipeline", ["C"] = "pipeline" },
            new SortedDictionary<string, string>(StringComparer.Ordinal) { ["C"] = "step" },
            new Dictionary<string, string?> { ["ZENKINS_RUN_ID"] = "run" });

        Assert.Equal("inherited", composed["A"]);
        Assert.Equal("pipeline", composed["B"]);
        Assert.Equal("step", composed["C"]);
        Assert.Equal("run", composed["ZENKINS_RUN_ID"]);
    }

    [Fact]
    public void A_condition_only_matches_when_every_declared_clause_holds()
    {
        var facts = new StepConditionFacts("main", "linux");
        var environment = new Dictionary<string, string?> { ["STAGE"] = "prod" };

        Assert.True(StepCondition.IsSatisfied(
            new PipelineCondition(Branch: "main", Platform: "linux", Env: new(StringComparer.Ordinal) { ["STAGE"] = "prod" }),
            facts,
            environment,
            out var reason));
        Assert.Null(reason);

        Assert.False(StepCondition.IsSatisfied(
            new PipelineCondition(Branch: "main", Platform: "macos"),
            facts,
            environment,
            out reason));
        Assert.Contains("when.platform \"macos\"", reason, StringComparison.Ordinal);

        Assert.False(StepCondition.IsSatisfied(
            new PipelineCondition(Env: new(StringComparer.Ordinal) { ["STAGE"] = "dev" }),
            facts,
            environment,
            out reason));
        Assert.Contains("when.env STAGE is \"prod\" (expected \"dev\")", reason, StringComparison.Ordinal);

        // An absent condition always holds; an empty one never reaches the executor (the reader refuses it).
        Assert.True(StepCondition.IsSatisfied(null, facts, environment, out reason));
        Assert.Null(reason);
    }
}
