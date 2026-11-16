using System.Text.Json;
using Xunit;
using Zenkins.Server.Projects;
using Zenkins.Server.Runs;
using Zenkins.Shared.Projects;

namespace Zenkins.Tests.Projects;

/// <summary>
/// Card F-021's parameter catalog: the definitions the editor's drafts become, and the resolution of a
/// trigger's supplied values against them. This is the single authority for both - the trigger form
/// renders what <see cref="ProjectParameters.Validate"/> accepted and the run snapshot is exactly what
/// <see cref="ProjectParameters.ResolveValues"/> returned, so the rules are tested here rather than
/// trusted to any caller.
/// </summary>
[Trait("Category", "Projects")]
public sealed class ProjectParametersTests
{
    [Fact]
    public void A_duplicate_parameter_name_is_refused()
    {
        var json = Build(
            Draft("GREETING", ProjectParameterTypes.String),
            Draft("GREETING", ProjectParameterTypes.String));

        Assert.Null(json);
        Assert.Contains("declared more than once", Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stored_json_is_the_definition_list_the_trigger_form_renders()
    {
        var json = Build(
            Draft("GREETING", ProjectParameterTypes.String, defaultValue: "hello", description: "what to say"),
            Draft("NOTES", ProjectParameterTypes.Text),
            Draft("DRY_RUN", ProjectParameterTypes.Boolean),
            Draft("MODE", ProjectParameterTypes.Choice, choices: "fast, slow"));

        Assert.NotNull(json);
        var definitions = JsonSerializer.Deserialize(
            json!, ProjectParameterJsonContext.Default.ProjectParameterArray);
        Assert.NotNull(definitions);
        Assert.Equal(["GREETING", "NOTES", "DRY_RUN", "MODE"], definitions!.Select(parameter => parameter.Name));
        Assert.Equal("hello", definitions[0].DefaultValue);
        Assert.Equal("what to say", definitions[0].Description);
        Assert.Equal(["fast", "slow"], definitions[3].Choices!);
        Assert.Null(definitions[2].DefaultValue);
    }

    [Theory]
    [InlineData("1LEADING_DIGIT", "must be an environment-variable-shaped name")]
    [InlineData("HAS-DASH", "must be an environment-variable-shaped name")]
    [InlineData("HAS SPACE", "must be an environment-variable-shaped name")]
    public void A_parameter_name_must_be_environment_variable_shaped(string name, string reason)
    {
        Assert.Null(Build(Draft(name, ProjectParameterTypes.String)));
        Assert.Contains(reason, Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_overlong_name_is_refused_with_the_bound()
    {
        var name = new string('A', ProjectParameterLimits.MaxNameLength + 1);
        Assert.Null(Build(Draft(name, ProjectParameterTypes.String)));
        Assert.Contains($"at most {ProjectParameterLimits.MaxNameLength} characters", Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_type_is_refused()
    {
        Assert.Null(Build(Draft("VALUE", "number")));
        Assert.Contains("type must be one of", Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_choice_parameter_needs_choices_and_a_default_that_is_one_of_them()
    {
        Assert.Null(Build(Draft("MODE", ProjectParameterTypes.Choice, choices: " ")));
        Assert.Contains("at least one value", Error, StringComparison.Ordinal);

        Assert.Null(Build(Draft("MODE", ProjectParameterTypes.Choice, choices: "fast,slow", defaultValue: "turbo")));
        Assert.Contains("must be one of the declared choices", Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_boolean_default_must_be_true_or_false()
    {
        Assert.Null(Build(Draft("DRY_RUN", ProjectParameterTypes.Boolean, defaultValue: "yes")));
        Assert.Contains("must be true or false", Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_default_over_its_type_bound_is_refused()
    {
        var tooLong = new string('x', ProjectParameterLimits.MaxStringValueLength + 1);
        Assert.Null(Build(Draft("VALUE", ProjectParameterTypes.String, defaultValue: tooLong)));
        Assert.Contains("must be at most", Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_list_clears_the_definitions_and_is_not_an_error()
    {
        var json = ProjectParameters.BuildDefinitionsJson(new ProjectParametersInput("[]"), out var error);

        Assert.Null(json);
        Assert.Null(error);
    }

    [Fact]
    public void A_payload_that_is_not_the_editors_json_is_refused()
    {
        var json = ProjectParameters.BuildDefinitionsJson(new ProjectParametersInput("not json"), out var error);

        Assert.Null(json);
        Assert.Contains("not valid JSON for the parameters editor", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Values_supplied_by_the_trigger_win_and_missing_ones_resolve_to_defaults()
    {
        var definitions = Validate(
            Draft("GREETING", ProjectParameterTypes.String, defaultValue: "hello"),
            Draft("NOTES", ProjectParameterTypes.Text),
            Draft("DRY_RUN", ProjectParameterTypes.Boolean),
            Draft("MODE", ProjectParameterTypes.Choice, choices: "fast,slow", defaultValue: "slow"));

        var values = ProjectParameters.ResolveValues(
            definitions,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GREETING"] = "hi there",
                ["DRY_RUN"] = "True",
                ["MODE"] = "fast",
            },
            out var error);

        Assert.Null(error);
        Assert.NotNull(values);
        Assert.Equal("hi there", values!["GREETING"]);
        Assert.Equal("true", values["DRY_RUN"]);
        Assert.Equal("fast", values["MODE"]);
        Assert.Equal(string.Empty, values["NOTES"]);
    }

    [Fact]
    public void A_headless_trigger_resolves_every_neutral_default()
    {
        var definitions = Validate(
            Draft("VALUE", ProjectParameterTypes.String),
            Draft("DRY_RUN", ProjectParameterTypes.Boolean),
            Draft("MODE", ProjectParameterTypes.Choice, choices: "fast,slow"));

        var values = ProjectParameters.ResolveValues(definitions, provided: null, out var error);

        Assert.Null(error);
        Assert.NotNull(values);
        Assert.Equal(string.Empty, values!["VALUE"]);
        Assert.Equal("false", values["DRY_RUN"]);
        Assert.Equal("fast", values["MODE"]);
    }

    [Fact]
    public void An_unknown_parameter_name_is_refused_with_the_stable_code()
    {
        var definitions = Validate(Draft("KNOWN", ProjectParameterTypes.String));

        var values = ProjectParameters.ResolveValues(
            definitions,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["UNKNOWN"] = "x" },
            out var error);

        Assert.Null(values);
        Assert.Equal(RunErrorCodes.ParameterUnknown, error);
    }

    [Fact]
    public void A_value_that_breaks_its_type_is_refused_with_the_stable_code()
    {
        var definitions = Validate(
            Draft("VALUE", ProjectParameterTypes.Boolean),
            Draft("MODE", ProjectParameterTypes.Choice, choices: "fast,slow"));

        var values = ProjectParameters.ResolveValues(
            definitions,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["VALUE"] = "yes" },
            out var error);

        Assert.Null(values);
        Assert.Equal(RunErrorCodes.ParameterInvalid, error);
    }

    [Fact]
    public void A_choice_value_must_be_one_of_the_declared_choices()
    {
        var definitions = Validate(Draft("MODE", ProjectParameterTypes.Choice, choices: "fast,slow"));

        var values = ProjectParameters.ResolveValues(
            definitions,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["MODE"] = "turbo" },
            out var error);

        Assert.Null(values);
        Assert.Equal(RunErrorCodes.ParameterInvalid, error);
    }

    [Fact]
    public void A_value_over_its_type_bound_is_refused()
    {
        var definitions = Validate(
            Draft("VALUE", ProjectParameterTypes.String),
            Draft("NOTES", ProjectParameterTypes.Text));

        var tooLong = new string('x', ProjectParameterLimits.MaxStringValueLength + 1);
        Assert.Null(ProjectParameters.ResolveValues(
            definitions,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["VALUE"] = tooLong },
            out var stringError));
        Assert.Equal(RunErrorCodes.ParameterInvalid, stringError);

        var tooLongText = new string('x', ProjectParameterLimits.MaxTextValueLength + 1);
        Assert.Null(ProjectParameters.ResolveValues(
            definitions,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["NOTES"] = tooLongText },
            out var textError));
        Assert.Equal(RunErrorCodes.ParameterInvalid, textError);
    }

    [Fact]
    public void A_choice_definition_with_no_choices_resolves_instead_of_throwing()
    {
        // Validate refuses an empty choice list, so this is a row corrupted after the fact: a trigger
        // must still resolve (to the empty string), never throw inside the request.
        var corrupted = new[]
        {
            new ProjectParameter("MODE", ProjectParameterTypes.Choice, DefaultValue: null, Choices: []),
        };

        var values = ProjectParameters.ResolveValues(corrupted, provided: null, out var error);

        Assert.Null(error);
        Assert.NotNull(values);
        Assert.Equal(string.Empty, values!["MODE"]);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string? Error;

    private static ProjectParameterDraft Draft(
        string name,
        string type,
        string? defaultValue = null,
        string? choices = null,
        string? description = null) =>
        new(name, type, defaultValue, choices, description);

    private static string? Build(params ProjectParameterDraft[] drafts) =>
        ProjectParameters.BuildDefinitionsJson(
            new ProjectParametersInput(JsonSerializer.Serialize(drafts, ProjectParameters.DraftsOptions)),
            out Error);

    private static ProjectParameter[] Validate(params ProjectParameterDraft[] drafts)
    {
        var definitions = ProjectParameters.Validate(drafts, out var error);
        Assert.Null(error);
        return definitions;
    }
}
