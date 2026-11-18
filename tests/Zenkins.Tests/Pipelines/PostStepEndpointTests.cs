using System.Net;
using System.Text.Json;
using Xunit;
using Zenkins.Shared.Pipelines;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.Pipelines;

/// <summary>
/// Card F-031's server half: a post step's record is accepted at the position its run reached (the main list
/// stays positional), anything the frozen spec does not declare is refused, and the project's
/// "a post failure fails the run" switch travels in the claim - the worker is the side that executes the post
/// phase.
/// </summary>
[Trait("Category", "Pipeline")]
public sealed class PostStepEndpointTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_declared_post_step_is_recorded_at_its_continuing_index()
    {
        using var host = await RunTestHost.CreateAsync();
        var (runId, workerClient, workerToken) = await StartDefinedRunAsync(host);

        using (var running = await RunTestHost.ReportStepAsync(
                   workerClient, workerToken, runId, 2, "post/cleanup", PipelineStepStatus.Running))
        {
            Assert.Equal(HttpStatusCode.OK, running.StatusCode);
        }

        using (var success = await RunTestHost.ReportStepAsync(
                   workerClient, workerToken, runId, 2, "post/cleanup", PipelineStepStatus.Success, exitCode: 0))
        {
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        }

        var row = Assert.Single(await host.InSystemScopeAsync(async context =>
            await context.SystemQueryAsync<Zenkins.Server.Data.Entities.BuildStep>(step => step.RunId == runId)));
        Assert.Equal("post/cleanup", row.Name);
        Assert.Equal(PipelineStepStatus.Success, row.Status);
    }

    [Fact]
    public async Task An_undeclared_post_name_or_a_position_beyond_the_spec_is_refused()
    {
        using var host = await RunTestHost.CreateAsync();
        var (runId, workerClient, workerToken) = await StartDefinedRunAsync(host);

        using (var unknownName = await RunTestHost.ReportStepAsync(
                   workerClient, workerToken, runId, 2, "post/not-declared", PipelineStepStatus.Running))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknownName.StatusCode);
        }

        using (var beyond = await RunTestHost.ReportStepAsync(
                   workerClient, workerToken, runId, 3, "post/cleanup", PipelineStepStatus.Running))
        {
            Assert.Equal(HttpStatusCode.BadRequest, beyond.StatusCode);
        }

        // The main list remains positional: a post name cannot occupy a main step's position.
        using (var positional = await RunTestHost.ReportStepAsync(
                   workerClient, workerToken, runId, 1, "post/cleanup", PipelineStepStatus.Running))
        {
            Assert.Equal(HttpStatusCode.BadRequest, positional.StatusCode);
        }
    }

    [Fact]
    public async Task The_claim_carries_the_projects_post_failure_switch()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("post-switch");
        await host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Zenkins.Server.Data.Entities.Project>(
                candidate => candidate.Id == projectId)).Single();
            project.PostFailureFailsRun = true;
            await context.SaveChangesAsync(CancellationToken);
            return true;
        });
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "post-switch-agent");
        await RunTestHost.TriggerRunAsync(owner, projectId);
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

        var body = await claimed.Content.ReadAsStringAsync(CancellationToken);
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.GetProperty("postFailureFailsRun").GetBoolean());
    }

    private static async Task<(Guid RunId, HttpClient WorkerClient, string WorkerToken)> StartDefinedRunAsync(
        RunTestHost host)
    {
        var (projectId, owner) = await host.CreateProjectAsync("post-steps");
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "post-agent");
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);
        var workerClient = host.Host.CreateClient();

        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(claimed));
        using var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, runId);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var spec = new PipelineSpec(
            PipelineSpecSchema.Version,
            "post-spec",
            [new PipelineStep("Build", Run: ["dotnet", "build"])],
            Post: new PipelinePost(Always: [new PipelineStep("post/cleanup", Run: ["cleanup"])]));
        using var definition = await RunTestHost.ReportDefinitionAsync(
            workerClient, workerToken, runId, PipelineSpecJson.Serialize(spec));
        Assert.Equal(HttpStatusCode.OK, definition.StatusCode);

        return (runId, workerClient, workerToken);
    }
}
