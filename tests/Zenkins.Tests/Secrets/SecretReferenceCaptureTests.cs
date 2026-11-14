using Xunit;
using Zenkins.Shared.Pipelines;
using Zenkins.Worker;
using Zenkins.Worker.Lua;
using Zenkins.Worker.Pipelines;

namespace Zenkins.Tests.Secrets;

/// <summary>
/// Card D-006's definition-phase capture: the sandbox records every <c>secret("NAME")</c> a pipeline
/// declares, the frozen spec carries those names (never a value), and the record survives the JSON
/// round-trip the server later reads back. The pre-capture shape (no field at all) stays readable and
/// is distinguishable from "captured, references nothing" - which is the whole point of the tri-state.
/// </summary>
[Trait("Category", "Secrets")]
public sealed class SecretReferenceCaptureTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public void The_definition_phase_records_each_referenced_name_once_and_freezes_it()
    {
        // TC-001's first half: API_KEY is called twice and OTHER once, so the record holds two names - and
        // the frozen JSON the server stores is where the answer comes from, not the script.
        using var definition = new DefinitionRun(
            """
            pipeline {
              name = "d006-pipeline",
              steps = {
                { name = "Restore", run = { "dotnet", "restore", secret("API_KEY") } },
                { name = "Build", run = { "dotnet", "build" } },
                { name = "Publish", run = { "dotnet", "publish", "--token", secret("API_KEY"), "--other", secret("OTHER") } },
              },
            }
            """);

        var result = definition.Run();

        Assert.Equal(["API_KEY", "OTHER"], result.Spec.SecretReferences!);

        // The record is names only: the frozen argv keeps the deferred marker (F-015's design - the value is
        // substituted at the step boundary), so nothing that reaches storage or the page can be a value.
        Assert.Contains("${secret:API_KEY}", result.SpecJson, StringComparison.Ordinal);
        Assert.True(PipelineSpecJson.TryRead(result.SpecJson, out var frozen, out var error), error);
        Assert.Equal(["API_KEY", "OTHER"], frozen!.SecretReferences!);
    }

    [Fact]
    public void A_reference_pasted_into_a_frozen_value_is_recorded_as_well()
    {
        // The executor substitutes any well-formed marker in an argv or env value, so a hand-written one is
        // a reference in every sense that matters - recording only the calls would understate what a run
        // can read, and "nothing references this" would then be a lie the delete flow relies on.
        using var definition = new DefinitionRun(
            """
            pipeline {
              name = "pasted",
              env = { GITHUB_TOKEN = "${secret:FROM_TEXT}" },
              steps = { { name = "Build", run = { "dotnet", "build", secret("FROM_CALL") } } },
            }
            """);

        var result = definition.Run();

        Assert.Equal(["FROM_CALL", "FROM_TEXT"], result.Spec.SecretReferences!);
    }

    [Fact]
    public void A_definition_without_secrets_records_an_empty_list_not_null()
    {
        using var definition = new DefinitionRun(
            """
            pipeline {
              name = "no-secrets",
              steps = { { name = "Build", run = { "dotnet", "build" } } },
            }
            """);

        var result = definition.Run();

        Assert.NotNull(result.Spec.SecretReferences);
        Assert.Empty(result.Spec.SecretReferences!);
        Assert.Contains("\"secretReferences\":[]", result.SpecJson, StringComparison.Ordinal);
    }

    [Fact]
    public void One_capture_never_leaks_into_the_next_run()
    {
        // One runner, two definitions: the worker executes many runs in one process, so the capture must be
        // drained per definition instead of accumulating.
        var capture = new SecretReferenceCapture();
        var definitions = new PipelineDefinitionRunner(
            PipelineDefinitionRunner.CreateRuntime(new LuaLogSink(), capture),
            capture);

        using var first = new DefinitionRun(
            """pipeline { name = "first", steps = { { name = "S", run = { "x", secret("API_KEY") } } } }""");
        using var second = new DefinitionRun(
            """pipeline { name = "second", steps = { { name = "S", run = { "x" } } } }""");

        Assert.Equal(["API_KEY"], first.Run(definitions).Spec.SecretReferences!);
        Assert.Empty(second.Run(definitions).Spec.SecretReferences!);
    }

    [Fact]
    public void A_spec_frozen_before_capture_reads_as_null_and_is_accepted()
    {
        // TC-003's storage half: an older frozen spec has no field at all. It must stay readable (the run
        // page renders it) while being distinguishable from a captured empty list.
        var legacy = PipelineSpecJson.Serialize(new PipelineSpec(
            PipelineSpecSchema.Version,
            "legacy",
            [new PipelineStep("Build", Run: ["dotnet", "build"])]));

        Assert.DoesNotContain("secretReferences", legacy, StringComparison.Ordinal);
        Assert.True(PipelineSpecJson.TryRead(legacy, out var spec, out var error), error);
        Assert.Null(spec!.SecretReferences);
    }

    [Fact]
    public void The_reference_list_is_normalised_rather_than_refused()
    {
        // Dirty metadata must never fail a run: duplicates collapse, and entries no stored secret could have
        // (a bad shape, or a name over the 64-character bound) are dropped by the reader instead.
        var dirty = PipelineSpecJson.Serialize(new PipelineSpec(
            PipelineSpecSchema.Version,
            "envelope",
            [new PipelineStep("Build", Run: ["dotnet", "build"])],
            SecretReferences:
            [
                "OK_NAME",
                "OK_NAME",
                "not a name",
                new string('A', PipelineSpecSchema.MaxSecretNameLength + 1),
            ]));

        Assert.True(PipelineSpecJson.TryRead(dirty, out var spec, out var error), error);
        Assert.Equal(["OK_NAME"], spec!.SecretReferences!);
    }

    [Fact]
    public void A_long_environment_variable_name_is_still_legal()
    {
        // Regression guard for the one behaviour this card must not touch: the 64-character bound belongs to
        // recorded secret names, not to env(). A long env name is still a valid reference, still expanded,
        // and still accepted by the definition phase.
        var longName = new string('E', PipelineSpecSchema.MaxSecretNameLength + 8);

        Assert.True(StepReference.IsValidName(longName));
        Assert.False(PipelineSpecSchema.IsSecretReferenceName(longName));
        Assert.Equal(
            "value",
            StepValueResolver.ResolveEnvironment(
                $"${{env:{longName}}}",
                new Dictionary<string, string?>(StringComparer.Ordinal) { [longName] = "value" }));

        using var definition = new DefinitionRun(
            $$"""
            pipeline {
              name = "long-env",
              env = { {{longName}} = "v" },
              steps = { { name = "Build", run = { "dotnet", "build", env("{{longName}}") } } },
            }
            """);

        var result = definition.Run();

        // The env reference is recorded nowhere (it is not a secret) and the definition still succeeded.
        Assert.Empty(result.Spec.SecretReferences!);
        Assert.Contains($"${{env:{longName}}}", result.SpecJson, StringComparison.Ordinal);
    }

    /// <summary>One definition run in a throwaway repository directory (no git, no process).</summary>
    private sealed class DefinitionRun : IDisposable
    {
        private readonly string _root;
        private readonly RunWorkDirectory _work;

        public DefinitionRun(string source, string pipelinePath = "pipeline.lua")
        {
            _root = Path.Combine(Path.GetTempPath(), $"zenkins-d006-def-{Guid.NewGuid():N}");
            var repository = Path.Combine(_root, "repository");
            Directory.CreateDirectory(repository);
            var target = Path.Combine(repository, pipelinePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, source);

            _work = new RunWorkDirectory(
                Guid.NewGuid(), _root, repository, Path.Combine(_root, "artifacts"), Path.Combine(_root, "temp"));
            PipelinePath = pipelinePath;
        }

        public string PipelinePath { get; }

        public PipelineDefinitionResult Run() =>
            Run(new PipelineDefinitionRunner(
                PipelineDefinitionRunner.CreateRuntime(new LuaLogSink())));

        /// <summary>Runs this definition through a caller-supplied runner (shared runtime/capture).</summary>
        public PipelineDefinitionResult Run(PipelineDefinitionRunner definitions) =>
            definitions.Run(
                _work,
                PipelinePath,
                DateTimeOffset.UtcNow.AddSeconds(20),
                CancellationToken);

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
}
