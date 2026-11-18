using Xunit;
using Zenkins.Server.Runs;
using Zenkins.Shared.Pipelines;
using Zenkins.Worker.Pipelines;

namespace Zenkins.Tests.Pipelines;

/// <summary>
/// Card F-030's stage grouping, at the two levels that can go wrong: the <b>frozen spec</b> (both Lua
/// spellings, the v3 rules, and the old versions that must keep reading) and the <b>grouping</b> a run page
/// renders (adjacent runs, worst state, wall-clock span, the unstaged step that belongs to no section).
/// <para>
/// The page's own rendering - sections in order, the failed one open, the jump marker - is
/// <c>RunStagePageTests</c>; this file is about the data those facts are computed from.
/// </para>
/// </summary>
[Trait("Category", "Pipeline")]
public sealed class PipelineStageTests
{
    // ------------------------------------------------------------------
    // The frozen spec: both Lua spellings
    // ------------------------------------------------------------------

    [Fact]
    public void A_step_declares_its_stage_and_the_frozen_spec_carries_it()
    {
        using var run = new LuaRuntimeTestDefinition(
            """
            pipeline {
              name = "release",
              steps = {
                step { name = "Compile", stage = "Build", run = { "dotnet", "build" } },
                step { name = "Unit", stage = "Test", run = { "dotnet", "test" } },
                step { name = "Publish", run = { "dotnet", "publish" } },
              },
            }
            """);
        var result = run.Run();

        Assert.Equal(PipelineSpecSchema.Version, result.Spec.Version);
        Assert.Equal(3, result.Spec.Version);
        Assert.Equal(["Build", "Test", null], result.Spec.Steps.Select(step => step.Stage));
        Assert.True(result.Spec.Steps[0].HasStage);
        Assert.False(result.Spec.Steps[2].HasStage);

        // The names live in the frozen JSON - a later reader never reconstructs them from step names.
        Assert.Contains("\"stage\":\"Build\"", result.SpecJson, StringComparison.Ordinal);

        // Byte-stable: re-serialising what was read produces the same document, stage included.
        Assert.True(PipelineSpecJson.TryRead(result.SpecJson, out var read, out var error), error);
        Assert.Equal(result.SpecJson, PipelineSpecJson.Serialize(read!));
    }

    [Fact]
    public void The_stages_sugar_assigns_the_entry_name_and_flattens_in_declaration_order()
    {
        using var run = new LuaRuntimeTestDefinition(
            """
            pipeline {
              name = "release",
              stages = {
                { name = "Build", steps = {
                    step { name = "Restore", run = { "dotnet", "restore" } },
                    step { name = "Compile", run = { "dotnet", "build" } },
                } },
                { name = "Test", steps = {
                    step { name = "Unit", run = { "dotnet", "test" } },
                } },
                { steps = {
                    step { name = "Notify", run = { "notify" } },
                } },
              },
            }
            """);
        var result = run.Run();

        // One flat list, in declaration order - the one index the executor, the step reports and the page use.
        Assert.Equal(
            ["Restore", "Compile", "Unit", "Notify"],
            result.Spec.Steps.Select(step => step.Name));
        Assert.Equal(["Build", "Build", "Test", null], result.Spec.Steps.Select(step => step.Stage));
    }

    [Fact]
    public void The_two_spellings_produce_the_same_frozen_spec()
    {
        // The sugar is syntax, not a second record shape: flattening happens before the spec is built.
        using var flat = new LuaRuntimeTestDefinition(
            """
            pipeline { name = "p", steps = {
              { name = "A", stage = "Build", run = { "x" } },
              { name = "B", stage = "Test", run = { "y" } },
            } }
            """);
        using var sugar = new LuaRuntimeTestDefinition(
            """
            pipeline { name = "p", stages = {
              { name = "Build", steps = { { name = "A", run = { "x" } } } },
              { name = "Test", steps = { { name = "B", run = { "y" } } } },
            } }
            """);

        Assert.Equal(flat.Run().SpecJson, sugar.Run().SpecJson);
    }

    // ------------------------------------------------------------------
    // The frozen spec: refusals, by field path
    // ------------------------------------------------------------------

