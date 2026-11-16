using BlazorBlueprint.Components;
using Xunit;
using Zenkins.Server.Components.Shared;
using Zenkins.Server.Dashboard;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Tenancy;

namespace Zenkins.Tests.Dashboard;

/// <summary>
/// Card D-002's read-model probes: the numbers are the rows (TC-001), recency is ordered and capped,
/// the failure window is what the card defines, switching organization replaces every figure, and
/// the whole summary costs a fixed number of database commands no matter how long the history is.
/// Each class owns its <see cref="TwoOrgsFixture"/> so the seeds never interfere.
/// </summary>
[Trait("Category", "Dashboard")]
public sealed class DashboardCountsTests : IClassFixture<TwoOrgsFixture>
{
    private readonly TwoOrgsFixture _fixture;

    public DashboardCountsTests(TwoOrgsFixture fixture) => _fixture = fixture;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    /// <summary>TC-001: every number on the dashboard equals the row counts of the active
    /// organization at the same instant, asserted against direct SQL, and the failure section is the
    /// terminal Failed/Lost runs with their reasons - never a Canceled one.</summary>
    [Fact]
    public async Task The_numbers_are_the_rows()
    {
        var now = DateTimeOffset.UtcNow;
        // Two projects, as TC-001 steps: recent runs name the project they belong to.
        await DashboardSeed.AddProjectAsync(_fixture, TwoOrgsFixture.OrgAId, "counts-second-app", CancellationToken);
        var busyWorkerId = await DashboardSeed.AddWorkerAsync(
            _fixture, TwoOrgsFixture.OrgAId, "counts-busy-agent", WorkerStatus.Online, now, CancellationToken);
        var runningRunId = await DashboardSeed.AddRunAsync(
            _fixture, TwoOrgsFixture.OrgAId, TwoOrgsFixture.ProjectAId, BuildRunStatus.Running,
            startedAt: now.AddMinutes(-2), workerId: busyWorkerId, cancellationToken: CancellationToken);
        var successRunId = await DashboardSeed.AddRunAsync(
            _fixture, TwoOrgsFixture.OrgAId, TwoOrgsFixture.ProjectAId, BuildRunStatus.Success,
            startedAt: now.AddHours(-1), endedAt: now.AddMinutes(-59), exitCode: 0, cancellationToken: CancellationToken);
        var failedRunId = await DashboardSeed.AddRunAsync(
            _fixture, TwoOrgsFixture.OrgAId, TwoOrgsFixture.ProjectAId, BuildRunStatus.Failed,
            startedAt: now.AddMinutes(-30), endedAt: now.AddMinutes(-29), exitCode: 3, cancellationToken: CancellationToken);
        var lostRunId = await DashboardSeed.AddRunAsync(
            _fixture, TwoOrgsFixture.OrgAId, TwoOrgsFixture.ProjectAId, BuildRunStatus.Lost,
            startedAt: now.AddMinutes(-20), endedAt: now.AddMinutes(-19), workerId: TwoOrgsFixture.WorkerAId,
            failureReason: RunFailureReasons.WorkerOffline, cancellationToken: CancellationToken);
        var canceledRunId = await DashboardSeed.AddRunAsync(
            _fixture, TwoOrgsFixture.OrgAId, TwoOrgsFixture.ProjectAId, BuildRunStatus.Canceled,
            cancellationToken: CancellationToken);
        // SaveChanges owns the audit stamps, so the queue time of the canceled-while-queued run is
        // pinned after the insert (its recency is its CreatedAt - there is no StartedAt).
        await DashboardSeed.SetRunCreatedAtAsync(
            _fixture, canceledRunId, now.AddMinutes(-10), CancellationToken);

        _fixture.UseOrganizationA();
        var summary = await DashboardQueryFactory.Create(_fixture).GetSummaryAsync(CancellationToken);

        Assert.NotNull(summary);
        // The tiles are the rows: compared with direct SQL on the same file first, then against the
        // exact row counts TC-001 describes.
        Assert.Equal(
            await DashboardSql.CountRunsAsync(_fixture, TwoOrgsFixture.OrgAId, BuildRunStatus.Queued, CancellationToken),
            summary.QueuedCount);
        Assert.Equal(
            await DashboardSql.CountRunsAsync(_fixture, TwoOrgsFixture.OrgAId, BuildRunStatus.Running, CancellationToken),
            summary.RunningCount);
        Assert.Equal(
            await DashboardSql.CountBusyWorkersAsync(_fixture, TwoOrgsFixture.OrgAId, CancellationToken),
            summary.WorkersBusy);
        Assert.Equal(
            await DashboardSql.CountWorkersAsync(_fixture, TwoOrgsFixture.OrgAId, WorkerStatus.Online, CancellationToken),
            summary.WorkersOnline);
        Assert.Equal(
            await DashboardSql.CountWorkersAsync(_fixture, TwoOrgsFixture.OrgAId, WorkerStatus.Offline, CancellationToken),
            summary.WorkersOffline);
        Assert.Equal(
            await DashboardSql.CountProjectsAsync(_fixture, TwoOrgsFixture.OrgAId, CancellationToken),
            summary.ProjectCount);
        Assert.Equal(2, summary.ProjectCount);

        Assert.Equal(1, summary.QueuedCount);
        Assert.Equal(1, summary.RunningCount);
        Assert.Equal(1, summary.WorkersOnline);
        Assert.Equal(1, summary.WorkersOffline);
        Assert.Equal(1, summary.WorkersBusy);
        Assert.Equal(2, summary.FailuresInWindow);

        // The failures section: the Failed and Lost runs, with the reason that was recorded - the
        // Canceled run is visible in the recent list but is not a failure.
        Assert.Equal(
            new[] { failedRunId, lostRunId }.OrderBy(id => id),
            summary.RecentFailures.Select(run => run.RunId).OrderBy(id => id));
        Assert.Equal(
            RunFailureReasons.WorkerOffline,
            summary.RecentRuns.Single(run => run.RunId == lostRunId).FailureReason);
        var recentIds = summary.RecentRuns.Select(run => run.RunId).ToList();
        Assert.Contains(canceledRunId, recentIds);
        Assert.Equal(
            new[] { TwoOrgsFixture.BuildRunAId, runningRunId, canceledRunId, lostRunId, failedRunId, successRunId },
            recentIds);
    }
}

