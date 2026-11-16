using System.Net;
using System.Text.Json;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Workers;

namespace Zenkins.Tests.Runs;

/// <summary>
/// Card F-021 on the trigger path: the parameters editor saves definitions, the start-run form posts a
/// value per definition, the service resolves them (supplied, else default, else the type's neutral
/// default), the run freezes the resolved values and the claim carries the same snapshot to the worker.
/// Both kinds share this path - a Pipeline project's parameters are tested beside a Freestyle one.
/// </summary>
[Trait("Category", "Runs")]
public sealed class ParameterizedRunTests
{
    [Fact]
    public async Task The_trigger_snapshots_the_supplied_values_and_the_claim_carries_them()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("parameterized-pipeline");
        Assert.True(await SaveParametersAsync(owner, projectId, Definitions()));

        var runId = await RunTestHost.TriggerRunAsync(
            owner,
            projectId,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GREETING"] = "hi there",
                ["DRY_RUN"] = "true",
                ["MODE"] = "slow",
            });

        // The run row is the snapshot: supplied values win, an untouched text parameter resolves to
        // the empty string - the type's neutral default.
        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Queued, run.Status);
        var values = ReadParameters(run.ParametersJson);
        Assert.Equal("hi there", values["GREETING"]);
        Assert.Equal("true", values["DRY_RUN"]);
        Assert.Equal("slow", values["MODE"]);
        Assert.Equal(string.Empty, values["NOTES"]);

        // The same snapshot travels to the worker; a Pipeline run's spec still comes from its
        // definition phase, so the parameters are the only new claim member it sees.
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "parameter-agent");
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        var claim = await ReadClaimAsync(claimed);
        Assert.Equal(runId, claim.RunId);
        Assert.NotNull(claim.Parameters);
        Assert.Equal("hi there", claim.Parameters!["GREETING"]);
        Assert.Equal("true", claim.Parameters["DRY_RUN"]);
        Assert.Equal("slow", claim.Parameters["MODE"]);
        Assert.Null(claim.PipelineSpecJson);
    }

    [Fact]
    public async Task A_trigger_without_inputs_resolves_every_default()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("defaulted");
        Assert.True(await SaveParametersAsync(owner, projectId, Definitions()));

        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);

        var values = ReadParameters((await host.ReadRunAsync(runId)).ParametersJson);
        Assert.Equal("hello", values["GREETING"]);
        Assert.Equal("false", values["DRY_RUN"]);
        Assert.Equal("slow", values["MODE"]);
        Assert.Equal(string.Empty, values["NOTES"]);
    }

    [Fact]
    public async Task A_refused_definition_list_lands_on_the_parameters_tab_with_its_message()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("refused-parameters");
        Assert.True(await SaveParametersAsync(owner, projectId, Definitions()));

        // A duplicate name is refused by the catalog; the previously saved definitions stay.
        var duplicate = JsonSerializer.Serialize(new object[]
        {
            new { name = "GREETING", type = "string", defaultValue = "", choicesText = "", description = "" },
            new { name = "GREETING", type = "text", defaultValue = "", choicesText = "", description = "" },
        });
        var refused = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/parameters",
            $"/projects/{projectId}/parameters",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ParametersJson"] = duplicate });

        Assert.Equal((HttpStatusCode)302, refused.StatusCode);

        // F-021's PRG target is the tab that owns the editor, so the code is rendered where the user
        // submitted (the overview reads neither `error` nor this notice).
        Assert.Contains(
            $"/projects/{projectId}/parameters?error=parameters-invalid",
            refused.Headers.Location!.OriginalString,
            StringComparison.Ordinal);
        var page = await AuthTestHost.GetStringAsync(
            owner,
            $"/projects/{projectId}/parameters?error=parameters-invalid");
        Assert.Contains("The parameter list was refused", page, StringComparison.Ordinal);

        // Nothing was replaced: an untouched trigger still resolves the saved defaults.
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        Assert.Equal("hello", ReadParameters((await host.ReadRunAsync(runId)).ParametersJson)["GREETING"]);
    }

    [Fact]
    public async Task A_project_without_parameters_carries_none()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("no-parameters");

        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);

        Assert.Null((await host.ReadRunAsync(runId)).ParametersJson);
    }

    [Fact]
    public async Task An_unknown_parameter_name_is_refused_and_queues_nothing()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("unknown-parameter");
        Assert.True(await SaveParametersAsync(owner, projectId, Definitions()));

        var response = await RunTestHost.StartRunAsync(
            owner,
            projectId,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal) { ["UNDECLARED"] = "x" });

        Assert.Equal((HttpStatusCode)302, response.StatusCode);
        Assert.Contains($"runError={RunErrorCodes.ParameterUnknown}", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Empty(await host.ReadRunsAsync(await host.OrganizationIdAsync()));
    }

    [Theory]
    [InlineData("DRY_RUN", "yes")]
    [InlineData("MODE", "turbo")]
    public async Task A_value_that_breaks_its_type_is_refused(string name, string value)
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("mistyped-parameter");
        Assert.True(await SaveParametersAsync(owner, projectId, Definitions()));

        var response = await RunTestHost.StartRunAsync(
            owner,
            projectId,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal) { [name] = value });

        Assert.Equal((HttpStatusCode)302, response.StatusCode);
        Assert.Contains($"runError={RunErrorCodes.ParameterInvalid}", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Empty(await host.ReadRunsAsync(await host.OrganizationIdAsync()));
    }

    [Fact]
    public async Task Editing_the_definitions_after_triggering_does_not_move_the_queued_run()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("frozen-values");
        Assert.True(await SaveParametersAsync(owner, projectId, Definitions()));

        var runId = await RunTestHost.TriggerRunAsync(
            owner,
            projectId,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal) { ["GREETING"] = "first" });

        // Redefine the parameter (and its default) after the run exists, then trigger a second run
        // that picks the new definition up. The first run's snapshot is untouched.
        Assert.True(await SaveParametersAsync(owner, projectId, Definitions(defaultGreeting: "changed")));
        var secondRunId = await RunTestHost.TriggerRunAsync(owner, projectId);

        Assert.Equal("first", ReadParameters((await host.ReadRunAsync(runId)).ParametersJson)["GREETING"]);
        Assert.Equal("changed", ReadParameters((await host.ReadRunAsync(secondRunId)).ParametersJson)["GREETING"]);
    }

    [Fact]
    public async Task A_freestyle_project_gets_the_same_trigger_parameters()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await CreateFreestyleProjectAsync(owner, "parameterized-freestyle");
        Assert.True(await SaveStepsAsync(owner, projectId));
        Assert.True(await SaveParametersAsync(owner, projectId, Definitions()));

        var runId = await RunTestHost.TriggerRunAsync(
            owner,
            projectId,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal) { ["GREETING"] = "hi" });

        var run = await host.ReadRunAsync(runId);
        Assert.NotNull(run.PipelineSpecJson);
        Assert.Equal("hi", ReadParameters(run.ParametersJson)["GREETING"]);
    }

    [Fact]
    public async Task The_project_page_renders_one_input_per_definition()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("parameter-form");
        Assert.True(await SaveParametersAsync(owner, projectId, Definitions()));

        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}");

        Assert.Contains("Parameters[GREETING]", page, StringComparison.Ordinal);
        Assert.Contains("Parameters[NOTES]", page, StringComparison.Ordinal);
        Assert.Contains("Parameters[DRY_RUN]", page, StringComparison.Ordinal);
        Assert.Contains("Parameters[MODE]", page, StringComparison.Ordinal);

        // The boolean is a radio pair (always posts a value), and the choice offers both options with
        // the default selected.
        Assert.Contains("value=\"true\"", page, StringComparison.Ordinal);
        Assert.Contains("value=\"false\"", page, StringComparison.Ordinal);
        Assert.Contains("value=\"fast\"", page, StringComparison.Ordinal);
        Assert.Contains("selected", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_run_page_shows_the_frozen_parameter_snapshot()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("parameter-run-page");

        // A value with a character that must survive the render, and a definition edit afterwards
        // that must not move the card.
        Assert.True(await SaveParametersAsync(owner, projectId, Definitions()));
        var runId = await RunTestHost.TriggerRunAsync(
            owner,
            projectId,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal) { ["GREETING"] = "snapshot <kept>" });
        Assert.True(await SaveParametersAsync(owner, projectId, Definitions(defaultGreeting: "later")));

        var page = await AuthTestHost.GetStringAsync(owner, $"/runs/{runId}");

        Assert.Contains("run-parameter-GREETING", page, StringComparison.Ordinal);
        Assert.Contains("snapshot &lt;kept&gt;", page, StringComparison.Ordinal);
        Assert.DoesNotContain(">later<", page, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// The definitions through the editor's real endpoint (a JSON draft list, exactly what the page
    /// serializes). The PRG redirect's notice distinguishes success from a refused payload.
    /// </summary>
    private static async Task<bool> SaveParametersAsync(HttpClient owner, Guid projectId, string definitionsJson)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}",
            $"/projects/{projectId}/parameters",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ParametersJson"] = definitionsJson,
            });
        return response.StatusCode is HttpStatusCode.Found or HttpStatusCode.RedirectKeepVerb
            && response.Headers.Location!.OriginalString.Contains("notice=parameters-saved", StringComparison.Ordinal);
    }

    private static string Definitions(string defaultGreeting = "hello") => JsonSerializer.Serialize(
        new object[]
        {
            new { name = "GREETING", type = "string", defaultValue = defaultGreeting, choicesText = "", description = "what to say" },
            new { name = "NOTES", type = "text", defaultValue = "", choicesText = "", description = "" },
            new { name = "DRY_RUN", type = "boolean", defaultValue = "false", choicesText = "", description = "" },
            new { name = "MODE", type = "choice", defaultValue = "slow", choicesText = "fast,slow", description = "" },
        });

    private static async Task<Guid> CreateFreestyleProjectAsync(HttpClient owner, string name)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            owner,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = name,
                ["Kind"] = ProjectKinds.Freestyle,
            });
        Assert.True((int)response.StatusCode is 302 or 303);
        return Guid.Parse(response.Headers.Location!.OriginalString["/projects/".Length..].Split('?')[0]);
    }

    private static async Task<bool> SaveStepsAsync(HttpClient owner, Guid projectId)
    {
        var stepsJson = JsonSerializer.Serialize(new object[]
        {
            new { name = "Greet", script = "echo hello", envLines = "", continueOnError = false, artifactsText = "" },
        });
        var response = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}",
            $"/projects/{projectId}/freestyle/steps",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["StepsJson"] = stepsJson });
        return response.StatusCode is HttpStatusCode.Found or HttpStatusCode.RedirectKeepVerb;
    }

    private static IReadOnlyDictionary<string, string> ReadParameters(string? json)
    {
        Assert.NotNull(json);
        return JsonSerializer.Deserialize(json!, ProjectParameterJsonContext.Default.DictionaryStringString)!;
    }

    private sealed record ClaimPayload(Guid RunId, string? PipelineSpecJson, IReadOnlyDictionary<string, string>? Parameters);

    private static async Task<ClaimPayload> ReadClaimAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Dictionary<string, string>? parameters = null;
        if (root.TryGetProperty("parameters", out var parametersElement)
            && parametersElement.ValueKind == JsonValueKind.Object)
        {
            parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in parametersElement.EnumerateObject())
            {
                parameters[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return new ClaimPayload(
            root.GetProperty("runId").GetGuid(),
            root.TryGetProperty("pipelineSpecJson", out var specJson) && specJson.ValueKind == JsonValueKind.String
                ? specJson.GetString()
                : null,
            parameters);
    }
}
