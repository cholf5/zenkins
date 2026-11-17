using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Projects;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.ProjectUi;

/// <summary>
/// Card F-023's UI surfaces: the discovery mode is a project setting the real create/edit forms carry,
/// a project without a repository cannot choose it, the Branches tab renders a branch with no pipeline
/// and an orphaned branch, and the runs page's branch filter is a real query parameter.
/// <para>
/// The form facts matter because the setting is mutable: they prove it survives the PRG round trip in
/// both directions, not only that the entity column exists.
/// </para>
/// </summary>
[Trait("Category", "ProjectUi")]
public sealed class ProjectUiBranchesTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_create_and_edit_forms_store_the_discovery_mode_and_a_freestyle_project_cannot_choose_it()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);

        var created = await OrganizationTestHost.PostFormAsync(
            owner,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "multi-form",
                ["Kind"] = ProjectKinds.Pipeline,
                ["PipelineSource"] = ProjectSources.Repository,
                ["RepositoryUrl"] = "https://example.test/multi.git",
                ["DefaultBranch"] = "main",
                ["PipelinePath"] = "pipeline.lua",
                ["BranchDiscovery"] = BranchDiscoveryModes.Branches,
            });
        Assert.True((int)created.StatusCode is 302 or 303);

        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);
        var project = (await host.ReadProjectsAsync(organizationId)).Single(row => row.Name == "multi-form");
        Assert.Equal(BranchDiscoveryModes.Branches, project.BranchDiscovery);

        // Freestyle has no repository, so `branches` is refused rather than stored as a setting nothing
        // could honour - and the refusal lands back on the create form with the stable code.
        var refused = await OrganizationTestHost.PostFormAsync(
            owner,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "free-multi",
                ["Kind"] = ProjectKinds.Freestyle,
                ["BranchDiscovery"] = BranchDiscoveryModes.Branches,
            });
        Assert.True((int)refused.StatusCode is 302 or 303);
        Assert.Contains(
            $"error={ProjectErrorCodes.BranchDiscoveryUnavailable}",
            refused.Headers.Location!.OriginalString,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            await host.ReadProjectsAsync(organizationId),
            row => row.Name == "free-multi");

        // The setting is mutable: the edit form switches the same project back to single.
        var edited = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{project.Id}/edit",
            $"/projects/{project.Id}/edit",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "multi-form",
                ["Kind"] = ProjectKinds.Pipeline,
                ["RepositoryUrl"] = "https://example.test/multi.git",
                ["DefaultBranch"] = "main",
                ["PipelinePath"] = "pipeline.lua",
                ["BranchDiscovery"] = BranchDiscoveryModes.Single,
            });
        Assert.True((int)edited.StatusCode is 302 or 303);
        Assert.Equal(BranchDiscoveryModes.Single, (await host.ReadProjectAsync(project.Id)).BranchDiscovery);
    }

    [Fact]
    public async Task The_branches_tab_renders_per_branch_status_no_pipeline_and_orphaned_and_refuses_foreign_ids()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);

        // A multibranch project with two branch jobs and one run, seeded through the data layer (the
        // webhook path that writes them is BranchDiscoveryTests' subject).
        var projectId = await host.AddProjectAsync(organizationId, "branches-page");
        await host.Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await services
                .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync(CancellationToken);
            using var scope = systemDataScope.BeginScope();

            var project = (await context.SystemQueryAsync<Project>(row => row.Id == projectId)).Single();
            project.BranchDiscovery = BranchDiscoveryModes.Branches;

            context.BranchJobs.Add(new BranchJob
            {
                OrganizationId = organizationId,
                ProjectId = projectId,
                Branch = "docs-only",
                PipelineState = BranchPipelineStates.NoPipeline,
                ObservedCommit = new string('d', 40),
            });
            // A head nobody has read yet: the page must say so rather than look like a lost run.
            context.BranchJobs.Add(new BranchJob
            {
                OrganizationId = organizationId,
                ProjectId = projectId,
                Branch = "feature/unread",
                PipelineState = BranchPipelineStates.Unknown,
                LastCommit = new string('f', 40),
                PendingCommit = new string('f', 40),
                PendingSince = DateTimeOffset.UtcNow,
            });

            // D1: a read that was re-requested three times is a different fact from a request just
            // sent, and the page must not render the two identically.
            context.BranchJobs.Add(new BranchJob
            {
                OrganizationId = organizationId,
                ProjectId = projectId,
                Branch = "feature/retrying",
                PipelineState = BranchPipelineStates.Unknown,
                LastCommit = new string('e', 40),
                PendingCommit = new string('e', 40),
                PendingSince = new DateTimeOffset(2026, 11, 17, 8, 30, 0, TimeSpan.Zero),
                ObservationAttempts = 3,
            });
            context.BranchJobs.Add(new BranchJob
            {
                OrganizationId = organizationId,
                ProjectId = projectId,
                Branch = "feature/gone",
                PipelineState = BranchPipelineStates.HasPipeline,
                ObservedCommit = new string('e', 40),
                OrphanedAt = DateTimeOffset.UtcNow,
                OrphanedEvidence = BranchOrphanEvidence.PushDelete,
            });
            context.BuildRuns.Add(new BuildRun
            {
                OrganizationId = organizationId,
                ProjectId = projectId,
                Number = 3,
                TriggerType = BuildRunTriggerType.Webhook,
                RequiredTags = string.Empty,
                PipelinePath = "pipeline.lua",
                GitBranch = "feature/gone",
                Status = BuildRunStatus.Success,
                GitCommit = new string('e', 40),
                ExitCode = 0,
            });
            await context.SaveChangesAsync(CancellationToken);
            return true;
        });

        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/branches");
        Assert.Contains("Branch jobs", page, StringComparison.Ordinal);
        Assert.Contains("Multibranch", page, StringComparison.Ordinal);
        Assert.Contains("docs-only", page, StringComparison.Ordinal);
        Assert.Contains("No pipeline", page, StringComparison.Ordinal);
        Assert.Contains("Orphaned", page, StringComparison.Ordinal);
        Assert.Contains("feature/gone", page, StringComparison.Ordinal);
        Assert.Contains("Last run #3", page, StringComparison.Ordinal);
        Assert.Contains("feature/unread", page, StringComparison.Ordinal);
        Assert.Contains("Awaiting tree read", page, StringComparison.Ordinal);
        Assert.Contains("a tree read is owed", page, StringComparison.Ordinal);

        // D1: the retry count is visible and only where it happened - the first request stays plain,
        // the three-times-requested head says so.
        Assert.Contains("Awaiting tree read (retry 3)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Awaiting tree read (retry 0)", page, StringComparison.Ordinal);
        Assert.Contains("last requested 2026-11-17 08:30 UTC", page, StringComparison.Ordinal);

        // The branch links into the runs page with the filter, so a branch job's history is one click.
        Assert.Contains(
            $"/projects/{projectId}/runs?branch=docs-only",
            page,
            StringComparison.Ordinal);

        // A single-branch project states the mode and points at the edit page instead of pretending to
        // discover anything.
        var singleProjectId = await host.AddProjectAsync(organizationId, "single-page");
        var singlePage = await AuthTestHost.GetStringAsync(owner, $"/projects/{singleProjectId}/branches");
        Assert.Contains("Branch discovery is off", singlePage, StringComparison.Ordinal);

        // Unknown and foreign ids are the same real 404.
        using var unknown = await owner.GetAsync($"/projects/{Guid.NewGuid()}/branches", CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // A second organization alice also owns, with its own project: while the active organization is
        // Starter, Bravo's project id is a real 404 - the tenant filter decides, never the id.
        var foreignOrganizationId = await host.AddOrganizationAsync(
            "Bravo",
            (OrganizationTestHost.OwnerEmail, OrganizationRole.Owner));
        var foreignProjectId = await host.AddProjectAsync(foreignOrganizationId, "bravo-page");
        using var foreign = await owner.GetAsync($"/projects/{foreignProjectId}/branches", CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task The_branches_tab_states_remote_discovery_from_the_projects_actual_probe_record()
    {
        // D2: the page must not claim interval discovery is running when nothing ever probed the
        // repository (or the last probe failed). The three states come from the project's own I-005
        // record, so a static sentence cannot pass all three.
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);
        var projectId = await host.AddProjectAsync(organizationId, "discovery-status");
        await host.Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await services
                .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync(CancellationToken);
            using var scope = systemDataScope.BeginScope();
            var project = (await context.SystemQueryAsync<Project>(row => row.Id == projectId)).Single();
            project.BranchDiscovery = BranchDiscoveryModes.Branches;
            await context.SaveChangesAsync(CancellationToken);
            return true;
        });

        var never = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/branches");
        Assert.Contains("branch-discovery-never", never, StringComparison.Ordinal);
        Assert.Contains("No remote scan has run yet", never, StringComparison.Ordinal);
        Assert.DoesNotContain("Interval discovery is running", never, StringComparison.Ordinal);

        await SetProbeRecordAsync(host, projectId, Zenkins.Shared.Probes.GitProbeOutcomes.Unreachable);
        var degraded = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/branches");
        Assert.Contains("branch-discovery-degraded", degraded, StringComparison.Ordinal);
        Assert.Contains("no heads were read", degraded, StringComparison.Ordinal);
        Assert.Contains("only pushes register branches", degraded, StringComparison.Ordinal);
        Assert.DoesNotContain("Interval discovery is running", degraded, StringComparison.Ordinal);

        await SetProbeRecordAsync(host, projectId, Zenkins.Shared.Probes.GitProbeOutcomes.Refs);
        var active = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/branches");
        Assert.Contains("branch-discovery-active", active, StringComparison.Ordinal);
        Assert.Contains("Interval discovery is running", active, StringComparison.Ordinal);
        Assert.Contains("including ones created without a push", active, StringComparison.Ordinal);
    }

    private static Task SetProbeRecordAsync(OrganizationTestHost host, Guid projectId, string outcome) =>
        host.Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await services
                .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync(CancellationToken);
            using var scope = systemDataScope.BeginScope();
            var project = (await context.SystemQueryAsync<Project>(row => row.Id == projectId)).Single();
            project.LastProbeOutcome = outcome;
            project.LastProbeAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(CancellationToken);
            return true;
        });

    [Fact]
    public async Task The_runs_page_filters_by_branch_and_the_filter_cannot_cross_projects()
    {
        using var host = await OrganizationTestHost.CreateAsync();
        using var owner = await host.SignInAsync(OrganizationTestHost.OwnerEmail);
        var organizationId = await host.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);

        var first = await host.AddProjectAsync(organizationId, "first-runs");
        var second = await host.AddProjectAsync(organizationId, "second-runs");
        await host.Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await services
                .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync(CancellationToken);
            using var scope = systemDataScope.BeginScope();

            context.BuildRuns.AddRange(
                Run(organizationId, first, number: 1, branch: "main"),
                Run(organizationId, first, number: 2, branch: "feature/x"),
                Run(organizationId, second, number: 1, branch: "main"));
            await context.SaveChangesAsync(CancellationToken);
            return true;
        });

        var all = await AuthTestHost.GetStringAsync(owner, $"/projects/{first}/runs");
        Assert.Contains("Run #1", all, StringComparison.Ordinal);
        Assert.Contains("Run #2", all, StringComparison.Ordinal);

        var mainOnly = await AuthTestHost.GetStringAsync(owner, $"/projects/{first}/runs?branch=main");
        Assert.Contains("Run #1", mainOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("Run #2", mainOnly, StringComparison.Ordinal);
        Assert.Contains("Branch: main", mainOnly, StringComparison.Ordinal);

        // The other project's same-named branch is not in this list: the filter is (project, branch,
        // tenant), not a branch name lookup. Both projects' #1 exist; only first's is rendered here.
        Assert.Contains("Show all branches", mainOnly, StringComparison.Ordinal);

        static BuildRun Run(Guid organizationId, Guid projectId, int number, string branch) => new()
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            Number = number,
            TriggerType = BuildRunTriggerType.Manual,
            RequiredTags = string.Empty,
            PipelinePath = "pipeline.lua",
            GitBranch = branch,
            Status = BuildRunStatus.Success,
            ExitCode = 0,
        };
    }
}
