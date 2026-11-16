using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// One cron schedule of a project (card F-024): a 5-field expression evaluated in an IANA time zone, whose
/// every due time either queues exactly one run or leaves a recorded reason why it did not.
/// <para>
/// <b>Why a table and not the pipeline.</b> A Freestyle project (F-020) has no repository file to declare a
/// schedule in, and the clock is the Server's, not the repository's - so a schedule is a row of the project,
/// like its queue controls (F-029), and it triggers both kinds through the same gate and the same insert path
/// as the manual form and a webhook delivery.
/// </para>
/// <para>
/// <b>The row is not the record of what happened.</b> <see cref="LastEvaluatedAt"/> is the
/// evaluator's watermark (the instant the sweeper last looked at this schedule), never a claim about a run;
/// the durable account of every due time - queued, skipped or missed - is one
/// <see cref="ScheduleOccurrence"/> row per <c>(schedule, due time)</c>, whose unique index is also what makes
/// two racing schedulers queue one run instead of two.
/// </para>
/// </summary>
public sealed class ProjectSchedule : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>The project whose runs this schedule queues. Cascade-deleted with it.</summary>
    public Guid ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    /// <summary>
    /// The 5-field cron expression (<c>minute hour day-of-month month day-of-week</c>), stored as the
    /// operator typed it after trimming. Validated at save time - a row that cannot be parsed here is
    /// refused before it is written, and a pre-existing bad row is logged instead of crashing a tick
    /// (see <c>ScheduleService</c>).
    /// </summary>
    public required string Cron { get; set; }

    /// <summary>
    /// The IANA time zone the expression is read in (<c>Europe/Berlin</c>, <c>Asia/Tokyo</c>, <c>UTC</c>).
    /// Stored as the operator's id: the evaluation resolves it through <see cref="TimeZoneInfo"/>, so a zone's
    /// DST rules - including a future rule change - are the operating system's, not a snapshot's.
    /// </summary>
    public required string TimeZone { get; set; }

    /// <summary>
    /// Whether the schedule is active. A disabled schedule queues nothing but is still evaluated, so every
    /// due time it passes is recorded as <c>skipped-disabled</c> rather than silently disappearing.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The per-schedule overlap switch (card F-024's explicit policy). <see langword="false"/> - the default -
    /// means a due time while any run of the project is queued or running is <b>skipped</b>
    /// (<c>skipped-overlap</c>), never stacked; <see langword="true"/> queues it anyway, still bounded by the
    /// project's queue cap (F-029), which then answers <c>skipped-queue-full</c>.
    /// </summary>
    public bool QueueAnyway { get; set; }

    /// <summary>
    /// The evaluator's watermark: the instant up to which this schedule's due times have been decided. Written
    /// by every tick, inside the same transaction as the occurrence rows it explains, so a crash can never leave
    /// a due time both "handled" and unrecorded. Null only for a row written out of band before the first tick;
    /// creation stamps it so a new schedule never fires for a due time that passed before it existed.
    /// </summary>
    public DateTimeOffset? LastEvaluatedAt { get; set; }

    /// <summary>
    /// How many due times the most recent evaluation recorded as <c>missed</c> (card F-024's "no backfill, but say
    /// how many"). Kept on the row so the count survives the ledger's retention - the page can always state the
    /// last evaluation's misses, and the folded <see cref="ScheduleOccurrence.CollapsedDueTimes"/> keeps the rows
    /// themselves consistent with it. Zero when the last evaluation missed nothing.
    /// </summary>
    public int LastMissedCount { get; set; }

    /// <summary>
    /// Whether the last evaluation's miss window was capped by <c>Schedules:MissWindowHours</c> - i.e.
    /// <see cref="LastMissedCount"/> counts the newest window and earlier missed due times were not enumerated.
    /// The page states that limit next to the count, so a three-day outage cannot read as "1440 missed" with no
    /// hint that the outage was longer (review L1: the note used to live on the oldest retained row, which the
    /// page never shows).
    /// </summary>
    public bool LastMissedWindowTruncated { get; set; }

    /// <summary>
    /// The Identity user id of the member who created the schedule. Deliberately no foreign key: deleting an
    /// account must not sweep a project's schedules away (the webhook/credential discipline).
    /// </summary>
    public required string CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One due time of a schedule and what became of it (card F-024) - the "audit of the last N outcomes", the
/// no-backfill record, and the race guard, in one row.
/// <para>
/// <b>The unique <c>(ScheduleId, DueAt)</c> index is the guarantee, not an afterthought.</b> Two servers (or two
/// ticks of one) that both decide the same due time is due cannot both queue a run: the row lock serializes
/// them, and the index refuses a second row even if a future caller ever stages outside it. The losing insert
/// rolls its own transaction back, so its run disappears with it - never a queued run without a record, never
/// two runs for one due time.
/// </para>
/// <para>
/// <b>Bounded by pruning, not by hope.</b> A schedule keeps its newest
/// <c>Schedules:RetainedOutcomes</c> rows; older ones are deleted by the same tick that writes new ones, so a
/// per-minute schedule cannot grow the table without limit. What the page loses is history it never showed;
/// what it keeps is the count - <see cref="CollapsedDueTimes"/> folds an unlistable backlog of missed due times
/// into the oldest row that is kept, so "38 missed while the Server was down" stays exact.
/// </para>
/// </summary>
public sealed class ScheduleOccurrence : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>The schedule this due time belongs to. Cascade-deleted with it.</summary>
    public Guid ScheduleId { get; set; }

    public ProjectSchedule? Schedule { get; set; }

    /// <summary>
    /// The due instant, in UTC - the schedule's cron read in its own zone and converted, so the record is
    /// unambiguous across DST. A due time whose local wall clock does not exist (the spring-forward gap) is
    /// recorded at the instant of the transition itself, with <see cref="ScheduleOutcomes.SkippedNonexistent"/>.
    /// </summary>
    public DateTimeOffset DueAt { get; set; }

    /// <summary>What happened at this due time: one value of <see cref="ScheduleOutcomes"/>.</summary>
    public required string Outcome { get; set; }

    /// <summary>The run this due time queued, when it queued one.</summary>
    public Guid? RunId { get; set; }

    /// <summary>
    /// The build number the page shows: the queued run's <c>#N</c>, or - for <c>skipped-overlap</c> - the number
    /// of the run that was in flight. Stored rather than joined so the history stays readable on its own (a
    /// skipped due time has no run row to join to at all).
    /// </summary>
    public int? RunNumber { get; set; }

    /// <summary>
    /// How many earlier due times this row stands for. Zero for a normal row; N when an unlistable backlog of
    /// misses was folded into the oldest kept row, so the total the page states is still the truth.
    /// </summary>
    public int CollapsedDueTimes { get; set; }

    /// <summary>
    /// A short, stable detail for the page: a refusal code (<c>steps-missing</c>) or the truncation marker
    /// <see cref="ScheduleOutcomes.TruncatedDetail"/> for a backlog the retention window could not list.
    /// </summary>
    public string? Detail { get; set; }

    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
}