    [Theory]
    // the two spellings are exclusive
    [InlineData(
        "pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" } } }, stages = { { name = \"B\", steps = { { name = \"b\", run = { \"y\" } } } } } }",
        "either steps or stages, not both")]
    // stages is an ordered list; a map has no declaration order
    [InlineData(
        "pipeline { name = \"x\", stages = { Build = { { name = \"a\", run = { \"x\" } } } } }",
        "pipeline.stages must be a list of stage tables")]
    [InlineData("pipeline { name = \"x\", stages = {} }", "pipeline.stages must declare at least one stage")]
    [InlineData("pipeline { name = \"x\", stages = { \"Build\" } }", "pipeline.stages[1] must be a stage table")]
    [InlineData(
        "pipeline { name = \"x\", stages = { { name = \"Build\", steps = { { name = \"a\", run = { \"x\" } } }, oops = 1 } } }",
        "pipeline.stages[1] has an unknown key 'oops'")]
    [InlineData("pipeline { name = \"x\", stages = { { name = \"Build\" } } }", "pipeline.stages[1].steps is required")]
    [InlineData(
        "pipeline { name = \"x\", stages = { { name = \"Build\", steps = {} } } }",
        "pipeline.stages[1].steps must declare at least one step")]
    // stage name shape, on the step and on the entry
    [InlineData(
        "pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" }, stage = \"\" } } }",
        "pipeline.steps[1].stage must be a non-empty string")]
    [InlineData(
        "pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" }, stage = \" Build\" } } }",
        "pipeline.steps[1].stage must not start or end with whitespace")]
    [InlineData(
        "pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" }, stage = 1 } } }",
        "pipeline.steps[1].stage must be a string")]
    [InlineData(
        "pipeline { name = \"x\", stages = { { name = \" Build \", steps = { { name = \"a\", run = { \"x\" } } } } } }",
        "pipeline.stages[1].name must not start or end with whitespace")]
    // a stage entry assigns its name; a contained step declaring another is ambiguous, not overridden
    [InlineData(
        "pipeline { name = \"x\", stages = { { name = \"Build\", steps = { { name = \"a\", run = { \"x\" }, stage = \"Test\" } } } } }",
        "pipeline.stages[1].steps[1].stage is assigned by the stage entry")]
    // step names stay unique across the flattened list
    [InlineData(
        "pipeline { name = \"x\", stages = { { name = \"A\", steps = { { name = \"s\", run = { \"x\" } } } }, { name = \"B\", steps = { { name = \"s\", run = { \"y\" } } } } } }",
        "pipeline.stages[2].steps[1].name 's' is already used by pipeline.stages[1].steps[1]")]
    public void An_invalid_stage_declaration_is_refused_with_its_field_path(string definition, string expected)
    {
        using var run = new LuaRuntimeTestDefinition(definition);
        var failure = Assert.Throws<PipelineDefinitionException>(() => run.Run());

        Assert.Equal(PipelineFailureReasons.PipelineInvalid, failure.FailureReason);
        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stage_name_over_the_bound_is_refused_with_the_bound()
    {
        var tooLong = new string('s', PipelineSpecSchema.MaxStageLength + 1);
        using var run = new LuaRuntimeTestDefinition(
            $$"""
            pipeline { name = "x", steps = { { name = "a", run = { "x" }, stage = "{{tooLong}}" } } }
            """);
        var failure = Assert.Throws<PipelineDefinitionException>(() => run.Run());

        Assert.Contains(
            $"pipeline.steps[1].stage must be at most {PipelineSpecSchema.MaxStageLength} characters",
            failure.Message,
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Old versions: read, render and execute exactly as before
    // ------------------------------------------------------------------

    [Fact]
    public void A_v1_or_v2_spec_reads_with_no_stage_and_serialises_without_the_field()
    {
        foreach (var version in new[] { 1, 2 })
        {
            var json = SpecJson(version, StepJson("a"));

            Assert.True(PipelineSpecJson.TryRead(json, out var read, out var error), error);
            Assert.NotNull(read);
            Assert.Equal(version, read!.Version);
            Assert.Null(read.Steps[0].Stage);

            // The old shape is byte-for-byte the old document: no `stage` key appears (the serializer omits
            // nulls), so re-freezing an old spec produces the document it always did - and stays stable.
            var reserialized = PipelineSpecJson.Serialize(read);
            Assert.DoesNotContain("stage", reserialized, StringComparison.Ordinal);
            Assert.True(PipelineSpecJson.TryRead(reserialized, out var again, out var againError), againError);
            Assert.Equal(reserialized, PipelineSpecJson.Serialize(again!));
        }
    }

    [Fact]
    public void A_v2_spec_that_carries_a_stage_is_refused_as_a_contradiction()
    {
        var json = SpecJson(2, """{ "name": "a", "run": [ "dotnet" ], "stage": "Build" }""");

        Assert.False(PipelineSpecJson.TryRead(json, out _, out var error));
        Assert.Contains("pipeline.steps[1].stage requires pipeline.version 3", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "name": "a", "run": [ "dotnet" ], "stage": "" }""", "must be a non-empty string")]
    [InlineData("""{ "name": "a", "run": [ "dotnet" ], "stage": " Build" }""", "must not start or end with whitespace")]
    public void The_envelope_gate_rechecks_a_stored_stage_shape(string step, string expected)
    {
        Assert.False(PipelineSpecJson.TryRead(SpecJson(3, step), out _, out var error));
        Assert.Contains("pipeline.steps[1].stage", error, StringComparison.Ordinal);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_envelope_gate_rechecks_the_stage_bound()
    {
        var tooLong = new string('s', PipelineSpecSchema.MaxStageLength + 1);
        Assert.False(
            PipelineSpecJson.TryRead(SpecJson(3, StepJson("a", stage: tooLong)), out _, out var error));
        Assert.Contains("at most 64 characters", error, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Grouping: adjacent runs, the unstaged step, the aggregate
    // ------------------------------------------------------------------

    [Fact]
    public void Adjacent_steps_of_one_name_form_one_section_and_a_later_repeat_is_another()
    {
        var groups = RunStages.Group(
        [
            Step(1, "Restore", "Build", PipelineStepStatus.Success),
            Step(2, "Compile", "Build", PipelineStepStatus.Success),
            Step(3, "Unit", "Test", PipelineStepStatus.Success),
            Step(4, "Package", "Build", PipelineStepStatus.Success),
        ]);

        Assert.Equal(["Build", "Test", "Build"], groups.Select(group => group.Name));
        Assert.Equal([2, 1, 1], groups.Select(group => group.StepCount));
        Assert.All(groups, group => Assert.True(group.IsNamed));
    }

    [Fact]
    public void A_step_without_a_stage_is_its_own_section_outside_every_named_one()
    {
        var groups = RunStages.Group(
        [
            Step(1, "Notify", null, PipelineStepStatus.Success),
            Step(2, "Compile", "Build", PipelineStepStatus.Success),
            Step(3, "Package", null, PipelineStepStatus.Success),
        ]);

        Assert.Equal([null, "Build", null], groups.Select(group => group.Name));
        Assert.False(groups[0].IsNamed);
        Assert.False(groups[2].IsNamed);
        Assert.Equal("Notify", Assert.Single(groups[0].Steps).Name);
        Assert.Equal([1, 2, 3], groups.SelectMany(group => group.Steps).Select(step => step.Index));
    }

    [Fact]
    public void A_spec_with_no_stage_at_all_is_one_unnamed_section()
    {
        // Every v1/v2 run: the page renders it as the flat list it always showed - no empty headers.
        var groups = RunStages.Group(
        [
            Step(1, "Restore", null, PipelineStepStatus.Success),
            Step(2, "Build", null, PipelineStepStatus.Success),
            Step(3, "Package", null, PipelineStepStatus.Success),
        ]);

        var only = Assert.Single(groups);
        Assert.Null(only.Name);
        Assert.Equal(3, only.StepCount);
    }

    [Theory]
    [InlineData(PipelineStepStatus.Failed, PipelineStepStatus.Success, PipelineStepStatus.Failed)]
    [InlineData(PipelineStepStatus.Success, PipelineStepStatus.Failed, PipelineStepStatus.Failed)]
    [InlineData(PipelineStepStatus.Running, PipelineStepStatus.Success, PipelineStepStatus.Running)]
    [InlineData(PipelineStepStatus.Pending, PipelineStepStatus.Success, PipelineStepStatus.Pending)]
    // a section that did real work is not "skipped" just because one of its steps was
    [InlineData(PipelineStepStatus.Success, PipelineStepStatus.Skipped, PipelineStepStatus.Success)]
    [InlineData(PipelineStepStatus.Skipped, PipelineStepStatus.Skipped, PipelineStepStatus.Skipped)]
    public void The_section_badge_is_the_worst_state_of_its_steps(
        string first,
        string second,
        string expected)
    {
        var groups = RunStages.Group(
        [
            Step(1, "A", "Build", first),
            Step(2, "B", "Build", second),
        ]);

        Assert.Equal(expected, Assert.Single(groups).Status);
    }

    [Fact]
    public void The_section_duration_is_the_last_end_minus_the_first_start_not_the_sum_of_steps()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var groups = RunStages.Group(
        [
            // 10s of work, then a 20s gap (another section could run there), then 10s more.
            Step(1, "A", "Build", PipelineStepStatus.Success, start, start.AddSeconds(10)),
            Step(2, "B", "Build", PipelineStepStatus.Success, start.AddSeconds(30), start.AddSeconds(40)),
        ]);

        var section = Assert.Single(groups);
        Assert.Equal(start, section.StartedAt);
        Assert.Equal(start.AddSeconds(40), section.EndedAt);
        Assert.Equal(TimeSpan.FromSeconds(40), section.Duration);

        // The per-step durations still sum to 20s - the stage is a span of the run, gaps included.
        Assert.Equal(TimeSpan.FromSeconds(20), section.Steps.Aggregate(TimeSpan.Zero, (sum, step) => sum + step.Duration!.Value));
    }

    [Fact]
    public void A_section_with_no_recorded_times_has_no_duration()
    {
        var group = Assert.Single(RunStages.Group([Step(1, "A", "Build", PipelineStepStatus.Pending)]));

        Assert.Null(group.StartedAt);
        Assert.Null(group.EndedAt);
        Assert.Null(group.Duration);
    }

    [Fact]
    public void The_section_names_its_first_failed_step()
    {
        var groups = RunStages.Group(
        [
            Step(1, "A", "Build", PipelineStepStatus.Success),
            Step(2, "B", "Build", PipelineStepStatus.Failed),
            Step(3, "C", "Build", PipelineStepStatus.Failed),
        ]);

        Assert.Equal(2, Assert.Single(groups).FirstFailedStepIndex);
        Assert.True(Assert.Single(groups).HasFailure);
        Assert.Null(Assert.Single(RunStages.Group([Step(1, "A", "Build", PipelineStepStatus.Success)])).FirstFailedStepIndex);
    }

    [Theory]
    [InlineData(PipelineStepStatus.Failed, true)]
    [InlineData(PipelineStepStatus.Running, true)]
    [InlineData(PipelineStepStatus.Success, false)]
    [InlineData(PipelineStepStatus.Skipped, false)]
    [InlineData(PipelineStepStatus.Pending, false)]
    public void A_failed_or_running_section_is_open_by_default_and_everything_else_is_collapsed(
        string status,
        bool expanded)
    {
        var group = Assert.Single(RunStages.Group([Step(1, "A", "Build", status)]));

        Assert.Equal(expanded, group.IsExpandedByDefault);
    }

    [Fact]
    public void An_empty_step_list_produces_no_sections()
    {
        Assert.Empty(RunStages.Group([]));
    }

    // ------------------------------------------------------------------

    private static RunStepView Step(
        int index,
        string name,
        string? stage,
        string status,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? endedAt = null) =>
        new(null, index, name, status, startedAt, endedAt, null, null, Stage: stage);

    private static string SpecJson(int version, string steps) =>
        $$"""{ "version": {{version}}, "name": "p", "steps": [ {{steps}} ] }""";

    private static string StepJson(string name, string? stage = null) =>
        stage is null
            ? $$"""{ "name": "{{name}}", "run": [ "dotnet" ] }"""
            : $$"""{ "name": "{{name}}", "run": [ "dotnet" ], "stage": "{{stage}}" }""";
}
