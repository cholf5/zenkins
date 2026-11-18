using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Projects;
using Zenkins.Server.Runs;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Workers;
using Zenkins.Worker;

namespace Zenkins.Tests.Runs;

/// <summary>
/// Card F-041's server half: the four new parameter types under F-021's contract - definitions on the project,
/// values frozen onto the run, injected into every step's environment.
/// <para>
/// Each fact pins the part of its type that <b>cannot be glossed over</b>: a password is masked in the value map
/// and sealed beside it, a run selection is checked against this project's own runs and yields its number plus
/// two convenience variables, and a git ref is a shape-checked value rather than a checkout instruction.
/// </para>
/// </summary>
[Trait("Category", "Run")]
public sealed class ParameterTypeTests
{
    private const string PasswordName = "DEPLOY_PW";
    private const string PasswordValue = "deploy-pw-8c31f7";
    private const string RunName = "PICK";
    private const string GitRefName = "GIT_REF";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------
    // password
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_password_parameter_is_masked_in_the_run_snapshot_and_sealed_beside_it()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("password-param");
        Assert.True(await SaveDefinitionsAsync(owner, projectId, PasswordDefinition()));

        var runId = await RunTestHost.TriggerRunAsync(
            owner,
            projectId,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal) { [PasswordName] = PasswordValue });

        var run = await host.ReadRunAsync(runId);

        // 1. The value map every reader shares carries the mask, never the value.
        Assert.Equal(ProjectParameterTypes.MaskedValue, ReadParameterValues(run.ParametersJson)[PasswordName]);
        Assert.DoesNotContain(PasswordValue, run.ParametersJson!, StringComparison.Ordinal);

        // 2. The run's own JSON cannot be used to recover it: what is stored beside the map is ciphertext.
        Assert.NotNull(run.ProtectedParametersJson);
        Assert.DoesNotContain(PasswordValue, run.ProtectedParametersJson!, StringComparison.Ordinal);

        // 3. The page renders what the row holds, which is the mask.
        var page = await AuthTestHost.GetStringAsync(owner, $"/runs/{runId}");
        Assert.Contains("run-parameter-" + PasswordName, page, StringComparison.Ordinal);
        Assert.Contains(ProjectParameterTypes.MaskedValue, page, StringComparison.Ordinal);
        Assert.DoesNotContain(PasswordValue, page, StringComparison.Ordinal);

        // 4. And the value really is still deliverable: the claim is the one reader that gets it, because the
        //    worker has to hand it to the step.
        var (token, _) = await host.RegisterWorkerAsync();
        using var worker = host.Host.CreateClient();
        using var claim = await RunTestHost.ClaimAsync(worker, token);
        var payload = JsonSerializer.Deserialize(
            await claim.Content.ReadAsStringAsync(CancellationToken),
            WorkerJsonContext.Default.WorkerClaimResponse);

        Assert.NotNull(payload);
        Assert.Equal(PasswordValue, payload!.Parameters![PasswordName]);
        Assert.Equal([PasswordName], payload.PasswordParameters);
    }

    [Fact]
    public async Task A_password_parameter_definition_may_not_declare_a_default()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("password-default");
        var before = (await host.ReadProjectAsync(projectId)).ParametersJson;

        var saved = await SaveDefinitionsAsync(
            owner,
            projectId,
            Drafts(new ProjectParameterDraft(PasswordName, ProjectParameterTypes.Password, "written-down", null, null)));

        // Refused with the editor's own code, and nothing was stored: a default would put the plaintext on the
        // project row, which is exactly what "a per-run input, not a Secret" rules out.
        Assert.False(saved);
        Assert.Equal(before, (await host.ReadProjectAsync(projectId)).ParametersJson);
        Assert.Null(before);
    }

    // ------------------------------------------------------------------
    // run
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_run_parameter_injects_the_number_and_two_convenience_variables()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("run-param");
        var earlierFirst = await SeedRunAsync(host, projectId, number: 1, BuildRunStatus.Success, "abc1234def");
        _ = await SeedRunAsync(host, projectId, number: 2, BuildRunStatus.Failed, "abc9999def");
        Assert.True(await SaveDefinitionsAsync(owner, projectId, RunDefinition()));

        var runId = await RunTestHost.TriggerRunAsync(
            owner,
            projectId,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal) { [RunName] = "1" });

        var (token, _) = await host.RegisterWorkerAsync();
        using var worker = host.Host.CreateClient();
        using var claim = await RunTestHost.ClaimAsync(worker, token);
        var payload = JsonSerializer.Deserialize(
            await claim.Content.ReadAsStringAsync(CancellationToken),
            WorkerJsonContext.Default.WorkerClaimResponse);

        Assert.NotNull(payload);
        Assert.Equal(runId, payload!.RunId);

        // The value is the number a person reads, and the two conveniences name the same run.
        Assert.Equal("1", payload.Parameters![RunName]);
        Assert.Equal(earlierFirst.ToString(), payload.Parameters[RunName + "_RUN_ID"]);
        Assert.Equal("abc1234def", payload.Parameters[RunName + "_COMMIT"]);

        // The page links the selection, so "#1" is navigable rather than a dead number.
        var page = await AuthTestHost.GetStringAsync(owner, $"/runs/{runId}");
        Assert.Contains($"/runs/{earlierFirst}", page, StringComparison.Ordinal);
        Assert.Contains("#1", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_parameter_without_a_value_selects_the_latest_successful_run()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("run-param-default");
        _ = await SeedRunAsync(host, projectId, number: 1, BuildRunStatus.Success, "aaa1111");
        _ = await SeedRunAsync(host, projectId, number: 2, BuildRunStatus.Failed, "bbb2222");
        var latestGood = await SeedRunAsync(host, projectId, number: 3, BuildRunStatus.Success, "ccc3333");
        Assert.True(await SaveDefinitionsAsync(owner, projectId, RunDefinition()));

        // No value at all - the shape a schedule, a poll or a webhook trigger takes.
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);

        var (token, _) = await host.RegisterWorkerAsync();
        using var worker = host.Host.CreateClient();
        using var claim = await RunTestHost.ClaimAsync(worker, token);
        var payload = JsonSerializer.Deserialize(
            await claim.Content.ReadAsStringAsync(CancellationToken),
            WorkerJsonContext.Default.WorkerClaimResponse);

        Assert.NotNull(payload);
        Assert.Equal(runId, payload!.RunId);
        Assert.Equal("3", payload.Parameters![RunName]);
        Assert.Equal(latestGood.ToString(), payload.Parameters[RunName + "_RUN_ID"]);
    }

    [Fact]
    public async Task A_run_parameter_naming_another_projects_run_is_refused()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("run-param-owner");
        var (otherProjectId, _) = await host.CreateProjectAsync("run-param-foreign");

        // #1 exists - but in the other project. A selection is scoped to the project that declares it, so this
        // number names nothing here.
        _ = await SeedRunAsync(host, otherProjectId, number: 1, BuildRunStatus.Success, "ddd4444");
        Assert.True(await SaveDefinitionsAsync(owner, projectId, RunDefinition()));

        using var response = await RunTestHost.StartRunAsync(
            owner,
            projectId,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal) { [RunName] = "1" });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            "/projects/" + projectId + "?runError=" + RunErrorCodes.ParameterInvalid,
            response.Headers.Location!.OriginalString);
        Assert.Empty(await ReadProjectRunsAsync(host, projectId));
    }

    [Fact]
    public async Task A_project_with_no_successful_run_refuses_a_run_parameter_with_its_own_code()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("run-param-empty");
        _ = await SeedRunAsync(host, projectId, number: 1, BuildRunStatus.Failed, "eee5555");
        Assert.True(await SaveDefinitionsAsync(owner, projectId, RunDefinition()));

        using var response = await RunTestHost.StartRunAsync(owner, projectId);

        // Its own code: nothing about the submitted value is wrong - there is simply no previous good run.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "runError=" + RunErrorCodes.ParameterNoRun,
            response.Headers.Location!.OriginalString,
            StringComparison.Ordinal);
        Assert.Single(await ReadProjectRunsAsync(host, projectId));
    }

    // ------------------------------------------------------------------
    // gitRef
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_git_ref_is_validated_at_save_and_at_trigger()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("gitref-param");

        // A leading dash is how a value becomes an option once it reaches argv, so the definition refuses it.
        Assert.False(await SaveDefinitionsAsync(
            owner,
            projectId,
            Drafts(new ProjectParameterDraft(GitRefName, ProjectParameterTypes.GitRef, "-upload-pack=evil", null, null))));
        Assert.Null((await host.ReadProjectAsync(projectId)).ParametersJson);

        // A legal default is accepted, and the trigger refuses a value that is not a ref.
        Assert.True(await SaveDefinitionsAsync(
            owner,
            projectId,
            Drafts(new ProjectParameterDraft(GitRefName, ProjectParameterTypes.GitRef, "v1.2.3", null, null))));

        using var response = await RunTestHost.StartRunAsync(
            owner,
            projectId,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal) { [GitRefName] = "v1 2" });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "runError=" + RunErrorCodes.ParameterInvalid,
            response.Headers.Location!.OriginalString,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_git_ref_parameter_never_moves_the_commit_a_rebuild_pins()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("gitref-authority");
        Assert.True(await SaveDefinitionsAsync(
            owner,
            projectId,
            Drafts(new ProjectParameterEntry(GitRefName, ProjectParameterTypes.GitRef, "v1.0.0"))));

        // A finished run that pinned a commit - the state a webhook push or an earlier rebuild leaves.
        var originalId = await SeedRunAsync(host, projectId, number: 1, BuildRunStatus.Success, "c0ffee13579");

        var rebuilt = await RebuildAsync(
            host,
            owner,
            originalId,
            new Dictionary<string, string>(StringComparer.Ordinal) { [GitRefName] = "v9.9.9" });

        var run = await host.ReadRunAsync(rebuilt);

        // The parameter is what the step reads; the pinned commit is what gets checked out.
        Assert.Equal("v9.9.9", ReadParameterValues(run.ParametersJson)[GitRefName]);
        Assert.Equal("c0ffee13579", run.GitCommit);
    }

    // ------------------------------------------------------------------
    // the trigger form
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_trigger_form_renders_a_control_for_each_new_type()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("param-form");
        _ = await SeedRunAsync(host, projectId, number: 1, BuildRunStatus.Success, "feed1234");
        Assert.True(await SaveDefinitionsAsync(
            owner,
            projectId,
            Drafts(
                new ProjectParameterDraft(PasswordName, ProjectParameterTypes.Password, null, null, null),
                new ProjectParameterDraft(RunName, ProjectParameterTypes.Run, null, null, null),
                new ProjectParameterDraft(GitRefName, ProjectParameterTypes.GitRef, null, null, null))));

        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}");

        // A password is a password input (a browser cannot autofill or reveal it in a plain text box) and the
        // form says what the masking contract is.
        Assert.Contains("name=\"Parameters[" + PasswordName + "]\"", page, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", page, StringComparison.Ordinal);
        Assert.Contains("Not a stored Secret", page, StringComparison.Ordinal);

        // A run parameter is a select over the project's own runs, labelled "#N status branch", with the
        // newest successful one preselected - the same run the headless path resolves to.
        Assert.Contains("id=\"run-param-" + RunName + "\"", page, StringComparison.Ordinal);
        Assert.Contains("value=\"1\"", page, StringComparison.Ordinal);
        Assert.Contains("#1 ", page, StringComparison.Ordinal);

        // A git ref is free text with the shape rule spelled out where it is typed.
        Assert.Contains("placeholder=\"v1.2.3\"", page, StringComparison.Ordinal);
        Assert.Contains("No whitespace, no leading dash", page, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    /// <summary>A project with one parameter of each new type, as the editor would submit it.</summary>
    private static string PasswordDefinition() =>
        Drafts(new ProjectParameterDraft(PasswordName, ProjectParameterTypes.Password, null, null, "deploy password"));

    private static string RunDefinition() =>
        Drafts(new ProjectParameterDraft(RunName, ProjectParameterTypes.Run, null, null, "deploy the previous build"));

    private sealed record ProjectParameterEntry(string Name, string Type, string? Default);

    private static string Drafts(params ProjectParameterDraft[] drafts) =>
        JsonSerializer.Serialize(drafts, ProjectParameters.DraftsOptions);

    private static string Drafts(params ProjectParameterEntry[] drafts) =>
        JsonSerializer.Serialize(
            drafts.Select(draft => new ProjectParameterDraft(draft.Name, draft.Type, draft.Default, null, null)).ToArray(),
            ProjectParameters.DraftsOptions);

    /// <summary>Saves the definitions through the real editor form; false when the editor refused them.</summary>
    private static async Task<bool> SaveDefinitionsAsync(HttpClient owner, Guid projectId, string draftsJson)
    {
        using var response = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}",
            $"/projects/{projectId}/parameters",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ParametersJson"] = draftsJson });

        return (int)response.StatusCode is 302 or 303
            && response.Headers.Location!.OriginalString.Contains("notice=parameters-saved", StringComparison.Ordinal);
    }

    /// <summary>A terminal run of the project, seeded directly (a run that already finished).</summary>
    private static Task<Guid> SeedRunAsync(
        RunTestHost host,
        Guid projectId,
        int number,
        string status,
        string? commit) =>
        host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId)).Single();
            var run = new BuildRun
            {
                OrganizationId = project.OrganizationId,
                RequiredTags = string.Empty,
                ProjectId = projectId,
                TriggerType = BuildRunTriggerType.Manual,
                GitBranch = "main",
                PipelinePath = "pipeline.lua",
                GitCommit = commit,
                Number = number,
                Status = status,
                EndedAt = DateTimeOffset.UtcNow,
            };
            context.BuildRuns.Add(run);
            await context.SaveChangesAsync();
            return run.Id;
        });

    /// <summary>Posts the run page's "Rebuild with parameters…" form; returns the new run's id.</summary>
    private static async Task<Guid> RebuildAsync(
        RunTestHost host,
        HttpClient owner,
        Guid originalRunId,
        IReadOnlyDictionary<string, string> overrides)
    {
        using var response = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/runs/{originalRunId}",
            $"/runs/{originalRunId}/rebuild",
            overrides.ToDictionary(
                entry => "Parameters[" + entry.Key + "]",
                entry => entry.Value,
                StringComparer.Ordinal));

        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"The rebuild answered {(int)response.StatusCode}: {response.Headers.Location}.");
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/runs/", location, StringComparison.Ordinal);
        return Guid.Parse(location["/runs/".Length..]);
    }

    /// <summary>The project's runs, through the product's own read (newest first).</summary>
    private static async Task<IReadOnlyList<BuildRun>> ReadProjectRunsAsync(RunTestHost host, Guid projectId)
    {
        var organizationId = await host.OrganizationIdAsync();
        var runs = await host.ReadRunsAsync(organizationId);
        return [.. runs.Where(run => run.ProjectId == projectId)];
    }

    /// <summary>The run's frozen value map, read the way every reader reads it.</summary>
    private static IReadOnlyDictionary<string, string> ReadParameterValues(string? json)
    {
        Assert.NotNull(json);
        return JsonSerializer.Deserialize(json!, ProjectParameterJsonContext.Default.DictionaryStringString)!;
    }
}
