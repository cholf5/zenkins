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

        var result = new LuaRuntimeTestDefinition(definition).Run();

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
        var result = new LuaRuntimeTestDefinition(
            """
            local spec = pipeline { name = "Build", steps = { { name = "Only", run = { "dotnet", "--info" } } } }
            return spec
            """).Run();

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

        var runner = new LuaRuntimeTestDefinition(definition);
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
    // triggers rules
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" } } }, triggers = { tags = { \"a\" } } }", "pipeline.triggers has an unknown key 'tags'")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" } } }, triggers = { branches = { 1 } } }", "pipeline.triggers.branches[1] must be a non-empty string")]
    public void An_invalid_definition_is_rejected_with_its_field_path(string definition, string expected)
    {
        var failure = Assert.Throws<PipelineDefinitionException>(() =>
            new LuaRuntimeTestDefinition(definition).Run());

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

        var failure = Assert.Throws<PipelineDefinitionException>(() =>
            new LuaRuntimeTestDefinition($"pipeline {{ name = \"x\", steps = {{\n{steps}\n}} }}").Run());

        Assert.Contains("at most 200 steps", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_definition_that_is_not_a_table_is_rejected()
    {
        var failure = Assert.Throws<PipelineDefinitionException>(() =>
            new LuaRuntimeTestDefinition("x = 1").Run());

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

        var failure = Assert.Throws<PipelineDefinitionException>(() =>
            new LuaRuntimeTestDefinition(definition).Run());

        Assert.Equal(PipelineFailureReasons.PipelineInvalid, failure.FailureReason);
        Assert.Contains("nests tables deeper than", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_env_and_triggers_tables_are_accepted_as_absent()
    {
        // An empty Lua table has no shape of its own (the seam reports it as an empty list), so every
        // map-typed field accepts it as "nothing declared".
        var result = new LuaRuntimeTestDefinition(
            """
            pipeline {
              name = "Empty",
              env = {},
              steps = { { name = "Only", run = { "dotnet", "--info" }, env = {} } },
              triggers = {},
            }
            """).Run();

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
        var failure = Assert.Throws<PipelineDefinitionException>(() =>
            new LuaRuntimeTestDefinition(
                "pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" } }, key = \"v\" } }").Run());

        Assert.Equal(PipelineFailureReasons.PipelineInvalid, failure.FailureReason);
        Assert.Contains("mixes array and map keys", failure.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // The server-side envelope gate
    // ------------------------------------------------------------------

    [Fact]
    public void The_envelope_gate_refuses_an_unknown_schema_version()
    {
        Assert.False(PipelineSpecJson.TryRead(SpecJson(version: 2, steps: StepJson("a")), out _, out var error));
        Assert.Contains("pipeline.version", error, StringComparison.Ordinal);
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
        Assert.Contains("exactly one of run or shell", error, StringComparison.Ordinal);
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
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
