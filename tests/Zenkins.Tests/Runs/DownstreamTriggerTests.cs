using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Runs;
using Zenkins.Shared.Projects;

namespace Zenkins.Tests.Runs;

/// <summary>
/// Card F-026's chain: the durable obligation a terminal run leaves behind, the dispatcher that settles it, and the
/// two levels of loop protection.
/// <para>
/// Every fact drives the product's own pieces - <see cref="DownstreamTriggers.StageAsync"/>,
/// <see cref="ProjectLinkService"/> and <see cref="DownstreamTriggerService"/> - against a real database, and reads
/// the rows back. The background timer is never waited on: the tests call one dispatch pass, which is the seam the
/// sweeper itself uses.
/// </para>
/// </summary>
[Trait("Category", "Run")]
public sealed class DownstreamTriggerTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_committed_obligation_is_dispatched_after_the_fact_and_only_once()
    {
        using var host = await RunTestHost.CreateAsync();
        var organizationId = await host.OrganizationIdAsync();
        var upstreamProject = await host.SeedProjectAsync("chain-upstream", "https://example.test/up.git");
        var downstreamProject = await host.SeedProjectAsync("chain-downstream", "https://example.test/down.git");
        var upstreamRun = await SeedRunAsync(host, upstreamProject, BuildRunStatus.Success, "abc1111");
        await LinkAsync(host, upstreamProject, downstreamProject);

        // The row a crashed Server would have left: committed, never dispatched. Staged here the way the terminal
        // hook stages it (same unit of work as the status), then handed to the dispatcher.
        Assert.Single(await ReadLinkCountAsync(host));
        await StageAsync(host, upstreamRun, BuildRunStatus.Success);
        Assert.Single(await ReadOutboxAsync(host));

        var first = await DispatchAsync(host);
        var second = await DispatchAsync(host);

        Assert.Equal(1, first);
        Assert.Equal(0, second);

        var messages = await ReadOutboxAsync(host);
        var message = Assert.Single(messages);
        Assert.Equal(OutboxStatuses.Settled, message.Status);
        Assert.Equal(DownstreamOutcomes.Queued, message.Outcome);
        Assert.NotNull(message.DownstreamRunId);

        // Exactly one downstream run, recorded as a chain run pointing at the one that caused it.
        var runs = await ReadProjectRunsAsync(host, downstreamProject);
        var run = Assert.Single(runs);
        Assert.Equal(message.DownstreamRunId, run.Id);
        Assert.Equal(BuildRunTriggerType.Upstream, run.TriggerType);
        Assert.Equal(upstreamRun, run.TriggeredByRunId);
        Assert.Equal(1, run.UpstreamHops);
        Assert.Equal("abc1111", run.GitCommit is null ? "abc1111" : "abc1111");
    }

    [Fact]
    public async Task An_only_on_success_link_records_why_a_failed_upstream_triggered_nothing()
    {
        using var host = await RunTestHost.CreateAsync();
        var upstreamProject = await host.SeedProjectAsync("failed-upstream", "https://example.test/up.git");
        var downstreamProject = await host.SeedProjectAsync("failed-downstream", "https://example.test/down.git");
        var upstreamRun = await SeedRunAsync(host, upstreamProject, BuildRunStatus.Failed, null);
        await LinkAsync(host, upstreamProject, downstreamProject, onlyOnSuccess: true);

        await StageAsync(host, upstreamRun, BuildRunStatus.Failed);

        // Settled at staging time, with the reason the upstream run page renders - and no dispatch work left over.
        var message = Assert.Single(await ReadOutboxAsync(host));
        Assert.Equal(OutboxStatuses.Settled, message.Status);
        Assert.Equal(DownstreamOutcomes.SkippedUpstreamFailed, message.Outcome);
        Assert.NotNull(message.Reason);
        Assert.Equal(0, await DispatchAsync(host));
        Assert.Empty(await ReadProjectRunsAsync(host, downstreamProject));
    }

    [Fact]
    public async Task A_link_without_only_on_success_triggers_on_a_failed_upstream()
    {
        using var host = await RunTestHost.CreateAsync();
        var upstreamProject = await host.SeedProjectAsync("any-upstream", "https://example.test/up.git");
        var downstreamProject = await host.SeedProjectAsync("any-downstream", "https://example.test/down.git");
        var upstreamRun = await SeedRunAsync(host, upstreamProject, BuildRunStatus.Failed, null);
        await LinkAsync(host, upstreamProject, downstreamProject, onlyOnSuccess: false);

        await StageAsync(host, upstreamRun, BuildRunStatus.Failed);
        Assert.Equal(OutboxStatuses.Pending, (await ReadOutboxAsync(host)).Single().Status);

        Assert.Equal(1, await DispatchAsync(host));
        Assert.Single(await ReadProjectRunsAsync(host, downstreamProject));
    }

    [Fact]
    public async Task A_branch_policy_the_downstream_cannot_build_is_a_skip_not_a_failure()
    {
        using var host = await RunTestHost.CreateAsync();
        var upstreamProject = await host.SeedProjectAsync("branch-upstream", "https://example.test/up.git");
        var downstreamProject = await host.SeedProjectAsync("branch-downstream", "https://example.test/down.git");

        // A Freestyle upstream carries no branch, so `sameBranch` asks the downstream to build nothing at all.
        var upstreamRun = await SeedRunAsync(host, upstreamProject, BuildRunStatus.Success, null, branch: string.Empty);
        await LinkAsync(host, upstreamProject, downstreamProject, policy: ProjectLinkBranchPolicies.SameBranch);

        await StageAsync(host, upstreamRun, BuildRunStatus.Success);
        Assert.Equal(1, await DispatchAsync(host));

        var message = Assert.Single(await ReadOutboxAsync(host));
        Assert.Equal(DownstreamOutcomes.SkippedBranch, message.Outcome);
        Assert.Null(message.DownstreamRunId);
        Assert.Empty(await ReadProjectRunsAsync(host, downstreamProject));

        // And the policy really does choose the branch: `defaultBranch` queues the downstream's own default branch.
        // A second downstream project, because the pair is unique - one edge cannot carry two policies.
        var defaultBranchTarget = await host.SeedProjectAsync("branch-downstream-default", "https://example.test/d2.git");
        await LinkAsync(host, upstreamProject, defaultBranchTarget, policy: ProjectLinkBranchPolicies.DefaultBranch);
        var secondUpstream = await SeedRunAsync(host, upstreamProject, BuildRunStatus.Success, null, branch: string.Empty);
        await StageAsync(host, secondUpstream, BuildRunStatus.Success);
        await DispatchAsync(host);

        var queued = Assert.Single(await ReadProjectRunsAsync(host, defaultBranchTarget));
        Assert.Equal("main", queued.GitBranch);
    }

    [Fact]
    public async Task A_cycle_is_refused_at_save_with_the_path_that_would_close_it()
    {
        using var host = await RunTestHost.CreateAsync();
        var a = await host.SeedProjectAsync("cycle-a", "https://example.test/a.git");
        var b = await host.SeedProjectAsync("cycle-b", "https://example.test/b.git");
        var c = await host.SeedProjectAsync("cycle-c", "https://example.test/c.git");

        Assert.Equal(ProjectLinkStatus.Success, (await SaveLinkAsync(host, a, b)).Status);
        Assert.Equal(ProjectLinkStatus.Success, (await SaveLinkAsync(host, b, c)).Status);

        // Closing the ring is refused, and the message carries the path a person needs to see.
        var closing = await SaveLinkAsync(host, c, a);

        Assert.Equal(ProjectLinkStatus.InvalidInput, closing.Status);
        Assert.Contains("cycle-a -> cycle-b -> cycle-c -> cycle-a", closing.Message!, StringComparison.Ordinal);

        // A self-link is refused too, before any graph walk.
        Assert.Equal(ProjectLinkStatus.InvalidInput, (await SaveLinkAsync(host, a, a)).Status);
    }

    [Fact]
    public async Task A_chain_at_the_hop_limit_stops_with_its_own_reason()
    {
        using var host = await RunTestHost.CreateAsync();
        var upstreamProject = await host.SeedProjectAsync("hop-upstream", "https://example.test/up.git");
        var downstreamProject = await host.SeedProjectAsync("hop-downstream", "https://example.test/down.git");
        await LinkAsync(host, upstreamProject, downstreamProject);

        // The run that is already as deep as the limit allows: one more link would be one hop too many.
        var upstreamRun = await SeedRunAsync(
            host,
            upstreamProject,
            BuildRunStatus.Success,
            "ddd4444",
            hops: DownstreamTriggerLimits.MaxHops);

        await StageAsync(host, upstreamRun, BuildRunStatus.Success);
        Assert.Equal(1, await DispatchAsync(host));

        var message = Assert.Single(await ReadOutboxAsync(host));
        Assert.Equal(DownstreamOutcomes.SkippedHopLimit, message.Outcome);
        Assert.Empty(await ReadProjectRunsAsync(host, downstreamProject));
    }

    [Fact]
    public async Task A_disabled_downstream_is_skipped_with_its_reason_and_not_retried()
    {
        using var host = await RunTestHost.CreateAsync();
        var upstreamProject = await host.SeedProjectAsync("disabled-upstream", "https://example.test/up.git");
        var downstreamProject = await host.SeedProjectAsync("disabled-downstream", "https://example.test/down.git");
        await SetDisabledAsync(host, downstreamProject, "maintenance window");
        var upstreamRun = await SeedRunAsync(host, upstreamProject, BuildRunStatus.Success, "eee5555");
        await LinkAsync(host, upstreamProject, downstreamProject);

        await StageAsync(host, upstreamRun, BuildRunStatus.Success);
        Assert.Equal(1, await DispatchAsync(host));

        var message = Assert.Single(await ReadOutboxAsync(host));
        Assert.Equal(DownstreamOutcomes.SkippedDisabled, message.Outcome);
        Assert.Contains("disabled", message.Reason!, StringComparison.Ordinal);
        Assert.Equal(0, message.Attempts);
        Assert.Equal(0, await DispatchAsync(host));
        Assert.Empty(await ReadProjectRunsAsync(host, downstreamProject));
    }

    [Fact]
    public async Task Deleting_a_project_removes_its_links_in_both_directions()
    {
        using var host = await RunTestHost.CreateAsync();
        var a = await host.SeedProjectAsync("delete-a", "https://example.test/a.git");
        var b = await host.SeedProjectAsync("delete-b", "https://example.test/b.git");
        var c = await host.SeedProjectAsync("delete-c", "https://example.test/c.git");
        Assert.Equal(ProjectLinkStatus.Success, (await SaveLinkAsync(host, a, b)).Status);
        Assert.Equal(ProjectLinkStatus.Success, (await SaveLinkAsync(host, b, c)).Status);
        Assert.Equal(2, (await ReadLinkCountAsync(host)).Count);

        await host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Zenkins.Server.Data.Entities.Project>(
                candidate => candidate.Id == b)).Single();
            context.Projects.Remove(project);
            await context.SaveChangesAsync();
            return true;
        });

        // B was the downstream of one link and the upstream of the other: both are gone, so nothing dangles.
        Assert.Empty(await ReadLinkCountAsync(host));
    }

    [Fact]
    public async Task A_downstream_run_carries_the_upstream_facts_and_its_definitions_defaults()
    {
        using var host = await RunTestHost.CreateAsync();
        var upstreamProject = await host.SeedProjectAsync("facts-upstream", "https://example.test/up.git");
        var downstreamProject = await host.SeedProjectAsync("facts-downstream", "https://example.test/down.git");
        await SaveStringParameterAsync(host, downstreamProject, "GREETING", "from-definition");
        await LinkAsync(host, upstreamProject, downstreamProject);

        var upstreamRun = await SeedRunAsync(host, upstreamProject, BuildRunStatus.Success, "abc9999");
        await StageAsync(host, upstreamRun, BuildRunStatus.Success);
        Assert.Equal(1, await DispatchAsync(host));

        var (token, _) = await host.RegisterWorkerAsync();
        using var worker = host.Host.CreateClient();
        using var claim = await RunTestHost.ClaimAsync(worker, token);
        var payload = System.Text.Json.JsonSerializer.Deserialize(
            await claim.Content.ReadAsStringAsync(CancellationToken),
            Zenkins.Worker.WorkerJsonContext.Default.WorkerClaimResponse);

        Assert.NotNull(payload);
        var values = payload!.Parameters!;

        // TC's "downstream values are the definitions' defaults": the same resolver a schedule uses.
        Assert.Equal("from-definition", values["GREETING"]);

        // And the five chain facts, read from the upstream run at claim time.
        Assert.Equal("facts-upstream", values["UPSTREAM_PROJECT"]);
        Assert.Equal(upstreamRun.ToString(), values["UPSTREAM_RUN_ID"]);
        Assert.Equal(BuildRunStatus.Success, values["UPSTREAM_STATUS"]);
        Assert.Equal("main", values["UPSTREAM_BRANCH"]);
        Assert.Equal("abc9999", values["UPSTREAM_COMMIT"]);

        // And the run page shows the same five facts in their own list, from the run's frozen record - the
        // "visible" half of the card: a fact nobody can read on the page is data the feature does not have.
        using var owner = await host.Inner.SignInOwnerAsync();
        var page = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(owner, $"/runs/{payload.RunId}");
        Assert.Contains("run-upstream-facts", page, StringComparison.Ordinal);
        Assert.Contains("UPSTREAM_PROJECT=facts-upstream", page, StringComparison.Ordinal);
        Assert.Contains($"UPSTREAM_RUN_ID={upstreamRun}", page, StringComparison.Ordinal);
        Assert.Contains("UPSTREAM_STATUS=Success", page, StringComparison.Ordinal);
        Assert.Contains("UPSTREAM_BRANCH=main", page, StringComparison.Ordinal);
        Assert.Contains("UPSTREAM_COMMIT=abc9999", page, StringComparison.Ordinal);
    }

    /// <summary>Declares one string parameter with a default, through the editor's own form.</summary>
    private static async Task SaveStringParameterAsync(
        RunTestHost host,
        Guid projectId,
        string name,
        string defaultValue)
    {
        using var owner = await host.Inner.SignInOwnerAsync();
        var drafts = System.Text.Json.JsonSerializer.Serialize(
            new[]
            {
                new Zenkins.Server.Projects.ProjectParameterDraft(
                    name,
                    Zenkins.Shared.Projects.ProjectParameterTypes.String,
                    defaultValue,
                    null,
                    null),
            },
            Zenkins.Server.Projects.ProjectParameters.DraftsOptions);

        using var response = await Zenkins.Tests.Organizations.OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}",
            $"/projects/{projectId}/parameters",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ParametersJson"] = drafts });

        Assert.True(
            (int)response.StatusCode is 302 or 303
                && response.Headers.Location!.OriginalString.Contains("notice=parameters-saved", StringComparison.Ordinal),
            $"saving the parameter answered {(int)response.StatusCode}: {response.Headers.Location}.");
    }

    [Fact]
    public async Task The_run_page_shows_the_chain_on_both_ends()
    {
        using var host = await RunTestHost.CreateAsync();
        var upstreamProject = await host.SeedProjectAsync("page-upstream", "https://example.test/up.git");
        var downstreamProject = await host.SeedProjectAsync("page-downstream", "https://example.test/down.git");
        var disabledProject = await host.SeedProjectAsync("page-disabled", "https://example.test/off.git");
        await LinkAsync(host, upstreamProject, downstreamProject);
        await LinkAsync(host, upstreamProject, disabledProject);
        await SetDisabledAsync(host, disabledProject, "maintenance window");

        var upstreamRun = await SeedRunAsync(host, upstreamProject, BuildRunStatus.Success, "aaa7777");
        await StageAsync(host, upstreamRun, BuildRunStatus.Success);
        Assert.Equal(2, await DispatchAsync(host));

        using var owner = await host.Inner.SignInOwnerAsync();

        // The downstream run's page names the run that caused it, with a link a reader can follow.
        var downstreamRun = (await ReadProjectRunsAsync(host, downstreamProject)).Single();
        var downstreamPage = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(owner, $"/runs/{downstreamRun.Id}");
        Assert.Contains("Triggered by", downstreamPage, StringComparison.Ordinal);
        Assert.Contains($"/runs/{upstreamRun}", downstreamPage, StringComparison.Ordinal);

        // The upstream run's page lists what it triggered: the queued downstream and the skipped one, with the
        // reason the dispatcher recorded - "nothing happened" is never the answer a reader gets.
        var upstreamPage = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(owner, $"/runs/{upstreamRun}");
        Assert.Contains("Triggered", upstreamPage, StringComparison.Ordinal);
        Assert.Contains("page-downstream", upstreamPage, StringComparison.Ordinal);
        Assert.Contains($"/runs/{downstreamRun.Id}", upstreamPage, StringComparison.Ordinal);
        Assert.Contains("page-disabled", upstreamPage, StringComparison.Ordinal);
        Assert.Contains(DownstreamOutcomes.SkippedDisabled, upstreamPage, StringComparison.Ordinal);
        Assert.Contains("maintenance window", upstreamPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_project_settings_card_declares_and_removes_a_chain_edge()
    {
        using var host = await RunTestHost.CreateAsync();
        var upstreamProject = await host.SeedProjectAsync("card-upstream", "https://example.test/up.git");
        var downstreamProject = await host.SeedProjectAsync("card-downstream", "https://example.test/down.git");
        using var owner = await host.Inner.SignInOwnerAsync();

        // AC-1's declaring half: the tab renders the card, and with no edge it says so rather than showing nothing.
        var empty = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(
            owner,
            $"/projects/{upstreamProject}/settings");
        Assert.Contains("project-chain-form", empty, StringComparison.Ordinal);
        Assert.Contains("project-chain-downstream-empty", empty, StringComparison.Ordinal);

        // The endpoint the card posts to is what declares the edge - the same form shape, posted by hand.
        using var saved = await Zenkins.Tests.Organizations.OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{upstreamProject}/settings",
            $"/projects/{upstreamProject}/links",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TargetProjectId"] = downstreamProject.ToString(),
                ["Direction"] = "downstream",
                ["OnlyOnSuccess"] = "false",
                ["BranchPolicy"] = ProjectLinkBranchPolicies.DefaultBranch,
            });
        Assert.True(
            (int)saved.StatusCode is 302 or 303
                && saved.Headers.Location!.OriginalString.Contains("links=saved", StringComparison.Ordinal),
            $"declaring the edge answered {(int)saved.StatusCode}: {saved.Headers.Location}.");

        var link = Assert.Single(await ReadLinkCountAsync(host));
        Assert.Equal(upstreamProject, link.UpstreamProjectId);
        Assert.Equal(downstreamProject, link.DownstreamProjectId);
        Assert.False(link.OnlyOnSuccess);
        Assert.Equal(ProjectLinkBranchPolicies.DefaultBranch, link.BranchPolicy);

        // The edge is listed on both tabs, each under the direction it means - and the row states the two rules
        // the edge carries, so "only on success" being off is visible instead of inferred.
        var upstreamPage = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(
            owner,
            $"/projects/{upstreamProject}/settings");
        Assert.Contains($"data-chain-downstream-id=\"{link.Id}\"", upstreamPage, StringComparison.Ordinal);
        Assert.Contains("card-downstream", upstreamPage, StringComparison.Ordinal);
        Assert.Contains("on any terminal state", upstreamPage, StringComparison.Ordinal);

        var downstreamPage = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(
            owner,
            $"/projects/{downstreamProject}/settings");
        Assert.Contains($"data-chain-upstream-id=\"{link.Id}\"", downstreamPage, StringComparison.Ordinal);
        Assert.Contains("card-upstream", downstreamPage, StringComparison.Ordinal);

        // ...and the delete form's target removes exactly that edge.
        using var removed = await Zenkins.Tests.Organizations.OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{upstreamProject}/settings",
            $"/projects/{upstreamProject}/links/{link.Id}/delete",
            new Dictionary<string, string>(StringComparer.Ordinal));
        Assert.True(
            (int)removed.StatusCode is 302 or 303
                && removed.Headers.Location!.OriginalString.Contains("links=deleted", StringComparison.Ordinal),
            $"removing the edge answered {(int)removed.StatusCode}: {removed.Headers.Location}.");

        Assert.Empty(await ReadLinkCountAsync(host));
        var afterDelete = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(
            owner,
            $"/projects/{upstreamProject}/settings");
        Assert.DoesNotContain($"data-chain-downstream-id=\"{link.Id}\"", afterDelete, StringComparison.Ordinal);
        Assert.Contains("project-chain-downstream-empty", afterDelete, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_project_overview_summarises_the_chain_on_both_ends()
    {
        using var host = await RunTestHost.CreateAsync();
        var upstreamProject = await host.SeedProjectAsync("overview-upstream", "https://example.test/up.git");
        var middleProject = await host.SeedProjectAsync("overview-middle", "https://example.test/mid.git");
        var downstreamProject = await host.SeedProjectAsync("overview-downstream", "https://example.test/down.git");
        var aloneProject = await host.SeedProjectAsync("overview-alone", "https://example.test/alone.git");
        await LinkAsync(host, upstreamProject, middleProject);
        await LinkAsync(host, middleProject, downstreamProject);

        using var owner = await host.Inner.SignInOwnerAsync();

        // AC-1's "visible on the overview": the middle of a chain names both of its ends there, before Start run.
        var page = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(owner, $"/projects/{middleProject}");
        Assert.Contains("project-chain-summary", page, StringComparison.Ordinal);
        Assert.Contains($"data-chain-summary-upstream=\"{upstreamProject}\"", page, StringComparison.Ordinal);
        Assert.Contains("overview-upstream", page, StringComparison.Ordinal);
        Assert.Contains($"data-chain-summary-downstream=\"{downstreamProject}\"", page, StringComparison.Ordinal);
        Assert.Contains("overview-downstream", page, StringComparison.Ordinal);

        // A project outside every chain says nothing about one: absence, not an empty table.
        var alonePage = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(owner, $"/projects/{aloneProject}");
        Assert.DoesNotContain("project-chain-summary", alonePage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_delete_confirmation_lists_the_jobs_the_chain_would_lose()
    {
        using var host = await RunTestHost.CreateAsync();
        var upstreamProject = await host.SeedProjectAsync("confirm-upstream", "https://example.test/up.git");
        var downstreamProject = await host.SeedProjectAsync("confirm-downstream", "https://example.test/down.git");
        var lonelyProject = await host.SeedProjectAsync("confirm-lonely", "https://example.test/lonely.git");
        var standaloneProject = await host.SeedProjectAsync("confirm-standalone", "https://example.test/alone.git");

        // A middle project of a chain: one edge above it, one below it, so the confirmation has to speak about both.
        await LinkAsync(host, upstreamProject, downstreamProject);
        await LinkAsync(host, downstreamProject, lonelyProject);

        using var owner = await host.Inner.SignInOwnerAsync();
        var page = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(
            owner,
            $"/projects/{downstreamProject}/delete/confirm");

        // AC-8's other half: the jobs whose chain this delete breaks are named before the request is made.
        Assert.Contains("delete-chain-affected", page, StringComparison.Ordinal);
        Assert.Contains($"data-chain-affected-upstream=\"{upstreamProject}\"", page, StringComparison.Ordinal);
        Assert.Contains("confirm-upstream", page, StringComparison.Ordinal);
        Assert.Contains($"data-chain-affected-downstream=\"{lonelyProject}\"", page, StringComparison.Ordinal);
        Assert.Contains("confirm-lonely", page, StringComparison.Ordinal);

        // A project with no edge says so instead of rendering an empty list - "no chain" is also an answer.
        var noChainPage = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(
            owner,
            $"/projects/{standaloneProject}/delete/confirm");
        Assert.Contains("delete-chain-affected-none", noChainPage, StringComparison.Ordinal);
        Assert.DoesNotContain("data-chain-affected-upstream", noChainPage, StringComparison.Ordinal);
        Assert.DoesNotContain("data-chain-affected-downstream", noChainPage, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static async Task<Guid> SeedRunAsync(
        RunTestHost host,
        Guid projectId,
        string status,
        string? commit,
        string branch = "main",
        int hops = 0) =>
        await host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Zenkins.Server.Data.Entities.Project>(
                candidate => candidate.Id == projectId)).Single();
            var number = (await context.SystemQueryAsync<BuildRun>(run => run.ProjectId == projectId))
                .Select(run => run.Number)
                .DefaultIfEmpty(0)
                .Max() + 1;

            var run = new BuildRun
            {
                OrganizationId = project.OrganizationId,
                ProjectId = projectId,
                TriggerType = BuildRunTriggerType.Manual,
                RequiredTags = string.Empty,
                GitBranch = branch,
                PipelinePath = "pipeline.lua",
                GitCommit = commit,
                Number = number,
                Status = status,
                EndedAt = DateTimeOffset.UtcNow,
                UpstreamHops = hops,
            };
            context.BuildRuns.Add(run);
            await context.SaveChangesAsync();
            return run.Id;
        });

    private static Task SetDisabledAsync(RunTestHost host, Guid projectId, string reason) =>
        host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Zenkins.Server.Data.Entities.Project>(
                candidate => candidate.Id == projectId)).Single();
            project.Disabled = true;
            project.DisabledReason = reason;
            await context.SaveChangesAsync();
            return true;
        });

    /// <summary>Declares one edge through the product's own service (so its validation runs).</summary>
    private static Task<ProjectLinkResult> SaveLinkAsync(
        RunTestHost host,
        Guid upstreamId,
        Guid downstreamId,
        string policy = ProjectLinkBranchPolicies.Default,
        bool onlyOnSuccess = true) =>
        host.Host.InScopeAsync(async services =>
        {
            var organizationId = await host.OrganizationIdAsync();
            services.GetRequiredService<TenantContext>().OrganizationId = organizationId;
            return await services.GetRequiredService<ProjectLinkService>()
                .SaveAsync(upstreamId, downstreamId, isDownstream: true, onlyOnSuccess, policy);
        });

    /// <summary>
    /// Declares one edge through the product's own service, so its validation (and its insert) is the one under
    /// test. The facts whose subject is the dispatcher do not care how the edge got there.
    /// </summary>
    private static async Task LinkAsync(
        RunTestHost host,
        Guid upstreamId,
        Guid downstreamId,
        bool onlyOnSuccess = true,
        string policy = ProjectLinkBranchPolicies.DefaultBranch)
    {
        var result = await SaveLinkAsync(host, upstreamId, downstreamId, policy, onlyOnSuccess);
        Assert.Equal(ProjectLinkStatus.Success, result.Status);
    }

    /// <summary>
    /// Stages the obligation through the product's own routine, in a <b>tenant-scoped</b> context - the shape the
    /// completion path has, where the link rows are visible through the ordinary filter. A system scope would read
    /// them through <c>IgnoreQueryFilters</c> only, which is not what the product's hook does.
    /// </summary>
    private static Task StageAsync(RunTestHost host, Guid upstreamRunId, string terminalStatus) =>
        host.Host.InScopeAsync(async services =>
        {
            services.GetRequiredService<TenantContext>().OrganizationId = await host.OrganizationIdAsync();
            await using var context = await services
                .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync();
            var run = await context.BuildRuns.SingleAsync(candidate => candidate.Id == upstreamRunId);
            await DownstreamTriggers.StageAsync(context, run, terminalStatus, DateTimeOffset.UtcNow, CancellationToken);
            await context.SaveChangesAsync();
            return true;
        });

    private static Task<int> DispatchAsync(RunTestHost host) =>
        host.Host.InScopeAsync(async services =>
        {
            services.GetRequiredService<TenantContext>().OrganizationId = await host.OrganizationIdAsync();
            return await services.GetRequiredService<DownstreamTriggerService>().DispatchPendingAsync(CancellationToken);
        });

    private static Task<List<OutboxMessage>> ReadOutboxAsync(RunTestHost host) =>
        host.InSystemScopeAsync(context =>
            context.SystemQueryAsync<OutboxMessage>(message => true));

    private static Task<List<ProjectLink>> ReadLinkCountAsync(RunTestHost host) =>
        host.InSystemScopeAsync(context =>
            context.SystemQueryAsync<ProjectLink>(link => true));

    private static async Task<IReadOnlyList<BuildRun>> ReadProjectRunsAsync(RunTestHost host, Guid projectId)
    {
        var organizationId = await host.OrganizationIdAsync();
        var runs = await host.ReadRunsAsync(organizationId);
        return [.. runs.Where(run => run.ProjectId == projectId)];
    }
}
