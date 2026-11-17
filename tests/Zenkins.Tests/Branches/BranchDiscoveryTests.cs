using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Xunit;
using Zenkins.Server.Branches;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;
using Zenkins.Server.Webhooks;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Runs;
using Zenkins.Tests.Webhooks;

namespace Zenkins.Tests.Branches;

/// <summary>
/// Card F-023's discovery path, driven through the product's own surfaces: a signed push through the
/// real webhook endpoint, the real worker API for the definition/complete reports a branch's verdict
/// comes from, and the real services for the branch list and the runs filter.
/// <para>
/// <b>The single-branch regression is a fact here, not a promise.</b>
/// <c>Single_mode_still_queues_a_branch_known_to_have_no_pipeline</c> seeds the very row the
/// multibranch gate reads, then proves a single-branch project ignores it and builds - which is only
/// true while the no-pipeline gate sits <em>behind</em> the mode switch.
/// </para>
/// </summary>
[Trait("Category", "Branches")]
public sealed class BranchDiscoveryTests
{
    private const string SpecJson = """
        { "version": 1, "name": "Build", "steps": [ { "name": "Only", "run": [ "dotnet", "--info" ] } ] }
        """;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------
    // The regression target: single mode is unchanged, and the gate is behind the switch
    // ------------------------------------------------------------------

    [Fact]
    public async Task Single_mode_still_queues_a_branch_known_to_have_no_pipeline()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var observed = WebhookPayloads.Commit('a');
        var pushed = WebhookPayloads.Commit('b');

        // The row a multibranch project would refuse on - present, evidence-bearing, and irrelevant to
        // a single-branch project. Its recorded head is deliberately a different commit from the push,
        // so "single mode did not touch the registry" is observable.
        await host.SeedBranchAsync(
            WebhooksTestHost.DefaultBranch,
            BranchPipelineStates.NoPipeline,
            observed);
        var webhookId = await host.SeedWebhookAsync("single-webhook", branchFilter: string.Empty);

