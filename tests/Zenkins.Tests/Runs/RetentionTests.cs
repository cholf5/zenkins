using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Zenkins.Server.Artifacts;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Runs;
using Zenkins.Shared.Artifacts;

namespace Zenkins.Tests.Runs;

/// <summary>
/// Card F-038's retention policy: what survives, what is deleted for real (rows and bytes), what may never be
/// deleted, and how the page says it before anything happens.
/// <para>
/// Every fact drives the product's own pieces - <see cref="RetentionService"/> through
/// <see cref="RetentionSweeper"/>'s tick, the pin and policy endpoints through their forms - against a real
/// database and a real artifact store directory. Time is passed in, never waited for: a build "ten days old" is a
/// row seeded with an old <see cref="BuildRun.CreatedAt"/>.
/// </para>
/// </summary>
[Trait("Category", "Run")]
public sealed class RetentionTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Retention_with_both_rules_unset_keeps_every_build()
    {
        using var host = await RunTestHost.CreateAsync();
        var project = await host.SeedProjectAsync("keep-all", "https://example.test/all.git");
        for (var index = 1; index <= 5; index++)
        {
            await SeedRunAsync(host, project, BuildRunStatus.Success, index, daysOld: 400);
        }

        // AC-1: both rules unset is not "keep nothing" - it is today's behaviour, and the sweep must be a no-op.
        var policy = await InTenantAsync(host, services =>
            services.GetRequiredService<RetentionService>().GetPolicyAsync(project, CancellationToken));
        Assert.NotNull(policy);
        Assert.True(policy!.IsUnset);

        var evaluation = await InTenantAsync(host, services =>
            services.GetRequiredService<RetentionService>()
                .EvaluateAsync(project, policy, DateTimeOffset.UtcNow, CancellationToken));
        Assert.NotNull(evaluation);
        Assert.Equal(0, evaluation!.WouldDelete);
        Assert.Equal(5, evaluation.TotalRuns);

        var swept = await SweepAsync(host);
        Assert.Equal(0, swept.Deleted);
        Assert.Equal(5, (await ReadRunsAsync(host, project)).Count);
    }

    [Fact]
    public async Task A_pinned_build_is_exempt_from_both_rules_and_pinning_is_a_member_action()
    {
        using var host = await RunTestHost.CreateAsync();
        var project = await host.SeedProjectAsync("pinned", "https://example.test/pin.git");
        var oldest = await SeedRunAsync(host, project, BuildRunStatus.Success, 1, daysOld: 400);
        var middle = await SeedRunAsync(host, project, BuildRunStatus.Success, 2, daysOld: 300);
        var newest = await SeedRunAsync(host, project, BuildRunStatus.Success, 3, daysOld: 200);

        // Both rules tightened in one go: keep 1 build AND everything younger than 0 days.
        await SavePolicyAsync(host, project, keep: 1, days: 0);

        // The pin toggle is the run page's form, posted by a member of the organization (the same guard shape as
        // cancel/rebuild: cookie + antiforgery, tenant-filtered run, membership in the service).
        using var owner = await host.Inner.SignInOwnerAsync();
        using var pinned = await PostFormAsync(host, owner, $"/runs/{oldest}", $"/runs/{oldest}/pin");
        Assert.True((int)pinned.StatusCode is 302 or 303, $"pinning answered {(int)pinned.StatusCode}.");

        var pinnedRun = await ReadRunAsync(host, oldest);
        Assert.NotNull(pinnedRun);
        Assert.True(pinnedRun!.Pinned);

        // The page says the state the sweep reads.
        var page = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(owner, $"/runs/{oldest}");
        Assert.Contains("run-pinned-badge", page, StringComparison.Ordinal);
        Assert.Contains("/unpin", page, StringComparison.Ordinal);

        var swept = await SweepAsync(host);
        Assert.Equal(1, swept.Deleted);

        var survivors = await ReadRunsAsync(host, project);
        Assert.Equal(
            new[] { oldest, newest }.Order().ToArray(),
            survivors.Select(run => run.Id).Order().ToArray());
        Assert.DoesNotContain(survivors, run => run.Id == middle);

        // Unpinning is not a delete by itself: the build is still there, and the *next* sweep is what takes it.
        using var unpinned = await PostFormAsync(host, owner, $"/runs/{oldest}", $"/runs/{oldest}/unpin");
        Assert.True((int)unpinned.StatusCode is 302 or 303, $"unpinning answered {(int)unpinned.StatusCode}.");
        Assert.NotNull(await ReadRunAsync(host, oldest));
        Assert.Equal(1, (await SweepAsync(host)).Deleted);
        Assert.Null(await ReadRunAsync(host, oldest));
    }

    [Fact]
    public async Task A_queued_or_running_build_is_never_deleted()
    {
        using var host = await RunTestHost.CreateAsync();
        var project = await host.SeedProjectAsync("in-flight", "https://example.test/fly.git");
        await SeedRunAsync(host, project, BuildRunStatus.Success, 1, daysOld: 400);
        await SeedRunAsync(host, project, BuildRunStatus.Failed, 2, daysOld: 390);
        var queued = await SeedRunAsync(host, project, BuildRunStatus.Queued, 3, daysOld: 380);
        var running = await SeedRunAsync(host, project, BuildRunStatus.Running, 4, daysOld: 370);
        var newest = await SeedRunAsync(host, project, BuildRunStatus.Success, 5, daysOld: 360);

        await SavePolicyAsync(host, project, keep: 0, days: 0);
        var swept = await SweepAsync(host);
        Assert.Equal(2, swept.Deleted);

        var survivors = (await ReadRunsAsync(host, project)).Select(run => run.Id).Order().ToArray();
        Assert.Equal(new[] { queued, running, newest }.Order().ToArray(), survivors);
    }

    [Fact]
    public async Task The_newest_build_of_a_project_survives_the_day_rule()
    {
        using var host = await RunTestHost.CreateAsync();
        var project = await host.SeedProjectAsync("last-result", "https://example.test/last.git");
        var only = await SeedRunAsync(host, project, BuildRunStatus.Failed, 1, daysOld: 10);
        await SavePolicyAsync(host, project, keep: null, days: 1);

        // TC-003: a single ten-day-old build is older than the window, and is still kept - it is the project's
        // last result, and a project that deletes its only build has thrown its history away.
        var policy = new RetentionPolicy(null, 1);
        var evaluation = await InTenantAsync(host, services =>
            services.GetRequiredService<RetentionService>()
                .EvaluateAsync(project, policy, DateTimeOffset.UtcNow, CancellationToken));
        Assert.NotNull(evaluation);
        Assert.Equal(1, evaluation!.NewestPerBranch);
        Assert.Equal(0, evaluation.WouldDelete);

        Assert.Equal(0, (await SweepAsync(host)).Deleted);
        Assert.NotNull(await ReadRunAsync(host, only));
    }

    [Fact]
    public async Task A_deleted_build_takes_its_rows_its_bytes_and_its_links()
    {
        using var host = await RunTestHost.CreateAsync();
        var project = await host.SeedProjectAsync("really-gone", "https://example.test/gone.git");
        var oldRun = await SeedRunAsync(host, project, BuildRunStatus.Success, 1, daysOld: 400);
        var newRun = await SeedRunAsync(host, project, BuildRunStatus.Success, 2, daysOld: 1);
        var oldWork = await SeedArtifactsAsync(host, project, oldRun);
        var newWork = await SeedArtifactsAsync(host, project, newRun);

        await SavePolicyAsync(host, project, keep: 1, days: null);
        Assert.Equal(1, (await SweepAsync(host)).Deleted);

        // AC-4: the four faces of "deleted" - the run row, its step and log rows, its artifact row, and the
        // artifact bytes in the store directory.
        Assert.Null(await ReadRunAsync(host, oldRun));
        Assert.Empty(await ReadStepsAsync(host, oldRun));
        Assert.Empty(await ReadLogsAsync(host, oldRun));
        Assert.Empty(await ReadArtifactsAsync(host, oldRun));
        Assert.False(Directory.Exists(oldWork), $"the artifact directory of the deleted run survived: {oldWork}");

        // ...and the kept build is untouched, bytes included.
        Assert.NotNull(await ReadRunAsync(host, newRun));
        Assert.NotEmpty(await ReadStepsAsync(host, newRun));
        Assert.NotEmpty(await ReadLogsAsync(host, newRun));
        Assert.Single(await ReadArtifactsAsync(host, newRun));
        Assert.True(Directory.Exists(newWork), $"the kept run's artifact directory is missing: {newWork}");
    }

    [Fact]
    public async Task A_second_sweep_deletes_nothing_and_an_orphaned_directory_is_retried()
    {
        using var host = await RunTestHost.CreateAsync();
        var project = await host.SeedProjectAsync("idempotent", "https://example.test/idem.git");
        var oldRun = await SeedRunAsync(host, project, BuildRunStatus.Success, 1, daysOld: 400);
        var newRun = await SeedRunAsync(host, project, BuildRunStatus.Success, 2, daysOld: 1);
        var oldWork = await SeedArtifactsAsync(host, project, oldRun);
        await SeedArtifactsAsync(host, project, newRun);
        await SavePolicyAsync(host, project, keep: 1, days: null);

        Assert.Equal(1, (await SweepAsync(host)).Deleted);

        // AC-4's idempotence: the same policy over the same history deletes nothing the second time.
        Assert.Equal(0, (await SweepAsync(host)).Deleted);

        // AC-4's retry: if the rows went but the bytes did not (the crash between the two halves this card's
        // deletion path allows), F-016's sweeper removes the orphaned directory on its own pass.
        var orphanRun = await SeedRunAsync(host, project, BuildRunStatus.Success, 3, daysOld: 300);
        var orphanWork = await SeedArtifactsAsync(host, project, orphanRun);
        await DeleteRunRowAsync(host, orphanRun);
        Assert.True(Directory.Exists(orphanWork));

        var artifactSweep = await host.Host.InScopeAsync(services => services
            .GetServices<IHostedService>()
            .OfType<ArtifactSweeper>()
            .Single()
            .SweepOnceAsync(CancellationToken));
        Assert.True(artifactSweep.RemovedDirectories >= 1);
        Assert.False(Directory.Exists(orphanWork));
    }

    [Fact]
    public async Task A_swept_build_leaves_no_ghost_on_any_surface()
    {
        using var host = await RunTestHost.CreateAsync();
        var project = await host.SeedProjectAsync("no-ghosts", "https://example.test/ghost.git");
        var oldRun = await SeedRunAsync(host, project, BuildRunStatus.Success, 1, daysOld: 400);
        var newRun = await SeedRunAsync(host, project, BuildRunStatus.Success, 2, daysOld: 1);
        await SavePolicyAsync(host, project, keep: 1, days: null);
        Assert.Equal(1, (await SweepAsync(host)).Deleted);

        using var owner = await host.Inner.SignInOwnerAsync();

        // The runs list and the project overview say nothing about the build that is gone...
        var list = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(owner, $"/projects/{project}/runs");
        Assert.DoesNotContain($"/projects/{project}/runs/1", list, StringComparison.Ordinal);
        Assert.Contains($"/projects/{project}/runs/2", list, StringComparison.Ordinal);
        Assert.DoesNotContain("Run #1<", list, StringComparison.Ordinal);

        var overview = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(owner, $"/projects/{project}");
        Assert.DoesNotContain($"/projects/{project}/runs/1", overview, StringComparison.Ordinal);

        // ...and both of the deleted build's addresses are a real 404 (AC-5): the guid permalink and F-036's
        // number permalink.
        foreach (var path in new[] { $"/runs/{oldRun}", $"/projects/{project}/runs/1" })
        {
            using var response = await owner.GetAsync(path, CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var kept = await owner.GetAsync($"/projects/{project}/runs/2", CancellationToken);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
    }

    [Fact]
    public async Task Retention_is_applied_per_branch_job()
    {
        using var host = await RunTestHost.CreateAsync();
        var project = await host.SeedProjectAsync("per-branch", "https://example.test/branch.git");
        var mainOldest = await SeedRunAsync(host, project, BuildRunStatus.Success, 1, daysOld: 400, branch: "main");
        var mainNewest = await SeedRunAsync(host, project, BuildRunStatus.Success, 2, daysOld: 300, branch: "main");
        var featureOnly = await SeedRunAsync(host, project, BuildRunStatus.Success, 3, daysOld: 200, branch: "feature");

        await SavePolicyAsync(host, project, keep: 1, days: null);
        Assert.Equal(1, (await SweepAsync(host)).Deleted);

        // AC-6: the count is per branch job, so each branch keeps its own newest build - the feature branch's
        // single build survives even though the project-wide count is 1.
        var survivors = (await ReadRunsAsync(host, project)).Select(run => run.Id).Order().ToArray();
        Assert.Equal(new[] { mainNewest, featureOnly }.Order().ToArray(), survivors);
        Assert.Null(await ReadRunAsync(host, mainOldest));
    }

    [Fact]
    public async Task The_settings_card_counts_the_history_and_previews_a_tighter_policy()
    {
        using var host = await RunTestHost.CreateAsync();
        var project = await host.SeedProjectAsync("preview", "https://example.test/preview.git");
        for (var index = 1; index <= 4; index++)
        {
            await SeedRunAsync(host, project, BuildRunStatus.Success, index, daysOld: 400 - index);
        }

        using var owner = await host.Inner.SignInOwnerAsync();

        // AC-7: the card states the retained counts of the policy that is stored (nothing yet: every build).
        var before = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(owner, $"/projects/{project}/settings");
        Assert.Contains("project-retention", before, StringComparison.Ordinal);
        Assert.Contains("every build (no rule set)", before, StringComparison.Ordinal);

        // Saving a tighter policy is the same form's save - and it says what it will do next.
        using var saved = await PostFormAsync(
            host,
            owner,
            $"/projects/{project}/settings",
            $"/projects/{project}/retention",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["RetentionKeep"] = "1",
                ["RetentionDays"] = string.Empty,
            });
        Assert.True(
            (int)saved.StatusCode is 302 or 303
                && saved.Headers.Location!.OriginalString.Contains("notice=retention-saved", StringComparison.Ordinal),
            $"saving the policy answered {(int)saved.StatusCode}: {saved.Headers.Location}.");

        var after = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(owner, $"/projects/{project}/settings");
        Assert.Contains("Project-retention", after, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("the newest 1 build(s) of each branch", after, StringComparison.Ordinal);
        Assert.Contains("project-retention-next-sweep", after, StringComparison.Ordinal);

        // The preview answers "what would these values delete?" and stores nothing (the save above stays).
        var preview = await PostFormAsync(
            host,
            owner,
            $"/projects/{project}/settings",
            $"/projects/{project}/retention",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["RetentionKeep"] = "0",
                ["RetentionDays"] = string.Empty,
                ["Action"] = "preview",
            });
        Assert.True((int)preview.StatusCode is 302 or 303);
        Assert.Contains("retentionPreviewKeep=0", preview.Headers.Location!.OriginalString, StringComparison.Ordinal);

        var previewed = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(
            owner,
            preview.Headers.Location!.OriginalString);
        Assert.Contains("project-retention-preview", previewed, StringComparison.Ordinal);
        Assert.Contains("would delete", previewed, StringComparison.Ordinal);

        var policy = await InTenantAsync(host, services =>
            services.GetRequiredService<RetentionService>().GetPolicyAsync(project, CancellationToken));
        Assert.Equal(1, policy!.Keep);
        Assert.Null(policy.Days);
        Assert.Equal(4, (await ReadRunsAsync(host, project)).Count);

        // And the sweep the card promised really removes exactly the three it named.
        Assert.Equal(3, (await SweepAsync(host)).Deleted);
        Assert.Single(await ReadRunsAsync(host, project));
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    /// <summary>One row in the tenant scope, the way a request reads it.</summary>
    private static Task<T> InTenantAsync<T>(RunTestHost host, Func<IServiceProvider, Task<T>> action) =>
        host.Host.InScopeAsync(async services =>
        {
            services.GetRequiredService<TenantContext>().OrganizationId = await host.OrganizationIdAsync();
            return await action(services);
        });

    /// <summary>One retention tick through the product's own hosted service (never waits on real time).</summary>
    private static Task<RetentionSweepResult> SweepAsync(RunTestHost host) =>
        host.Host.InScopeAsync(services => services
            .GetServices<IHostedService>()
            .OfType<RetentionSweeper>()
            .Single()
            .SweepOnceAsync(CancellationToken));

    private static Task<RetentionMutationResult> SavePolicyAsync(
        RunTestHost host,
        Guid projectId,
        int? keep,
        int? days) =>
        InTenantAsync(host, async services =>
        {
            var userId = await OwnerUserIdAsync(host, services);
            return await services.GetRequiredService<RetentionService>()
                .SavePolicyAsync(userId, projectId, keep, days, CancellationToken);
        });

    private static async Task<string> OwnerUserIdAsync(RunTestHost host, IServiceProvider services)
    {
        var organizationId = await host.OrganizationIdAsync();
        await using var context = await services
            .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
            .CreateDbContextAsync(CancellationToken);
        var owner = await context.OrganizationMembers
            .Where(member => member.OrganizationId == organizationId
                && member.Role == OrganizationRole.Owner)
            .Select(member => member.UserId)
            .FirstAsync(CancellationToken);
        return owner;
    }

    private static Task<Guid> SeedRunAsync(
        RunTestHost host,
        Guid projectId,
        string status,
        int number,
        int daysOld,
        string branch = "main") =>
        host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId)).Single();
            var created = DateTimeOffset.UtcNow.AddDays(-daysOld);
            var run = new BuildRun
            {
                OrganizationId = project.OrganizationId,
                ProjectId = projectId,
                TriggerType = BuildRunTriggerType.Manual,
                RequiredTags = string.Empty,
                GitBranch = branch,
                PipelinePath = "pipeline.lua",
                GitCommit = "abc0123",
                Number = number,
                Status = status,
                CreatedAt = created,
                UpdatedAt = created,
                StartedAt = status is BuildRunStatus.Queued ? null : created,
                EndedAt = status is BuildRunStatus.Queued or BuildRunStatus.Running ? null : created.AddMinutes(1),
            };
            context.BuildRuns.Add(run);

            context.BuildSteps.Add(new BuildStep
            {
                OrganizationId = project.OrganizationId,
                RunId = run.Id,
                Index = 1,
                Name = "build",
                Status = "Success",
                StartedAt = created,
                EndedAt = created.AddSeconds(30),
            });
            context.BuildLogs.Add(new BuildLog
            {
                OrganizationId = project.OrganizationId,
                RunId = run.Id,
                Seq = 1,
                Stream = Zenkins.Shared.LogStreaming.LogStream.StdOut,
                Message = $"line of {status} #{number}",
                Timestamp = created,
            });
            await context.SaveChangesAsync();
            return run.Id;
        });

    /// <summary>
    /// One artifact row plus its bytes on disk (card F-016's layout), so "deleted" can be checked against both
    /// halves. Returns the run's store directory.
    /// </summary>
    private static Task<string> SeedArtifactsAsync(RunTestHost host, Guid projectId, Guid runId) =>
        host.Host.InScopeAsync(async services =>
        {
            var store = services.GetRequiredService<ArtifactStore>();

            // The store layout is the product's own (F-016): {root}/{org}/{run}/{artifact}/file. Reading the
            // project and writing the row happen in the system scope, because this helper has no request tenant.
            using var systemScope = services.GetRequiredService<ISystemDataScope>().BeginScope();
            await using var context = await services
                .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync(CancellationToken);

            var project = (await context.SystemQueryAsync<Project>(
                candidate => candidate.Id == projectId)).Single();

            var directory = store.RunDirectory(project.OrganizationId, runId);
            var artifactId = Guid.NewGuid();
            var artifactDirectory = Path.Combine(directory, artifactId.ToString("D"));
            Directory.CreateDirectory(artifactDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(artifactDirectory, "bin.txt"),
                "artifact bytes",
                CancellationToken);

            context.Set<Artifact>().Add(new Artifact
            {
                OrganizationId = project.OrganizationId,
                RunId = runId,
                Name = "bin",
                Kind = ArtifactKind.File,
                Status = ArtifactStatus.Ready,
                FileCount = 1,
                TotalBytes = 14,
            });
            await context.SaveChangesAsync(CancellationToken);
            return directory;
        });

    private static Task DeleteRunRowAsync(RunTestHost host, Guid runId) =>
        host.InSystemScopeAsync(async context =>
        {
            var run = (await context.SystemQueryAsync<BuildRun>(candidate => candidate.Id == runId)).Single();
            context.BuildRuns.Remove(run);
            await context.SaveChangesAsync();
            return true;
        });

    private static Task<List<BuildRun>> ReadRunsAsync(RunTestHost host, Guid projectId) =>
        host.InSystemScopeAsync(context => context.SystemQueryAsync<BuildRun>(run => run.ProjectId == projectId));

    private static async Task<BuildRun?> ReadRunAsync(RunTestHost host, Guid runId) =>
        (await host.InSystemScopeAsync(context => context.SystemQueryAsync<BuildRun>(run => run.Id == runId)))
        .SingleOrDefault();

    private static Task<List<BuildStep>> ReadStepsAsync(RunTestHost host, Guid runId) =>
        host.InSystemScopeAsync(context => context.SystemQueryAsync<BuildStep>(step => step.RunId == runId));

    private static Task<List<BuildLog>> ReadLogsAsync(RunTestHost host, Guid runId) =>
        host.InSystemScopeAsync(context => context.SystemQueryAsync<BuildLog>(log => log.RunId == runId));

    private static Task<List<Artifact>> ReadArtifactsAsync(RunTestHost host, Guid runId) =>
        host.InSystemScopeAsync(context => context.SystemQueryAsync<Artifact>(artifact => artifact.RunId == runId));

    private static Task<HttpResponseMessage> PostFormAsync(
        RunTestHost host,
        HttpClient client,
        string pageUrl,
        string postUrl,
        IReadOnlyDictionary<string, string>? fields = null) =>
        Zenkins.Tests.Organizations.OrganizationTestHost.PostFormAsync(
            client,
            pageUrl,
            postUrl,
            fields ?? new Dictionary<string, string>(StringComparer.Ordinal));
}