/// <summary>Recency is the last 20 runs by <c>StartedAt ?? CreatedAt</c>, newest first (D-002).</summary>
[Trait("Category", "Dashboard")]
public sealed class DashboardOrderingTests : IClassFixture<TwoOrgsFixture>
{
    private readonly TwoOrgsFixture _fixture;

    public DashboardOrderingTests(TwoOrgsFixture fixture) => _fixture = fixture;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Recent_runs_are_capped_at_twenty_and_ordered_by_started_or_created()
    {
        var baseTime = DateTimeOffset.UtcNow.AddHours(-2);
        var ids = new List<Guid>();
        for (var i = 0; i < 25; i++)
        {
            var createdAt = baseTime.AddMinutes(-(i + 1));
            var runId = await DashboardSeed.AddRunAsync(
                _fixture,
                TwoOrgsFixture.OrgAId,
                TwoOrgsFixture.ProjectAId,
                i % 2 == 0 ? BuildRunStatus.Queued : BuildRunStatus.Success,
                startedAt: i % 2 == 0 ? null : createdAt,
                endedAt: i % 2 == 0 ? null : createdAt.AddMinutes(1),
                exitCode: i % 2 == 0 ? null : 0,
                cancellationToken: CancellationToken);
            // SaveChanges owns CreatedAt, so the intended queue time is pinned after the insert;
            // for the queued half this is the recency instant the ordering must follow.
            await DashboardSeed.SetRunCreatedAtAsync(_fixture, runId, createdAt, CancellationToken);
            ids.Add(runId);
        }

        // A run created long ago but started recently: recency follows StartedAt when there is one,
        // so this run outranks every CreatedAt-only run above despite being created last.
        var lateStartedId = await DashboardSeed.AddRunAsync(
            _fixture, TwoOrgsFixture.OrgAId, TwoOrgsFixture.ProjectAId, BuildRunStatus.Running,
            startedAt: baseTime.AddHours(1).AddMinutes(-30),
            cancellationToken: CancellationToken);
        await DashboardSeed.SetRunCreatedAtAsync(
            _fixture, lateStartedId, baseTime.AddMinutes(-31), CancellationToken);

        _fixture.UseOrganizationA();
        var summary = await DashboardQueryFactory.Create(_fixture).GetSummaryAsync(CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(DashboardQuery.RecentRunCap, summary.RecentRuns.Count);

        // The fixture's own run was created "now", so it is the newest; then the late-started probe,
        // then the staged runs by recency - 20 in total, the oldest excluded.
        var expected = new List<Guid> { TwoOrgsFixture.BuildRunAId, lateStartedId };
        expected.AddRange(ids.Take(18));
        var recentIds = summary.RecentRuns.Select(run => run.RunId).ToList();
        Assert.Equal(expected, recentIds);
        Assert.DoesNotContain(ids[18], recentIds);
        Assert.DoesNotContain(ids[24], recentIds);
    }
}

/// <summary>The failure window is 7 days over the whole history; Canceled is never a failure.</summary>
[Trait("Category", "Dashboard")]
public sealed class DashboardFailureWindowTests : IClassFixture<TwoOrgsFixture>
{
    private readonly TwoOrgsFixture _fixture;

