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
using Zenkins.Server.Schedules;
using Zenkins.Server.Webhooks;
using Zenkins.Shared.Artifacts;

namespace Zenkins.Tests.Runs;

/// <summary>
/// Card F-038's retention policy: what survives, what is deleted for real (rows and bytes), what may never be
/// deleted, and how the page says it before anything happens.
/// <para>
/// Every fact drives the product's own pieces - <see cref="RetentionService"/> through
/// <see cref="RetentionSweeper"/>'s tick, the pin and policy endpoints through their forms - against a real
/// database and a real artifact store directory. Time is passed in, never waited for: a build "ten days old" is a
/// row whose <see cref="BuildRun.CreatedAt"/> was written back after the insert (see <c>SeedRunAsync</c> - the
/// context stamps every added row, so setting the value on the entity would be a seed that never happened).
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

        // The premise, checked rather than assumed: the row really is ten days old, past a one-day window. A seed
        // whose date the insert stamp overwrote would make this fact pass for the age rule's reason instead of the
        // newest-build's, which is the false fact this assertion exists to keep out.
        var seeded = await ReadRunAsync(host, only);
        Assert.NotNull(seeded);
        Assert.True(
            DateTimeOffset.UtcNow - seeded!.CreatedAt > TimeSpan.FromDays(9),
            $"the seeded build is not ten days old: CreatedAt={seeded.CreatedAt:O}.");

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

    /// <summary>
    /// The day rule in the direction that keeps: a build inside the window is retained because of its age, and the
    /// build the assertion is about is <em>not</em> the branch's newest, so the newest-build exemption cannot be the
    /// reason. Together with <see cref="A_build_older_than_the_day_window_is_deleted"/> this is the pair that fails
    /// whichever way the day comparison is broken (always true, always false).
    /// </summary>
    [Fact]
    public async Task A_build_inside_the_day_window_is_kept_by_the_day_rule()
    {
        using var host = await RunTestHost.CreateAsync();
        var project = await host.SeedProjectAsync("day-window-kept", "https://example.test/day-kept.git");
        var inside = await SeedRunAsync(host, project, BuildRunStatus.Success, 1, daysOld: 10);
        await SeedRunAsync(host, project, BuildRunStatus.Success, 2, daysOld: 5);

        // The premise, checked: #1 really is ten days old. Without that read this fact would only prove that a
        // build the database thinks is brand new is inside every window.
        var seeded = await ReadRunAsync(host, inside);
        Assert.NotNull(seeded);
        Assert.True(
            DateTimeOffset.UtcNow - seeded!.CreatedAt > TimeSpan.FromDays(9),
            $"the seeded build is not ten days old: CreatedAt={seeded.CreatedAt:O}.");

        // The count rule is unset, so only the day rule can say anything about #1.
        await SavePolicyAsync(host, project, keep: null, days: 30);

        var evaluation = await InTenantAsync(host, services =>
            services.GetRequiredService<RetentionService>()
                .EvaluateAsync(project, new RetentionPolicy(null, 30), DateTimeOffset.UtcNow, CancellationToken));
        Assert.NotNull(evaluation);
        Assert.Equal(0, evaluation!.WouldDelete);

        Assert.Equal(0, (await SweepAsync(host)).Deleted);
        Assert.NotNull(await ReadRunAsync(host, inside));
        Assert.Equal(2, (await ReadRunsAsync(host, project)).Count);
    }

    /// <summary>
    /// The day rule in the direction that deletes: a build past the window goes, with the sentence that says why.
    /// Its counterpart (<see cref="A_build_inside_the_day_window_is_kept_by_the_day_rule"/>) keeps an in-window
    /// build, so a comparison that ignores the age cannot satisfy both.
    /// </summary>
    [Fact]
    public async Task A_build_older_than_the_day_window_is_deleted()
    {
        using var host = await RunTestHost.CreateAsync();
        var project = await host.SeedProjectAsync("day-window-dropped", "https://example.test/day-dropped.git");
        var old = await SeedRunAsync(host, project, BuildRunStatus.Success, 1, daysOld: 10);
        var newest = await SeedRunAsync(host, project, BuildRunStatus.Success, 2, daysOld: 0);

        var seeded = await ReadRunAsync(host, old);
        Assert.NotNull(seeded);
        Assert.True(
            DateTimeOffset.UtcNow - seeded!.CreatedAt > TimeSpan.FromDays(9),
            $"the seeded build is not ten days old: CreatedAt={seeded.CreatedAt:O}.");

        await SavePolicyAsync(host, project, keep: null, days: 1);

        // #1 is not the newest build of the branch, so the exemption does not cover it and the window decides.
        var evaluation = await InTenantAsync(host, services =>
            services.GetRequiredService<RetentionService>()
                .EvaluateAsync(project, new RetentionPolicy(null, 1), DateTimeOffset.UtcNow, CancellationToken));
        Assert.NotNull(evaluation);
        Assert.Equal(1, evaluation!.WouldDelete);
        var candidate = Assert.Single(evaluation.Candidates);
        Assert.Equal(old, candidate.RunId);
        Assert.Contains("older than 1 day(s)", candidate.Reason, StringComparison.Ordinal);

        Assert.Equal(1, (await SweepAsync(host)).Deleted);
        Assert.Null(await ReadRunAsync(host, old));
        Assert.NotNull(await ReadRunAsync(host, newest));
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

    /// <summary>
    /// F-026's "exactly once" against F-038's trimming: a run whose chain obligation has not been dispatched yet is
    /// not history to delete, because deleting it would cascade the pending row away and lose a downstream trigger
    /// that was promised. Once the obligation is settled the same policy takes the run.
    /// </summary>
    [Fact]
    public async Task A_run_that_still_owes_a_downstream_trigger_is_spared_until_the_obligation_settles()
    {
        using var host = await RunTestHost.CreateAsync();
        var project = await host.SeedProjectAsync("owed-trigger", "https://example.test/owed.git");
        var owed = await SeedRunAsync(host, project, BuildRunStatus.Success, 1, daysOld: 400);
        var newest = await SeedRunAsync(host, project, BuildRunStatus.Success, 2, daysOld: 1);
        var obligation = await SeedPendingObligationAsync(host, project, owed);

        // The premise: the row the sparing rule reads is really there and really pending.
        var pending = Assert.Single(await ReadObligationsAsync(host, owed));
        Assert.Equal(OutboxStatuses.Pending, pending.Status);

        await SavePolicyAsync(host, project, keep: 1, days: null);

        // Without the sparing rule #1 is a plain candidate: it is 400 days old and outside the newest one.
        var evaluation = await InTenantAsync(host, services =>
            services.GetRequiredService<RetentionService>()
                .EvaluateAsync(project, new RetentionPolicy(1, null), DateTimeOffset.UtcNow, CancellationToken));
        Assert.NotNull(evaluation);
        Assert.Equal(0, evaluation!.WouldDelete);

        Assert.Equal(0, (await SweepAsync(host)).Deleted);
        Assert.NotNull(await ReadRunAsync(host, owed));
        Assert.Equal(OutboxStatuses.Pending, Assert.Single(await ReadObligationsAsync(host, owed)).Status);

        // Settled: the promise was kept, so the trigger is no longer a reason to keep the history - and the row
        // goes with the run it hangs from (the cascade above), never as a dangling obligation.
        await SettleObligationAsync(host, obligation);
        Assert.Equal(1, (await SweepAsync(host)).Deleted);
        Assert.Null(await ReadRunAsync(host, owed));
        Assert.Empty(await ReadObligationsAsync(host, owed));
        Assert.NotNull(await ReadRunAsync(host, newest));
    }

    /// <summary>
    /// AC-5's "no ghosts" where the product chose clearing over deleting: a webhook delivery and a schedule
    /// occurrence are dedupe and audit records, so deleting the build they named must leave them in place with no
    /// run - not take them away and not leave a dangling id behind.
    /// </summary>
    [Fact]
    public async Task A_deleted_build_clears_the_rows_that_name_it_instead_of_taking_them()
    {
        using var host = await RunTestHost.CreateAsync();
        var project = await host.SeedProjectAsync("cleared-references", "https://example.test/refs.git");
        var oldRun = await SeedRunAsync(host, project, BuildRunStatus.Success, 1, daysOld: 400);
        await SeedRunAsync(host, project, BuildRunStatus.Success, 2, daysOld: 1);
        var delivery = await SeedWebhookDeliveryAsync(host, project, oldRun);
        var occurrence = await SeedScheduleOccurrenceAsync(host, project, oldRun);

        // The premise: both rows really name the run before the sweep, so a seed that pointed nowhere cannot make
        // the assertions below pass on their own.
        Assert.Equal(oldRun, (await ReadDeliveryAsync(host, delivery))!.RunId);
        Assert.Equal(oldRun, (await ReadOccurrenceAsync(host, occurrence))!.RunId);

        await SavePolicyAsync(host, project, keep: 1, days: null);
        Assert.Equal(1, (await SweepAsync(host)).Deleted);
        Assert.Null(await ReadRunAsync(host, oldRun));

        var keptDelivery = await ReadDeliveryAsync(host, delivery);
        Assert.NotNull(keptDelivery);
        Assert.Null(keptDelivery!.RunId);
        var keptOccurrence = await ReadOccurrenceAsync(host, occurrence);
        Assert.NotNull(keptOccurrence);
        Assert.Null(keptOccurrence!.RunId);
    }

    /// <summary>
    /// Q-006's other half of the retention guard: the save answers <c>Forbidden</c> for a caller who is not a member
    /// of the active organization, and the row keeps both rules unset.
    /// <para>
    /// This is a service-level fact on purpose. The HTTP surface cannot reach this branch - the active organization
    /// is chosen from the caller's own memberships, so another organization's project is a 404 before any role is
    /// considered (that half is <c>TenantIsolationHttpProbeTests</c>). The refusal is still real, and Q-006's
    /// inventory may only claim it because this fact exists.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Saving_a_policy_as_a_non_member_is_forbidden_and_leaves_the_row_alone()
    {
        using var host = await RunTestHost.CreateAsync();
        var project = await host.SeedProjectAsync("non-member-policy", "https://example.test/nm-policy.git");

        var denied = await SavePolicyAsync(host, project, keep: 0, days: 1, actingUserId: "not-a-member");
        Assert.Equal(RetentionMutationStatus.Forbidden, denied.Status);

        var untouched = await InTenantAsync(host, services =>
            services.GetRequiredService<RetentionService>().GetPolicyAsync(project, CancellationToken));
        Assert.NotNull(untouched);
        Assert.True(untouched!.IsUnset);

        // Positive control: the same call from a member of the organization is stored, so the refusal above is the
        // membership guard and not a call that could never have worked.
        var allowed = await SavePolicyAsync(host, project, keep: 0, days: 1);
        Assert.Equal(RetentionMutationStatus.Success, allowed.Status);
    }

    /// <summary>
    /// The pin route's half of the same guard (Q-006): a caller who is not a member of the active organization gets
    /// <c>Forbidden</c> and the run's pin column is untouched. Service-level for the same reason as
    /// <see cref="Saving_a_policy_as_a_non_member_is_forbidden_and_leaves_the_row_alone"/>.
    /// </summary>
    [Fact]
    public async Task Pinning_a_run_as_a_non_member_is_forbidden_and_leaves_the_row_alone()
    {
        using var host = await RunTestHost.CreateAsync();
        var project = await host.SeedProjectAsync("non-member-pin", "https://example.test/nm-pin.git");
        var run = await SeedRunAsync(host, project, BuildRunStatus.Success, 1, daysOld: 1);

        var denied = await InTenantAsync(host, services =>
            services.GetRequiredService<RetentionService>()
                .SetPinnedAsync("not-a-member", run, true, CancellationToken));
        Assert.Equal(RunPinStatus.Forbidden, denied.Status);
        Assert.False((await ReadRunAsync(host, run))!.Pinned);

        var allowed = await InTenantAsync(host, async services =>
            await services.GetRequiredService<RetentionService>()
                .SetPinnedAsync(await OwnerUserIdAsync(host, services), run, true, CancellationToken));
        Assert.Equal(RunPinStatus.Success, allowed.Status);
        Assert.True((await ReadRunAsync(host, run))!.Pinned);
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

    /// <param name="actingUserId">
    /// The caller the service authorizes; null means the organization's owner (the member case). A value that is
    /// not a member is how the non-member facts reach the service's own guard.
    /// </param>
    private static Task<RetentionMutationResult> SavePolicyAsync(
        RunTestHost host,
        Guid projectId,
        int? keep,
        int? days,
        string? actingUserId = null) =>
        InTenantAsync(host, async services =>
        {
            var userId = actingUserId ?? await OwnerUserIdAsync(host, services);
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

    /// <summary>
    /// One run (with a step and a log) <paramref name="daysOld"/> days in the past.
    /// <para>
    /// <b>The age is written after the insert, on purpose.</b> <c>ZenkinsDbContext.StampTimestamps</c> overwrites
    /// <see cref="BuildRun.CreatedAt"/> for every added row, so a "ten days old" value set on the entity never
    /// reaches the column: the row would be brand new and every date rule in this file would be measured against
    /// the wrong instant. <c>ExecuteUpdate</c> is the honest fix and the product's stamping behaviour stays as it
    /// is; the date facts then read the column back (see their premise assertions) so the lie cannot come back
    /// unnoticed.
    /// </para>
    /// </summary>
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

            // Now that the insert has been stamped, put the two instants the seed asked for back on the row. The
            // column update is deliberately a direct UPDATE: a tracked write would go through StampTimestamps again.
            await context.BuildRuns
                .IgnoreQueryFilters()
                .Where(candidate => candidate.Id == run.Id)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.CreatedAt, created)
                        .SetProperty(candidate => candidate.UpdatedAt, created),
                    CancellationToken);
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

    /// <summary>
    /// One pending chain obligation hanging from <paramref name="upstreamRunId"/> (F-026), written the way the
    /// completion path writes it. It is due in an hour on purpose: the background dispatcher only works rows whose
    /// <see cref="OutboxMessage.NextAttemptAt"/> has passed, so it stays out of this fact's way while the row is
    /// still <c>pending</c> - which is the only thing the retention rule reads.
    /// </summary>
    private static Task<Guid> SeedPendingObligationAsync(RunTestHost host, Guid projectId, Guid upstreamRunId) =>
        host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId)).Single();
            var message = new OutboxMessage
            {
                OrganizationId = project.OrganizationId,
                Kind = OutboxKinds.DownstreamTrigger,
                UpstreamRunId = upstreamRunId,
                // The edge the obligation came from. No foreign key: the row is a record of what was promised.
                LinkId = Guid.NewGuid(),
                Status = OutboxStatuses.Pending,
                NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1),
            };
            context.OutboxMessages.Add(message);
            await context.SaveChangesAsync();
            return message.Id;
        });

    private static Task<List<OutboxMessage>> ReadObligationsAsync(RunTestHost host, Guid upstreamRunId) =>
        host.InSystemScopeAsync(context =>
            context.SystemQueryAsync<OutboxMessage>(message => message.UpstreamRunId == upstreamRunId));

    /// <summary>Settles one obligation the way a dispatch does: status, outcome and the instant, together.</summary>
    private static Task<bool> SettleObligationAsync(RunTestHost host, Guid obligationId) =>
        host.InSystemScopeAsync(async context =>
            await context.OutboxMessages
                .IgnoreQueryFilters()
                .Where(message => message.Id == obligationId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(message => message.Status, OutboxStatuses.Settled)
                        .SetProperty(message => message.SettledAt, DateTimeOffset.UtcNow)
                        .SetProperty(message => message.Outcome, DownstreamOutcomes.Queued),
                    CancellationToken) == 1);

    /// <summary>
    /// One webhook of the project plus a delivery record naming <paramref name="runId"/> (F-017). The delivery's
    /// <see cref="WebhookDelivery.RunId"/> carries no foreign key - it is a link, not an owner - which is exactly
    /// why deleting a build has to clear it (AC-5) and why this row can outlive the run.
    /// </summary>
    private static Task<Guid> SeedWebhookDeliveryAsync(RunTestHost host, Guid projectId, Guid runId) =>
        host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId)).Single();
            var webhook = new Webhook
            {
                OrganizationId = project.OrganizationId,
                ProjectId = projectId,
                Name = "delivery-link",
                // Nothing reads the ciphertext in this fact; the column only has to be the string the model demands.
                SecretCiphertext = "seeded-not-a-ciphertext",
                BranchFilter = "main",
                RequiredTags = string.Empty,
                CreatedByUserId = await OwnerUserIdInSystemScopeAsync(context, project.OrganizationId),
            };
            context.Set<Webhook>().Add(webhook);

            var delivery = new WebhookDelivery
            {
                OrganizationId = project.OrganizationId,
                WebhookId = webhook.Id,
                // The key's shape belongs to the delivery path; here it only has to be unique per webhook.
                DedupeKey = $"id:{Guid.NewGuid():N}",
                Outcome = WebhookOutcomes.Queued,
                RunId = runId,
                ReceivedAt = DateTimeOffset.UtcNow,
            };
            context.Set<WebhookDelivery>().Add(delivery);
            await context.SaveChangesAsync();
            return delivery.Id;
        });

    private static async Task<WebhookDelivery?> ReadDeliveryAsync(RunTestHost host, Guid deliveryId) =>
        (await host.InSystemScopeAsync(context =>
            context.SystemQueryAsync<WebhookDelivery>(delivery => delivery.Id == deliveryId))).SingleOrDefault();

    /// <summary>
    /// One schedule of the project plus a due-time row naming <paramref name="runId"/> (F-024). The schedule is
    /// disabled with its watermark at now, so the schedule sweeper has no due time to record while this fact runs;
    /// <see cref="ScheduleOccurrence.RunId"/> has no foreign key for the same reason the delivery's has none.
    /// </summary>
    private static Task<Guid> SeedScheduleOccurrenceAsync(RunTestHost host, Guid projectId, Guid runId) =>
        host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId)).Single();
            var schedule = new ProjectSchedule
            {
                OrganizationId = project.OrganizationId,
                ProjectId = projectId,
                Cron = "0 3 * * *",
                TimeZone = "UTC",
                Enabled = false,
                LastEvaluatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = await OwnerUserIdInSystemScopeAsync(context, project.OrganizationId),
            };
            context.Set<ProjectSchedule>().Add(schedule);

            var occurrence = new ScheduleOccurrence
            {
                OrganizationId = project.OrganizationId,
                ScheduleId = schedule.Id,
                DueAt = DateTimeOffset.UtcNow.AddHours(-1),
                Outcome = ScheduleOutcomes.Queued,
                RunId = runId,
                RunNumber = 1,
            };
            context.ScheduleOccurrences.Add(occurrence);
            await context.SaveChangesAsync();
            return occurrence.Id;
        });

    private static async Task<ScheduleOccurrence?> ReadOccurrenceAsync(RunTestHost host, Guid occurrenceId) =>
        (await host.InSystemScopeAsync(context =>
            context.SystemQueryAsync<ScheduleOccurrence>(occurrence => occurrence.Id == occurrenceId))).SingleOrDefault();

    /// <summary>The organization's owner, read inside an already-active system scope (a seed has no request).</summary>
    private static async Task<string> OwnerUserIdInSystemScopeAsync(ZenkinsDbContext context, Guid organizationId) =>
        (await context.SystemQueryAsync<OrganizationMember>(
            member => member.OrganizationId == organizationId && member.Role == OrganizationRole.Owner))
        .First()
        .UserId;

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
