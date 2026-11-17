using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Zenkins.Server.Branches;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Probes;
using Zenkins.Server.Webhooks;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Probes;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Probes;
using Zenkins.Tests.Webhooks;

namespace Zenkins.Tests.Branches;

/// <summary>
/// Card F-023's definition-only observation, driven through the real surfaces: a signed push, the real
/// definition-task claim/report routes, and the real interval sweeper.
/// <para>
/// The facts here are the card's hard rules: <b>unknown is never queued</b> (a delivery waits for a tree
/// read), <b>no-pipeline is only ever an observation</b> (a failed read keeps the previous verdict and
/// retries), the same commit is observed once, and interval discovery registers heads, asks for the
/// reads, and orphans only what a <b>successful</b> refs answer no longer lists.
/// </para>
/// </summary>
[Trait("Category", "Branches")]
public sealed class BranchObservationTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_unreadable_observation_keeps_the_previous_verdict_and_stays_pending()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        var webhookId = await host.SeedWebhookAsync("retry-verdict", branchFilter: string.Empty);
        var first = WebhookPayloads.Commit('1');
        var moved = WebhookPayloads.Commit('2');

        // The branch's verdict is no-pipeline at commit 1; a push moves the head to 2, which the old
        // verdict does not cover - so the delivery asks for a fresh read and queues nothing.
        await host.SeedBranchAsync("docs-only", BranchPipelineStates.NoPipeline, first);
        using var push = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("docs-only", moved),
            host.SecretFor("retry-verdict"),
            deliveryId: "unreadable-1");
        Assert.Equal(HttpStatusCode.Accepted, push.StatusCode);
        Assert.Empty(await host.ReadRunsAsync());

        var workerToken = await host.CreateWorkerAsync("retry-agent");
        var claim = await host.ObserveOnceAsync(
            workerToken,
            DefinitionValidationOutcomes.Unreadable,
            diagnostic: "the remote could not be reached",
            failureReason: PipelineFailureReasons.GitUnreachable);
        Assert.NotNull(claim);
        Assert.Equal(moved, claim!.Commit);

        // A failed read is not a verdict: the old no-pipeline stands, the new head stays pending, and
        // the attempt count moves the retry clock.
        var afterFailure = Assert.Single(await host.ReadBranchesAsync());
        Assert.Equal(BranchPipelineStates.NoPipeline, afterFailure.PipelineState);
        Assert.Equal(first, afterFailure.ObservedCommit);
        Assert.Equal(moved, afterFailure.PendingCommit);
        Assert.Equal(1, afterFailure.ObservationAttempts);
        Assert.Empty(await host.ReadRunsAsync());

        // A second push for the same head asks again; this time the read succeeds and the run - which
        // the branch was never allowed to build before - is queued pinned to that commit.
        using var retry = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("docs-only", moved),
            host.SecretFor("retry-verdict"),
            deliveryId: "unreadable-2");
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);

        _ = await host.ObserveOnceAsync(workerToken, DefinitionValidationOutcomes.HasPipeline);
        var recovered = Assert.Single(await host.ReadBranchesAsync());
        Assert.Equal(BranchPipelineStates.HasPipeline, recovered.PipelineState);
        Assert.Equal(moved, recovered.ObservedCommit);
        Assert.Null(recovered.PendingCommit);
        Assert.Equal(moved, Assert.Single(await host.ReadRunsAsync()).GitCommit);
    }

    [Fact]
    public async Task Two_deliveries_for_the_same_commit_share_one_observation()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        var webhookId = await host.SeedWebhookAsync("dedupe-observe", branchFilter: string.Empty);
        var commit = WebhookPayloads.Commit('3');

        foreach (var deliveryId in new[] { "same-1", "same-2" })
        {
            using var response = await host.DeliverSignedAsync(
                webhookId,
                WebhookPayloads.GitHub("feature/shared", commit),
                host.SecretFor("dedupe-observe"),
                deliveryId: deliveryId);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        var observations = (await host.ObservationsAsync())
            .Where(entry => entry.Kind == DefinitionValidationKinds.RepositoryBranch)
            .ToArray();
        var queued = Assert.Single(observations);
        Assert.Equal("feature/shared", queued.Branch);
        Assert.Equal(commit, queued.Commit);

        // One task, one claim: the second push joined the first request instead of cloning twice.
        var workerToken = await host.CreateWorkerAsync("dedupe-agent");
        using var client = host.CreateDeliveryClient();
        var firstClaim = await host.ClaimDefinitionAsync(client, workerToken);
        Assert.NotNull(firstClaim);
        Assert.Null(await host.ClaimDefinitionAsync(client, workerToken));
    }

    [Fact]
    public async Task Definition_credentials_are_bound_to_the_leased_task_and_its_worker()
    {
        // The observation's clone needs the project's credential, and the third material route must be as
        // narrow as the run and probe routes: only the worker holding this task's live lease may decrypt
        // the project's material, and a foreign task id is the same bodyless 404.
        using var host = await WebhooksTestHost.CreateAsync();
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        var webhookId = await host.SeedWebhookAsync("credential-observed", branchFilter: string.Empty);
        using var push = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("feature/needs-credential", WebhookPayloads.Commit('9')),
            host.SecretFor("credential-observed"),
            deliveryId: "credential-1");
        Assert.Equal(HttpStatusCode.Accepted, push.StatusCode);

        var owner = await host.SignInOwnerAsync();
        var created = await OrganizationTestHost.PostFormAsync(
            owner,
            "/",
            "/git-credentials/create",
            new Dictionary<string, string>
            {
                ["Name"] = "OBSERVATION_CREDENTIAL",
                ["Kind"] = "HttpsToken",
                ["Value"] = "zkpat_0123456789abcdef0123456789abcdef",
                ["UserName"] = "x-access-token",
            });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var credentialId = System.Text.Json.JsonDocument.Parse(
            await created.Content.ReadAsStringAsync(CancellationToken))
            .RootElement.GetProperty("id").GetGuid();

        await host.Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<Zenkins.Server.Data.Tenancy.ISystemDataScope>();
            await using var context = await services
                .GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Zenkins.Server.Data.ZenkinsDbContext>>()
                .CreateDbContextAsync(CancellationToken);
            using var scope = systemDataScope.BeginScope();
            var project = (await context.SystemQueryAsync<Zenkins.Server.Data.Entities.Project>(
                candidate => candidate.Id == host.ProjectId)).Single();
            project.GitCredentialId = credentialId;
            await context.SaveChangesAsync(CancellationToken);
            return true;
        });

        var ownerToken = await host.CreateWorkerAsync("credential-owner");
        var otherToken = await host.CreateWorkerAsync("credential-other");
        using var client = host.CreateDeliveryClient();
        var claim = await host.ClaimDefinitionAsync(client, ownerToken);
        Assert.NotNull(claim);

        var allowed = await FetchDefinitionCredentialAsync(client, ownerToken, claim!.ValidationId);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var body = await allowed.Content.ReadAsStringAsync(CancellationToken);
        Assert.Contains("zkpat_0123456789abcdef0123456789abcdef", body, StringComparison.Ordinal);
        Assert.Equal("no-store", allowed.Headers.CacheControl?.ToString());

        // Another worker's token, and a made-up task id, are the same bodyless 404.
        using var foreignWorker = await FetchDefinitionCredentialAsync(client, otherToken, claim.ValidationId);
        Assert.Equal(HttpStatusCode.NotFound, foreignWorker.StatusCode);
        using var unknown = await FetchDefinitionCredentialAsync(client, ownerToken, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private static Task<HttpResponseMessage> FetchDefinitionCredentialAsync(
        HttpClient client,
        string workerToken,
        Guid validationId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/workers/definitions/{validationId}/git-credential");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", workerToken);
        return client.SendAsync(request, CancellationToken);
    }

    [Fact]
    public async Task The_retry_sweeper_backs_off_instead_of_storming()
    {
        using var host = await WebhooksTestHost.CreateAsync(new Dictionary<string, string?>
        {
            // The observation is abandoned almost immediately; the retry waits the production base
            // (60 s), so a second tick has nothing due - the backoff, not the tick, sets the pace.
            [BranchDiscoveryOptions.ObservationStaleSecondsKey] = "0.005",
            [BranchDiscoveryOptions.ObservationRetrySecondsKey] = "60",
            [BranchDiscoveryOptions.SweepIntervalSecondsKey] = "3600",
            [BranchDiscoveryOptions.DiscoveryIntervalSecondsKey] = "3600",
            [BranchDiscoveryOptions.DiscoveryProbeWaitSecondsKey] = "0.1",
        });
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        var webhookId = await host.SeedWebhookAsync("backoff", branchFilter: string.Empty);

        using var push = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("feature/waiting", WebhookPayloads.Commit('4')),
            host.SecretFor("backoff"),
            deliveryId: "backoff-1");
        Assert.Equal(HttpStatusCode.Accepted, push.StatusCode);

        var sweeper = await host.Host.InScopeAsync(services => Task.FromResult(
            services.GetServices<IHostedService>().OfType<BranchDiscoverySweeper>().Single()));

        // The in-flight task nobody answered is abandoned, and the branch's retry clock moves.
        await Task.Delay(80, CancellationToken);
        var first = await sweeper.SweepOnceAsync(CancellationToken);
        Assert.Equal(1, first.Abandoned);

        var pending = Assert.Single(await host.ReadBranchesAsync());
        Assert.Equal(1, pending.ObservationAttempts);
        Assert.Equal(BranchPipelineStates.Unknown, pending.PipelineState);
        Assert.Empty(await host.ReadRunsAsync());

        // The base delay is a minute: the next tick must not re-ask (no storm), and the pending commit
        // and the unknown verdict are still recorded.
        var second = await sweeper.SweepOnceAsync(CancellationToken);
        Assert.Equal(0, second.Abandoned);
        Assert.Equal(0, second.Retried);
        Assert.DoesNotContain(
            await host.ObservationsAsync(),
            entry => entry.Kind == DefinitionValidationKinds.RepositoryBranch && !entry.Settled);
    }

    [Fact]
    public async Task Interval_discovery_registers_new_heads_asks_for_reads_and_orphans_only_absent_ones()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        var workerToken = await host.CreateWorkerAsync("discovery-agent", canProbe: true);

        // A branch the remote no longer has. It must be marked orphaned (successful refs evidence) and
        // its row must survive.
        await host.SeedBranchAsync("feature/gone", BranchPipelineStates.HasPipeline, WebhookPayloads.Commit('5'));

        var mainSha = WebhookPayloads.Commit('6');
        var docsSha = WebhookPayloads.Commit('7');

        var discovery = host.WithTenantAsync(services =>
            services.GetRequiredService<BranchDiscoveryService>()
                .DiscoverAsync(host.ProjectId, CancellationToken));
        var probe = await host.WaitForUnsettledProbeAsync(TimeSpan.FromSeconds(10));

        using (var workerClient = host.CreateDeliveryClient())
        {
            var claim = await GitProbeTestHost.ClaimOneProbeAsync(workerClient, workerToken);
            Assert.Equal(probe.Id, claim.ProbeId);
            using var reported = await GitProbeTestHost.ReportProbeAsync(
                workerClient,
                workerToken,
                claim.ProbeId,
                GitProbeOutcomes.Refs,
                [
                    new GitProbeRef("refs/heads/docs", docsSha),
                    new GitProbeRef("refs/heads/main", mainSha),
                ],
                headSha: mainSha,
                headBranch: "refs/heads/main");
            Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        }

        var result = await discovery;
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Heads);
        Assert.Equal(2, result.Registered);
        Assert.Equal(1, result.Orphaned);

        var rows = await host.ReadBranchesAsync();
        Assert.Equal(
            new[] { "docs", "feature/gone", "main" },
            rows.OrderBy(row => row.Branch, StringComparer.Ordinal).Select(row => row.Branch));
        Assert.Equal(docsSha, rows.Single(row => row.Branch == "docs").PendingCommit);
        Assert.Equal(mainSha, rows.Single(row => row.Branch == "main").PendingCommit);
        Assert.All(
            rows.Where(row => row.Branch != "feature/gone"),
            row => Assert.Equal(BranchPipelineStates.Unknown, row.PipelineState));

        var gone = rows.Single(row => row.Branch == "feature/gone");
        Assert.NotNull(gone.OrphanedAt);
        Assert.Equal(BranchOrphanEvidence.RefsAbsent, gone.OrphanedEvidence);

        // Discovery is not a build: it registered branches and asked for reads, nothing is queued yet.
        Assert.Empty(await host.ReadRunsAsync());

        // Two tree reads are queued, one per new head, and the observation of a has-pipeline head is
        // what queues its run.
        var observations = (await host.ObservationsAsync())
            .Where(entry => entry.Kind == DefinitionValidationKinds.RepositoryBranch && !entry.Settled)
            .ToArray();
        Assert.Equal(2, observations.Length);
    }

    [Fact]
    public async Task A_failed_probe_never_orphans_and_registers_nothing()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        var workerToken = await host.CreateWorkerAsync("failing-discovery-agent", canProbe: true);
        await host.SeedBranchAsync("feature/still-there", BranchPipelineStates.HasPipeline, WebhookPayloads.Commit('8'));

        var discovery = host.WithTenantAsync(services =>
            services.GetRequiredService<BranchDiscoveryService>()
                .DiscoverAsync(host.ProjectId, CancellationToken));
        var probe = await host.WaitForUnsettledProbeAsync(TimeSpan.FromSeconds(10));

        using (var workerClient = host.CreateDeliveryClient())
        {
            var claim = await GitProbeTestHost.ClaimOneProbeAsync(workerClient, workerToken);
            Assert.Equal(probe.Id, claim.ProbeId);
            using var reported = await GitProbeTestHost.ReportProbeAsync(
                workerClient,
                workerToken,
                claim.ProbeId,
                GitProbeOutcomes.AuthRejected,
                message: "the remote rejected the credential");
            Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        }

        var result = await discovery;
        Assert.False(result.Succeeded);
        Assert.Equal(0, result.Registered);
        Assert.Equal(0, result.Orphaned);

        // The existing row is untouched: a failed probe is not an absence.
        var row = Assert.Single(await host.ReadBranchesAsync());
        Assert.Equal("feature/still-there", row.Branch);
        Assert.Null(row.OrphanedAt);
        Assert.Equal(BranchPipelineStates.HasPipeline, row.PipelineState);
    }
}
