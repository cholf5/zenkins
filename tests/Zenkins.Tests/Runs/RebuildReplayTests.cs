using System.Net;
using System.Text.Json;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Projects;
using Zenkins.Server.Runs;
using Zenkins.Shared.Pipelines;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Pipelines;

namespace Zenkins.Tests.Runs;

/// <summary>
/// Card F-037: rebuild and replay. Both are driven the way a member drives them - the run page's forms -
/// and every verdict is read back from the rows the server wrote and the claim payload the worker receives:
/// <list type="bullet">
/// <item><b>TC-001:</b> a rebuild pins the original's commit, while a fresh trigger at the same moment
/// carries none (the worker resolves the branch tip) - the server half of the fact; the worker half (a
/// pinned commit really is checked out after the branch moved) is
/// <c>Zenkins.Tests.Git.RebuildPinTests</c>.</item>
/// <item><b>TC-002:</b> a Freestyle rebuild executes the steps the original ran even after the project's
/// steps changed, while a fresh trigger takes the new ones.</item>
/// <item><b>TC-004:</b> the project's own gate (F-028 disable, F-029 queue cap) refuses a rebuild exactly as
/// it refuses a fresh trigger, and nothing is written.</item>
/// </list>
/// <para>
/// The card's other facts: replay exists only where the run's snapshot <em>is</em> its definition; the
/// original's artifacts, logs and steps are never touched; and the original's parameter values are
/// re-resolved against today's definitions (the trigger form's own codes answer a name that vanished).
/// </para>
/// </summary>
[Trait("Category", "Runs")]
public sealed class RebuildReplayTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    /// <summary>A commit-shaped value (a hex object name, which is what the server validates).</summary>
    private static string Commit(char digit) => new(digit, 40);

    [Fact]
    public async Task A_rebuild_pins_the_original_commit_while_a_fresh_trigger_takes_the_branch()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await host.SeedProjectAsync("rebuild-pin", "file:///tmp/rebuild-pin.git");

        var originalId = await RunTestHost.TriggerRunAsync(owner, projectId);
        await FinishRunAsync(host, originalId, BuildRunStatus.Success, Commit('a'));

        var response = await RebuildAsync(owner, originalId);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var rebuiltId = Guid.Parse(response.Headers.Location!.OriginalString["/runs/".Length..]);
        Assert.NotEqual(originalId, rebuiltId);

        // The new run's page is where the user learns what happened: it links back to #1 by the
        // permalink, and the link is the number the run page prints (review M3: this is the assertion the
        // AC's "rebuilt from #N" needs, not only the row's columns).
        var rebuiltPage = await AuthTestHost.GetStringAsync(owner, $"/runs/{rebuiltId}");
        Assert.Contains("id=\"run-rebuilt-from-link\"", rebuiltPage, StringComparison.Ordinal);
        Assert.Contains($"href=\"/projects/{projectId}/runs/1\"", rebuiltPage, StringComparison.Ordinal);
        Assert.Contains(">#1<", rebuiltPage, StringComparison.Ordinal);
        Assert.Contains("This run is rebuilt from", rebuiltPage, StringComparison.Ordinal);

        // The rebuild's snapshot is the original's, pinned commit included - so the claim hands the worker
        // that commit instead of a branch to resolve.
        var rebuilt = await host.ReadRunAsync(rebuiltId);
        Assert.Equal(BuildRunTriggerType.Rebuild, rebuilt.TriggerType);
        Assert.Equal(originalId, rebuilt.RebuiltFromRunId);
        Assert.Equal(Commit('a'), rebuilt.GitCommit);
        Assert.Equal("main", rebuilt.GitBranch);
        Assert.Equal(2, rebuilt.Number);

        var (firstWorker, _) = await host.RegisterWorkerAsync(name: "rebuild-worker");
        using var firstClient = host.Host.CreateClient();
        using var rebuiltClaim = await RunTestHost.ClaimAsync(firstClient, firstWorker);
        Assert.Equal(rebuiltId, await RunTestHost.ReadClaimedRunIdAsync(rebuiltClaim));
        var claim = await ReadClaimAsync(rebuiltClaim);
        Assert.Equal(Commit('a'), claim.Commit);
        Assert.Equal("main", claim.Branch);
        Assert.Equal(2, claim.Number);

        // A fresh trigger at the same moment is a different intent: it carries no commit, so the worker
        // resolves whatever the branch points at now (the branch may have moved - a rebuild must not care).
        var freshId = await RunTestHost.TriggerRunAsync(owner, projectId);
        var (secondWorker, _) = await host.RegisterWorkerAsync(name: "fresh-worker");
        using var secondClient = host.Host.CreateClient();
        using var freshClaim = await RunTestHost.ClaimAsync(secondClient, secondWorker);
        Assert.Equal(freshId, await RunTestHost.ReadClaimedRunIdAsync(freshClaim));
        Assert.Null((await ReadClaimAsync(freshClaim)).Commit);
    }

    [Fact]
    public async Task A_rebuild_runs_the_original_freestyle_steps_after_the_project_changed()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await CreateFreestyleProjectAsync(owner, "rebuild-freestyle");

        // The definition the original ran.
        Assert.True(await SaveStepsAsync(owner, projectId, FreestyleSteps("Greet", "echo one")));
        var originalId = await RunTestHost.TriggerRunAsync(owner, projectId);
        var originalSpec = (await host.ReadRunAsync(originalId)).PipelineSpecJson;
        Assert.NotNull(originalSpec);
        await FinishRunAsync(host, originalId, BuildRunStatus.Success, gitCommit: null);

        // ...and then the project's steps change. A rebuild must still run the original's.
        Assert.True(await SaveStepsAsync(owner, projectId, FreestyleSteps("Greet", "echo two")));

        var response = await RebuildAsync(owner, originalId);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var rebuiltId = Guid.Parse(response.Headers.Location!.OriginalString["/runs/".Length..]);

        var rebuilt = await host.ReadRunAsync(rebuiltId);
        Assert.Equal(originalSpec, rebuilt.PipelineSpecJson);
        Assert.Equal(2, rebuilt.Number);

        var (worker, _) = await host.RegisterWorkerAsync(name: "freestyle-rebuild");
        using var workerClient = host.Host.CreateClient();
        using var rebuiltClaim = await RunTestHost.ClaimAsync(workerClient, worker);
        Assert.Equal(rebuiltId, await RunTestHost.ReadClaimedRunIdAsync(rebuiltClaim));
        Assert.Equal(originalSpec, (await ReadClaimAsync(rebuiltClaim)).PipelineSpecJson);

        // A fresh trigger really does take the new definition, so the rebuild above is a difference and not
        // the only thing this project can do.
        var freshId = await RunTestHost.TriggerRunAsync(owner, projectId);
        var freshRun = await host.ReadRunAsync(freshId);
        Assert.NotNull(freshRun.PipelineSpecJson);
        Assert.NotEqual(originalSpec, freshRun.PipelineSpecJson);
    }

    [Fact]
    public async Task A_replay_reruns_the_runs_own_definition_and_a_repository_pipeline_cannot_be_replayed()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();

        // A repository pipeline: its run stores the spec its worker produced, not the source behind it, so
        // replay is refused - and the refusal says which code it was.
        var pipelineProjectId = await host.SeedProjectAsync("replay-pipeline", "file:///tmp/replay.git");
        var pipelineRunId = await RunTestHost.TriggerRunAsync(owner, pipelineProjectId);
        await FinishRunAsync(host, pipelineRunId, BuildRunStatus.Success, Commit('b'));

        var refused = await PostFormAsync(owner, $"/runs/{pipelineRunId}", $"/runs/{pipelineRunId}/replay",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Equal(
            $"/runs/{pipelineRunId}?replayError={RunErrorCodes.ReplayUnavailable}",
            refused.Headers.Location!.OriginalString);
        Assert.Single(await host.ReadRunsAsync(await host.OrganizationIdAsync()));

        // The refusal is also readable on the page it redirects to.
        var page = await AuthTestHost.GetStringAsync(owner, refused.Headers.Location!.OriginalString);
        Assert.Contains("cannot be replayed as a definition", page, StringComparison.Ordinal);

        // A repository pipeline offers no Replay button at all, and the disabled control says why - the
        // AC's "the button's tooltip says why it is absent" (review M3).
        var pipelinePage = await AuthTestHost.GetStringAsync(owner, $"/runs/{pipelineRunId}");
        Assert.Contains("id=\"run-replay-unavailable\"", pipelinePage, StringComparison.Ordinal);
        Assert.Contains("disabled", pipelinePage, StringComparison.Ordinal);
        Assert.Contains("not the source that produced it", pipelinePage, StringComparison.Ordinal);
        Assert.Contains("action=\"/runs/" + pipelineRunId + "/rebuild\"", pipelinePage, StringComparison.Ordinal);

        // A Freestyle run's snapshot IS its definition, so replay re-runs exactly the steps it executed -
        // even though the project's steps have changed since.
        var freestyleProjectId = await CreateFreestyleProjectAsync(owner, "replay-freestyle");
        Assert.True(await SaveStepsAsync(owner, freestyleProjectId, FreestyleSteps("Greet", "echo original")));
        var originalId = await RunTestHost.TriggerRunAsync(owner, freestyleProjectId);
        var originalSpec = (await host.ReadRunAsync(originalId)).PipelineSpecJson;
        await FinishRunAsync(host, originalId, BuildRunStatus.Success, gitCommit: null);
        Assert.True(await SaveStepsAsync(owner, freestyleProjectId, FreestyleSteps("Greet", "echo changed")));

        var replayed = await PostFormAsync(owner, $"/runs/{originalId}", $"/runs/{originalId}/replay",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, replayed.StatusCode);
        var replayedId = Guid.Parse(replayed.Headers.Location!.OriginalString["/runs/".Length..]);

        var replayPage = await AuthTestHost.GetStringAsync(owner, $"/runs/{replayedId}");
        Assert.Contains("This run is replayed from", replayPage, StringComparison.Ordinal);
        Assert.Contains($"href=\"/projects/{freestyleProjectId}/runs/1\"", replayPage, StringComparison.Ordinal);

        var replayRun = await host.ReadRunAsync(replayedId);
        Assert.Equal(BuildRunTriggerType.Replay, replayRun.TriggerType);
        Assert.Equal(originalId, replayRun.RebuiltFromRunId);
        Assert.Equal(originalSpec, replayRun.PipelineSpecJson);
    }

    [Fact]
    public async Task The_projects_gate_refuses_a_rebuild_exactly_as_it_refuses_a_fresh_trigger()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await host.SeedProjectAsync("rebuild-gate", "file:///tmp/rebuild-gate.git");
        var organizationId = await host.OrganizationIdAsync();

        var originalId = await RunTestHost.TriggerRunAsync(owner, projectId);
        await FinishRunAsync(host, originalId, BuildRunStatus.Success, Commit('c'));

        // F-029: the queue is at its cap, so the rebuild is refused - by the same gate, with the same code a
        // fresh trigger gets, and with no row written.
        Assert.Equal(
            HttpStatusCode.Redirect,
            (await RunTestHost.SaveLimitsAsync(owner, projectId, maxQueuedRuns: 1)).StatusCode);
        await RunTestHost.TriggerRunAsync(owner, projectId);

        var queueFull = await RebuildAsync(owner, originalId);
        Assert.Equal(HttpStatusCode.Redirect, queueFull.StatusCode);
        Assert.Equal(
            $"/runs/{originalId}?rebuildError={ProjectTriggerRefusals.QueueFull}",
            queueFull.Headers.Location!.OriginalString);
        Assert.Equal(2, (await host.ReadRunsAsync(organizationId)).Count);

        // Drain the queue and disable the project: now the other half of the gate answers.
        await CancelQueuedRunsAsync(host, projectId);
        Assert.Equal(
            HttpStatusCode.Redirect,
            (await RunTestHost.DisableProjectAsync(owner, projectId, "mid-migration")).StatusCode);

        var disabled = await RebuildAsync(owner, originalId);
        Assert.Equal(HttpStatusCode.Redirect, disabled.StatusCode);
        Assert.Equal(
            $"/runs/{originalId}?rebuildError={ProjectTriggerRefusals.ProjectDisabled}",
            disabled.Headers.Location!.OriginalString);
        Assert.Equal(2, (await host.ReadRunsAsync(organizationId)).Count);

        // Re-enabling restores the button: the refusal is the project's state, not a broken run.
        Assert.Equal(
            HttpStatusCode.Redirect,
            (await RunTestHost.EnableProjectAsync(owner, projectId)).StatusCode);
        var afterEnable = await RebuildAsync(owner, originalId);
        Assert.Equal(HttpStatusCode.Redirect, afterEnable.StatusCode);
        Assert.StartsWith("/runs/", afterEnable.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(3, (await host.ReadRunsAsync(organizationId)).Count);
    }

    [Fact]
    public async Task A_rebuild_leaves_the_originals_artifacts_logs_and_steps_untouched()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await host.SeedProjectAsync("rebuild-readonly", "file:///tmp/rebuild-readonly.git");

        var originalId = await RunTestHost.TriggerRunAsync(owner, projectId);
        await FinishRunAsync(host, originalId, BuildRunStatus.Success, Commit('d'));

        // The original's records: one step, two log lines.
        await host.InSystemScopeAsync(async context =>
        {
            var run = (await context.SystemQueryAsync<BuildRun>(candidate => candidate.Id == originalId)).Single();
            context.BuildSteps.Add(new BuildStep
            {
                OrganizationId = run.OrganizationId,
                RunId = originalId,
                Index = 1,
                Name = "Build",
                Status = PipelineStepStatus.Success,
                ExitCode = 0,
            });
            context.BuildLogs.Add(new BuildLog
            {
                OrganizationId = run.OrganizationId,
                RunId = originalId,
                Seq = 1,
                Stream = "StdOut",
                Message = "the original's only line",
                Timestamp = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
            return true;
        });

        var before = await host.ReadRunAsync(originalId);
        var response = await RebuildAsync(owner, originalId);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var rebuiltId = Guid.Parse(response.Headers.Location!.OriginalString["/runs/".Length..]);

        var after = await host.ReadRunAsync(originalId);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.GitCommit, after.GitCommit);
        Assert.Equal(before.Number, after.Number);
        Assert.Equal(before.EndedAt, after.EndedAt);
        Assert.Equal(before.ExitCode, after.ExitCode);

        // The records still belong to the original, unchanged; the rebuild starts empty.
        var (originalSteps, originalLogs) = await ReadRecordsAsync(host, originalId);
        Assert.Single(originalSteps);
        Assert.Single(originalLogs);
        Assert.Equal("Build", originalSteps[0].Name);
        Assert.Equal("the original's only line", originalLogs[0].Message);

        var (rebuiltSteps, rebuiltLogs) = await ReadRecordsAsync(host, rebuiltId);
        Assert.Empty(rebuiltSteps);
        Assert.Empty(rebuiltLogs);
    }

    [Fact]
    public async Task An_unfinished_run_or_one_without_a_commit_cannot_be_rebuilt()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await host.SeedProjectAsync("rebuild-preconditions", "file:///tmp/rebuild-preconditions.git");
        var organizationId = await host.OrganizationIdAsync();

        // Still queued: "the same inputs" is a guess about a run that has not decided them yet.
        var queuedId = await RunTestHost.TriggerRunAsync(owner, projectId);
        var notTerminal = await RebuildAsync(owner, queuedId);
        Assert.Equal(
            $"/runs/{queuedId}?rebuildError={RunErrorCodes.RebuildNotTerminal}",
            notTerminal.Headers.Location!.OriginalString);

        // Finished, but with no commit: a rebuild could only take the branch head, which is what the
        // button promises not to do.
        await FinishRunAsync(host, queuedId, BuildRunStatus.Failed, gitCommit: null);
        var noCommit = await RebuildAsync(owner, queuedId);
        Assert.Equal(
            $"/runs/{queuedId}?rebuildError={RunErrorCodes.RebuildNoCommit}",
            noCommit.Headers.Location!.OriginalString);

        // Neither refusal wrote anything, and a run the page cannot rebuild offers no button.
        Assert.Single(await host.ReadRunsAsync(organizationId));
        var page = await AuthTestHost.GetStringAsync(owner, $"/runs/{queuedId}");
        Assert.DoesNotContain($"action=\"/runs/{queuedId}/rebuild\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rebuild_with_parameters_prefills_the_originals_values_and_resolves_overrides()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await host.SeedProjectAsync("rebuild-parameters", "file:///tmp/rebuild-parameters.git");

        // The project declares one boolean parameter (written the way the parameters page ultimately
        // writes it: the project's own definitions column).
        await host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId)).Single();
            project.ParametersJson = JsonSerializer.Serialize(
                new[] { new Shared.Projects.ProjectParameter("flag", Shared.Projects.ProjectParameterTypes.Boolean, "true") },
                Shared.Projects.ProjectParameterJsonContext.Default.ProjectParameterArray);
            await context.SaveChangesAsync();
            return true;
        });

        var originalId = await RunTestHost.TriggerRunAsync(owner, projectId);
        Assert.Equal("{\"flag\":\"true\"}", (await host.ReadRunAsync(originalId)).ParametersJson);
        await FinishRunAsync(host, originalId, BuildRunStatus.Success, Commit('e'));

        // The page prefills the form with the original's values.
        var page = await AuthTestHost.GetStringAsync(owner, $"/runs/{originalId}");
        Assert.Contains("id=\"run-rebuild-parameters\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"Parameters[flag]\"", page, StringComparison.Ordinal);

        // "Rebuild with parameters…" overrides one value and keeps everything else from the original.
        var overridden = await PostFormAsync(
            owner,
            $"/runs/{originalId}",
            $"/runs/{originalId}/rebuild",
            new Dictionary<string, string> { ["Parameters[flag]"] = "false" });
        var overriddenId = Guid.Parse(overridden.Headers.Location!.OriginalString["/runs/".Length..]);
        Assert.Equal("{\"flag\":\"false\"}", (await host.ReadRunAsync(overriddenId)).ParametersJson);
        Assert.Equal(Commit('e'), (await host.ReadRunAsync(overriddenId)).GitCommit);

        // The plain button keeps the original's value.
        var unchanged = await RebuildAsync(owner, originalId);
        var unchangedId = Guid.Parse(unchanged.Headers.Location!.OriginalString["/runs/".Length..]);
        Assert.Equal("{\"flag\":\"true\"}", (await host.ReadRunAsync(unchangedId)).ParametersJson);

        // A definition edit that removes the name is refused with the trigger form's own code: a run's
        // values are re-resolved against today's definitions, never replayed blindly.
        await host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId)).Single();
            project.ParametersJson = null;
            await context.SaveChangesAsync();
            return true;
        });

        var stale = await RebuildAsync(owner, originalId);
        Assert.Equal(
            $"/runs/{originalId}?rebuildError={RunErrorCodes.ParameterUnknown}",
            stale.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_tokenless_rebuild_is_refused_before_it_can_queue_anything()
    {
        using var host = await RunTestHost.CreateAsync();
        var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await host.SeedProjectAsync("rebuild-csrf", "file:///tmp/rebuild-csrf.git");
        var organizationId = await host.OrganizationIdAsync();

        var originalId = await RunTestHost.TriggerRunAsync(owner, projectId);
        await FinishRunAsync(host, originalId, BuildRunStatus.Success, Commit('f'));

        // The route carries its antiforgery requirement explicitly (the plain Rebuild form is legitimately
        // empty, so it cannot rely on a bound model to attach it). Without a token the request must die in
        // the middleware: no service call, no second run.
        using var refused = await owner.PostAsync(
            $"/runs/{originalId}/rebuild",
            new FormUrlEncodedContent(new Dictionary<string, string>()),
            CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Single(await host.ReadRunsAsync(organizationId));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private sealed record ClaimPayload(
        Guid RunId,
        string? Commit,
        string? Branch,
        string? PipelineSpecJson,
        int Number);

    private static async Task<ClaimPayload> ReadClaimAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return new ClaimPayload(
            root.GetProperty("runId").GetGuid(),
            root.TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.String
                ? commit.GetString()
                : null,
            root.TryGetProperty("branch", out var branch) && branch.ValueKind == JsonValueKind.String
                ? branch.GetString()
                : null,
            root.TryGetProperty("pipelineSpecJson", out var spec) && spec.ValueKind == JsonValueKind.String
                ? spec.GetString()
                : null,
            root.GetProperty("number").GetInt32());
    }

    private static Task<HttpResponseMessage> RebuildAsync(HttpClient owner, Guid runId) =>
        PostFormAsync(owner, $"/runs/{runId}", $"/runs/{runId}/rebuild", new Dictionary<string, string>());

    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client,
        string pagePath,
        string actionPath,
        Dictionary<string, string> fields) =>
        OrganizationTestHost.PostFormAsync(client, pagePath, actionPath, fields);

    /// <summary>
    /// Drives a queued run to a terminal state directly - the worker's accepted/completed reports would be
    /// the product path, but a rebuild test needs a finished run quickly, and the columns written here are
    /// exactly the ones those reports write.
    /// </summary>
    private static Task FinishRunAsync(
        RunTestHost host,
        Guid runId,
        string status,
        string? gitCommit) =>
        host.InSystemScopeAsync(async context =>
        {
            var run = (await context.SystemQueryAsync<BuildRun>(candidate => candidate.Id == runId)).Single();
            run.Status = status;
            run.EndedAt = DateTimeOffset.UtcNow;
            run.ExitCode = status == BuildRunStatus.Success ? 0 : 1;
            run.GitCommit = gitCommit ?? run.GitCommit;
            await context.SaveChangesAsync();
            return true;
        });

    private static Task CancelQueuedRunsAsync(RunTestHost host, Guid projectId) =>
        host.InSystemScopeAsync(async context =>
        {
            var queued = await context.SystemQueryAsync<BuildRun>(
                run => run.ProjectId == projectId && run.Status == BuildRunStatus.Queued);
            foreach (var run in queued)
            {
                run.Status = BuildRunStatus.Canceled;
                run.EndedAt = DateTimeOffset.UtcNow;
                run.FailureReason = "canceled-by-the-test";
            }

            await context.SaveChangesAsync();
            return true;
        });

    private static Task<(IReadOnlyList<BuildStep> Steps, IReadOnlyList<BuildLog> Logs)> ReadRecordsAsync(
        RunTestHost host,
        Guid runId) =>
        host.InSystemScopeAsync(async context =>
        {
            var steps = await context.SystemQueryAsync<BuildStep>(step => step.RunId == runId);
            var logs = await context.SystemQueryAsync<BuildLog>(log => log.RunId == runId);
            return ((IReadOnlyList<BuildStep>)steps, (IReadOnlyList<BuildLog>)logs);
        });

    /// <summary>Creates a Freestyle project through the real form (the kind selector's own path).</summary>
    private static async Task<Guid> CreateFreestyleProjectAsync(HttpClient owner, string name)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            owner,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = name,
                ["Kind"] = "freestyle",
            });
        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"Creating the freestyle project failed with {(int)response.StatusCode}.");
        var location = response.Headers.Location!.OriginalString;
        return Guid.Parse(location["/projects/".Length..].Split('?')[0]);
    }

    /// <summary>One Freestyle step, in the shape the steps editor posts.</summary>
    private static string FreestyleSteps(string name, string script) => JsonSerializer.Serialize(
        new object[]
        {
            new
            {
                name,
                script,
                envLines = string.Empty,
                continueOnError = false,
                artifactsText = string.Empty,
            },
        });

    private static async Task<bool> SaveStepsAsync(HttpClient owner, Guid projectId, string stepsJson)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}",
            $"/projects/{projectId}/freestyle/steps",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["StepsJson"] = stepsJson });
        return (int)response.StatusCode is 302 or 303;
    }
}
