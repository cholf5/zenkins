using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Webhooks;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Projects;
using Zenkins.Shared.Workers;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Webhooks;

namespace Zenkins.Tests.Runs;

/// <summary>
/// Card F-033's third path to a run: the branch observation (card F-023). A multibranch delivery that reaches
/// a branch nobody has read yet queues nothing and asks for a tree read; when the observer answers
/// <c>has-pipeline</c>, <b>that</b> path queues the run. It must therefore carry the same requirement, in the
/// same normal form, as the manual trigger and the webhook - and refuse the same things.
/// <para>
/// <b>Why this is its own file.</b> A structural argument ("it calls the one queue routine") is not evidence
/// and stops being true the day somebody gives it a row insert of its own. The three cases below are the
/// evidence: someone satisfies it, nobody does yet, and nobody ever can.
/// </para>
/// </summary>
public sealed class WorkerRequirementObservationTests
{
    [Fact]
    public async Task A_run_queued_by_the_observation_carries_the_requirement_and_a_matching_worker_claims_it()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        var webhookId = await host.SeedWebhookAsync("obs-claimable", branchFilter: string.Empty, requiredTags: "ci");
        var commit = WebhookPayloads.Commit('e');

        await DeliverAsync(host, webhookId, "obs-claimable", "feature/claimable", commit);

        // Nothing is queued before the tree is read: the delivery asked for an observation instead.
        Assert.Empty(await host.ReadRunsAsync());

        var workerToken = await host.CreateWorkerAsync("observation-agent", tags: "ci,linux");
        Assert.NotNull(await host.ObserveOnceAsync(workerToken, DefinitionValidationOutcomes.HasPipeline));

        var run = Assert.Single(await host.ReadRunsAsync());
        Assert.Equal(BuildRunStatus.Queued, run.Status);
        Assert.Equal("ci", run.RequiredTags);
        Assert.Equal(WorkerRequirement.FromTags("ci").StorageForm, run.RequiredRequirement);