    public DashboardFailureWindowTests(TwoOrgsFixture fixture) => _fixture = fixture;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Failures_are_the_windowed_terminal_failed_and_lost_runs()
    {
        var now = DateTimeOffset.UtcNow;
        var freshFailedId = await DashboardSeed.AddRunAsync(
            _fixture, TwoOrgsFixture.OrgAId, TwoOrgsFixture.ProjectAId, BuildRunStatus.Failed,
            startedAt: now.AddHours(-1), endedAt: now.AddMinutes(-59), exitCode: 3, cancellationToken: CancellationToken);
        var freshLostId = await DashboardSeed.AddRunAsync(
            _fixture, TwoOrgsFixture.OrgAId, TwoOrgsFixture.ProjectAId, BuildRunStatus.Lost,
            startedAt: now.AddHours(-2), endedAt: now.AddHours(-2).AddMinutes(1),
            failureReason: RunFailureReasons.WorkerOffline, cancellationToken: CancellationToken);
        var oldFailedId = await DashboardSeed.AddRunAsync(
            _fixture, TwoOrgsFixture.OrgAId, TwoOrgsFixture.ProjectAId, BuildRunStatus.Failed,
            startedAt: now.AddDays(-10), endedAt: now.AddDays(-10).AddMinutes(5), exitCode: 1, cancellationToken: CancellationToken);
        var canceledId = await DashboardSeed.AddRunAsync(
            _fixture, TwoOrgsFixture.OrgAId, TwoOrgsFixture.ProjectAId, BuildRunStatus.Canceled,
            createdAt: now.AddMinutes(-30), cancellationToken: CancellationToken);

        _fixture.UseOrganizationA();
        var summary = await DashboardQueryFactory.Create(_fixture).GetSummaryAsync(CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(2, summary.FailuresInWindow);
        Assert.Equal(
            new[] { freshFailedId, freshLostId }.Order(),
            summary.RecentFailures.Select(run => run.RunId).Order().ToArray());

        // A short history keeps the old failure inside the 20 most recent runs - it is still shown
        // there as a run, but it is outside the window and so is neither counted nor listed.
        var recentIds = summary.RecentRuns.Select(run => run.RunId).ToList();
        Assert.Contains(oldFailedId, recentIds);
        Assert.Contains(canceledId, recentIds);
        Assert.DoesNotContain(oldFailedId, summary.RecentFailures.Select(run => run.RunId));
        Assert.DoesNotContain(canceledId, summary.RecentFailures.Select(run => run.RunId));

        // The window the tile names is really now-7d (the read model's clock may be a beat later).
        Assert.True(
            (summary.WindowStart - now.AddDays(-7)).Duration() <= TimeSpan.FromSeconds(5),
            $"The failure window started at {summary.WindowStart:O}, expected about {now.AddDays(-7):O}.");
    }
}

/// <summary>
/// Tenant honesty of the read model: switching the active organization replaces every figure, in
/// both directions, and the other organization's rows never appear.
/// </summary>
[Trait("Category", "Dashboard")]
public sealed class DashboardOrganizationSwitchTests : IClassFixture<TwoOrgsFixture>
{
    private readonly TwoOrgsFixture _fixture;

