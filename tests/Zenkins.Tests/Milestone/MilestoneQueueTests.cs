using System.Net;
using Xunit;

namespace Zenkins.Tests.Milestone;

/// <summary>
/// Q-002's no-worker case: <b>a push that arrives while no worker is connected must wait, not be lost</b>.
/// The delivery queues a run, the run sits in <c>Queued</c> with nothing running, and when a worker
/// finally connects the run executes - <b>once</b>.
/// <para>
/// "Once" is asserted three ways, all observable: one run row for the project, one artifact row for the
/// run, and exactly one <c>zenkins: step 1/3 … started</c> line in the persisted log (a second execution
/// would write a second one, and the step index is unique per run anyway). The queue window is a bounded
/// poll, not a fixed sleep: the status is sampled throughout and must never leave <c>Queued</c>.
/// </para>
/// </summary>
[Trait("Category", "Milestone")]
public sealed class MilestoneQueueTests
{
    private static readonly TimeSpan QueueObservationWindow = TimeSpan.FromSeconds(5);

    [Fact(Timeout = 6 * 60 * 1000)]
    public async Task A_delivery_without_a_worker_waits_and_then_runs_exactly_once()
    {
        const string secretValue = "queue-s3cr3t";

        using var repository = MilestoneRepository.Create(MilestonePipelines.Happy);
        using var host = await MilestoneHost.StartAsync();

        var projectId = await host.CreateProjectAsync("milestone-queue");
        await host.PointProjectAtAsync(projectId, repository.FileUrl);
        await host.CreateSecretAsync(MilestonePipelines.SecretName, secretValue);

        // Deliberately no worker yet: the delivery has nothing that could claim it, and the server's own
        // worker directory agrees.
        Assert.Equal(0, await host.CountOnlineWorkersAsync());
        var (webhookId, webhookSecret) = await host.CreateWebhookAsync(projectId, "push-main");
        var deliveryId = "delivery-" + Guid.NewGuid().ToString("N");
        var payload = MilestonePushPayloads.GitHub("main", repository.MainTip);

        using (var delivered = await host.DeliverAsync(webhookId, payload, webhookSecret, deliveryId))
        {
            Assert.Equal(HttpStatusCode.Accepted, delivered.StatusCode);
        }

        var queued = await host.WaitForAnyRunAsync(projectId);
        Assert.Equal("Queued", queued.Status);
        Assert.Equal("Webhook", queued.TriggerType);

        // Nobody has it: the run is queued, unclaimed, with no lease-holder and no start instant.
        Assert.Null(queued.WorkerId);
        Assert.Null(queued.EndedAt);
        Assert.Equal(0, await host.CountOnlineWorkersAsync());
        Assert.Equal(repository.MainTip, queued.GitCommit);
        Assert.Null(queued.ExitCode);
        Assert.Empty(await host.ReadStepsAsync(queued.Id));

        // It really waits: the status never leaves Queued while no worker exists.
        var deadline = DateTime.UtcNow + QueueObservationWindow;
        while (DateTime.UtcNow < deadline)
        {
            Assert.Equal("Queued", (await host.ReadRunAsync(queued.Id)).Status);
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        Assert.Single(await host.ReadProjectRunsAsync(projectId));

        // ----- A worker connects; the queued run executes without a new delivery. -----
        await host.StartWorkerAsync();

        var terminal = await host.WaitForTerminalAsync(queued.Id);
        Assert.Equal("Success", terminal.Status);
        Assert.Equal("Webhook", terminal.TriggerType);
        Assert.Equal(0, terminal.ExitCode);
        Assert.Null(terminal.FailureReason);
        Assert.Equal(repository.MainTip, terminal.GitCommit);

        var steps = await host.ReadStepsAsync(queued.Id);
        Assert.Equal(new[] { "Restore", "Build", "Publish" }, steps.Select(step => step.Name));
        Assert.All(steps, step =>
        {
            Assert.Equal("Success", step.Status);
            Assert.Equal(0, step.ExitCode);
        });

        // Exactly one execution: one run, one artifact, and one "step 1 started" line.
        Assert.Single(await host.ReadProjectRunsAsync(projectId));
        var artifact = Assert.Single(await host.ReadArtifactsAsync(queued.Id));
        Assert.Equal(MilestonePipelines.ArtifactBytes, await host.DownloadAsync(
            $"/runs/{queued.Id}/artifacts/{artifact.Id}/download"));

        Assert.NotNull(terminal.WorkerId);

        // F-008's drain lands the lines around the terminal report; waited for, not read once.
        var log = await host.WaitForLogLinesAsync(
            queued.Id,
            lines => lines.Any(line => line.Message.StartsWith(
                "zenkins: step 1/3 \"Restore\" started",
                StringComparison.Ordinal)));
        Assert.Single(log, line => line.Message.StartsWith(
            "zenkins: step 1/3 \"Restore\" started",
            StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Message.Contains(secretValue, StringComparison.Ordinal));

        // Nothing about the queue left the value behind either: no second run appears afterwards.
        var settleDeadline = DateTime.UtcNow + QueueObservationWindow;
        while (DateTime.UtcNow < settleDeadline)
        {
            Assert.Single(await host.ReadProjectRunsAsync(projectId));
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }
}