        using var response = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub(WebhooksTestHost.DefaultBranch, pushed),
            host.SecretFor("single-webhook"),
            deliveryId: "single-1");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // F-017's behaviour, unchanged: the push queues, pinned to the pushed commit. (Its run would
        // later settle `pipeline-missing` - the worker, not this endpoint, finds the missing file.)
        var runs = await host.ReadRunsAsync();
        var run = Assert.Single(runs);
        Assert.Equal(WebhooksTestHost.DefaultBranch, run.GitBranch);
        Assert.Equal(pushed, run.GitCommit);
        Assert.Equal(WebhookOutcomes.Queued, (await host.ReadDeliveriesAsync(webhookId)).Single().Outcome);

        // And a single-branch project writes nothing at all on the branch registry: no row for the
        // push, and the existing row still says what it said.
        var branches = await host.ReadBranchesAsync();
        var seeded = Assert.Single(branches);
        Assert.Equal(BranchPipelineStates.NoPipeline, seeded.PipelineState);
        Assert.Equal(observed, seeded.ObservedCommit);
        Assert.Equal(observed, seeded.LastCommit);
    }

    [Fact]
    public async Task A_single_branch_project_ignores_a_branch_outside_its_filter_exactly_as_before()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("main-only", branchFilter: string.Empty);

        using var response = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("feature/x", WebhookPayloads.Commit('b')),
            host.SecretFor("main-only"),
            deliveryId: "single-2");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await host.ReadRunsAsync());
        Assert.Equal(WebhookOutcomes.IgnoredBranch, (await host.ReadDeliveriesAsync(webhookId)).Single().Outcome);
        Assert.Empty(await host.ReadBranchesAsync());
    }

    // ------------------------------------------------------------------
    // TC-001: a push to a new branch creates its branch job
    // ------------------------------------------------------------------

    [Fact]
    public async Task Branches_mode_registers_a_new_branch_and_queues_its_run_only_after_the_tree_is_read()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        var webhookId = await host.SeedWebhookAsync("multibranch-webhook", branchFilter: string.Empty);
        var commit = WebhookPayloads.Commit('c');

        using var response = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("feature/x", commit),
            host.SecretFor("multibranch-webhook"),
            deliveryId: "multi-1");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The push registers the branch and asks for a tree read - it does NOT queue a run it cannot
        // justify: nobody has seen this branch's tree yet, and "unknown" is never guessed.
        Assert.Empty(await host.ReadRunsAsync());
        Assert.Equal(
            WebhookOutcomes.AwaitingObservation,
            (await host.ReadDeliveriesAsync(webhookId)).Single().Outcome);

        var branch = Assert.Single(await host.ReadBranchesAsync());
        Assert.Equal("feature/x", branch.Branch);
        Assert.Equal(BranchPipelineStates.Unknown, branch.PipelineState);
        Assert.Null(branch.ObservedCommit);
        Assert.Equal(commit, branch.LastCommit);
        Assert.Equal(commit, branch.PendingCommit);
        Assert.Null(branch.OrphanedAt);

        // The observer answers has-pipeline about the pushed commit: the run is queued now, pinned to
        // that commit, and the verdict is stored with its evidence commit.
        var workerToken = await host.CreateWorkerAsync("observation-agent");
        var claim = await host.ObserveOnceAsync(workerToken, Zenkins.Shared.Pipelines.DefinitionValidationOutcomes.HasPipeline);
        Assert.NotNull(claim);
        Assert.Equal(Zenkins.Shared.Pipelines.DefinitionValidationKinds.RepositoryBranch, claim!.Kind);
        Assert.Equal("feature/x", claim.Branch);
        Assert.Equal(commit, claim.Commit);
        Assert.Equal("pipeline.lua", claim.PipelinePath);

        var run = Assert.Single(await host.ReadRunsAsync());
        Assert.Equal("feature/x", run.GitBranch);
        Assert.Equal(commit, run.GitCommit);

        var observed = Assert.Single(await host.ReadBranchesAsync());
        Assert.Equal(BranchPipelineStates.HasPipeline, observed.PipelineState);
        Assert.Equal(commit, observed.ObservedCommit);
        Assert.Null(observed.PendingCommit);

        var listed = await BranchesAsync(host, host.ProjectId);
        Assert.Equal(run.Number, Assert.Single(listed).LastRun?.Number);
    }

    // ------------------------------------------------------------------
    // AC-5: the manual trigger picks the branch (default: the project's default branch)
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_manual_trigger_on_a_multibranch_project_picks_the_branch_and_defaults_to_the_projects_default()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        using var owner = await host.SignInOwnerAsync();

        // The same form and the same validation every project already had: an explicit branch is
        // snapshotted, and a blank one resolves to the project's default branch.
        var chosenRunId = await RunTestHost.TriggerRunAsync(owner, host.ProjectId, branch: "feature/chosen");
        Assert.Equal("feature/chosen", (await host.ReadRunAsync(chosenRunId)).GitBranch);

        var defaultRunId = await RunTestHost.TriggerRunAsync(owner, host.ProjectId);
        Assert.Equal(WebhooksTestHost.DefaultBranch, (await host.ReadRunAsync(defaultRunId)).GitBranch);

        // Both branches are listed, each with its own last run - neither needed a registry row to exist.
        var branches = await BranchesAsync(host, host.ProjectId);
        Assert.Equal(new[] { "feature/chosen", WebhooksTestHost.DefaultBranch }, branches.Select(branch => branch.Branch));
        Assert.All(branches, branch => Assert.False(branch.HasRecord));
    }

    // ------------------------------------------------------------------
    // TC-002's shape: a branch explicitly known to have no pipeline is listed and never queued
    // ------------------------------------------------------------------

    [Fact]
    public async Task Branches_mode_lists_a_no_pipeline_branch_and_never_queues_it()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        var webhookId = await host.SeedWebhookAsync("no-pipeline", branchFilter: string.Empty);
        var observed = WebhookPayloads.Commit('d');
        var workerToken = await host.CreateWorkerAsync("docs-observer");

        // A first sighting is an observation, not a run: nobody has read this tree.
        using var first = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("docs-only", observed),
            host.SecretFor("no-pipeline"),
            deliveryId: "docs-1");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Empty(await host.ReadRunsAsync());

        var claim = await host.ObserveOnceAsync(
            workerToken, Zenkins.Shared.Pipelines.DefinitionValidationOutcomes.NoPipeline);
        Assert.NotNull(claim);
        Assert.Equal("docs-only", claim!.Branch);
        Assert.Equal(observed, claim.Commit);

        // No run: not a failure, not a skipped run - nothing.
        Assert.Empty(await host.ReadRunsAsync());

        // The branch is listed as no-pipeline with its evidence commit, and the pending marker is gone.
        var branch = Assert.Single(await host.ReadBranchesAsync());
        Assert.Equal(BranchPipelineStates.NoPipeline, branch.PipelineState);
        Assert.Equal(observed, branch.ObservedCommit);
        Assert.Null(branch.PendingCommit);

        var listed = Assert.Single(await BranchesAsync(host, host.ProjectId));
        Assert.True(listed.IsNoPipeline);
        Assert.Null(listed.LastRun);

        // The same commit again is answered by the recorded verdict - no observation, no run.
        using var repeat = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("docs-only", observed),
            host.SecretFor("no-pipeline"),
            deliveryId: "docs-2");
        Assert.Equal(HttpStatusCode.Accepted, repeat.StatusCode);
        Assert.Equal(WebhookOutcomes.NoPipeline, (await host.ReadDeliveriesAsync(webhookId))
            .Single(delivery => delivery.Outcome == WebhookOutcomes.NoPipeline).Outcome);
        Assert.Empty(await host.ReadRunsAsync());

        // A new head is not covered by the old verdict: the delivery asks for a fresh read and still
        // queues nothing until an observation says otherwise.
        using var moved = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("docs-only", WebhookPayloads.Commit('e')),
            host.SecretFor("no-pipeline"),
            deliveryId: "docs-3");
        Assert.Equal(HttpStatusCode.Accepted, moved.StatusCode);
        Assert.Empty(await host.ReadRunsAsync());
        Assert.Equal(
            WebhookPayloads.Commit('e'),
            Assert.Single(await host.ReadBranchesAsync()).PendingCommit);
        Assert.Equal(BranchPipelineStates.NoPipeline, Assert.Single(await host.ReadBranchesAsync()).PipelineState);
    }

    [Fact]
    public async Task Branches_mode_ignores_an_over_long_branch_instead_of_failing_the_insert()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        var webhookId = await host.SeedWebhookAsync("long-branch", branchFilter: string.Empty);

        // A probe may report a 255-character ref; a run's branch column holds 200. The delivery records
        // the refusal - the run insert is never attempted, and the name is never truncated.
        var tooLong = new string('a', BranchIdentity.MaxLength + 1);
        using var refused = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub(tooLong, WebhookPayloads.Commit('f')),
            host.SecretFor("long-branch"),
            deliveryId: "long-1");
        Assert.Equal(HttpStatusCode.Accepted, refused.StatusCode);
        Assert.Empty(await host.ReadRunsAsync());
        Assert.Empty(await host.ReadBranchesAsync());
        Assert.Equal(
            WebhookOutcomes.BranchTooLong,
            (await host.ReadDeliveriesAsync(webhookId)).Single().Outcome);

        // Exactly at the bound is storable: it is registered and observed like any other branch, and the
        // observation's has-pipeline answer is what queues its run.
        var atBound = new string('b', BranchIdentity.MaxLength);
        using var accepted = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub(atBound, WebhookPayloads.Commit('1')),
            host.SecretFor("long-branch"),
            deliveryId: "long-2");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(atBound, Assert.Single(await host.ReadBranchesAsync()).Branch);
        var workerToken = await host.CreateWorkerAsync("bound-agent");
        _ = await host.ObserveOnceAsync(workerToken, Zenkins.Shared.Pipelines.DefinitionValidationOutcomes.HasPipeline);
        Assert.Equal(atBound, Assert.Single(await host.ReadRunsAsync()).GitBranch);
    }

    // ------------------------------------------------------------------
    // The filter is the discovery rule
    // ------------------------------------------------------------------

    [Fact]
    public async Task Branches_mode_reads_the_filter_as_the_discovery_rule_and_registers_nothing_it_excludes()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        var webhookId = await host.SeedWebhookAsync("release-only", branchFilter: "release/*");

        using var excluded = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("main", WebhookPayloads.Commit('2')),
            host.SecretFor("release-only"),
            deliveryId: "rule-1");
        Assert.Equal(HttpStatusCode.Accepted, excluded.StatusCode);
        Assert.Empty(await host.ReadRunsAsync());
        Assert.Empty(await host.ReadBranchesAsync());
        Assert.Equal(WebhookOutcomes.IgnoredBranch, (await host.ReadDeliveriesAsync(webhookId)).Single().Outcome);

        using var selected = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("release/1.2", WebhookPayloads.Commit('3')),
            host.SecretFor("release-only"),
            deliveryId: "rule-2");
        Assert.Equal(HttpStatusCode.Accepted, selected.StatusCode);
        Assert.Empty(await host.ReadRunsAsync());
        Assert.Equal("release/1.2", Assert.Single(await host.ReadBranchesAsync()).Branch);
        Assert.Single(
            await host.ReadDeliveriesAsync(webhookId),
            delivery => delivery.Outcome == WebhookOutcomes.AwaitingObservation);
    }

    // ------------------------------------------------------------------
    // Orphaned: push-delete evidence only, history retained
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_push_delete_orphans_the_branch_and_keeps_its_runs()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        var webhookId = await host.SeedWebhookAsync("deleting", branchFilter: string.Empty);

        using var push = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("feature/gone", WebhookPayloads.Commit('4')),
            host.SecretFor("deleting"),
            deliveryId: "delete-1");
        Assert.Equal(HttpStatusCode.Accepted, push.StatusCode);
        var workerToken = await host.CreateWorkerAsync("orphan-agent");
        _ = await host.ObserveOnceAsync(workerToken, Zenkins.Shared.Pipelines.DefinitionValidationOutcomes.HasPipeline);
        var run = Assert.Single(await host.ReadRunsAsync());

        using var deleted = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("feature/gone", WebhookPayloads.Deleted),
            host.SecretFor("deleting"),
            deliveryId: "delete-2");
        Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode);

        // The marker is evidence-bearing, and nothing was deleted: the run is still there.
        var branch = Assert.Single(await host.ReadBranchesAsync());
        Assert.NotNull(branch.OrphanedAt);
        Assert.Equal(BranchOrphanEvidence.PushDelete, branch.OrphanedEvidence);
        Assert.Equal(run.Id, Assert.Single(await host.ReadRunsAsync()).Id);

        var listed = Assert.Single(await BranchesAsync(host, host.ProjectId));
        Assert.True(listed.IsOrphaned);
        Assert.Equal(BranchOrphanEvidence.PushDelete, listed.OrphanedEvidence);
        Assert.Equal(run.Number, listed.LastRun?.Number);

        // A later push proves the branch exists again: the marker clears, history untouched. The branch
        // already has a verdict (has-pipeline), so the push builds - pinned to the new head - without
        // another observation.
        using var revived = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("feature/gone", WebhookPayloads.Commit('5')),
            host.SecretFor("deleting"),
            deliveryId: "delete-3");
        Assert.Equal(HttpStatusCode.Accepted, revived.StatusCode);
        var again = Assert.Single(await host.ReadBranchesAsync());
        Assert.Null(again.OrphanedAt);
        Assert.Null(again.OrphanedEvidence);
        Assert.Equal(2, (await host.ReadRunsAsync()).Count);
        Assert.Equal(run.Id, Assert.Single(await host.ReadRunsAsync(), candidate => candidate.Id == run.Id).Id);
    }

    // ------------------------------------------------------------------
    // TC-003: two branches never share history; the filter is project + tenant scoped
    // ------------------------------------------------------------------

    [Fact]
    public async Task Two_branches_never_share_history_and_the_filter_never_crosses_projects_or_tenants()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        var alphaWebhook = await host.SeedWebhookAsync("alpha-branches", branchFilter: string.Empty);
        var workerToken = await host.CreateWorkerAsync("branch-builder");

        // main: has-pipeline (observed) then Success. feature/x: unreadable once, then no-pipeline.
        // Both runs go through the real worker API, so the numbers, commits and statuses are real.
        using var mainPush = await host.DeliverSignedAsync(
            alphaWebhook,
            WebhookPayloads.GitHub("main", WebhookPayloads.Commit('6')),
            host.SecretFor("alpha-branches"),
            deliveryId: "tc3-1");
        _ = await host.ObserveOnceAsync(workerToken, Zenkins.Shared.Pipelines.DefinitionValidationOutcomes.HasPipeline);
        var successRun = await RunAsync(host, SpecJson, exitCode: 0);

        using var featurePush = await host.DeliverSignedAsync(
            alphaWebhook,
            WebhookPayloads.GitHub("feature/x", WebhookPayloads.Commit('7')),
            host.SecretFor("alpha-branches"),
            deliveryId: "tc3-2");
        _ = await host.ObserveOnceAsync(workerToken, Zenkins.Shared.Pipelines.DefinitionValidationOutcomes.HasPipeline);
        var failedRun = await RunAsync(host, specJson: null, exitCode: null, failureReason: "step-failed");

        Assert.NotEqual(successRun.Number, failedRun.Number);

        var mainRuns = await RunsAsync(host, host.ProjectId, "main");
        Assert.Equal(new[] { successRun.Number }, mainRuns.Select(run => run.Number!.Value));

        // The same branch name in another project of the same organization is a different branch job,
        // and its runs must never appear under alpha.
        var betaProjectId = await host.AddProjectAsync(
            host.OrganizationId,
            "beta-app",
            WebhooksTestHost.RepositoryUrl,
            WebhooksTestHost.DefaultBranch,
            BranchDiscoveryModes.Branches);
        var betaWebhook = await host.SeedWebhookAsync(
            "beta-branches",
            branchFilter: string.Empty,
            projectId: betaProjectId);
        using var betaPush = await host.DeliverSignedAsync(
            betaWebhook,
            WebhookPayloads.GitHub("main", WebhookPayloads.Commit('8')),
            host.SecretFor("beta-branches"),
            deliveryId: "tc3-3");
        _ = await host.ObserveOnceAsync(workerToken, Zenkins.Shared.Pipelines.DefinitionValidationOutcomes.HasPipeline);

        var alphaMain = await RunsAsync(host, host.ProjectId, "main");
        Assert.Equal(new[] { successRun.Number }, alphaMain.Select(run => run.Number!.Value));
        Assert.Single(await RunsAsync(host, betaProjectId, "main"));

        // The branch list is project-scoped too: alpha lists exactly its two branches, with their own
        // last statuses and numbers.
        var alphaBranches = await BranchesAsync(host, host.ProjectId);
        Assert.Equal(new[] { "feature/x", "main" }, alphaBranches.Select(branch => branch.Branch));
        Assert.Equal(successRun.Number, alphaBranches.Single(b => b.Branch == "main").LastRun?.Number);
        Assert.Equal(BuildRunStatus.Success, alphaBranches.Single(b => b.Branch == "main").LastRun?.Status);
        Assert.Equal(failedRun.Number, alphaBranches.Single(b => b.Branch == "feature/x").LastRun?.Number);
        Assert.Equal(BuildRunStatus.Failed, alphaBranches.Single(b => b.Branch == "feature/x").LastRun?.Status);

        var betaBranches = await BranchesAsync(host, betaProjectId);
        Assert.Equal(new[] { "main" }, betaBranches.Select(branch => branch.Branch));

        // A foreign tenant's list is empty rather than another organization's rows: the query filter,
        // not a permission check on the id.
        var foreignOrganizationId = await host.CreateForeignOrganizationAsync();
        var foreign = await host.Host.InScopeAsync(async services =>
        {
            services.GetRequiredService<Zenkins.Server.Data.Tenancy.TenantContext>().OrganizationId = foreignOrganizationId;
            return await services.GetRequiredService<BranchService>().GetBranchesAsync(host.ProjectId, CancellationToken);
        });
        Assert.Empty(foreign);
        Assert.Empty(await RunsAsync(host, host.ProjectId, "main", foreignOrganizationId));
    }

    [Fact]
    public async Task The_branch_list_keeps_a_run_whose_branch_has_no_registry_row()
    {
        // The run's snapshot is the authority. A project that predates the registry (or was switched
        // from single to branches) lists every branch its runs already name - with the definition the
        // run froze as the proof its pipeline existed.
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("history", branchFilter: string.Empty);
        _ = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub(WebhooksTestHost.DefaultBranch, WebhookPayloads.Commit('9')),
            host.SecretFor("history"),
            deliveryId: "history-1");
        _ = await RunAsync(host, SpecJson, exitCode: 0);

        Assert.Empty(await host.ReadBranchesAsync());

        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);

        var branch = Assert.Single(await BranchesAsync(host, host.ProjectId));
        Assert.Equal(WebhooksTestHost.DefaultBranch, branch.Branch);
        Assert.False(branch.HasRecord);
        Assert.Equal(BranchPipelineStates.HasPipeline, branch.PipelineState);
        Assert.NotNull(branch.LastRun);
        Assert.Equal(BuildRunStatus.Success, branch.LastRun!.Status);
    }

    // ------------------------------------------------------------------
    // Evidence from a run: the definition freezes → has-pipeline; pipeline-missing → no-pipeline
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_runs_definition_records_the_pipeline_verdict_on_its_branch()
    {
        // The run-side evidence path, independent of the observation: a manual run on a multibranch
        // branch freezes a definition (the file was there) or ends `pipeline-missing` (it was not), and
        // either way the branch row keeps the commit the worker reported.
        using var host = await WebhooksTestHost.CreateAsync();
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        using var owner = await host.SignInOwnerAsync();
        var workerToken = await host.CreateWorkerAsync("branch-evidence-agent");
        using var workerClient = host.CreateDeliveryClient();

        var hasPipelineCommit = WebhookPayloads.Commit('a');
        var runId = await RunTestHost.TriggerRunAsync(owner, host.ProjectId, branch: "feature/builds");
        using (var claimed = await host.ClaimAsync(workerToken))
        {
            Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(claimed));
        }

        using (var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, runId))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using (var definition = await RunTestHost.ReportDefinitionAsync(
            workerClient, workerToken, runId, SpecJson, hasPipelineCommit))
        {
            Assert.Equal(HttpStatusCode.OK, definition.StatusCode);
        }

        using (var completed = await RunTestHost.CompleteAsync(workerClient, workerToken, runId, exitCode: 0))
        {
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        }

        var observed = (await host.ReadBranchesAsync()).Single(row => row.Branch == "feature/builds");
        Assert.Equal(BranchPipelineStates.HasPipeline, observed.PipelineState);
        Assert.Equal(hasPipelineCommit, observed.ObservedCommit);

        // The second branch: the worker checks out the commit, finds no pipeline file, and reports
        // `pipeline-missing`. That report is the evidence that makes the branch no-pipeline - and from
        // then on a delivery for the same commit queues nothing.
        var missingCommit = WebhookPayloads.Commit('b');
        var missingRunId = await RunTestHost.TriggerRunAsync(owner, host.ProjectId, branch: "feature/docs");
        using (var claimedMissing = await host.ClaimAsync(workerToken))
        {
            Assert.Equal(missingRunId, await RunTestHost.ReadClaimedRunIdAsync(claimedMissing));
        }

        using (var acceptedMissing = await RunTestHost.AcceptAsync(workerClient, workerToken, missingRunId))
        {
            Assert.Equal(HttpStatusCode.OK, acceptedMissing.StatusCode);
        }

        using (var commitOnly = await RunTestHost.ReportDefinitionAsync(
            workerClient, workerToken, missingRunId, pipelineSpecJson: null, gitCommit: missingCommit))
        {
            Assert.Equal(HttpStatusCode.OK, commitOnly.StatusCode);
        }

        using (var missing = await RunTestHost.CompleteAsync(
            workerClient, workerToken, missingRunId, exitCode: null, failureReason: "pipeline-missing"))
        {
            Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        }

        var verdict = (await host.ReadBranchesAsync()).Single(row => row.Branch == "feature/docs");
        Assert.Equal(BranchPipelineStates.NoPipeline, verdict.PipelineState);
        Assert.Equal(missingCommit, verdict.ObservedCommit);

        // And the verdict gates the next push at that commit - no new run.
        var webhookId = await host.SeedWebhookAsync("evidence-gate", branchFilter: string.Empty);
        var runsBefore = (await host.ReadRunsAsync()).Count;
        using var after = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("feature/docs", missingCommit),
            host.SecretFor("evidence-gate"),
            deliveryId: "evidence-3");
        Assert.Equal(HttpStatusCode.Accepted, after.StatusCode);
        Assert.Equal(runsBefore, (await host.ReadRunsAsync()).Count);
        Assert.Equal(WebhookOutcomes.NoPipeline, (await host.ReadDeliveriesAsync(webhookId)).Single().Outcome);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Claims the single queued run, accepts it, reports the definition (when any) and completes it.</summary>
    private static async Task<BuildRun> RunAsync(
        WebhooksTestHost host,
        string? specJson,
        int? exitCode,
        string? failureReason = null)
    {
        var workerToken = await host.CreateWorkerAsync($"agent-{Guid.NewGuid():N}");
        using var workerClient = host.CreateDeliveryClient();
        using var claimed = await host.ClaimAsync(workerToken);
        var runId = await RunTestHost.ReadClaimedRunIdAsync(claimed);

        using var accepted = await RunTestHost.AcceptAsync(workerClient, workerToken, runId);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var run = await host.ReadRunAsync(runId);
        using var definition = await RunTestHost.ReportDefinitionAsync(
            workerClient, workerToken, runId, specJson, run.GitCommit);
        Assert.Equal(HttpStatusCode.OK, definition.StatusCode);

        using var completed = await RunTestHost.CompleteAsync(
            workerClient, workerToken, runId, exitCode, failureReason);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        return await host.ReadRunAsync(runId);
    }

    private static Task<IReadOnlyList<BranchView>> BranchesAsync(WebhooksTestHost host, Guid projectId) =>
        host.WithTenantAsync(services =>
            services.GetRequiredService<BranchService>().GetBranchesAsync(projectId, CancellationToken));

    private static Task<IReadOnlyList<RunView>> RunsAsync(WebhooksTestHost host, Guid projectId, string branch) =>
        host.WithTenantAsync(services =>
            services.GetRequiredService<RunService>().GetProjectRunsAsync(projectId, branch, CancellationToken));

    private static Task<IReadOnlyList<RunView>> RunsAsync(
        WebhooksTestHost host,
        Guid projectId,
        string branch,
        Guid organizationId) =>
        host.Host.InScopeAsync(async services =>
        {
            services.GetRequiredService<Zenkins.Server.Data.Tenancy.TenantContext>().OrganizationId = organizationId;
            return await services.GetRequiredService<RunService>()
                .GetProjectRunsAsync(projectId, branch, CancellationToken);
        });
}