    public DashboardOrganizationSwitchTests(TwoOrgsFixture fixture) => _fixture = fixture;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Switching_organizations_changes_every_figure_in_both_directions()
    {
        var now = DateTimeOffset.UtcNow;
        // A runs things and breaks them: two online workers (one busy), a running run, a failure.
        var busyWorkerId = await DashboardSeed.AddWorkerAsync(
            _fixture, TwoOrgsFixture.OrgAId, "switch-agent-busy", WorkerStatus.Online, now, CancellationToken);
        await DashboardSeed.AddWorkerAsync(
            _fixture, TwoOrgsFixture.OrgAId, "switch-agent-idle", WorkerStatus.Online, now, CancellationToken);
        await DashboardSeed.AddRunAsync(
            _fixture, TwoOrgsFixture.OrgAId, TwoOrgsFixture.ProjectAId, BuildRunStatus.Running,
            startedAt: now.AddMinutes(-1), workerId: busyWorkerId, cancellationToken: CancellationToken);
        await DashboardSeed.AddRunAsync(
            _fixture, TwoOrgsFixture.OrgAId, TwoOrgsFixture.ProjectAId, BuildRunStatus.Failed,
            startedAt: now.AddMinutes(-5), endedAt: now.AddMinutes(-4), exitCode: 2, cancellationToken: CancellationToken);

        // B has queue pressure and nothing executing: a second queued run next to the seeded one.
        await DashboardSeed.AddRunAsync(
            _fixture, TwoOrgsFixture.OrgBId, TwoOrgsFixture.ProjectBId, BuildRunStatus.Queued,
            cancellationToken: CancellationToken);

        var query = DashboardQueryFactory.Create(_fixture);

        _fixture.UseOrganizationA();
        var alpha = await query.GetSummaryAsync(CancellationToken);
        _fixture.UseOrganizationB();
        var beta = await query.GetSummaryAsync(CancellationToken);
        _fixture.UseOrganizationA();
        var alphaAgain = await query.GetSummaryAsync(CancellationToken);

        Assert.NotNull(alpha);
        Assert.NotNull(beta);
        Assert.NotNull(alphaAgain);

        // Every figure changes with the active organization, and each matches that organization's
        // rows read straight from SQL. ProjectCount happens to tie (one project each); the content
        // of the lists below is what proves the switch.
        Assert.Equal((1, 1), (alpha.ProjectCount, beta.ProjectCount));
        Assert.Equal((1, 2), (alpha.QueuedCount, beta.QueuedCount));
        Assert.Equal((1, 0), (alpha.RunningCount, beta.RunningCount));
        Assert.Equal((1, 0), (alpha.WorkersBusy, beta.WorkersBusy));
        Assert.Equal((2, 1), (alpha.WorkersOnline, beta.WorkersOnline));
        Assert.Equal((1, 0), (alpha.WorkersOffline, beta.WorkersOffline));
        Assert.Equal((1, 0), (alpha.FailuresInWindow, beta.FailuresInWindow));
        Assert.Equal(alpha.QueuedCount, await DashboardSql.CountRunsAsync(_fixture, TwoOrgsFixture.OrgAId, BuildRunStatus.Queued, CancellationToken));
        Assert.Equal(beta.QueuedCount, await DashboardSql.CountRunsAsync(_fixture, TwoOrgsFixture.OrgBId, BuildRunStatus.Queued, CancellationToken));

        // Not one foreign row: A's list is A's projects only, B's is B's only - both directions.
        Assert.All(alpha.RecentRuns, run => Assert.Equal(TwoOrgsFixture.ProjectAName, run.ProjectName));
        Assert.All(beta.RecentRuns, run => Assert.Equal(TwoOrgsFixture.ProjectBName, run.ProjectName));
        Assert.All(alpha.RecentFailures, run => Assert.Equal(TwoOrgsFixture.ProjectAName, run.ProjectName));

        // Switching back is the same dashboard again, figure for figure (GeneratedAt aside).
        Assert.Equal(
            (alpha.ProjectCount, alpha.QueuedCount, alpha.RunningCount, alpha.WorkersOnline,
                alpha.WorkersOffline, alpha.WorkersBusy, alpha.FailuresInWindow),
            (alphaAgain.ProjectCount, alphaAgain.QueuedCount, alphaAgain.RunningCount, alphaAgain.WorkersOnline,
                alphaAgain.WorkersOffline, alphaAgain.WorkersBusy, alphaAgain.FailuresInWindow));
        Assert.Equal(
            alpha.RecentRuns.Select(run => run.RunId),
            alphaAgain.RecentRuns.Select(run => run.RunId));
    }
}

/// <summary>
/// The cost ceiling (TC-003): with 200 runs behind a 20-row cap, the read model and the page both
/// cost a fixed number of commands. Documented ceiling, recorded in
/// <c>docs/kanban/issues/d-002-dashboard.md</c>: the read model issues exactly 4 commands (worker
/// aggregate, run scan, recent-20 detail, project count - one grouped aggregate that answers both the
/// total and card F-028's disabled count); the page render is bounded by 16.
/// <para>
/// <b>One fixture per test, never one per class (card B-003).</b> Both facts seed 200 <c>Success</c>
/// runs into the same organization A and neither cleans up, so a class fixture made whichever fact ran
/// second count the first one's history - the read-model fact then saw 401 runs where its seeding
/// promises 201. xunit constructs a new instance of a test class per fact, so holding the fixture in an
/// instance field (instead of <c>IClassFixture</c>) gives each fact its own temporary database and host.
/// </para>
/// </summary>
[Trait("Category", "Dashboard")]
public sealed class DashboardQueryCeilingTests : IDisposable
{
    /// <summary>The documented read-model ceiling (D-002's 4; card F-028's disabled count rides the
    /// same grouped aggregate as the total, so the ceiling does not move).</summary>
    public const int ReadModelCommandCeiling = 4;

