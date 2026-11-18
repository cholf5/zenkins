using Xunit;
using Zenkins.Server.Projects;
using Zenkins.Shared.Pipelines;

namespace Zenkins.Tests.Projects;

/// <summary>
/// Card F-020's server-side steps builder: the drafts the editor posts become the frozen spec JSON
/// only through <see cref="FreestyleSteps.BuildSpecJson"/> - the env/artifact text shapes are parsed
/// here, the secret references are captured here (the same scanner the Worker substitutes with), and
/// the envelope check has the final word. The stored JSON is exactly what a run will execute.
/// </summary>
[Trait("Category", "Projects")]
public sealed class FreestyleStepsTests
{
    [Fact]
    public void Drafts_become_an_envelope_valid_v2_spec_with_the_project_name()
    {
        var json = Build(
            projectName: "nightly-index",
            drafts: [Step("Hello", "echo hello")]);

        Assert.NotNull(json);
        Assert.True(PipelineSpecJson.TryRead(json, out var spec, out var error), error);
        Assert.NotNull(spec);
        Assert.Equal(PipelineSpecSchema.Version, spec!.Version);
        Assert.Equal("nightly-index", spec.Name);
        Assert.Single(spec.Steps);
        Assert.Equal("Hello", spec.Steps[0].Name);
        Assert.Equal("echo hello", spec.Steps[0].Script);
        Assert.True(spec.Steps[0].IsScript);
        Assert.Null(spec.Steps[0].Run);
        Assert.Null(spec.Steps[0].Shell);
    }

    [Fact]
    public void Env_lines_and_artifact_text_are_parsed_server_side()
    {
        var json = Build(
            "indexer",
            [
                new FreestyleStepDraft(
                    Name: "Index",
                    Script: "python3 index.py",
                    EnvLines: "# a comment\n\n  INDEX_ROOT=/srv/index  \nDRY_RUN=1",
                    ContinueOnError: false,
                    ArtifactsText: " out/reports , ,out/logs "),
            ]);

        Assert.NotNull(json);
        Assert.True(PipelineSpecJson.TryRead(json, out var spec, out var error), error);
        Assert.NotNull(spec);
        Assert.Equal("/srv/index", spec!.Steps[0].Env!["INDEX_ROOT"]);
        Assert.Equal("1", spec.Steps[0].Env!["DRY_RUN"]);
        Assert.Equal(["out/reports", "out/logs"], spec.Steps[0].Artifacts!);
    }

    [Fact]
    public void Secret_names_in_scripts_and_env_values_are_captured_into_the_spec()
    {
        var json = Build(
            "publisher",
            [
                Step("Upload", "echo ${secret:FEED_TOKEN}"),
                new FreestyleStepDraft(
                    Name: "Sign",
                    Script: "python3 sign.py",
                    EnvLines: "API_KEY=${secret:SIGNING_KEY}\nPLAIN=${env:PATH}",
                    ContinueOnError: false,
                    ArtifactsText: null),
            ]);

        Assert.NotNull(json);
        Assert.True(PipelineSpecJson.TryRead(json, out var spec, out var error), error);
        Assert.NotNull(spec);

        // Names only, never values (F-012's invariant), normalised and de-duplicated.
        Assert.NotNull(spec!.SecretReferences);
        Assert.Equal(["FEED_TOKEN", "SIGNING_KEY"], spec.SecretReferences);
    }

