using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Xunit.Sdk;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Schedules;
using Zenkins.Tests.Concurrency;

namespace Zenkins.Tests.Schedules;

/// <summary>
/// Card Q-018: the one path F-024's review-M1 fix touched - a writer committing between the tick's ledger read and
/// its save - driven deterministically on the provider where it exists at all, plus the disposition of the due time
/// the losing tick caught.
/// <para>
/// <b>Why PostgreSQL, and why this shape.</b> SQLite serializes every write on one database lock, so a second
/// connection cannot hold a row that the tick's own transaction then collides with: the conflict this fact is about
/// is unconstructible there. On PostgreSQL the shape is the one the fix was written for - a <b>missed backlog plus a
/// newer due time to queue</b>, with the rival holding <em>one of the missed</em> due times. That matters: the
/// staging routine saves, which flushes the missed rows with it, so the refusal surfaces from <b>inside</b>
/// <c>ScheduleService.DecideAsync</c>. A rival holding the candidate's own due time would instead be refused by the
/// tick's final <c>SaveChanges</c>, which sits inside the guard both before and after the fix - a green fact that
/// proves nothing about it.
/// </para>
/// <para>
/// <b>How the collision is made deterministic.</b> A second connection inserts the rival's due time and does not
/// commit. Under READ COMMITTED the tick's ledger read cannot see it, so the tick decides that due time too; its
/// INSERT then blocks on <c>IX_ScheduleOccurrences_ScheduleId_DueAt</c> until the rival commits, at which point the
/// statement fails with <c>23505</c> rather than silently writing a duplicate. The fact waits for that lock wait to
/// appear in <c>pg_stat_activity</c> before it commits the rival, and it asserts the tick was still running at that
/// moment - so nothing here depends on a sleep (the same discipline as <see cref="PostgresBuildNumberTests"/>).
/// </para>
/// <para>
/// <b>What it holds the tick to (Q-018's AC-3).</b> The whole attempt rolls back, the candidate included. Re-reading
/// after the rollback is the only disposition that accounts for it: the winner's rows are committed by then, so a
/// second attempt records the remaining backlog and queues the candidate in the same tick. This fact therefore
/// asserts the candidate's due time ends up <b>queued</b>, with a ledger row naming the run - not merely that the
/// tick said <c>Raced</c>. On the pre-Q-018 tree it is red here (the settle moved the watermark to <c>now</c>, so
/// the candidate was neither queued nor recorded), and with the review-M1 fix reverted it is red at
/// <c>Failed=1</c> instead.
/// </para>
/// <para>
/// <b>Prerequisite.</b> The same disposable database as <see cref="PostgresProjectCapTests"/>, named by
/// <c>ZENKINS_TEST_POSTGRES</c>; without it the fact skips with the recorded reason, and because the category is
/// excluded from the fast filter (development.md §2) it costs the default loop nothing.
/// </para>
/// </summary>
[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresScheduleRaceTests
{
    /// <summary>The schedule service's clock: 30 seconds past noon, so the newest whole minute is 30s old.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 30, TimeSpan.Zero);

    /// <summary>A watermark four minutes back: four due times, the last one the run to queue.</summary>
    private static readonly DateTimeOffset Watermark = Now.AddMinutes(-4);

    /// <summary>The newest ordinary due time - the tick's candidate.</summary>
    private static readonly DateTimeOffset Candidate = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The due time the rival connection records - a miss, two minutes before the candidate.</summary>
    private static readonly DateTimeOffset RivalsDueTime = Candidate.AddMinutes(-2);

    [Fact(Timeout = 5 * 60 * 1000)]
    public async Task A_missed_backlog_and_the_due_time_to_queue_survive_a_due_time_another_writer_recorded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var skipReason = PostgresProjectCapTests.UnavailabilityReason();
        if (skipReason is not null)
        {
            throw SkipException.ForSkip(skipReason);
        }

        var connectionString = Environment.GetEnvironmentVariable(
            PostgresProjectCapTests.ConnectionStringVariable)!;
        await PostgresProjectCapTests.ResetDatabaseAsync(connectionString, cancellationToken);

        // The product's own clock, fixed: "a due time arrived" is exact instead of depending on where in the
        // minute the test runs, and the two ticks below are the same instant.
        using var host = await ScheduleTestHost.CreateAsync(
            configureAppConfiguration: configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Postgres",
                    ["ConnectionStrings:Default"] = connectionString,
                    ["Database:ApplyMigrationsOnStartup"] = "true",
                }),
            configureTestServices: services =>
            {
                services.RemoveAll<ZenkinsDataOptions>();
                services.AddSingleton(new ZenkinsDataOptions(DatabaseProvider.Postgres, connectionString));
            },
            clock: new TestClock(Now));

        var (projectId, _) = await host.CreateProjectAsync("pg-schedule-race");
        var organizationId = await host.OrganizationIdAsync();
        var scheduleId = await host.SeedScheduleAsync(projectId, evaluatedAt: Watermark);

        var tenant = new TenantContext { OrganizationId = organizationId };
        var systemScope = new SystemDataScope();
        var options = new DbContextOptionsBuilder<ZenkinsDbContext>()
            .UseZenkinsDatabase(new ZenkinsDataOptions(DatabaseProvider.Postgres, connectionString))
            .Options;

        // ---- The rival: an UNCOMMITTED missed due time that the tick's ledger read cannot see ---------------
        await using var rival = new ZenkinsDbContext(options, tenant, systemScope);
        await using var rivalTransaction = await rival.Database.BeginTransactionAsync(cancellationToken);
        await rival.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "ScheduleOccurrences" ("Id", "OrganizationId", "ScheduleId", "DueAt", "Outcome",
                                               "CollapsedDueTimes", "RecordedAt")
            VALUES ({0}, {1}, {2}, {3}, {4}, 0, {3});
            """,
            new object[] { Guid.NewGuid(), organizationId, scheduleId, RivalsDueTime, ScheduleOutcomes.Missed },
            cancellationToken);

        // ---- The tick, in the background: it must block on the index the rival is holding -------------------
        var tick = Task.Run(() => host.SweepAsync(), cancellationToken);

        // Wait for the collision itself, not for a clock: the tick's ScheduleOccurrences insert is blocked on
        // the unique index the rival holds. Only then does the rival commit, turning the block into 23505.
        var blocked = await WaitForOccurrenceIndexWaitAsync(options, tenant, systemScope, cancellationToken);
        if (!blocked)
        {
            // Leave nothing behind for the rest of the collection: release the row, let the tick finish, and
            // only then report - a blocked backend outliving a red fact would stall the next fact's schema
            // drop instead.
            await rivalTransaction.RollbackAsync(cancellationToken);
            try
            {
                await tick;
            }
            catch (Exception)
            {
                // The assertion below is the report; the tick's own failure is not the finding.
            }

            Assert.Fail(
                "The tick never waited on IX_ScheduleOccurrences_ScheduleId_DueAt, so the collision this fact "
                + "exists for did not happen and its assertions would prove nothing about the catch.");
        }

        // The tick is inside its save, waiting - so it did reach the insert with the rival's due time still
        // invisible to it, which is exactly the window review M1 widened the guard to cover.
        Assert.False(
            tick.IsCompleted,
            "The tick finished before the rival committed - it cannot have collided on the rival's due time.");

        await rivalTransaction.CommitAsync(cancellationToken);

        var result = await tick;

        // ---- AC-1: the refusal was handled, not raised: no `Failed`, and it is counted as a lost due time ----
        Assert.Equal(0, result.Failed);
        Assert.Equal(1, result.Raced);

        // ---- AC-3: the due time that ran into the refusal is accounted for, in the same tick ---------------
        // The rest of the window was decided after the rollback: the other two misses recorded, the candidate
        // queued. Before Q-018 the candidate was neither: the settle moved the watermark to `now` and this
        // assertion read `0`.
        Assert.Equal(1, result.Queued);
        Assert.Equal(2, result.Missed);

        var runs = await host.ReadRunsAsync(organizationId);
        var run = Assert.Single(runs);
        Assert.Equal(BuildRunTriggerType.Schedule, run.TriggerType);
        Assert.Equal(BuildRunStatus.Queued, run.Status);

        var occurrences = await host.ReadOccurrencesAsync(scheduleId);
        Assert.Equal(4, occurrences.Count);

        var candidateRow = Assert.Single(occurrences, occurrence => occurrence.DueAt == Candidate);
        Assert.Equal(ScheduleOutcomes.Queued, candidateRow.Outcome);
        Assert.Equal(run.Id, candidateRow.RunId);
        Assert.Equal(run.Number, candidateRow.RunNumber);

        // The rival's own decision stands untouched - it is the row the tick lost to - and it produced no run:
        // F-024's "one due time never queues two runs" holds for the due time that started the collision too.
        var rivalRow = Assert.Single(occurrences, occurrence => occurrence.DueAt == RivalsDueTime);
        Assert.Equal(ScheduleOutcomes.Missed, rivalRow.Outcome);
        Assert.Null(rivalRow.RunId);

        Assert.Equal(
            [Candidate.AddMinutes(-3), Candidate.AddMinutes(-1)],
            occurrences
                .Where(occurrence => occurrence.DueAt != RivalsDueTime && occurrence.DueAt != Candidate)
                .OrderBy(occurrence => occurrence.DueAt)
                .Select(occurrence => occurrence.DueAt));

        // The retry closed the window the ordinary way: the watermark reached this tick's instant, and the miss
        // count still states the whole window (the rival's miss included).
        var schedule = await host.ReadScheduleAsync(scheduleId);
        Assert.Equal(Now, schedule.LastEvaluatedAt);
        Assert.Equal(3, schedule.LastMissedCount);
    }

    /// <summary>
    /// Waits until a backend other than this one is blocked on the <c>ScheduleOccurrences</c> unique index - the
    /// tick's INSERT waiting on the rival's uncommitted row. A bounded poll (20 s), so a regression fails with a
    /// readable message instead of hanging, and the <c>query</c> filter keeps it from mistaking any other lock wait
    /// in the database for this fact's.
    /// </summary>
    private static async Task<bool> WaitForOccurrenceIndexWaitAsync(
        DbContextOptions<ZenkinsDbContext> options,
        TenantContext tenant,
        SystemDataScope systemScope,
        CancellationToken cancellationToken)
    {
        await using var probe = new ZenkinsDbContext(options, tenant, systemScope);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var waiting = await probe.Database
                .SqlQueryRaw<int>(
                    """
                    SELECT COUNT(*) AS "Value"
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND pid <> pg_backend_pid()
                      AND wait_event_type = 'Lock'
                      AND query LIKE '%ScheduleOccurrences%'
                    """)
                .ToListAsync(cancellationToken);
            if (waiting.Single() > 0)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        return false;
    }
}