    /// <summary>The documented page-render ceiling (session + shell + read model).</summary>
    public const int PageCommandCeiling = 16;

    /// <summary>This fact's own host and database; disposed with the test instance.</summary>
    private readonly DashboardPageFixture _host = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_read_model_command_count_is_fixed_regardless_of_history_length()
    {
        var counter = new DbCommandCounter();
        var query = DashboardQueryFactory.Create(_host.Data, counter);

        var baseTime = DateTimeOffset.UtcNow.AddHours(-1);
        for (var i = 0; i < 200; i++)
        {
            await DashboardSeed.AddRunAsync(
                _host.Data, TwoOrgsFixture.OrgAId, TwoOrgsFixture.ProjectAId, BuildRunStatus.Success,
                startedAt: baseTime.AddSeconds(-i), endedAt: baseTime.AddSeconds(-i).AddSeconds(10),
                createdAt: baseTime.AddSeconds(-i), exitCode: 0, cancellationToken: CancellationToken);
        }

        _host.Data.UseOrganizationA();
        counter.Reset();
        var alpha = await query.GetSummaryAsync(CancellationToken);

        Assert.Equal(DashboardQuery.RecentRunCap, alpha!.RecentRuns.Count);
        Assert.Equal(
            201,
            await DashboardSql.CountRunsAsync(_host.Data, TwoOrgsFixture.OrgAId, BuildRunStatus.Success, CancellationToken)
            + await DashboardSql.CountRunsAsync(_host.Data, TwoOrgsFixture.OrgAId, BuildRunStatus.Queued, CancellationToken));
        Assert.Equal(ReadModelCommandCeiling, counter.Count);

        // The same fixed cost for an organization with a single run: the count cannot scale with rows.
        _host.Data.UseOrganizationB();
        counter.Reset();
        var beta = await query.GetSummaryAsync(CancellationToken);

        Assert.Single(beta!.RecentRuns);
        Assert.Equal(ReadModelCommandCeiling, counter.Count);
    }

