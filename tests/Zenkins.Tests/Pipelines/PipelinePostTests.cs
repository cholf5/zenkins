using Xunit;
using Zenkins.Shared.Pipelines;
using Zenkins.Worker.Pipelines;

namespace Zenkins.Tests.Pipelines;

/// <summary>
/// Card F-031's frozen post lists: both the Lua table and the envelope, plus the small lookups the Server uses
/// to resolve a recorded <c>post/…</c> row. Execution facts (order, failures, the cancel grace) live in
/// <c>PipelineRunnerTests</c>; the page's own rendering is <c>RunPostPageTests</c>.
/// </summary>
[Trait("Category", "Pipeline")]
public sealed class PipelinePostTests
{
    // ------------------------------------------------------------------
    // The Lua table and the frozen record
    // ------------------------------------------------------------------

    [Fact]
    public void The_post_table_is_frozen_with_prefixed_names_and_the_step_shape_intact()
    {
        using var run = new LuaRuntimeTestDefinition(
            """
            pipeline {
              name = "release",
              steps = { { name = "Build", run = { "dotnet", "build" } } },
              post = {
                success = { { name = "Report", run = { "report" }, env = { CHANNEL = "release" } } },
                failure = { { name = "Notify", run = { "notify" } } },
                always = { { name = "Cleanup", run = { "cleanup" }, timeout = 30 } },
              },
            }
            """);
        var result = run.Run();

        Assert.Equal(PipelineSpecSchema.Version, result.Spec.Version);
        var post = Assert.IsType<PipelinePost>(result.Spec.Post);
        Assert.Equal(["post/Report"], post.Success!.Select(step => step.Name));
        Assert.Equal(["post/Notify"], post.Failure!.Select(step => step.Name));
        Assert.Equal(["post/Cleanup"], post.Always!.Select(step => step.Name));

        // A post step is a step: env, timeout and argv survive exactly as the main list's do.
        var report = post.Success![0];
        Assert.Equal("release", report.Env!["CHANNEL"]);
        Assert.Equal(30, post.Always![0].TimeoutSeconds);
        Assert.Equal(["report"], report.Run!);

        // The names are in the frozen JSON, prefixed - a record and a log line cannot be mistaken for a main
        // step's.
        Assert.Contains("\"post/Report\"", result.SpecJson, StringComparison.Ordinal);
        Assert.True(PipelineSpecJson.TryRead(result.SpecJson, out var read, out var error), error);
        Assert.Equal(result.SpecJson, PipelineSpecJson.Serialize(read!));
    }

    [Fact]
    public void An_empty_post_table_is_nothing_declared_like_every_other_optional_table()
    {
        using var run = new LuaRuntimeTestDefinition(
            "pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" } } }, post = {} }");

        Assert.Null(run.Run().Spec.Post);
    }

    [Fact]
    public void A_pipeline_without_post_has_no_post_the_old_shape_is_unchanged()
    {
        using var run = new LuaRuntimeTestDefinition(
            "pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" } } } }");
        var result = run.Run();

        Assert.Null(result.Spec.Post);
        Assert.DoesNotContain("\"post\"", result.SpecJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" } } }, post = \"always\" }",
        "pipeline.post must be a table")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" } } }, post = { oops = {} } }",
        "pipeline.post has an unknown key 'oops'")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" } } }, post = { always = \"cleanup\" } }",
        "pipeline.post.always must be a list of steps")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" } } }, post = { always = { { name = \"post/cleanup\", run = { \"x\" } } } } }",
        "must not start with 'post/'")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" } } }, post = { always = { { name = \"cleanup\", run = { \"x\" }, stage = \"Build\" } } } }",
        "pipeline.post.always[1].stage must not be declared")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"a\", run = { \"x\" } } }, post = { always = { { name = \"cleanup\", run = { \"x\" } } }, success = { { name = \"cleanup\", run = { \"y\" } } } } }",
        "pipeline.post.always[1].name 'cleanup' is already used by pipeline.post.success[1]")]
    [InlineData("pipeline { name = \"x\", steps = { { name = \"post/cleanup\", run = { \"x\" } } }, post = { always = { { name = \"cleanup\", run = { \"y\" } } } } }",
        "pipeline.post.always[1].name 'cleanup' is already used")]
    public void An_invalid_post_declaration_is_refused_with_its_field_path(string definition, string expected)
    {
        using var run = new LuaRuntimeTestDefinition(definition);
        var failure = Assert.Throws<PipelineDefinitionException>(() => run.Run());

        Assert.Equal(PipelineFailureReasons.PipelineInvalid, failure.FailureReason);
        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // The envelope gate (a forged or older document)
    // ------------------------------------------------------------------

    [Fact]
    public void The_envelope_gate_requires_the_post_prefix()
    {
        var json = SpecJson(
            version: 3,
            steps: StepJson("a"),
            post: """{ "always": [ { "name": "cleanup", "run": [ "x" ] } ] }""");

        Assert.False(PipelineSpecJson.TryRead(json, out _, out var error));
        Assert.Contains("pipeline.post.always[1].name must start with 'post/'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_envelope_gate_bounds_one_post_list()
    {
        var steps = string.Join(
            ",",
            Enumerable.Range(1, PipelineSpecSchema.MaxPostStepsPerList + 1)
                .Select(index => $$"""{ "name": "post/s{{index}}", "run": [ "x" ] }"""));
        var json = SpecJson(3, StepJson("a"), $$"""{ "always": [ {{steps}} ] }""");

        Assert.False(PipelineSpecJson.TryRead(json, out _, out var error));
        Assert.Contains("pipeline.post.always must hold at most", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_v2_spec_that_carries_post_is_refused_as_a_version_contradiction()
    {
        var json = SpecJson(2, StepJson("a"), """{ "always": [ { "name": "post/cleanup", "run": [ "x" ] } ] }""");

        Assert.False(PipelineSpecJson.TryRead(json, out _, out var error));
        Assert.Contains("pipeline.post requires pipeline.version 3", error, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // The lookups the Server resolves a recorded post row with
    // ------------------------------------------------------------------

    [Fact]
    public void The_declared_steps_are_enumerated_with_their_outcome_and_resolved_by_name()
    {
        var post = new PipelinePost(
            Success: [new PipelineStep("post/report", Run: ["report"])],
            Failure: [new PipelineStep("post/notify", Run: ["notify"])],
            Always: [new PipelineStep("post/cleanup", Run: ["cleanup"])]);

        Assert.Equal(3, post.Count);
        Assert.False(post.IsEmpty);
        Assert.Equal(
            [PipelinePostOutcomes.Success, PipelinePostOutcomes.Failure, PipelinePostOutcomes.Always],
            post.DeclaredSteps.Select(declared => declared.Outcome));
        Assert.Equal("cleanup", post.Find("post/cleanup")!.Argv[0]);
        Assert.Equal(PipelinePostOutcomes.Failure, post.OutcomeOf("post/notify"));
        Assert.Null(post.Find("post/missing"));
        Assert.Null(post.OutcomeOf("post/missing"));
        Assert.True(new PipelinePost().IsEmpty);
    }

    private static string SpecJson(int version, string steps, string post) =>
        $$"""{ "version": {{version}}, "name": "p", "steps": [ {{steps}} ], "post": {{post}} }""";

    private static string StepJson(string name) =>
        $$"""{ "name": "{{name}}", "run": [ "dotnet" ] }""";
}
