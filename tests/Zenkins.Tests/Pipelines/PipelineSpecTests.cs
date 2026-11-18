using Xunit;
using Zenkins.Shared.Pipelines;
using Zenkins.Worker.Lua;
using Zenkins.Worker.Pipelines;

namespace Zenkins.Tests.Pipelines;

/// <summary>
/// Card F-014's spec schema: the Lua table a <c>pipeline.lua</c> declares becomes a
/// <see cref="PipelineSpec"/> with <b>field-path</b> errors, the frozen JSON round-trips, and the
/// server-side envelope check refuses what the issue says it must (unknown version, empty/oversized
/// step list, duplicate names, a step with both or neither command).
/// </summary>
[Trait("Category", "Pipeline")]
public sealed class PipelineSpecTests
{
    // ------------------------------------------------------------------
    // The frozen round-trip
    // ------------------------------------------------------------------

    [Fact]
    public void A_spec_round_trips_through_the_frozen_json_unchanged()
    {
        var spec = new PipelineSpec(
            PipelineSpecSchema.Version,
            "Build",
            [
                new PipelineStep("Restore", Run: ["dotnet", "restore"]),
                new PipelineStep(
                    "Package",
                    Shell: ["bash", "-c", "tar czf out.tgz bin"],
                    Env: new SortedDictionary<string, string>(StringComparer.Ordinal) { ["CONFIG"] = "Release" },
                    ContinueOnError: true),
            ],
            new SortedDictionary<string, string>(StringComparer.Ordinal) { ["GLOBAL"] = "1" },
            new PipelineTriggers(["main", "develop"]));

        var json = PipelineSpecJson.Serialize(spec);

        Assert.True(PipelineSpecJson.TryRead(json, out var read, out var error), error);
        Assert.NotNull(read);
        Assert.Equal("Build", read!.Name);
        Assert.Equal(2, read.Steps.Length);
        Assert.Equal(["dotnet", "restore"], read.Steps[0].Run!);
        Assert.Equal(["bash", "-c", "tar czf out.tgz bin"], read.Steps[1].Shell!);
        Assert.Equal("Release", read.Steps[1].Env!["CONFIG"]);
        Assert.True(read.Steps[1].ContinueOnError);
        Assert.Equal("1", read.Env!["GLOBAL"]);
        Assert.Equal(["main", "develop"], read.Triggers!.Branches!);

        // Byte-stable: serialising the read-back spec produces the same JSON again.
        Assert.Equal(json, PipelineSpecJson.Serialize(read));
    }

    [Fact]
    public void A_declared_definition_becomes_the_expected_spec()
    {
        var definition = """
            pipeline {
              name = "Build",
              env = { GLOBAL = "1" },
              steps = {
                { name = "Restore", run = { "dotnet", "restore" } },
                { name = "Build", run = { "dotnet", "build", "-c", "Release" },
                  env = { CONFIG = "Release" }, continue_on_error = true },
                { name = "Package", shell = { "bash", "-c", "tar czf out.tgz bin" } },
              },
              triggers = { branches = { "main" } },
            }
            """;

        using var run = new LuaRuntimeTestDefinition(definition);
        var result = run.Run();

        Assert.Equal(PipelineSpecSchema.Version, result.Spec.Version);
        Assert.Equal("Build", result.Spec.Name);
        Assert.Equal(["Restore", "Build", "Package"], result.Spec.Steps.Select(step => step.Name));
        Assert.Equal(["dotnet", "restore"], result.Spec.Steps[0].Run!);
        Assert.False(result.Spec.Steps[0].IsShell);
        Assert.True(result.Spec.Steps[2].IsShell);
        Assert.Equal(["bash", "-c", "tar czf out.tgz bin"], result.Spec.Steps[2].Argv);
        Assert.Equal("Release", result.Spec.Steps[1].Env!["CONFIG"]);
        Assert.True(result.Spec.Steps[1].ContinueOnError);
        Assert.Equal("1", result.Spec.Env!["GLOBAL"]);
        Assert.Equal(["main"], result.Spec.Triggers!.Branches!);
    }