    [Fact]
    public async Task The_page_command_count_stays_under_the_ceiling_and_shows_exactly_twenty_runs()
    {
        var baseTime = DateTimeOffset.UtcNow.AddHours(-1);
        for (var i = 0; i < 200; i++)
        {
            await DashboardSeed.AddRunAsync(
                _host.Data, TwoOrgsFixture.OrgAId, TwoOrgsFixture.ProjectAId, BuildRunStatus.Success,
                startedAt: baseTime.AddSeconds(-i), endedAt: baseTime.AddSeconds(-i).AddSeconds(10),
                createdAt: baseTime.AddSeconds(-i), exitCode: 0, cancellationToken: CancellationToken);
        }

        using var client = await _host.SignInAsync("alice@example.test");

        // Warm-up: the very first render persists the active-organization fallback (an extra write)
        // and primes the host; the measured render must show the steady-state cost.
        _ = await AuthTestHost.GetStringAsync(client, "/");
        _host.Counter.Reset();

        var html = await AuthTestHost.GetStringAsync(client, "/");

        Assert.True(
            _host.Counter.Count <= PageCommandCeiling,
            $"The page issued {_host.Counter.Count} commands with 200 runs of history; the documented ceiling is {PageCommandCeiling}.");
        var runIds = DashboardPageReader.RecentRunIds(html);
        Assert.Equal(DashboardQuery.RecentRunCap, runIds.Count);
        Assert.Equal(runIds.Count, runIds.Distinct().Count());
    }

    public void Dispose() => _host.Dispose();
}

/// <summary>The shared display bits (D-002): one badge map and one duration formatter for every page.</summary>
[Trait("Category", "Dashboard")]
public sealed class DashboardDisplayFormatTests
{
    [Fact]
    public void Every_run_status_has_one_badge_variant_across_all_pages()
    {
        Assert.Equal(BadgeVariant.Secondary, RunDisplay.BadgeFor(BuildRunStatus.Queued));
        Assert.Equal(BadgeVariant.Default, RunDisplay.BadgeFor(BuildRunStatus.Running));
        Assert.Equal(BadgeVariant.Success, RunDisplay.BadgeFor(BuildRunStatus.Success));
        Assert.Equal(BadgeVariant.Destructive, RunDisplay.BadgeFor(BuildRunStatus.Failed));
        Assert.Equal(BadgeVariant.Outline, RunDisplay.BadgeFor(BuildRunStatus.Canceled));
        Assert.Equal(BadgeVariant.Destructive, RunDisplay.BadgeFor(BuildRunStatus.Lost));
        Assert.Equal(BadgeVariant.Outline, RunDisplay.BadgeFor("Something new"));
    }

    [Fact]
    public void Duration_is_prose_and_elapsed_stays_live()
    {
        var start = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal("42.0s", RunDisplay.FormatDuration(TimeSpan.FromSeconds(42)));
        Assert.Equal("42.4s", RunDisplay.FormatDuration(TimeSpan.FromSeconds(42.4)));
        Assert.Equal("1m 05s", RunDisplay.FormatDuration(TimeSpan.FromSeconds(65)));
        Assert.Equal("90m 05s", RunDisplay.FormatDuration(TimeSpan.FromMinutes(90) + TimeSpan.FromSeconds(5)));

        // A run that never started shows "-", never a zero or a fabricated duration.
        Assert.Equal("-", RunDisplay.DurationOrElapsed(null, null, start));
        Assert.Equal("-", RunDisplay.DurationOrElapsed(null, start, start));

        // A running run shows live elapsed time against the caller's clock instant, marked as running.
        Assert.Equal("42.0s (running)", RunDisplay.DurationOrElapsed(start, null, start.AddSeconds(42)));
        Assert.Equal("2m 05s (running)", RunDisplay.DurationOrElapsed(start, null, start.AddMinutes(2).AddSeconds(5)));

        // A finished run shows its stored duration; the clock instant is irrelevant.
        Assert.Equal(
            RunDisplay.DurationOrElapsed(start, start.AddMinutes(1).AddSeconds(5), start),
            RunDisplay.DurationOrElapsed(start, start.AddMinutes(1).AddSeconds(5), start.AddYears(1)));
    }
}
