using System.Net;
using System.Text.Json;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Shared.Pipelines;
using Zenkins.Tests.Git;
using Zenkins.Tests.Pipelines;
using Zenkins.Tests.Runs;
using Zenkins.Worker.Git;
using Zenkins.Worker.Pipelines;

namespace Zenkins.Tests.Webhooks;

/// <summary>
/// Review M1 (option A): a webhook run must <b>build the commit its push named</b>.
/// <para>
/// The defect this pins: the run recorded the pushed commit, but the claim carried no commit and the worker
/// cloned <c>--branch</c> and then pinned the branch <em>tip</em>. Two pushes to one branch seconds apart are
/// explicitly allowed (the card keeps overlapping runs), so run A could clone after B landed and build B's
/// tree while its record still said A - and the accept report could never correct it, because the column is
/// write-once and the report was never compared.
/// </para>
/// <para>
/// The fix has three parts, and each has a test here: the claim names the commit, the worker pins it with
/// <c>checkout()</c> instead of trusting the branch, and a report that disagrees with the recorded commit is
/// refused (409 <c>git-commit-mismatch</c>) without writing anything.
/// </para>
/// </summary>
[Trait("Category", "Webhooks")]
public sealed class WebhookCommitPinTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_claim_names_the_pushed_commit_while_a_manual_run_names_none()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("pinned");
        var secret = host.SecretFor("pinned");
        var token = await host.CreateWorkerAsync();

        var first = WebhookPayloads.Commit('1');
        var second = WebhookPayloads.Commit('2');

        // Two pushes to the same branch, moments apart: both must be built, each at its own commit.
        using (var accepted = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("main", first),
                   secret,
                   deliveryId: "pin-1"))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using (var accepted = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("main", second),
                   secret,
                   deliveryId: "pin-2"))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        var claimed = new List<string?>();
        for (var index = 0; index < 2; index++)
        {
            using var claim = await host.ClaimAsync(token);
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
            claimed.Add(await ReadClaimCommitAsync(claim));
        }

        // FIFO: the first push's run is handed out first, with its own commit - never the branch tip.
        Assert.Equal([first, second], claimed);

        // A manual run has nothing to pin: its commit is reported by the worker after it clones.
        using var owner = await host.SignInOwnerAsync();
        var manualRunId = await RunTestHost.TriggerRunAsync(owner, host.ProjectId);
        using var manualClaim = await host.ClaimAsync(token);
        Assert.Equal(HttpStatusCode.OK, manualClaim.StatusCode);
        Assert.Equal(manualRunId, await RunTestHost.ReadClaimedRunIdAsync(manualClaim));
        Assert.Null(await ReadClaimCommitAsync(manualClaim));
    }

    [Fact]
    public async Task A_webhook_run_builds_the_pushed_commit_even_after_the_branch_moved()
    {
        var token = CancellationToken;

        // The fixture: main's pipeline, then a second commit that moves the branch on - the overlap the card
        // allows. The webhook named the first commit; the branch tip is now the second.
        using var repository = GitFixtureRepository.Create("pin");
        var environment = repository.Environment;
        GitCli.MustRun(repository.SourcePath, environment, "remote", "add", "origin", repository.BarePath);

        var pushedCommit = GitFixtureRepository.CommitFile(
            repository.SourcePath,
            environment,
            "pipeline.lua",
            PipelineTestData.MarkerPipeline("marker.txt"),
            "pipeline");
        GitCli.MustRun(repository.SourcePath, environment, "push", "origin", "main");

        var movedTip = GitFixtureRepository.CommitFile(
            repository.SourcePath,
            environment,
            "later.txt",
            "this file must not exist in the built tree\n",
            "the branch moved");
        GitCli.MustRun(repository.SourcePath, environment, "push", "origin", "main");
        Assert.NotEqual(pushedCommit, movedTip);

        using var directory = new GitTestDirectory("pin-run");
        var work = GitTestWorkspace.CreateWork(directory.RootPath);
        string? reportedCommit = null;

        var runner = new PipelineRunner(
            new PipelineRunOptions
            {
                Work = work,
                RepositoryUrl = repository.FileUrl,
                Branch = "main",
                PipelinePath = "pipeline.lua",
                Deadline = DateTimeOffset.UtcNow.AddMinutes(5),
                GitExecutable = GitCli.ResolveGitPath(),
                GitEnvironment = environment,

                // The claim's commit: the push this run came from, not the branch tip.
                ExpectedCommit = pushedCommit,
                Log = _ => { },
            },
            new PipelineDefinitionRunner(
                PipelineDefinitionRunner.CreateRuntime(PipelineRunner.CreateLogSink(_ => { }))),
            _ => Task.FromResult<GitCredentialMaterial?>(null),
            (commit, specJson, _) =>
            {
                if (specJson is null)
                {
                    reportedCommit = commit;
                }

                return Task.FromResult(true);
            });

        var outcome = await runner.RunAsync(token);

        // The worker checked out the pushed commit, not the tip: HEAD is the push and the tree lacks what the
        // later commit added.
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(pushedCommit, reportedCommit);
        Assert.Equal(pushedCommit, GitCli.RevParse(work.RepositoryPath, environment, "HEAD"));
        Assert.False(File.Exists(Path.Combine(work.RepositoryPath, "later.txt")));

        // ...and the steps really ran on that tree (the pipeline's own marker file).
        Assert.Equal(
            ["restore", "build", "package"],
            File.ReadAllLines(Path.Combine(work.RepositoryPath, "marker.txt")));
    }

    [Fact]
    public async Task A_report_that_disagrees_with_the_pinned_commit_is_refused_and_changes_nothing()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("mismatch");
        var secret = host.SecretFor("mismatch");
        var pinned = WebhookPayloads.Commit('3');
        var other = WebhookPayloads.Commit('4');
        var token = await host.CreateWorkerAsync();

        using (var accepted = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("main", pinned),
                   secret,
                   deliveryId: "mismatch-1"))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        var run = Assert.Single(await host.ReadRunsAsync());
        Assert.Equal(pinned, run.GitCommit);

        using var claim = await host.ClaimAsync(token);
        using var acceptedReport = await RunTestHost.AcceptAsync(host.DeliveryClient, token, run.Id);
        Assert.Equal(HttpStatusCode.OK, acceptedReport.StatusCode);

        // A spec that the server would otherwise accept, so the 409 below is the commit guard's and not the
        // spec validator's.
        var specJson = PipelineSpecJson.Serialize(
            new PipelineSpec(1, "pinned", [new PipelineStep("Build", Run: ["/bin/true"])]));

        // The definition report is where a worker states what it checked out. It says something else.
        using var refused = await RunTestHost.ReportDefinitionAsync(
            host.DeliveryClient,
            token,
            run.Id,
            pipelineSpecJson: specJson,
            gitCommit: other);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("git-commit-mismatch", await ReadErrorCodeAsync(refused));

        // Nothing was written: the recorded commit survives and no spec was frozen.
        var after = await host.ReadRunAsync(run.Id);
        Assert.Equal(pinned, after.GitCommit);
        Assert.Null(after.PipelineSpecJson);
        Assert.Equal(BuildRunStatus.Running, after.Status);

        // The honest report still works - the guard refuses a disagreement, not reporting.
        using var honest = await RunTestHost.ReportDefinitionAsync(
            host.DeliveryClient,
            token,
            run.Id,
            pipelineSpecJson: specJson,
            gitCommit: pinned);
        Assert.Equal(HttpStatusCode.OK, honest.StatusCode);
        Assert.Equal(pinned, (await host.ReadRunAsync(run.Id)).GitCommit);
        Assert.NotNull((await host.ReadRunAsync(run.Id)).PipelineSpecJson);
    }

    [Fact]
    public async Task An_accepted_report_that_disagrees_is_refused_before_the_transition()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("accept-mismatch");
        var secret = host.SecretFor("accept-mismatch");
        var pinned = WebhookPayloads.Commit('5');
        var token = await host.CreateWorkerAsync();

        using (var delivery = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("main", pinned),
                   secret,
                   deliveryId: "accept-mismatch-1"))
        {
            Assert.Equal(HttpStatusCode.Accepted, delivery.StatusCode);
        }

        var run = Assert.Single(await host.ReadRunsAsync());
        using var claim = await host.ClaimAsync(token);

        using var refused = await RunTestHost.AcceptAsync(
            host.DeliveryClient,
            token,
            run.Id);
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);

        // A second run to prove the pre-transition guard: a fresh webhook run, leased, then an accept that
        // names a different commit is refused with the run still Queued and its lease intact.
        using (var delivery = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("main", WebhookPayloads.Commit('6')),
                   secret,
                   deliveryId: "accept-mismatch-2"))
        {
            Assert.Equal(HttpStatusCode.Accepted, delivery.StatusCode);
        }

        var second = (await host.ReadRunsAsync()).Single(candidate => candidate.Status == BuildRunStatus.Queued);
        using var secondClaim = await host.ClaimAsync(token);
        Assert.Equal(second.Id, await RunTestHost.ReadClaimedRunIdAsync(secondClaim));

        using var mismatch = await WorkerAcceptWithCommitAsync(host, token, second.Id, pinned);
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        Assert.Equal("git-commit-mismatch", await ReadErrorCodeAsync(mismatch));

        var refusedRun = await host.ReadRunAsync(second.Id);
        Assert.Equal(BuildRunStatus.Queued, refusedRun.Status);
        Assert.NotNull(refusedRun.LeaseExpiresAt);
        Assert.Equal(WebhookPayloads.Commit('6'), refusedRun.GitCommit);
    }

    private static async Task<HttpResponseMessage> WorkerAcceptWithCommitAsync(
        WebhooksTestHost host,
        string workerToken,
        Guid runId,
        string commit) =>
        await Zenkins.Tests.Workers.WorkerTestHost.SendWorkerJsonAsync(
            host.DeliveryClient,
            workerToken,
            $"/api/workers/runs/{runId}/accepted",
            new { gitCommit = commit });

    private static async Task<string?> ReadClaimCommitAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("commit", out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("error", out var value) ? value.GetString() : null;
    }
}