        // The same requirement decides the claim: this worker carries the label, so it gets the run.
        using var workerClient = host.CreateDeliveryClient();
        using var claimed = await Zenkins.Tests.Runs.RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
    }

    [Fact]
    public async Task A_run_queued_by_the_observation_for_a_label_nobody_has_stays_queued()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        var webhookId = await host.SeedWebhookAsync("obs-waiting", branchFilter: string.Empty, requiredTags: "windows");
        var commit = WebhookPayloads.Commit('f');

        await DeliverAsync(host, webhookId, "obs-waiting", "feature/waiting", commit);

        var workerToken = await host.CreateWorkerAsync("observation-agent", tags: "linux");
        Assert.NotNull(await host.ObserveOnceAsync(workerToken, DefinitionValidationOutcomes.HasPipeline));

        // Queued - not refused, not failed. The requirement is stored and readable on the row; the linux
        // worker does not get it. (The page's "waiting for windows" line and the missing-label record are the
        // run page's own facts; this one pins the row.)
        var run = Assert.Single(await host.ReadRunsAsync());
        Assert.Equal(BuildRunStatus.Queued, run.Status);
        Assert.Equal(WorkerRequirement.FromTags("windows").StorageForm, run.RequiredRequirement);

        using var workerClient = host.CreateDeliveryClient();
        using var claimed = await Zenkins.Tests.Runs.RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.NoContent, claimed.StatusCode);
    }

    [Fact]
    public async Task A_requirement_that_can_never_hold_queues_nothing_on_the_observation_path()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        var webhookId = await host.SeedWebhookAsync(
            "obs-impossible",
            branchFilter: string.Empty,
            requiredTags: "linux,!linux");
        var commit = WebhookPayloads.Commit('1');

        await DeliverAsync(host, webhookId, "obs-impossible", "feature/impossible", commit);

        var workerToken = await host.CreateWorkerAsync("observation-agent", tags: "linux");
        Assert.NotNull(await host.ObserveOnceAsync(workerToken, DefinitionValidationOutcomes.HasPipeline));

        // Contradictory by construction: no run is created at all. Refusing at the queue (which every trigger
        // funnels through) is what keeps this from being a run that waits forever.
        Assert.Empty(await host.ReadRunsAsync());

        // ...and the refusal left a durable record, not just a log line: the branch keeps its pending
        // observation (the commit whose tree said "has-pipeline"), where clearing it would have said "nothing
        // is outstanding". The requirement that caused it is the entry's own tag list; a dedicated refusal
        // column on the branch is the honest next step and is not claimed here.
        var branch = Assert.Single(await host.ReadBranchesAsync());
        Assert.Equal("feature/impossible", branch.Branch);
        Assert.Equal(BranchPipelineStates.HasPipeline, branch.PipelineState);
        Assert.Equal(commit, branch.ObservedCommit);
    }

    [Fact]
    public async Task A_webhook_whose_requirement_can_never_hold_records_a_delivery_instead_of_nothing()
    {
        // Single-branch project: the delivery goes straight to the queue, which refuses a contradictory tag
        // list. The sender is a git host with no page to read, so the delivery history is the record - a
        // rolled-back "nothing happened" would be the silent drop this whole card is about.
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync(
            "obs-webhook-impossible",
            branchFilter: string.Empty,
            requiredTags: "linux,!linux");

        await DeliverAsync(host, webhookId, "obs-webhook-impossible", "main", WebhookPayloads.Commit('2'));

        Assert.Empty(await host.ReadRunsAsync());
        var delivery = Assert.Single(await host.ReadDeliveriesAsync(webhookId));
        Assert.Equal(WebhookOutcomes.RefusedRequirement, delivery.Outcome);
        Assert.Null(delivery.RunId);
    }

    [Fact]
    public async Task The_run_page_reads_the_requirement_as_prose_and_names_what_no_worker_has()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        var webhookId = await host.SeedWebhookAsync(
            "obs-readable",
            branchFilter: string.Empty,
            requiredTags: "linux,arm64");
        var commit = WebhookPayloads.Commit('3');

        await DeliverAsync(host, webhookId, "obs-readable", "feature/readable", commit);

        // Only `linux` exists: the requirement (both labels) is unsatisfied, and it is queued.
        var workerToken = await host.CreateWorkerAsync("observation-agent", tags: "linux");
        Assert.NotNull(await host.ObserveOnceAsync(workerToken, DefinitionValidationOutcomes.HasPipeline));
        var run = Assert.Single(await host.ReadRunsAsync());

        using var owner = await host.SignInOwnerAsync();
        var page = await AuthTestHost.GetStringAsync(owner, $"/runs/{run.Id}");

        // Readable form, never the stored DNF.
        Assert.Contains("Needs a Worker with:", page, StringComparison.Ordinal);
        Assert.Contains("arm64 and linux", page, StringComparison.Ordinal);
        Assert.DoesNotContain("arm64&&linux", page, StringComparison.Ordinal);

        // The atom nobody carries, in words a boolean requirement can actually support.
        Assert.Contains("Queued - no known Worker has: arm64", page, StringComparison.Ordinal);

        // The third trace is a storage fact, not a log line: a queued run that carries a requirement is
        // distinguishable from one that asks for nothing.
        var waitingRuns = await host.InSystemScopeAsync(context => context.SystemQueryAsync<BuildRun>(
            candidate => candidate.OrganizationId == host.OrganizationId
                && candidate.Status == BuildRunStatus.Queued
                && candidate.RequiredRequirement != string.Empty));
        Assert.Equal([run.Id], waitingRuns.Select(candidate => candidate.Id).ToArray());
    }

    private static async Task DeliverAsync(
        WebhooksTestHost host,
        Guid webhookId,
        string webhookName,
        string branch,
        string commit)
    {
        using var response = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub(branch, commit),
            host.SecretFor(webhookName),
            deliveryId: $"obs-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
}
