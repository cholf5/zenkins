using System.Net;
using System.Text.Json;
using Xunit;

namespace Zenkins.Tests.Milestone;

/// <summary>
/// Q-002 TC-002: <b>a <c>git push</c> triggers the build</b>. The delivery is a correctly signed
/// GitHub-shaped push payload posted by a cookie-less client - exactly what a git host does - and the
/// run it starts is the same full chain TC-001 proves: real clone, real definition, real steps, real
/// artifact.
/// <para>
/// The replay is the second half of the card: the same delivery id posted again must be answered (202)
/// and must create nothing. The observation window is bounded polling, not a fixed wait: the count is
/// sampled repeatedly and asserted each time.
/// </para>
/// </summary>
[Trait("Category", "Milestone")]
public sealed class MilestoneWebhookTests
{
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromSeconds(3);

    [Fact(Timeout = 6 * 60 * 1000)]
    public async Task A_signed_push_starts_exactly_one_run_and_the_replay_creates_nothing()
    {
        const string secretValue = "webhook-run-s3cr3t";

        using var repository = MilestoneRepository.Create(MilestonePipelines.Happy);
        using var host = await MilestoneHost.StartAsync();

        var projectId = await host.CreateProjectAsync("milestone-webhook");
        await host.PointProjectAtAsync(projectId, repository.FileUrl);
        await host.CreateSecretAsync(MilestonePipelines.SecretName, secretValue);
        await host.StartWorkerAsync();

        // The webhook is created through the project page's own form, so its secret is the product's.
        var (webhookId, webhookSecret) = await host.CreateWebhookAsync(projectId, "push-main", branchFilter: "main");
        var deliveryId = "delivery-" + Guid.NewGuid().ToString("N");
        var payload = MilestonePushPayloads.GitHub("main", repository.MainTip);

        // ----- The delivery. -----
        using (var delivered = await host.DeliverAsync(webhookId, payload, webhookSecret, deliveryId))
        {
            Assert.Equal(HttpStatusCode.Accepted, delivered.StatusCode);
            using var body = JsonDocument.Parse(await delivered.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("queued", body.RootElement.GetProperty("status").GetString());
            Assert.Equal("main", body.RootElement.GetProperty("branch").GetString());
            Assert.Equal(repository.MainTip, body.RootElement.GetProperty("commit").GetString());
        }

        var run = await host.WaitForAnyRunAsync(projectId);

        // Executed by a real, separate worker process: sampled while the run executes, and tied to the
        // Workers row the server registered for this pid's worker name.
        var workerRow = await host.SampleWorkerDuringRunAsync(run.Id);

        var terminal = await host.WaitForTerminalAsync(run.Id);
        Assert.Equal(workerRow.Id, terminal.WorkerId);
        Assert.NotEqual(Environment.ProcessId, host.WorkerProcessId);
        Assert.NotEqual(Environment.ProcessId, host.ServerProcessId);

        Assert.Equal("Success", terminal.Status);
        Assert.Equal("Webhook", terminal.TriggerType);
        Assert.Equal(0, terminal.ExitCode);
        Assert.Null(terminal.FailureReason);

        // Pinned to the pushed commit, not merely to whatever the branch points at now.
        Assert.Equal(repository.MainTip, terminal.GitCommit);

        var steps = await host.ReadStepsAsync(run.Id);
        Assert.Equal(new[] { "Restore", "Build", "Publish" }, steps.Select(step => step.Name));
        Assert.All(steps, step => Assert.Equal("Success", step.Status));

        var artifacts = await host.ReadArtifactsAsync(run.Id);
        var artifact = Assert.Single(artifacts);
        Assert.Equal(MilestonePipelines.ArtifactBytes, await host.DownloadAsync(
            $"/runs/{run.Id}/artifacts/{artifact.Id}/download"));

        // The payload's own clone_url was ignored: the run built the project's repository (the fixture's),
        // which is the only repository that holds this commit.
        Assert.Single(await host.ReadProjectRunsAsync(projectId));

        // ----- The replay: same delivery id, same bytes, same signature. -----
        using (var replayed = await host.DeliverAsync(webhookId, payload, webhookSecret, deliveryId))
        {
            Assert.Equal(HttpStatusCode.Accepted, replayed.StatusCode);
            using var body = JsonDocument.Parse(await replayed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("duplicate", body.RootElement.GetProperty("status").GetString());
            Assert.Equal(run.Id, body.RootElement.GetProperty("runId").GetGuid());
        }

        // Bounded observation: nothing appears in the window after the replay.
        var deadline = DateTime.UtcNow + ObservationWindow;
        while (DateTime.UtcNow < deadline)
        {
            Assert.Single(await host.ReadProjectRunsAsync(projectId));
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
    }
}
