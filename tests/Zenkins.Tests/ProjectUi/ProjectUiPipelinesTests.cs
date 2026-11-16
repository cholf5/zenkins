using System.Net;
using Xunit;
using Zenkins.Shared.Pipelines;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.ProjectUi;

/// <summary>
/// Card D-003's Pipelines tab: it shows the pipeline path from settings and, when a run exists, the
/// frozen spec of the newest run (F-014) - the pipeline name, the validated shape (schema version,
/// step count) and the step names in declaration order, next to the run's branch and commit. The tab
/// is read-only by decision; a project without runs says so instead of inventing a spec.
/// </summary>
[Trait("Category", "ProjectUi")]
public sealed class ProjectUiPipelinesTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task The_tab_shows_the_newest_runs_frozen_spec()
    {
        using var host = await RunTestHost.CreateAsync();
        using var owner = await host.Inner.SignInOwnerAsync();
        var (projectId, _) = await host.CreateProjectAsync("frozen-app");

        // No run yet: the tab says so, and it still names the pipeline file.
        var empty = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/pipelines");
        Assert.Contains("pipeline.lua", empty, StringComparison.Ordinal);
        Assert.Contains("pipelines-path", empty, StringComparison.Ordinal);
        Assert.Contains("No runs yet", empty, StringComparison.Ordinal);

        // One run, whose worker reports the commit and freezes a three-step spec before step 1.
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "frozen-agent", maxConcurrency: 8);
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(claimed));
        using var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, runId);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var spec = new PipelineSpec(
            PipelineSpecSchema.Version,
            "build-pipeline",
            [new PipelineStep("Restore", Run: ["dotnet", "restore"]),
             new PipelineStep("Build", Run: ["dotnet", "build"]),
             new PipelineStep("Test", Run: ["dotnet", "test"])]);
        using var definition = await RunTestHost.ReportDefinitionAsync(
            workerClient, workerToken, runId, PipelineSpecJson.Serialize(spec), gitCommit: Commit);
        Assert.Equal(HttpStatusCode.OK, definition.StatusCode);

        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/pipelines");
        Assert.Contains("pipeline.lua", page, StringComparison.Ordinal);
        Assert.Contains(Commit, page, StringComparison.Ordinal);
        Assert.Contains("build-pipeline", page, StringComparison.Ordinal);
        Assert.Contains($"schema v{PipelineSpecSchema.Version}", page, StringComparison.Ordinal);
        Assert.Contains("3 step(s)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("No pipeline definition was reported", page, StringComparison.Ordinal);

        // The step names appear in declaration order: the index prefixes are ascending and each
        // name's first occurrence follows the previous one.
        var restore = page.IndexOf("pipelines-step-Restore", StringComparison.Ordinal);
        var build = page.IndexOf("pipelines-step-Build", StringComparison.Ordinal);
        var test = page.IndexOf("pipelines-step-Test", StringComparison.Ordinal);
        Assert.True(restore >= 0 && build > restore && test > build,
            "the Pipelines tab must list the steps in declaration order.");

        // The newest run is the one whose spec is frozen: two runs, and the second one's data wins.
        var secondRunId = await RunTestHost.TriggerRunAsync(owner, projectId);
        using var secondClaimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(secondRunId, await RunTestHost.ReadClaimedRunIdAsync(secondClaimed));
        using var secondAccepted = await RunTestHost.AcceptAsync(workerClient, workerToken, secondRunId);
        Assert.Equal(HttpStatusCode.OK, secondAccepted.StatusCode);

        var secondCommit = "fedcba9876543210fedcba9876543210fedcba98";
        var secondSpec = new PipelineSpec(
            PipelineSpecSchema.Version,
            "second-pipeline",
            [new PipelineStep("Only", Run: ["echo", "second"])]);
        using var secondDefinition = await RunTestHost.ReportDefinitionAsync(
            workerClient, workerToken, secondRunId, PipelineSpecJson.Serialize(secondSpec), gitCommit: secondCommit);
        Assert.Equal(HttpStatusCode.OK, secondDefinition.StatusCode);

        var newest = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/pipelines");
        Assert.Contains("second-pipeline", newest, StringComparison.Ordinal);
        Assert.Contains(secondCommit, newest, StringComparison.Ordinal);
        Assert.DoesNotContain("build-pipeline", newest, StringComparison.Ordinal);
        Assert.DoesNotContain(Commit, newest, StringComparison.Ordinal);
        Assert.DoesNotContain("pipelines-step-Restore", newest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_without_a_reported_definition_says_so()
    {
        using var host = await RunTestHost.CreateAsync();
        using var owner = await host.Inner.SignInOwnerAsync();
        var (projectId, _) = await host.CreateProjectAsync("pending-app");

        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "pending-agent");
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(claimed));
        using var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, runId);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        // The commit-only definition report (a clone that found no pipeline file) freezes no spec.
        using var definition = await RunTestHost.ReportDefinitionAsync(
            workerClient, workerToken, runId, pipelineSpecJson: null, gitCommit: Commit);
        Assert.Equal(HttpStatusCode.OK, definition.StatusCode);

        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/pipelines");
        Assert.Contains("No pipeline definition was reported", page, StringComparison.Ordinal);
        Assert.Contains(Commit, page, StringComparison.Ordinal);

        // The page renders nothing that looks like a parsed spec.
        Assert.DoesNotContain("pipelines-steps", page, StringComparison.Ordinal);
        Assert.DoesNotContain("schema v", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tab_is_a_real_route_for_a_member_too()
    {
        using var host = await RunTestHost.CreateAsync();
        using var owner = await host.Inner.SignInOwnerAsync();
        var (projectId, _) = await host.CreateProjectAsync("read-only-app");

        // A Member reads the tab (read-only); the same auth the other tabs carry.
        using var member = await host.Inner.Inner.RegisterUserAsync("pipelines-bob@example.test");
        using var ownerForInvite = await host.Inner.Inner.SignInAsync(Zenkins.Tests.Organizations.OrganizationTestHost.OwnerEmail);
        using var invite = await Zenkins.Tests.Organizations.OrganizationTestHost.PostFormAsync(
            ownerForInvite,
            "/organizations/members",
            "/organizations/members/invite",
            new Dictionary<string, string> { ["Email"] = "pipelines-bob@example.test" });
        Assert.True((int)invite.StatusCode is 302 or 303);
        _ = await AuthTestHost.GetStringAsync(member, "/");

        var page = await AuthTestHost.GetStringAsync(member, $"/projects/{projectId}/pipelines");
        Assert.Contains("pipelines-path", page, StringComparison.Ordinal);
        Assert.Contains("No runs yet", page, StringComparison.Ordinal);
    }
}