    [Fact]
    public void A_returned_pipeline_table_is_read_like_a_recorded_one()
    {
        // PRD §3.3.2 writes `pipeline { … }` without a return; a script may also `return` it. Both
        // reach the host through the same structured seam value.
        using var run = new LuaRuntimeTestDefinition(
            """
            local spec = pipeline { name = "Build", steps = { { name = "Only", run = { "dotnet", "--info" } } } }
            return spec
            """);
        var result = run.Run();

        Assert.Equal("Build", result.Spec.Name);
        Assert.Single(result.Spec.Steps);
    }

    [Fact]
    public void The_prelude_offers_only_pure_helpers_and_the_platform_is_host_supplied()
    {
        var definition = """
            log("describing")
            if not (is_windows() or is_linux() or is_macos()) then
              error("no platform")
            end
            pipeline { name = "Pure", steps = { { name = "Only", run = { "dotnet", "--info" } } } }
            """;

        using var runner = new LuaRuntimeTestDefinition(definition);
        var result = runner.Run();

        Assert.Equal("Pure", result.Spec.Name);
        Assert.Contains("describing", runner.LogLines, StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------
    // Field-path rejections
    // ------------------------------------------------------------------

    [Theory]
    // unknown keys at both levels
    [InlineData("pipeline { name = \"x\", steps = {}, unknown = 1 }", "pipeline has an unknown key 'unknown'")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" }, oops = true } } }", "pipeline.steps[1] has an unknown key 'oops'")]
    // name rules
    [InlineData("pipeline { steps = { { name = \"a\", run = { \"x\" } } } }", "pipeline.name is required")]
    [InlineData("pipeline { name = \"\", steps = { { name = \"a\", run = { \"x\" } } } }", "pipeline.name must be a non-empty string")]
    [InlineData("pipeline { name = \"x\", steps = { { run = { \"x\" } } } }", "pipeline.steps[1].name is required")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"\", run = { \"x\" } } } }", "pipeline.steps[1].name must be a non-empty string")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" } }, { name = \"a\", run = { \"y\" } } } }", "pipeline.steps[2].name 'a' is already used by pipeline.steps[1]")]
    // steps rules
    [InlineData("pipeline { name = \"x\" }", "pipeline.steps is required")]
    [InlineData("pipeline { name = \"x\", steps = {} }", "pipeline.steps must declare at least one step")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\" } } }", "pipeline.steps[1] must declare exactly one of run or shell")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" }, shell = { \"y\" } } } }", "pipeline.steps[1] must declare exactly one of run or shell")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = {} } } }", "pipeline.steps[1].run must hold at least one argument")]
    // argv element rules
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\", 2 } } } }", "pipeline.steps[1].run[2] must be a string")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\", \"\" } } } }", "pipeline.steps[1].run[2] is empty")]
    // env / continue_on_error rules
    [InlineData("pipeline { name = \"x\", env = { K = 1 }, steps = { { name = \"a\", run = { \"x\" } } } }", "pipeline.env.K must be a string")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" }, env = { K = true } } } }", "pipeline.steps[1].env.K must be a string")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" }, continue_on_error = \"yes\" } } }", "pipeline.steps[1].continue_on_error must be true or false")]
    // F-032's two knobs: bounded, whole numbers
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" }, timeout = 0 } } }", "pipeline.steps[1].timeout must be a whole number between 1 and 86400")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" }, timeout = 86401 } } }", "pipeline.steps[1].timeout must be a whole number between 1 and 86400")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" }, timeout = 1.5 } } }", "pipeline.steps[1].timeout must be a whole number between 1 and 86400")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" }, timeout = \"600\" } } }", "pipeline.steps[1].timeout must be an integer")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" }, retry = 0 } } }", "pipeline.steps[1].retry must be a whole number between 1 and 5")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" }, retry = 6 } } }", "pipeline.steps[1].retry must be a whole number between 1 and 5")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" }, retry = true } } }", "pipeline.steps[1].retry must be an integer")]
    // triggers rules
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" } } }, triggers = { tags = { \"a\" } } }", "pipeline.triggers has an unknown key 'tags'")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" } } }, triggers = { branches = { 1 } } }", "pipeline.triggers.branches[1] must be a non-empty string")]
    public void An_invalid_definition_is_rejected_with_its_field_path(string definition, string expected)
    {
        using var run = new LuaRuntimeTestDefinition(definition);
        var failure = Assert.Throws<PipelineDefinitionException>(() => run.Run());

        Assert.Equal(PipelineFailureReasons.PipelineInvalid, failure.FailureReason);
        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void More_than_200_steps_are_rejected_with_the_step_path()
    {
        var steps = string.Join(
            ",\n",
            Enumerable.Range(1, PipelineSpecSchema.MaxSteps + 1)
                .Select(index => $"{{ name = \"s{index}\", run = {{ \"x\" }} }}"));

        using var run = new LuaRuntimeTestDefinition($"pipeline {{ name = \"x\", steps = {{\n{steps}\n}} }}");
        var failure = Assert.Throws<PipelineDefinitionException>(() => run.Run());

        Assert.Contains("at most 200 steps", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_definition_that_is_not_a_table_is_rejected()
    {
        using var run = new LuaRuntimeTestDefinition("x = 1");
        var failure = Assert.Throws<PipelineDefinitionException>(() => run.Run());

        Assert.Equal(PipelineFailureReasons.PipelineInvalid, failure.FailureReason);
        Assert.Contains("pipeline { name = ..., steps = { ... } }", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_nested_deeper_than_the_host_walks_is_rejected()
    {
        // 40 levels: over the seam's MaxResultDepth (32), so the host refuses it instead of recursing
        // until the stack gives out.
        var definition = "local t = {} local inner = t for i = 1, 40 do inner.x = {} inner = inner.x end "
            + "return { name = \"deep\", steps = { { name = \"Only\", run = { \"dotnet\" } } }, extra = t }";

        using var run = new LuaRuntimeTestDefinition(definition);
        var failure = Assert.Throws<PipelineDefinitionException>(() => run.Run());

        Assert.Equal(PipelineFailureReasons.PipelineInvalid, failure.FailureReason);
        Assert.Contains("nests tables deeper than", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_env_and_triggers_tables_are_accepted_as_absent()
    {
        // An empty Lua table has no shape of its own (the seam reports it as an empty list), so every
        // map-typed field accepts it as "nothing declared".
        using var run = new LuaRuntimeTestDefinition(
            """
            pipeline {
              name = "Empty",
              env = {},
              steps = { { name = "Only", run = { "dotnet", "--info" }, env = {} } },
              triggers = {},
            }
            """);
        var result = run.Run();

        Assert.Equal("Empty", result.Spec.Name);
        Assert.Null(result.Spec.Env);
        Assert.Null(result.Spec.Triggers);
        Assert.Null(result.Spec.Steps[0].Env);
        // The frozen JSON therefore carries no empty containers the executor would have to interpret.
        Assert.DoesNotContain("\"env\"", result.SpecJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"triggers\"", result.SpecJson, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_that_mixes_array_and_map_keys_is_a_clear_error_not_a_lossy_read()
    {
        using var run = new LuaRuntimeTestDefinition(
            "pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" } }, key = \"v\" } }");
        var failure = Assert.Throws<PipelineDefinitionException>(() => run.Run());

        Assert.Equal(PipelineFailureReasons.PipelineInvalid, failure.FailureReason);
        Assert.Contains("mixes array and map keys", failure.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // The server-side envelope gate
    // ------------------------------------------------------------------

    [Fact]
    public void The_envelope_gate_refuses_an_unknown_schema_version()
    {
        // F-020 moved the current schema to v2 (the script step) with v1 still readable; v3 is the
        // unknown version, refused rather than guessed.
        Assert.False(PipelineSpecJson.TryRead(SpecJson(version: 3, steps: StepJson("a")), out _, out var error));
        Assert.Contains("pipeline.version", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_v1_spec_still_reads_back_after_the_script_step_arrived()
    {
        // Every spec frozen before F-020 is v1 and has no script field; the envelope must keep
        // accepting them for the historical runs' pages.
        var json = SpecJson(version: 1, steps: StepJson("a"));

        Assert.True(PipelineSpecJson.TryRead(json, out var read, out var error), error);
        Assert.NotNull(read);
        Assert.Equal(1, read!.Version);
        Assert.Equal(["dotnet", "--info"], read.Steps[0].Run!);
    }

    [Fact]
    public void The_step_timeout_and_retry_are_optional_bounded_knobs_the_frozen_record_carries()
    {
        // Card F-032: `timeout = 600, retry = 2` on one step, nothing on the other. Both are additive
        // optional fields - the schema version does not move, and an absent one means "no limit of its
        // own" / "one attempt", which is exactly the pre-F-032 behaviour.
        using var run = new LuaRuntimeTestDefinition(
            """
            pipeline {
              name = "Knobs",
              steps = {
                { name = "Flaky", run = { "dotnet", "--info" }, timeout = 600, retry = 2 },
                { name = "Plain", run = { "dotnet", "--info" } },
              },
            }
            """);

        var result = run.Run();

        Assert.Equal(PipelineSpecSchema.Version, result.Spec.Version);
        Assert.Equal(600, result.Spec.Steps[0].TimeoutSeconds);
        Assert.Equal(2, result.Spec.Steps[0].Retry);
        Assert.Equal(TimeSpan.FromMinutes(10), result.Spec.Steps[0].OwnTimeout);
        Assert.Equal(2, result.Spec.Steps[0].AttemptCount);
        Assert.True(result.Spec.Steps[0].HasOwnTimeout);

        // The step that declared neither keeps the pre-F-032 shape...
        Assert.Null(result.Spec.Steps[1].TimeoutSeconds);
        Assert.Null(result.Spec.Steps[1].Retry);
        Assert.Equal(1, result.Spec.Steps[1].AttemptCount);
        Assert.False(result.Spec.Steps[1].HasOwnTimeout);

        // ...and the frozen JSON round-trips both, omitting the absent pair entirely (nulls are not
        // serialised), so an older reader sees exactly the document it always did.
        Assert.True(PipelineSpecJson.TryRead(result.SpecJson, out var frozen, out var error), error);
        Assert.Equal(600, frozen!.Steps[0].TimeoutSeconds);
        Assert.Equal(2, frozen.Steps[0].Retry);
        Assert.Null(frozen.Steps[1].TimeoutSeconds);
        var plainDocument = result.SpecJson[result.SpecJson.IndexOf("Plain", StringComparison.Ordinal)..];
        Assert.DoesNotContain("timeoutSeconds", plainDocument, StringComparison.Ordinal);
        Assert.DoesNotContain("retry", plainDocument, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("timeout = 1", 1, null)]
    [InlineData("timeout = 86400", 86400, null)]
    [InlineData("retry = 1", null, 1)]
    [InlineData("retry = 5", null, 5)]
    public void The_bounds_of_the_two_knobs_are_inclusive(string knobs, int? timeoutSeconds, int? retry)
    {
        // The other half of the bound: exactly 1 and exactly 86400 / 5 must be ACCEPTED. A `<=`/`>=` written
        // the wrong way round would silently make a legal extreme unusable, and the out-of-range rows above
        // would not notice (they only ever test one past the edge).
        using var run = new LuaRuntimeTestDefinition(
            $"pipeline {{ name = \"x\", steps = {{ {{ name = \"a\", run = {{ \"x\" }}, {knobs} }} }} }}");

        var result = run.Run();

        Assert.Equal(timeoutSeconds, result.Spec.Steps[0].TimeoutSeconds);
        Assert.Equal(retry, result.Spec.Steps[0].Retry);

        // ...and the Server's envelope accepts the same extremes, so the two gates agree on the bound.
        Assert.True(PipelineSpecJson.TryRead(result.SpecJson, out var frozen, out var error), error);
        Assert.Equal(timeoutSeconds, frozen!.Steps[0].TimeoutSeconds);
        Assert.Equal(retry, frozen.Steps[0].Retry);
    }

    [Theory]
    [InlineData("\"timeoutSeconds\": 0", "pipeline.steps[1].timeoutSeconds must be between 1 and 86400")]
    [InlineData("\"timeoutSeconds\": 86401", "pipeline.steps[1].timeoutSeconds must be between 1 and 86400")]
    [InlineData("\"retry\": 0", "pipeline.steps[1].retry must be between 1 and 5")]
    [InlineData("\"retry\": 6", "pipeline.steps[1].retry must be between 1 and 5")]
    public void The_envelope_gate_refuses_a_step_knob_outside_its_bounds(string knob, string expected)
    {
        // Card F-032: the Worker refuses these with a field path; this is the Server's own gate, so a buggy
        // or hostile worker cannot freeze a limit the executor would have to guess about.
        var json = SpecJson(
            version: PipelineSpecSchema.Version,
            steps: $$"""{ "name": "a", "run": [ "x" ], {{knob}} }""");

        Assert.False(PipelineSpecJson.TryRead(json, out _, out var error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_envelope_gate_refuses_an_empty_step_list()
    {
        Assert.False(PipelineSpecJson.TryRead(SpecJson(version: 1, steps: ""), out _, out var error));
        Assert.Contains("1..200", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_envelope_gate_refuses_duplicate_step_names()
    {
        var json = SpecJson(version: 1, steps: StepJson("a") + "," + StepJson("a"));

        Assert.False(PipelineSpecJson.TryRead(json, out _, out var error));
        Assert.Contains("more than one step", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_envelope_gate_refuses_a_step_with_neither_command()
    {
        var json = SpecJson(version: 1, steps: """{ "name": "a" }""");

        Assert.False(PipelineSpecJson.TryRead(json, out _, out var error));
        Assert.Contains("exactly one of run, shell or script", error, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // The Freestyle script step (card F-020)
    // ------------------------------------------------------------------

    [Fact]
    public void A_script_step_round_trips_through_the_frozen_json()
    {
        var spec = new PipelineSpec(
            PipelineSpecSchema.Version,
            "Freestyle",
            [
                new PipelineStep("Hello", Script: "echo hello"),
                new PipelineStep(
                    "Build",
                    Script: "dotnet build\nif errorlevel 1 exit 1",
                    Env: new SortedDictionary<string, string>(StringComparer.Ordinal) { ["CONFIG"] = "Release" },
                    ContinueOnError: true),
            ]);

        var json = PipelineSpecJson.Serialize(spec);

        Assert.True(PipelineSpecJson.TryRead(json, out var read, out var error), error);
        Assert.NotNull(read);
        Assert.Equal(2, read!.Version);
        Assert.Equal("echo hello", read.Steps[0].Script);
        Assert.True(read.Steps[0].IsScript);
        Assert.Empty(read.Steps[0].Argv);
        Assert.Null(read.Steps[0].Run);
        Assert.Null(read.Steps[0].Shell);
        Assert.Equal("dotnet build\nif errorlevel 1 exit 1", read.Steps[1].Script);
        Assert.True(read.Steps[1].ContinueOnError);

        // Byte-stable, like every frozen spec.
        Assert.Equal(json, PipelineSpecJson.Serialize(read));
    }

    [Fact]
    public void The_envelope_gate_refuses_a_step_with_a_script_and_a_command()
    {
        var json = SpecJson(
            version: 2,
            steps: """{ "name": "a", "run": [ "dotnet" ], "script": "echo hi" }""");

        Assert.False(PipelineSpecJson.TryRead(json, out _, out var error));
        Assert.Contains("exactly one of run, shell or script", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_envelope_gate_refuses_an_empty_script()
    {
        var json = SpecJson(version: 2, steps: """{ "name": "a", "script": "   " }""");

        Assert.False(PipelineSpecJson.TryRead(json, out _, out var error));
        Assert.Contains("script must be a non-empty script text", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_envelope_gate_refuses_an_oversized_script()
    {
        var json = SpecJson(
            version: 2,
            steps: $$"""{ "name": "a", "script": "{{new string('x', PipelineSpecSchema.MaxScriptChars + 1)}}" }""");

        Assert.False(PipelineSpecJson.TryRead(json, out _, out var error));
        Assert.Contains("at most", error, StringComparison.Ordinal);
        Assert.Contains("characters", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_envelope_gate_refuses_an_oversized_payload()
    {
        var json = SpecJson(
            version: 1,
            steps: StepJson(new string('a', PipelineSpecSchema.MaxJsonBytes)));

        Assert.False(PipelineSpecJson.TryRead(json, out _, out var error));
        Assert.Contains("exceeds", error, StringComparison.Ordinal);
    }

    private static string StepJson(string name) =>
        $$"""{ "name": "{{name}}", "run": [ "dotnet", "--info" ] }""";

    private static string SpecJson(int version, string steps) => $$"""
        {
          "version": {{version}},
          "name": "Build",
          "steps": [ {{steps}} ]
        }
        """;
    // ------------------------------------------------------------------
    // Card F-033: per-step `runs_on`
    // ------------------------------------------------------------------

    [Fact]
    public void A_runs_on_table_is_the_conjunction_of_its_entries()
    {
        using var run = new LuaRuntimeTestDefinition(
            "pipeline { name = \"x\", steps = { { name = \"Build\", run = { \"x\" }, "
            + "runs_on = { \"linux\", \"arm64\" } } } }");

        var spec = run.Run().Spec;

        var step = Assert.Single(spec.Steps);
        Assert.Equal("linux,arm64", step.RunsOn);
        Assert.Equal("arm64&&linux", step.Requirement);

        var requirement = Parse(step.Requirement!);
        Assert.True(requirement.SatisfiedBy(["linux", "arm64"]));
        Assert.False(requirement.SatisfiedBy(["linux"]));
        Assert.False(requirement.SatisfiedBy([]));
    }

    [Fact]
    public void A_runs_on_expression_is_normalized_for_the_machine_and_kept_as_written_for_people()
    {
        using var run = new LuaRuntimeTestDefinition(
            "pipeline { name = \"x\", steps = { { name = \"Build\", run = { \"x\" }, "
            + "runs_on = \"linux && (arm64 || x64)\" } } }");

        var step = Assert.Single(run.Run().Spec.Steps);

        // The page shows what the author wrote; the claim loop reads the DNF.
        Assert.Equal("linux && (arm64 || x64)", step.RunsOn);
        Assert.NotEqual(step.RunsOn, step.Requirement);

        var requirement = Parse(step.Requirement!);
        Assert.True(requirement.SatisfiedBy(["linux", "x64"]));
        Assert.True(requirement.SatisfiedBy(["linux", "arm64"]));
        Assert.False(requirement.SatisfiedBy(["arm64"]));
    }

    [Fact]
    public void Two_steps_that_cannot_share_one_worker_are_refused_by_path_naming_both()
    {
        using var run = new LuaRuntimeTestDefinition(
            "pipeline { name = \"x\", steps = { { name = \"Build\", run = { \"x\" }, runs_on = { \"linux\" } }, "
            + "{ name = \"Test\", run = { \"y\" }, runs_on = \"!linux\" } } }");

        var failure = Assert.Throws<PipelineDefinitionException>(() => run.Run());

        Assert.Equal(PipelineFailureReasons.PipelineInvalid, failure.FailureReason);
        Assert.Contains("pipeline.steps[1]", failure.Message, StringComparison.Ordinal);
        Assert.Contains("pipeline.steps[2]", failure.Message, StringComparison.Ordinal);
        Assert.Contains("linux", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Steps_whose_alternatives_clash_without_sharing_a_clash_are_refused_too()
    {
        // No single literal is asserted and denied by one clause: step 1 offers a or b, step 2 needs neither.
        // A literal-level check passes this definition; the union's cross product has no satisfiable
        // alternative left, which is the question that decides whether one Worker can run both steps.
        using var run = new LuaRuntimeTestDefinition(
            "pipeline { name = \"x\", steps = { { name = \"Build\", run = { \"x\" }, runs_on = \"a || b\" }, "
            + "{ name = \"Test\", run = { \"y\" }, runs_on = \"!a && !b\" } } }");

        var failure = Assert.Throws<PipelineDefinitionException>(() => run.Run());

        Assert.Equal(PipelineFailureReasons.PipelineInvalid, failure.FailureReason);
        Assert.Contains("pipeline.steps[1]", failure.Message, StringComparison.Ordinal);
        Assert.Contains("pipeline.steps[2]", failure.Message, StringComparison.Ordinal);
        Assert.Contains("no single Worker can satisfy both", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Steps_that_merely_share_a_label_are_not_refused()
    {
        // The other half of the same rule: `a && b` and `a` share the atom a and are perfectly compatible -
        // a Worker carrying both runs either step. A "do they mention the same label" check would refuse this
        // definition, which is why the question is satisfiability rather than overlap.
        using var run = new LuaRuntimeTestDefinition(
            "pipeline { name = \"x\", steps = { { name = \"Build\", run = { \"x\" }, runs_on = \"a && b\" }, "
            + "{ name = \"Test\", run = { \"y\" }, runs_on = \"a\" } } }");

        var spec = run.Run().Spec;

        Assert.Equal(2, spec.Steps.Length);
    }

    [Fact]
    public void A_step_without_runs_on_asks_for_nothing_rather_than_for_the_empty_expression()
    {
        using var run = new LuaRuntimeTestDefinition(
            "pipeline { name = \"x\", steps = { { name = \"Build\", run = { \"x\" } } } }");

        var step = Assert.Single(run.Run().Spec.Steps);

        // "No requirement" is not an expression: an empty expression would be a contradiction nobody could
        // satisfy, and the claim loop treats a missing requirement as "any Worker".
        Assert.Null(step.RunsOn);
        Assert.Null(step.Requirement);
        Assert.False(step.HasWorkerRequirement);
    }

    private static Zenkins.Shared.Workers.WorkerRequirement Parse(string value)
    {
        Assert.True(Zenkins.Shared.Workers.WorkerRequirement.TryParse(value, out var requirement, out var error), error);
        return requirement;
    }

}

/// <summary>
/// Runs one definition under the real definition phase (F-011's runtime plus the embedded prelude) in a
/// throwaway work directory, which is all the definition phase needs: no git, no process.
/// </summary>
internal sealed class LuaRuntimeTestDefinition : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"zenkins-f014-def-{Guid.NewGuid():N}");

    private readonly string _source;

    public LuaRuntimeTestDefinition(string definition, string pipelinePath = "pipeline.lua")
    {
        _source = definition;
        var repository = Path.Combine(_root, "repository");
        Directory.CreateDirectory(repository);
        File.WriteAllText(Path.Combine(repository, pipelinePath), definition);
        PipelinePath = pipelinePath;
        Sink = new LuaLogSink();
        Runner = new PipelineDefinitionRunner(PipelineDefinitionRunner.CreateRuntime(Sink));
    }

    public string PipelinePath { get; }

    public LuaLogSink Sink { get; }

    public PipelineDefinitionRunner Runner { get; }

    public string[] LogLines => [.. Sink.Lines];

    /// <summary>The run's deadline used by these tests: far enough away that only a real runaway hits it.</summary>
    public DateTimeOffset Deadline { get; set; } = DateTimeOffset.UtcNow.AddSeconds(20);

    public PipelineDefinitionResult Run() => Runner.Run(
        new Zenkins.Worker.RunWorkDirectory(
            Guid.NewGuid(),
            _root,
            Path.Combine(_root, "repository"),
            Path.Combine(_root, "artifacts"),
            Path.Combine(_root, "temp")),
        PipelinePath,
        Deadline,
        CancellationToken.None);

    public void Dispose()
    {
        // Q-011: a failed delete used to be swallowed (IOException-only) and the root was left in
        // the system temp forever; TempRootCleanup retries the transient failures instead. This
        // fixture is created per test case, so it was the dominant leak source (f014-def-*).
        TempRootCleanup.DeleteDirectory(_root);
    }
}
