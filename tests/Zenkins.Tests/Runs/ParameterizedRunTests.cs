using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    public async Task A_repeated_choice_entry_is_refused_at_save_and_no_duplicate_option_renders()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("duplicate-choices");
        var savedDefinitionsJson = Definitions();
        Assert.True(await SaveParametersAsync(owner, projectId, savedDefinitionsJson));
        var storedBeforeRefusal = (await host.ReadProjectAsync(projectId)).ParametersJson;
        Assert.NotNull(storedBeforeRefusal);

        // TC-001's payload: the editor's own draft list for `fast,fast`, posted to the real endpoint.
        var duplicateChoices = JsonSerializer.Serialize(new object[]
        {
            new { name = "MODE", type = "choice", defaultValue = "fast", choicesText = "fast,fast", description = "" },
        });
        var refused = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}",
            $"/projects/{projectId}/parameters",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ParametersJson"] = duplicateChoices });

        // Refused with a stable code, on the tab that owns the editor (F-021's PRG target).
        Assert.Equal((HttpStatusCode)302, refused.StatusCode);
        Assert.Contains(
            $"/projects/{projectId}/parameters?error=parameters-invalid",
            refused.Headers.Location!.OriginalString,
            StringComparison.Ordinal);
        var refusalPage = await AuthTestHost.GetStringAsync(
            owner,
            $"/projects/{projectId}/parameters?error=parameters-invalid");
        Assert.Contains("The parameter list was refused", refusalPage, StringComparison.Ordinal);

        // Nothing was replaced: the stored definitions are byte for byte the ones saved above (the
        // refusal happens before the write, so not even a re-serialization moved them).
        var after = (await host.ReadProjectAsync(projectId)).ParametersJson;
        Assert.Equal(storedBeforeRefusal, after);
        Assert.Equal(
            ["GREETING", "NOTES", "DRY_RUN", "MODE"],
            JsonSerializer.Deserialize(after!, ProjectParameterJsonContext.Default.ProjectParameterArray)!
                .Select(parameter => parameter.Name));

        // And the form a person reads renders one option per declared value - the duplicate can never
        // reach the markup, which is the half of TC-001 a stored list alone would not prove.
        var trigger = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}");
        var mode = Between(trigger, "select", "run-param-MODE", "</select>");
        Assert.Equal(2, Regex.Matches(mode, "<option", RegexOptions.CultureInvariant).Count);
        Assert.Single(Regex.Matches(mode, "value=\"fast\"", RegexOptions.CultureInvariant));

        // The refusal cost nothing: a trigger still resolves `fast`, so the fact pins a refused edit
        // rather than a broken definition list.
        var runId = await RunTestHost.TriggerRunAsync(
            owner,
            projectId,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal) { ["MODE"] = "fast" });
        Assert.Equal("fast", ReadParameters((await host.ReadRunAsync(runId)).ParametersJson)["MODE"]);
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

    [Fact]
    public async Task The_trigger_form_prefills_every_parameter_input_with_its_definitions_default()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("parameter-prefill");
        Assert.True(await SaveParametersAsync(owner, projectId, PrefillDefinitions()));

        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}");

        // A single-line string: the input's value is the definition's default, not an empty box with a
        // legal field name (the shape this fact exists to be distinguishable from).
        var greeting = NormalizeWhitespace(Element(page, "input", "run-param-GREETING"));
        Assert.Contains("name=\"Parameters[GREETING]\"", greeting, StringComparison.Ordinal);
        Assert.Contains("value=\"prefilled-hello\"", greeting, StringComparison.Ordinal);

        // A multi-line text: the textarea's own content is the default - a two-line one, so "prefilled"
        // and "empty" cannot be confused, and the newline survives the render (the markup carries it
        // escaped, as HTML requires, so the content is decoded the way a browser decodes it).
        Assert.Equal(
            "prefilled notes\nline two",
            WebUtility.HtmlDecode(Between(page, "textarea", "run-param-NOTES", "</textarea>")));

        // A boolean is a radio pair (an unchecked checkbox posts nothing at all): the default's radio is
        // the checked one, so an untouched submit posts the default instead of omitting the field.
        var dryRun = NormalizeWhitespace(Between(page, "div", "run-param-DRY_RUN", "</div>"));
        Assert.Contains("value=\"true\" checked", dryRun, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"false\" checked", dryRun, StringComparison.Ordinal);

        // A choice: the default's option is the selected one (not merely that some option is selected).
        var mode = NormalizeWhitespace(Between(page, "select", "run-param-MODE", "</select>"));
        Assert.Contains("value=\"slow\" selected", mode, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"fast\" selected", mode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_out_of_bounds_value_is_refused_at_the_trigger_endpoint()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("parameter-bounds");
        Assert.True(await SaveParametersAsync(owner, projectId, Definitions()));

        // One character over the string bound: the trigger, not only the unit-level resolver, is where
        // this has to be refused - a hand-built POST never goes through the form's own inputs.
        var overBound = new string('x', ProjectParameterLimits.MaxStringValueLength + 1);
        var response = await RunTestHost.StartRunAsync(
            owner,
            projectId,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal) { ["GREETING"] = overBound });

        Assert.Equal((HttpStatusCode)302, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.Contains($"runError={RunErrorCodes.ParameterInvalid}", location, StringComparison.Ordinal);

        // No run exists: the refusal happens before the row, so nothing is queued for a worker to claim.
        Assert.Empty(await host.ReadRunsAsync(await host.OrganizationIdAsync()));

        // And the trigger page renders the refusal instead of dropping it on the redirect.
        var page = await AuthTestHost.GetStringAsync(owner, location);
        Assert.Contains("id=\"run-form-error\"", page, StringComparison.Ordinal);

        // The bound is two-sided: a value exactly at it is accepted and snapshotted, so the fact pins
        // the edge rather than "long values are always refused".
        var atBound = new string('y', ProjectParameterLimits.MaxStringValueLength);
        var runId = await RunTestHost.TriggerRunAsync(
            owner,
            projectId,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal) { ["GREETING"] = atBound });
        Assert.Equal(atBound, ReadParameters((await host.ReadRunAsync(runId)).ParametersJson)["GREETING"]);
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

    /// <summary>
    /// The definitions the prefill fact renders (Q-012): every default is a value an empty input could
    /// not produce, and the text default has two lines so a whitespace-only render cannot pass.
    /// </summary>
    private static string PrefillDefinitions() => JsonSerializer.Serialize(
        new object[]
        {
            new { name = "GREETING", type = "string", defaultValue = "prefilled-hello", choicesText = "", description = "" },
            new { name = "NOTES", type = "text", defaultValue = "prefilled notes\nline two", choicesText = "", description = "" },
            new { name = "DRY_RUN", type = "boolean", defaultValue = "true", choicesText = "", description = "" },
            new { name = "MODE", type = "choice", defaultValue = "slow", choicesText = "fast,slow", description = "" },
        });

    /// <summary>The opening tag of the element whose <c>id</c> is <paramref name="id"/>.</summary>
    private static string Element(string page, string tag, string id)
    {
        var match = Regex.Match(
            page,
            $"<{tag}\\b[^>]*id=\"{Regex.Escape(id)}\"[^>]*>",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"the page does not render a <{tag}> with id=\"{id}\"");
        return match.Value;
    }

    /// <summary>The content between an element's opening tag and <paramref name="closing"/>.</summary>
    private static string Between(string page, string tag, string id, string closing)
    {
        var open = Element(page, tag, id);
        var start = page.IndexOf(open, StringComparison.Ordinal) + open.Length;
        var end = page.IndexOf(closing, start, StringComparison.Ordinal);
        Assert.True(end > start, $"<{tag} id=\"{id}\"> is not closed by '{closing}'");
        return page[start..end];
    }

    /// <summary>Collapses the renderer's indentation so attribute order is what is asserted, not layout.</summary>
    private static string NormalizeWhitespace(string markup) => Regex.Replace(markup, "\\s+", " ");

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
