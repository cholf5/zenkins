using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Schedules;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Schedules;

/// <summary>
/// Card F-024: per-project cron schedules, driven through the product's own surfaces - the settings card's forms
/// for the configuration, a seeded watermark for "a due time arrived", one evaluator tick through the sweeper's
/// seam for the decision, and the real claim/accept/complete API for the run it queues.
/// <para>
/// <b>The clock is fixed, the product is not.</b> The schedule service reads one <see cref="TimeProvider"/>; giving
/// it a stated instant (2026-11-16 12:00:30Z) makes "a due time arrived" exact - a watermark of 11:59:59 makes
/// 12:00:00 the newest one - instead of depending on where in the minute a test happens to run. Every other clock
/// (run timestamps, the worker API, the sweeper's timer) stays real.
/// </para>
/// <para>
/// Every verdict is read from the rows the server wrote (the run table and the due-time ledger), never from a
/// response alone, and every expectation names the policy the card and the PRD state: one run per due time, a skip
/// while a run is in flight, a recorded miss when nothing was evaluating the schedule, and the project gate
/// refusing a disabled project exactly as it refuses a click.
/// </para>
/// </summary>
[Trait("Category", "Schedules")]
public sealed class ScheduleTests
{
    /// <summary>The schedule service's clock: 30 seconds past noon, so the newest whole minute is 30s old.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 30, TimeSpan.Zero);

    /// <summary>One second before that minute: the only due time in the window is 12:00:00Z.</summary>
    private static readonly DateTimeOffset JustBeforeTheDueTime = new(2026, 11, 16, 11, 59, 59, TimeSpan.Zero);

    /// <summary>This minute's due time - what a per-minute schedule at the fixed instant evaluates.</summary>
    private static readonly DateTimeOffset ThisMinute = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static Task<ScheduleTestHost> CreateHostAsync(DateTimeOffset? clock = null) =>
        ScheduleTestHost.CreateAsync(clock: new TestClock(clock ?? Now));

    // ------------------------------------------------------------------
    // TC-001: one due time, one run, with the definition's defaults
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_due_schedule_queues_one_run_with_the_defaults_and_never_twice()
    {
        using var host = await CreateHostAsync();
        var (projectId, owner) = await host.CreateProjectAsync("schedule-queues");
        var organizationId = await host.OrganizationIdAsync();

        // F-021: a parameter with a default. A headless trigger cannot fill a form, so the default is the answer.
        await host.SaveStringParameterAsync(owner, projectId, "COUNT", "42");

        var scheduleId = await host.SeedScheduleAsync(projectId, evaluatedAt: JustBeforeTheDueTime);

        var tick = await host.SweepAsync();
        Assert.Equal(1, tick.Queued);

        var run = Assert.Single(await host.ReadRunsAsync(organizationId));
        Assert.Equal(BuildRunTriggerType.Schedule, run.TriggerType);
        Assert.Equal(BuildRunStatus.Queued, run.Status);
        Assert.Null(run.TriggerUserId);
        Assert.Equal("main", run.GitBranch);
        Assert.Equal(1, run.Number);
        Assert.Equal("42", ScheduleTestHost.ReadParameters(run.ParametersJson)["COUNT"]);

        var queued = Assert.Single(
            await host.ReadOccurrencesAsync(scheduleId),
            occurrence => occurrence.Outcome == ScheduleOutcomes.Queued);
        Assert.Equal(run.Id, queued.RunId);
        Assert.Equal(1, queued.RunNumber);
        Assert.Equal(ThisMinute, queued.DueAt);
        Assert.Equal(0, queued.CollapsedDueTimes);

        // The watermark moved with the record: the same due time can never be decided twice.
        Assert.Equal(Now, (await host.ReadScheduleAsync(scheduleId)).LastEvaluatedAt);

        // TC-001's second half: a second tick queues nothing, because the due time is already answered.
        var second = await host.SweepAsync();
        Assert.Equal(0, second.Queued);
        Assert.Single(await host.ReadRunsAsync(organizationId));
        Assert.Single(
            await host.ReadOccurrencesAsync(scheduleId),
            occurrence => occurrence.Outcome == ScheduleOutcomes.Queued);
    }