    [Fact]
    public void The_refused_list_carries_the_envelope_reason()
    {
        var json = Build("nameless", [Step("", "echo hi")]);

        Assert.Null(json);
        Assert.Contains("pipeline.steps[1].name must be a non-empty string", Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_oversized_script_is_refused_with_the_bound()
    {
        var json = Build("big", [Step("Big", new string('x', PipelineSpecSchema.MaxScriptChars + 1))]);

        Assert.Null(json);
        Assert.Contains("must be at most", Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_step_list_is_refused()
    {
        Assert.Null(Build("empty", []));
        Assert.Contains("at least one step", Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Drafts_that_are_not_the_editors_json_are_refused()
    {
        Assert.Null(Build("bad", input: new FreestyleStepsInput("not json at all")));
        Assert.Contains("not valid JSON for the steps editor", Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_payload_is_refused_like_an_empty_list()
    {
        Assert.Null(Build("blank", input: new FreestyleStepsInput(null)));
        Assert.Contains("at least one step", Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_env_line_without_a_separator_is_refused_with_its_line_number()
    {
        var json = Build(
            "typo",
            [new FreestyleStepDraft("Env", "echo", EnvLines: "GOOD=1\nNOSEPARATOR", ContinueOnError: false, ArtifactsText: null)]);

        Assert.Null(json);
        Assert.Contains("pipeline.steps[1].env line 2 must be NAME=value", Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicated_env_name_is_refused_not_silently_overwritten()
    {
        var json = Build(
            "dup",
            [new FreestyleStepDraft("Env", "echo", EnvLines: "K=1\nK=2", ContinueOnError: false, ArtifactsText: null)]);

        Assert.Null(json);
        Assert.Contains("'K' more than once", Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Declared_artifact_paths_that_repeat_are_refused_by_the_envelope()
    {
        var json = Build(
            "dup-artifact",
            [new FreestyleStepDraft("Pack", "tar czf out.tgz bin", EnvLines: null, ContinueOnError: false, ArtifactsText: "dist, dist")]);

        Assert.Null(json);
        Assert.Contains("declared twice", Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_editors_two_numeric_fields_are_parsed_and_bounded_server_side()
    {
        // Card F-032: the step's timeout and attempt count travel as the editor's raw text, are parsed here
        // (never in the browser) and land on the frozen step - blank means "not declared".
        var json = Build(
            "knobs",
            [
                new FreestyleStepDraft(
                    Name: "Flaky",
                    Script: "dotnet test",
                    EnvLines: null,
                    ContinueOnError: false,
                    ArtifactsText: null,
                    TimeoutSeconds: " 600 ",
                    Retry: "2"),
                new FreestyleStepDraft(
                    Name: "Plain",
                    Script: "dotnet build",
                    EnvLines: null,
                    ContinueOnError: false,
                    ArtifactsText: null,
                    TimeoutSeconds: "",
                    Retry: null),
            ]);

        Assert.NotNull(json);
        Assert.True(PipelineSpecJson.TryRead(json, out var spec, out var error), error);
        Assert.Equal(600, spec!.Steps[0].TimeoutSeconds);
        Assert.Equal(2, spec.Steps[0].Retry);
        Assert.Null(spec.Steps[1].TimeoutSeconds);
        Assert.Null(spec.Steps[1].Retry);

        // Out of bounds, not a number, or not whole: refused with the field path the envelope would use.
        foreach (var (timeout, retry, expected) in new (string? Timeout, string? Retry, string Expected)[]
                 {
                     ("0", null, "pipeline.steps[1].timeoutSeconds must be a whole number between 1 and 86400"),
                     ("86401", null, "pipeline.steps[1].timeoutSeconds must be a whole number between 1 and 86400"),
                     ("ten", null, "pipeline.steps[1].timeoutSeconds must be a whole number between 1 and 86400"),
                     (null, "6", "pipeline.steps[1].retry must be a whole number between 1 and 5"),
                     (null, "0", "pipeline.steps[1].retry must be a whole number between 1 and 5"),
                 })
        {
            _ = Build(
                "knobs",
                [
                    new FreestyleStepDraft(
                        Name: "Bad",
                        Script: "dotnet build",
                        EnvLines: null,
                        ContinueOnError: false,
                        ArtifactsText: null,
                        TimeoutSeconds: timeout,
                        Retry: retry),
                ]);
            Assert.Contains(expected, Error ?? string.Empty, StringComparison.Ordinal);
        }
    }

    private static FreestyleStepDraft Step(string name, string script) =>
        new(Name: name, Script: script, EnvLines: null, ContinueOnError: false, ArtifactsText: null);

    private static string? Build(
        string projectName,
        IReadOnlyList<FreestyleStepDraft>? drafts = null,
        FreestyleStepsInput? input = null)
    {
        Error = null;
        var payload = input ?? new FreestyleStepsInput(
            System.Text.Json.JsonSerializer.Serialize(
                drafts ?? [],
                FreestyleSteps.DraftsOptions));
        var json = FreestyleSteps.BuildSpecJson(projectName, payload, out var error);
        Error = error;
        return json;
    }

    private static string? Error { get; set; }
}
