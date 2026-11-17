using System.Net;
using System.Text.Json;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Pipelines;
using Zenkins.Tests.Workers;

namespace Zenkins.Tests.Runs;

/// <summary>
/// Card F-020's server-side freestyle loop, over the real forms and the real worker claim API: the
/// kind selector creates a repository-free project, the steps editor's POST freezes the spec on the
/// row, the trigger snapshots it onto the run (the same discipline the branch snapshot follows), and
/// the claim payload carries the frozen spec to the worker - with no repository URL, because there is
/// nothing to clone.
/// </summary>
[Trait("Category", "Runs")]
public sealed class FreestyleRunTests
{
    [Fact]
    public async Task The_trigger_freezes_the_saved_spec_and_the_claim_carries_it_without_a_repository()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await CreateFreestyleProjectAsync(owner);
        Assert.True(await SaveStepsAsync(owner, projectId, TwoSteps()));

        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);

        // The run snapshots the spec and carries no branch/pipeline path - there is no repository
        // and no definition phase on this path.
        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Queued, run.Status);
        Assert.NotNull(run.PipelineSpecJson);
        Assert.True(PipelineSpecJson.TryRead(run.PipelineSpecJson, out var spec, out var error), error);
        Assert.NotNull(spec);
        Assert.Equal("freestyle-e2e", spec!.Name);
        Assert.Equal(["Greet", "Index"], spec.Steps.Select(step => step.Name));
        Assert.All(spec.Steps, step => Assert.True(step.IsScript));
        Assert.Equal(string.Empty, run.GitBranch);
        Assert.Equal(string.Empty, run.PipelinePath);

        // The claim payload: the frozen spec travels to the worker; the repository URL is null -
        // the JSON writes the null explicitly, so a reader can tell "no repository" from "absent".
        var (workerToken, _) = await host.RegisterWorkerAsync();
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        var claim = await ReadClaimAsync(claimed);
        Assert.Equal(runId, claim.RunId);
        Assert.Null(claim.RepositoryUrl);
        Assert.Equal(string.Empty, claim.PipelinePath);
        Assert.NotNull(claim.PipelineSpecJson);
        Assert.Contains("\"script\"", claim.PipelineSpecJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_freestyle_project_whose_steps_were_never_saved_cannot_be_triggered()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await CreateFreestyleProjectAsync(owner);

        var response = await RunTestHost.StartRunAsync(owner, projectId);

        Assert.Equal((HttpStatusCode)302, response.StatusCode);
        Assert.Contains($"runError={RunErrorCodes.StepsMissing}", response.Headers.Location!.OriginalString, StringComparison.Ordinal);

        // Nothing was queued: the refusal happens before the row exists.
        Assert.Empty(await host.ReadRunsAsync(await host.OrganizationIdAsync()));
    }

    [Fact]
    public async Task Refused_steps_do_not_replace_the_saved_spec()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await CreateFreestyleProjectAsync(owner);
        Assert.True(await SaveStepsAsync(owner, projectId, TwoSteps()));
        var before = (await host.ReadRunAsync(await TriggerForAssertionAsync(owner, projectId))).PipelineSpecJson;

        // An invalid payload (no steps at all) is refused and the previously saved spec stays.
        var refused = await SaveStepsRawAsync(owner, projectId, "[]");
        Assert.Equal((HttpStatusCode)302, refused.StatusCode);

        // The refusal lands on the tab that owns the editor (F-020's PRG target), so the code it
        // carries is actually rendered - the overview reads neither `error` nor this notice.
        Assert.Contains(
            $"/projects/{projectId}/pipelines?error=steps-invalid",
            refused.Headers.Location!.OriginalString,
            StringComparison.Ordinal);
        var editorPage = await AuthTestHost.GetStringAsync(
            owner,
            $"/projects/{projectId}/pipelines?error=steps-invalid");
        Assert.Contains("The step list was refused", editorPage, StringComparison.Ordinal);

        var after = (await host.ReadRunAsync(await TriggerForAssertionAsync(owner, projectId))).PipelineSpecJson;
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Editing_the_steps_after_a_trigger_does_not_move_the_queued_or_running_runs_snapshot()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await CreateFreestyleProjectAsync(owner, "freestyle-snapshot");
        Assert.True(await SaveStepsAsync(owner, projectId, TwoSteps()));

        // A run is queued ...
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        var frozen = (await host.ReadRunAsync(runId)).PipelineSpecJson;
        Assert.NotNull(frozen);

        // ... the project's steps are edited while it waits, and the queued run keeps the spec it
        // froze at trigger time.
        Assert.True(await SaveStepsAsync(owner, projectId, RewrittenSteps("Publish")));
        Assert.Equal(frozen, (await host.ReadRunAsync(runId)).PipelineSpecJson);

        // The same run is then claimed and accepted, i.e. it is running ...
        var (workerToken, _) = await host.RegisterWorkerAsync();
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        Assert.Equal(runId, (await ReadClaimAsync(claimed)).RunId);
        using var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, runId);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(BuildRunStatus.Running, (await host.ReadRunAsync(runId)).Status);

        // ... and a second edit does not move it either.
        Assert.True(await SaveStepsAsync(owner, projectId, RewrittenSteps("Release")));

        // The edit really landed on the project (so the assertion above is not vacuous) ...
        Assert.Contains(
            "Release",
            (await host.ReadProjectAsync(projectId)).FreestyleSpecJson!,
            StringComparison.Ordinal);

        // ... while the running run still holds exactly the spec it froze at trigger time: the snapshot
        // is the run's, not a view over the project's current steps.
        Assert.Equal(frozen, (await host.ReadRunAsync(runId)).PipelineSpecJson);
        Assert.True(PipelineSpecJson.TryRead(frozen, out var executed, out var error), error);
        Assert.Equal(["Greet", "Index"], executed!.Steps.Select(step => step.Name));
    }

    [Fact]
    public async Task An_unknown_kind_is_refused_at_create()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();

        var response = await OrganizationTestHost.PostFormAsync(
            owner,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "bogus-kind",
                ["Kind"] = "freeform",
            });

        Assert.Equal((HttpStatusCode)302, response.StatusCode);
        Assert.Contains("error=kind-invalid", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    private static async Task<Guid> CreateFreestyleProjectAsync(HttpClient owner, string name = "freestyle-e2e")
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
        Assert.True((int)response.StatusCode is 302 or 303,
            $"Creating the freestyle project failed with {(int)response.StatusCode}.");
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/projects/", location, StringComparison.Ordinal);
        return Guid.Parse(location["/projects/".Length..].Split('?')[0]);
    }

    private static string TwoSteps() => JsonSerializer.Serialize(
        new object[]
        {
            new { name = "Greet", script = "echo hello", envLines = "", continueOnError = false, artifactsText = "" },
            new { name = "Index", script = "python3 index.py", envLines = "INDEX_ROOT=/srv/index", continueOnError = false, artifactsText = " out/index " },
        });

    /// <summary>
    /// The steps an edit posts after the run was triggered (Q-012): one differently-named step, so the
    /// project's stored spec and the run's snapshot cannot agree by accident.
    /// </summary>
    private static string RewrittenSteps(string stepName) => JsonSerializer.Serialize(
        new object[]
        {
            new { name = stepName, script = "echo rewritten", envLines = "", continueOnError = false, artifactsText = "" },
        });

    private static async Task<bool> SaveStepsAsync(HttpClient owner, Guid projectId, string stepsJson) =>
        (await SaveStepsRawAsync(owner, projectId, stepsJson)).StatusCode is HttpStatusCode.Found or HttpStatusCode.RedirectKeepVerb;

    private static async Task<HttpResponseMessage> SaveStepsRawAsync(HttpClient owner, Guid projectId, string stepsJson) =>
        await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}",
            $"/projects/{projectId}/freestyle/steps",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["StepsJson"] = stepsJson,
            });

    private static async Task<Guid> TriggerForAssertionAsync(HttpClient owner, Guid projectId) =>
        await RunTestHost.TriggerRunAsync(owner, projectId);

    private sealed record ClaimPayload(Guid RunId, string? RepositoryUrl, string PipelinePath, string? PipelineSpecJson);

    private static async Task<ClaimPayload> ReadClaimAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return new ClaimPayload(
            root.GetProperty("runId").GetGuid(),
            root.TryGetProperty("repositoryUrl", out var repositoryUrl) && repositoryUrl.ValueKind == JsonValueKind.String
                ? repositoryUrl.GetString()
                : null,
            root.GetProperty("pipelinePath").GetString() ?? string.Empty,
            root.TryGetProperty("pipelineSpecJson", out var specJson) && specJson.ValueKind == JsonValueKind.String
                ? specJson.GetString()
                : null);
    }
}