    [Fact]
    public async Task A_scheduled_run_is_an_ordinary_run_for_the_worker_contract()
    {
        // The card's cross-cutting requirement: a schedule does not get a private insert path, so its run is
        // claimable, acceptable and completable exactly like a click's.
        using var host = await CreateHostAsync();
        var (projectId, _) = await host.CreateProjectAsync("schedule-worker");
        await host.SeedScheduleAsync(projectId, evaluatedAt: JustBeforeTheDueTime);
        Assert.Equal(1, (await host.SweepAsync()).Queued);

        var (workerToken, _) = await host.Inner.RegisterWorkerAsync(name: "schedule-agent");
        using var workerClient = host.CreateClient();
        var claimed = await Zenkins.Tests.Runs.RunTestHost.ClaimAsync(workerClient, workerToken);
        var runId = await Zenkins.Tests.Runs.RunTestHost.ReadClaimedRunIdAsync(claimed);

        Assert.Equal(
            HttpStatusCode.OK,
            (await Zenkins.Tests.Runs.RunTestHost.AcceptAsync(workerClient, workerToken, runId)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await Zenkins.Tests.Runs.RunTestHost.CompleteAsync(workerClient, workerToken, runId, exitCode: 0)).StatusCode);

        var run = await host.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Success, run.Status);
        Assert.Equal(BuildRunTriggerType.Schedule, run.TriggerType);
    }

    [Fact]
    public async Task A_freestyle_project_can_carry_a_schedule_too()
    {
        // Card AC1's "both kinds": a Freestyle project has no repository file to declare a schedule in, so the
        // schedule row is the only place it can live - and its run carries the frozen step spec.
        using var host = await CreateHostAsync();
        var projectId = await host.SeedFreestyleProjectAsync("freestyle-scheduled");
        await host.SeedScheduleAsync(projectId, evaluatedAt: JustBeforeTheDueTime);

        Assert.Equal(1, (await host.SweepAsync()).Queued);

        var project = await host.ReadProjectAsync(projectId);
        var run = Assert.Single(await host.ReadRunsAsync(await host.OrganizationIdAsync()));
        Assert.Equal(project.FreestyleSpecJson, run.PipelineSpecJson);
        Assert.Equal(string.Empty, run.GitBranch);
    }

    // ------------------------------------------------------------------
    // TC-002: the overlap policy, and that it says which run it met
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_due_time_while_a_run_is_in_flight_is_skipped_and_names_the_run()
    {
        using var host = await CreateHostAsync();
        var (projectId, owner) = await host.CreateProjectAsync("schedule-overlap");
        var organizationId = await host.OrganizationIdAsync();

        // A run already waiting (no worker), and a schedule whose due time arrives now. The default policy is
        // "skip", never stack.
        var waitingRunId = await host.Inner.AddRunAsync(projectId, organizationId: organizationId);
        var scheduleId = await host.SeedScheduleAsync(projectId, evaluatedAt: JustBeforeTheDueTime);

        var tick = await host.SweepAsync();
        Assert.Equal(1, tick.SkippedOverlap);
        Assert.Equal(0, tick.Queued);

        // Nobody stacked a second run.
        Assert.Single(await host.ReadRunsAsync(organizationId));

        var waiting = await host.ReadRunAsync(waitingRunId);
        var occurrence = Assert.Single(await host.ReadOccurrencesAsync(scheduleId));
        Assert.Equal(ScheduleOutcomes.SkippedOverlap, occurrence.Outcome);
        Assert.Equal(waiting.Number, occurrence.RunNumber);
        Assert.Null(occurrence.RunId);

        // TC-002's last line: the project page shows the skip as the last outcome.
        var page = await ScheduleTestHost.ReadSettingsPageAsync(owner, projectId);
        Assert.Contains("skipped-overlap", page, StringComparison.Ordinal);
        Assert.Contains($"run #{waiting.Number} was still queued or running", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queue_anyway_queues_the_second_run_instead_of_skipping()
    {
        using var host = await CreateHostAsync();
        var (projectId, _) = await host.CreateProjectAsync("schedule-anyway");
        var organizationId = await host.OrganizationIdAsync();

        await host.Inner.AddRunAsync(projectId, organizationId: organizationId);
        var scheduleId = await host.SeedScheduleAsync(
            projectId,
            queueAnyway: true,
            evaluatedAt: JustBeforeTheDueTime);

        var tick = await host.SweepAsync();
        Assert.Equal(1, tick.Queued);
        Assert.Equal(2, (await host.ReadRunsAsync(organizationId)).Count);
        Assert.Single(
            await host.ReadOccurrencesAsync(scheduleId),
            occurrence => occurrence.Outcome == ScheduleOutcomes.Queued);
    }

    [Fact]
    public async Task Queue_anyway_is_still_bounded_by_the_projects_queue_cap()
    {
        // F-029's cap is checked inside the one insert path, so "queue anyway" asks the same gate a click does -
        // and the refusal is recorded rather than swallowed.
        using var host = await CreateHostAsync();
        var (projectId, owner) = await host.CreateProjectAsync("schedule-queue-cap");
        var organizationId = await host.OrganizationIdAsync();
        Assert.Equal(
            HttpStatusCode.Redirect,
            (await Zenkins.Tests.Runs.RunTestHost.SaveLimitsAsync(owner, projectId, maxQueuedRuns: 1)).StatusCode);
        await host.Inner.AddRunAsync(projectId, organizationId: organizationId);

        var scheduleId = await host.SeedScheduleAsync(
            projectId,
            queueAnyway: true,
            evaluatedAt: JustBeforeTheDueTime);

        var tick = await host.SweepAsync();
        Assert.Equal(1, tick.SkippedQueueFull);
        Assert.Single(await host.ReadRunsAsync(organizationId));

        var occurrence = Assert.Single(await host.ReadOccurrencesAsync(scheduleId));
        Assert.Equal(ScheduleOutcomes.SkippedQueueFull, occurrence.Outcome);
    }

    // ------------------------------------------------------------------
    // TC-003: no backfill, but the misses are recorded and counted
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_server_that_was_down_records_every_missed_due_time_and_queues_nothing()
    {
        using var host = await CreateHostAsync();
        var (projectId, owner) = await host.CreateProjectAsync("schedule-catch-up");
        var organizationId = await host.OrganizationIdAsync();

        // The watermark says nothing has evaluated this schedule for 32 minutes: the Server was down (or the
        // schedule's row outlived a restart). A per-minute cron therefore has 32 due times in the window.
        var watermark = Now.AddMinutes(-32).AddSeconds(-30);   // 11:28:00Z, exactly 32 whole minutes back
        var scheduleId = await host.SeedScheduleAsync(projectId, evaluatedAt: watermark);

        var tick = await host.SweepAsync();

        // No backfill: not one of those 32 due times queued a run.
        Assert.Equal(0, tick.Queued);
        Assert.Empty(await host.ReadRunsAsync(organizationId));
        Assert.True(tick.Missed >= 20, $"expected at least the recorded cap of misses, got {tick.Missed}");

        var occurrences = await host.ReadOccurrencesAsync(scheduleId);
        Assert.All(occurrences, occurrence => Assert.Equal(ScheduleOutcomes.Missed, occurrence.Outcome));

        // The list is bounded (Schedules:MaxRecordedMisses), the count is not: the folded number on the oldest
        // kept row makes the listed total exact, and the schedule keeps the evaluation's own number.
        Assert.Equal(ScheduleOptions.DefaultMaxRecordedMisses, occurrences.Count);
        Assert.Equal(32, occurrences.Sum(occurrence => 1 + occurrence.CollapsedDueTimes));
        Assert.Equal(12, occurrences[^1].CollapsedDueTimes);
        Assert.Equal(32, (await host.ReadScheduleAsync(scheduleId)).LastMissedCount);

        // The page can say how many it missed...
        var page = await ScheduleTestHost.ReadSettingsPageAsync(owner, projectId);
        Assert.Contains("32 due time(s)", page, StringComparison.Ordinal);
        Assert.Contains("recorded as missed", page, StringComparison.Ordinal);

        // ...and the schedule is still armed: the evaluation moved on, and the next due times are previewed.
        Assert.Equal(Now, (await host.ReadScheduleAsync(scheduleId)).LastEvaluatedAt);
        Assert.Contains("Next runs", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_outage_lists_a_bounded_backlog_and_never_states_a_wrong_total()
    {
        // Same rule, further back: a three-day outage on a per-minute schedule is 4320 due times. The ledger keeps
        // the retention's newest rows, the folded count keeps the total, and the note says earlier misses are not
        // listed - the honest alternative to a million rows or a false "0 missed".
        using var host = await CreateHostAsync();
        var (projectId, owner) = await host.CreateProjectAsync("schedule-long-outage");
        var watermark = Now.AddDays(-3);
        var scheduleId = await host.SeedScheduleAsync(projectId, evaluatedAt: watermark);

        var tick = await host.SweepAsync();
        Assert.Equal(0, tick.Queued);

        var occurrences = await host.ReadOccurrencesAsync(scheduleId);
        Assert.Equal(ScheduleOptions.DefaultMaxRecordedMisses, occurrences.Count);

        // The window, not the outage, bounds the count: one day of a per-minute schedule is 1440 due times, and
        // the schedule states exactly that instead of claiming to know about the other two days.
        var schedule = await host.ReadScheduleAsync(scheduleId);
        Assert.InRange(schedule.LastMissedCount, 1430, 1440);
        Assert.Equal(
            schedule.LastMissedCount,
            occurrences.Sum(occurrence => 1 + occurrence.CollapsedDueTimes));

        // The retention window's edge is stated, not hidden: the oldest kept row carries the marker.
        Assert.Contains(occurrences, occurrence => occurrence.Detail == ScheduleOutcomes.TruncatedDetail);
        Assert.Contains(
            "Earlier misses are not listed",
            ScheduleOutcomes.Describe(
                occurrences[^1].Outcome,
                occurrences[^1].RunNumber,
                occurrences[^1].CollapsedDueTimes,
                occurrences[^1].Detail),
            StringComparison.Ordinal);

        // Review L1: the marker sits on a row the page never shows (it lists the newest ten), so the *count*
        // sentence has to state the window itself - otherwise "1440 missed" reads as the whole outage.
        Assert.True(schedule.LastMissedWindowTruncated);
        var page = await ScheduleTestHost.ReadSettingsPageAsync(owner, projectId);
        Assert.Contains("due time(s)", page, StringComparison.Ordinal);
        Assert.Contains("Only the newest 24", page, StringComparison.Ordinal);
        Assert.Contains("earlier missed due times are not listed", page, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // A disabled project or schedule queues nothing, and says why
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_disabled_project_records_skipped_disabled_and_queues_nothing()
    {
        // F-028's AC2, schedule half: the shared refusal vocabulary's `skipped-disabled` finally has a writer.
        using var host = await CreateHostAsync();
        var (projectId, owner) = await host.CreateProjectAsync("schedule-disabled-project");
        var organizationId = await host.OrganizationIdAsync();
        Assert.Equal(
            HttpStatusCode.Redirect,
            (await Zenkins.Tests.Runs.RunTestHost.DisableProjectAsync(owner, projectId, "repository is mid-migration")).StatusCode);

        var scheduleId = await host.SeedScheduleAsync(projectId, evaluatedAt: JustBeforeTheDueTime);

        var tick = await host.SweepAsync();
        Assert.Equal(1, tick.SkippedDisabled);
        Assert.Equal(0, tick.Queued);
        Assert.Empty(await host.ReadRunsAsync(organizationId));

        var occurrence = Assert.Single(await host.ReadOccurrencesAsync(scheduleId));
        Assert.Equal(ScheduleOutcomes.SkippedDisabled, occurrence.Outcome);
        Assert.Equal("project-disabled", occurrence.Detail);

        // The page states it, in the trigger-source vocabulary the card asked for.
        var page = await ScheduleTestHost.ReadSettingsPageAsync(owner, projectId);
        Assert.Contains("skipped-disabled", page, StringComparison.Ordinal);
        Assert.Contains("the project or the schedule is disabled", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disabled_schedule_is_evaluated_but_records_a_skip()
    {
        var clock = new TestClock(Now);
        using var host = await ScheduleTestHost.CreateAsync(clock: clock);
        var (projectId, owner) = await host.CreateProjectAsync("schedule-disabled-row");
        var organizationId = await host.OrganizationIdAsync();

        var scheduleId = await host.SeedScheduleAsync(
            projectId,
            enabled: false,
            evaluatedAt: JustBeforeTheDueTime);

        var tick = await host.SweepAsync();
        Assert.Equal(1, tick.SkippedDisabled);
        Assert.Empty(await host.ReadRunsAsync(organizationId));
        Assert.Equal(
            ScheduleOutcomes.SkippedDisabled,
            Assert.Single(await host.ReadOccurrencesAsync(scheduleId)).Outcome);

        // Enabling it queues the *next* due time - a disabled schedule is not a replayable backlog.
        Assert.EndsWith(
            "notice=schedule-enabled",
            ScheduleTestHost.Redirect(
                await ScheduleTestHost.ToggleScheduleAsync(
                    owner,
                    projectId,
                    scheduleId,
                    ScheduleEndpoints.FlagEnabled,
                    value: true)),
            StringComparison.Ordinal);

        // The next due time, not the one already answered: an enabled schedule fires from here on, and the due
        // time the disabled schedule skipped keeps its record (one due time, one row).
        clock.Advance(TimeSpan.FromMinutes(1));
        await host.InSystemScopeAsync(async context =>
        {
            var schedule = (await context.SystemQueryAsync<ProjectSchedule>(row => row.Id == scheduleId)).Single();
            schedule.LastEvaluatedAt = ThisMinute;
            await context.SaveChangesAsync();
            return true;
        });

        Assert.Equal(1, (await host.SweepAsync()).Queued);
        Assert.Single(await host.ReadRunsAsync(organizationId));
    }

    // ------------------------------------------------------------------
    // The race guard: one (schedule, due time) row, one run
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_due_time_another_writer_already_recorded_queues_nothing()
    {
        // The unique (ScheduleId, DueAt) index is the guard for a writer outside the row lock. This test plays that
        // writer by recording the due time first, then lets the next tick find it: the tick must recognise the due
        // time as answered (not queue for it, not insert a second row, not fail) - review M1/M2's self-healing
        // read, which is what an exception-only guard could not do.
        using var host = await CreateHostAsync();
        var (projectId, _) = await host.CreateProjectAsync("schedule-race");
        var organizationId = await host.OrganizationIdAsync();

        var scheduleId = await host.SeedScheduleAsync(projectId, evaluatedAt: JustBeforeTheDueTime);
        await RecordDueTimeAsync(host, scheduleId, ThisMinute, ScheduleOutcomes.Queued);

        var tick = await host.SweepAsync();
        Assert.Equal(1, tick.Raced);
        Assert.Equal(0, tick.Queued);
        Assert.Equal(0, tick.Failed);

        // No run at all, and exactly the writer's row - nothing was duplicated, nothing had to be rolled back.
        Assert.Empty(await host.ReadRunsAsync(organizationId));
        var occurrence = Assert.Single(await host.ReadOccurrencesAsync(scheduleId));
        Assert.Equal(ScheduleOutcomes.Queued, occurrence.Outcome);

        // ...and the tick moved its watermark past the due time, so it is not recomputed (review M2).
        Assert.Equal(Now, (await host.ReadScheduleAsync(scheduleId)).LastEvaluatedAt);
    }

    [Fact]
    public async Task A_lost_due_time_does_not_stall_the_schedule_and_the_next_one_queues()
    {
        // Review M2's fact: a due time another writer recorded must not become a permanent failure loop. The first
        // tick loses the due time and advances the watermark; the next due time queues normally.
        var clock = new TestClock(Now);
        using var host = await ScheduleTestHost.CreateAsync(clock: clock);
        var (projectId, _) = await host.CreateProjectAsync("schedule-race-heals");
        var organizationId = await host.OrganizationIdAsync();

        var scheduleId = await host.SeedScheduleAsync(projectId, evaluatedAt: JustBeforeTheDueTime);
        await RecordDueTimeAsync(host, scheduleId, ThisMinute, ScheduleOutcomes.Queued);

        var lost = await host.SweepAsync();
        Assert.Equal(1, lost.Raced);
        Assert.Equal(0, lost.Queued);
        Assert.Equal(0, lost.Failed);
        Assert.Empty(await host.ReadRunsAsync(organizationId));

        // The next due time: one minute later, the same schedule, nothing else changed.
        clock.Advance(TimeSpan.FromMinutes(1));
        var next = await host.SweepAsync();
        Assert.Equal(0, next.Raced);
        Assert.Equal(0, next.Failed);
        Assert.Equal(1, next.Queued);

        var run = Assert.Single(await host.ReadRunsAsync(organizationId));
        var queued = Assert.Single(
            await host.ReadOccurrencesAsync(scheduleId),
            occurrence => occurrence.Outcome == ScheduleOutcomes.Queued && occurrence.RunId == run.Id);
        Assert.Equal(ThisMinute.AddMinutes(1), queued.DueAt);
    }

    [Fact]
    public async Task A_missed_backlog_and_a_due_time_to_queue_survive_a_due_time_another_writer_recorded()
    {
        // Review M1's shape, and the one the SQLite facts could not reach before: one tick that has *both* missed
        // rows to write and a run to queue, while a due time in the window is already recorded. The missed rows are
        // flushed by the run's own save, so a conflict used to surface outside the tick's guard and turn this into
        // `Failed`; it must read `Raced`, write the other rows and still queue the run.
        using var host = await CreateHostAsync();
        var (projectId, _) = await host.CreateProjectAsync("schedule-race-with-misses");
        var organizationId = await host.OrganizationIdAsync();

        // Four due times in the window (11:57:00 … 12:00:00Z); the last one is the run to queue, the other three
        // are the missed backlog, and the middle miss was recorded by another writer.
        var scheduleId = await host.SeedScheduleAsync(projectId, evaluatedAt: Now.AddMinutes(-4));
        await RecordDueTimeAsync(host, scheduleId, ThisMinute.AddMinutes(-2), ScheduleOutcomes.Missed);

        var tick = await host.SweepAsync();
        Assert.Equal(0, tick.Failed);
        Assert.Equal(1, tick.Raced);
        Assert.Equal(2, tick.Missed);
        Assert.Equal(1, tick.Queued);

        var run = Assert.Single(await host.ReadRunsAsync(organizationId));
        Assert.Equal(BuildRunTriggerType.Schedule, run.TriggerType);

        var occurrences = await host.ReadOccurrencesAsync(scheduleId);
        Assert.Equal(4, occurrences.Count);
        Assert.Single(occurrences, occurrence => occurrence.Outcome == ScheduleOutcomes.Queued
            && occurrence.RunId == run.Id);
        Assert.Equal(
            [ThisMinute.AddMinutes(-3), ThisMinute.AddMinutes(-2), ThisMinute.AddMinutes(-1)],
            occurrences
                .Where(occurrence => occurrence.Outcome == ScheduleOutcomes.Missed)
                .OrderBy(occurrence => occurrence.DueAt)
                .Select(occurrence => occurrence.DueAt));

        // The count states the whole window, including the miss another writer recorded.
        Assert.Equal(3, (await host.ReadScheduleAsync(scheduleId)).LastMissedCount);
    }

    // ------------------------------------------------------------------
    // DST through the tick: the clock the zone skips is recorded, not run
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_due_time_the_zone_skips_is_recorded_by_the_tick_and_never_run()
    {
        // A controlled clock, because the fact is about a specific night: 2026-03-29 02:30 does not exist in
        // Europe/Berlin. Everything else - the tick, the ledger, the insert path - is the product's.
        var fixedNow = new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero);
        using var host = await ScheduleTestHost.CreateAsync(clock: new TestClock(fixedNow));
        var (projectId, _) = await host.CreateProjectAsync("schedule-dst");
        var organizationId = await host.OrganizationIdAsync();

        var scheduleId = await host.SeedScheduleAsync(
            projectId,
            cron: "30 2 * * *",
            timeZone: "Europe/Berlin",
            evaluatedAt: new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero));

        var tick = await host.SweepAsync();
        Assert.Equal(1, tick.SkippedNonexistent);
        Assert.Equal(0, tick.Queued);
        Assert.Empty(await host.ReadRunsAsync(organizationId));

        var occurrence = Assert.Single(await host.ReadOccurrencesAsync(scheduleId));
        Assert.Equal(ScheduleOutcomes.SkippedNonexistent, occurrence.Outcome);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), occurrence.DueAt);
    }

    /// <summary>
    /// Plays the "writer outside the row lock" the unique index exists for: records one due time directly, as
    /// another scheduler (or a future trigger source) would, so the next tick has to recognise it as answered.
    /// </summary>
    private static Task RecordDueTimeAsync(
        ScheduleTestHost host,
        Guid scheduleId,
        DateTimeOffset dueAt,
        string outcome) =>
        host.InSystemScopeAsync(async context =>
        {
            var schedule = (await context.SystemQueryAsync<ProjectSchedule>(row => row.Id == scheduleId)).Single();
            context.ScheduleOccurrences.Add(new ScheduleOccurrence
            {
                OrganizationId = schedule.OrganizationId,
                ScheduleId = scheduleId,
                DueAt = dueAt,
                Outcome = outcome,
                RecordedAt = dueAt,
            });
            await context.SaveChangesAsync();
            return true;
        });

    // ------------------------------------------------------------------
    // The settings card's forms
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_card_saves_a_valid_schedule_and_refuses_an_invalid_one_with_a_field_code()
    {
        using var host = await CreateHostAsync();
        var (projectId, owner) = await host.CreateProjectAsync("schedule-forms");

        Assert.EndsWith(
            $"/projects/{projectId}/settings?notice=schedule-created",
            ScheduleTestHost.Redirect(
                await ScheduleTestHost.CreateScheduleAsync(owner, projectId, "0 3 * * *", "Europe/Berlin")),
            StringComparison.Ordinal);

        var saved = Assert.Single(await host.InSystemScopeAsync(context =>
            context.SystemQueryAsync<ProjectSchedule>(schedule => schedule.ProjectId == projectId)));
        Assert.Equal("0 3 * * *", saved.Cron);
        Assert.Equal("Europe/Berlin", saved.TimeZone);
        Assert.True(saved.Enabled);
        Assert.False(saved.QueueAnyway);

        // A new schedule starts armed but never retroactive: its watermark is its creation instant. (The row's
        // CreatedAt is the context's own audit stamp - a real-clock value - which is why the assertion is about
        // the watermark, the field the evaluator reads.)
        Assert.Equal(Now, saved.LastEvaluatedAt);

        // An expression this product does not read is refused with the field code, and nothing is stored.
        var invalidCron = await ScheduleTestHost.CreateScheduleAsync(owner, projectId, "@daily", "UTC");
        Assert.EndsWith(
            $"/projects/{projectId}/settings?error={ScheduleErrorCodes.CronInvalid}",
            ScheduleTestHost.Redirect(invalidCron),
            StringComparison.Ordinal);
        var page = await ScheduleTestHost.ReadSettingsPageAsync(
            owner,
            projectId,
            "?error=" + ScheduleErrorCodes.CronInvalid);
        Assert.Contains("That is not a 5-field cron expression", page, StringComparison.Ordinal);

        // ...so is an unknown zone, and an exact duplicate of the schedule already saved.
        Assert.EndsWith(
            $"/projects/{projectId}/settings?error={ScheduleErrorCodes.TimeZoneUnknown}",
            ScheduleTestHost.Redirect(
                await ScheduleTestHost.CreateScheduleAsync(owner, projectId, "0 3 * * *", "Mars/Olympus")),
            StringComparison.Ordinal);

        Assert.EndsWith(
            $"/projects/{projectId}/settings?error={ScheduleErrorCodes.ScheduleDuplicate}",
            ScheduleTestHost.Redirect(
                await ScheduleTestHost.CreateScheduleAsync(owner, projectId, "0 3 * * *", "Europe/Berlin")),
            StringComparison.Ordinal);

        Assert.Single(await host.InSystemScopeAsync(context =>
            context.SystemQueryAsync<ProjectSchedule>(schedule => schedule.ProjectId == projectId)));
    }

    [Fact]
    public async Task The_card_previews_the_next_three_due_times_before_anything_is_saved()
    {
        using var host = await CreateHostAsync();
        var (projectId, owner) = await host.CreateProjectAsync("schedule-preview");

        var location = ScheduleTestHost.Redirect(
            await ScheduleTestHost.CreateScheduleAsync(
                owner,
                projectId,
                "0 3 * * *",
                "Europe/Berlin",
                preview: true));
        Assert.StartsWith($"/projects/{projectId}/settings?", location, StringComparison.Ordinal);
        Assert.Contains("previewCron=", location, StringComparison.Ordinal);
        Assert.Contains("previewZone=", location, StringComparison.Ordinal);
        Assert.DoesNotContain("error=", location, StringComparison.Ordinal);

        var page = await AuthTestHost.GetStringAsync(owner, location);
        Assert.Contains("id=\"schedule-preview\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("schedule-preview-error", page, StringComparison.Ordinal);

        // The three times are the schedule's own: 03:00 Berlin is 02:00Z in November, three days running.
        var section = page[page.IndexOf("id=\"schedule-preview\"", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("</div>", StringComparison.Ordinal)];
        Assert.Equal(3, section.Split("<li>", StringSplitOptions.None).Length - 1);
        Assert.Contains("2026-11-17 02:00", section, StringComparison.Ordinal);
        Assert.Contains("2026-11-18 02:00", section, StringComparison.Ordinal);
        Assert.Contains("2026-11-19 02:00", section, StringComparison.Ordinal);

        // Nothing was saved by the preview.
        Assert.Empty(await host.InSystemScopeAsync(context =>
            context.SystemQueryAsync<ProjectSchedule>(schedule => schedule.ProjectId == projectId)));

        // The same form's *invalid* preview answers the field code instead of a set of times.
        var refusedLocation = ScheduleTestHost.Redirect(
            await ScheduleTestHost.CreateScheduleAsync(owner, projectId, "0 3 * *", "UTC", preview: true));
        Assert.Contains($"error={ScheduleErrorCodes.CronInvalid}", refusedLocation, StringComparison.Ordinal);
        Assert.Contains(
            "schedule-preview-error",
            await AuthTestHost.GetStringAsync(owner, refusedLocation),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_card_toggles_and_deletes_a_schedule()
    {
        using var host = await CreateHostAsync();
        var (projectId, owner) = await host.CreateProjectAsync("schedule-toggle");

        Assert.EndsWith(
            "notice=schedule-created",
            ScheduleTestHost.Redirect(
                await ScheduleTestHost.CreateScheduleAsync(owner, projectId, "*/15 * * * *", "UTC")),
            StringComparison.Ordinal);

        var saved = Assert.Single(await host.InSystemScopeAsync(context =>
            context.SystemQueryAsync<ProjectSchedule>(schedule => schedule.ProjectId == projectId)));

        // The two switches are separate facts, each with its own notice.
        Assert.EndsWith(
            "notice=schedule-disabled",
            ScheduleTestHost.Redirect(
                await ScheduleTestHost.ToggleScheduleAsync(
                    owner,
                    projectId,
                    saved.Id,
                    ScheduleEndpoints.FlagEnabled,
                    value: false)),
            StringComparison.Ordinal);
        Assert.False((await host.ReadScheduleAsync(saved.Id)).Enabled);

        Assert.EndsWith(
            "notice=schedule-queue-anyway",
            ScheduleTestHost.Redirect(
                await ScheduleTestHost.ToggleScheduleAsync(
                    owner,
                    projectId,
                    saved.Id,
                    ScheduleEndpoints.FlagQueueAnyway,
                    value: true)),
            StringComparison.Ordinal);
        Assert.True((await host.ReadScheduleAsync(saved.Id)).QueueAnyway);

        // An unknown switch is a field error, not a silent success.
        Assert.EndsWith(
            $"?error={ScheduleErrorCodes.FieldInvalid}",
            ScheduleTestHost.Redirect(
                await ScheduleTestHost.ToggleScheduleAsync(owner, projectId, saved.Id, "bogus", value: true)),
            StringComparison.Ordinal);

        // The page lists it...
        var page = await ScheduleTestHost.ReadSettingsPageAsync(owner, projectId);
        Assert.Contains($"id=\"schedule-{saved.Id}\"", page, StringComparison.Ordinal);

        // ...and deleting it removes the row, its ledger and its card.
        Assert.EndsWith(
            "notice=schedule-deleted",
            ScheduleTestHost.Redirect(await ScheduleTestHost.DeleteScheduleAsync(owner, projectId, saved.Id)),
            StringComparison.Ordinal);
        Assert.Empty(await host.InSystemScopeAsync(context =>
            context.SystemQueryAsync<ProjectSchedule>(schedule => schedule.Id == saved.Id)));
        Assert.Contains(
            "schedules-empty",
            await ScheduleTestHost.ReadSettingsPageAsync(owner, projectId),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_foreign_or_unknown_project_id_is_a_404()
    {
        // The route is authoritative and every read goes through the tenant filter, so an id this organization
        // does not have answers exactly like one that never existed. (The antiforgery token comes from a page
        // this tenant can render: the token is bound to the cookie, not to the URL.)
        using var host = await CreateHostAsync();
        var (projectId, owner) = await host.CreateProjectAsync("schedule-unknown-id");
        var html = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/settings");
        var token = OrganizationTestHost.ExtractAntiforgeryMetaToken(html);

        var unknownProject = Guid.NewGuid();
        var unknownSchedule = Guid.NewGuid();

        // Every schedule route answers it the same way: create, toggle and delete each resolve the project
        // through the tenant filter, so an id this tenant does not have is not a mutation target.
        foreach (var (url, fields) in new[]
        {
            (
                $"/projects/{unknownProject}/schedules/create",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Cron"] = "0 5 * * *",
                    ["TimeZone"] = "UTC",
                    ["Enabled"] = "true",
                }),
            (
                $"/projects/{unknownProject}/schedules/{unknownSchedule}/toggle",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Flag"] = ScheduleEndpoints.FlagEnabled,
                    ["Value"] = "false",
                }),
            (
                $"/projects/{unknownProject}/schedules/{unknownSchedule}/delete",
                new Dictionary<string, string>(StringComparer.Ordinal)),
        })
        {
            fields["__RequestVerificationToken"] = token;
            var refused = await owner.PostAsync(
                url,
                new FormUrlEncodedContent(fields),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        }
    }
}
