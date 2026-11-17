using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Projects;
using Zenkins.Server.Runs;

namespace Zenkins.Server.Schedules;

/// <summary>The status of a schedule read or mutation (the project/webhook vocabulary).</summary>
public enum ScheduleManagementStatus
{
    Success = 0,
    Forbidden = 1,
    NotFound = 2,
    Invalid = 3,
}

/// <summary>One recorded due time, as the settings page shows it.</summary>
/// <param name="DueAt">The due instant in UTC (see <see cref="ScheduleOccurrence.DueAt"/>).</param>
/// <param name="RunNumber">The queued run's number, or the in-flight run's for an overlap skip.</param>
/// <param name="CollapsedDueTimes">Earlier due times this row stands for (0 for a normal row).</param>
public sealed record ScheduleOutcomeView(
    DateTimeOffset DueAt,
    string Outcome,
    Guid? RunId,
    int? RunNumber,
    int CollapsedDueTimes,
    string? Detail)
{
    /// <summary>The sentence the page renders for this row.</summary>
    public string Description => ScheduleOutcomes.Describe(Outcome, RunNumber, CollapsedDueTimes, Detail);
}

/// <summary>One schedule as the settings page shows it: its configuration, its next due times, its last outcomes.</summary>
public sealed record ScheduleView(
    Guid Id,
    Guid ProjectId,
    string Cron,
    string TimeZone,
    bool Enabled,
    bool QueueAnyway,
    DateTimeOffset? LastEvaluatedAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<DateTimeOffset> NextDueTimes,
    IReadOnlyList<ScheduleOutcomeView> RecentOutcomes,

    /// <summary>
    /// How many due times the last evaluation recorded as missed (card F-024). Exact for the evaluation's window;
    /// the row's folded count and the retention window document where the listed backlog stops.
    /// </summary>
    int LastMissedCount,

    /// <summary>
    /// Whether that window was capped by <c>Schedules:MissWindowHours</c>, i.e. the count covers the newest
    /// window and earlier missed due times were not enumerated - the page says so beside the number.
    /// </summary>
    bool LastMissedWindowTruncated)
{
    /// <summary>Whether the next-due preview could be computed (a broken stored expression says so).</summary>
    public bool HasNextDue => NextDueTimes.Count > 0;
}

/// <summary>The list result: the rows for a member, or the reason there are none.</summary>
public sealed record ScheduleListResult(
    ScheduleManagementStatus Status,
    IReadOnlyList<ScheduleView> Schedules,
    string? Message = null)
{
    public static ScheduleListResult Success(IReadOnlyList<ScheduleView> schedules) =>
        new(ScheduleManagementStatus.Success, schedules);

    public static ScheduleListResult Forbidden(string message) =>
        new(ScheduleManagementStatus.Forbidden, [], message);

    public static ScheduleListResult NotFound() => new(ScheduleManagementStatus.NotFound, []);
}

/// <summary>A mutation's answer: the id it touched, or the field error that refused it.</summary>
public sealed record ScheduleMutationResult(
    ScheduleManagementStatus Status,
    Guid? ScheduleId = null,
    string? ErrorCode = null,
    string? Message = null)
{
    public static ScheduleMutationResult Success(Guid scheduleId) =>
        new(ScheduleManagementStatus.Success, scheduleId);

    public static ScheduleMutationResult NotFound(string message) =>
        new(ScheduleManagementStatus.NotFound, Message: message);

    public static ScheduleMutationResult Forbidden(string message) =>
        new(ScheduleManagementStatus.Forbidden, Message: message);

    public static ScheduleMutationResult Invalid(string code, string? message = null) =>
        new(ScheduleManagementStatus.Invalid, ErrorCode: code, Message: message ?? ScheduleErrorCodes.MessageFor(code));
}

/// <summary>The raw create-form values, before trimming and validation.</summary>
public sealed record ScheduleInput(string? Cron, string? TimeZone, bool QueueAnyway, bool Enabled);

/// <summary>
/// The "next three due times" preview (card F-024): computed from submitted values, never stored, so an operator
/// sees what a save would schedule before saving it. It answers with the same field codes a save would, so a
/// useless expression is visible at the preview instead of after the save.
/// </summary>
public sealed record SchedulePreview(
    bool IsValid,
    IReadOnlyList<DateTimeOffset> DueTimes,
    TimeZoneInfo? Zone,
    string? ErrorCode,
    string? Message)
{
    public static SchedulePreview Invalid(string code) =>
        new(false, [], null, code, ScheduleErrorCodes.MessageFor(code));

    /// <summary>Validates one expression/zone pair and resolves its next due times.</summary>
    public static SchedulePreview Compute(string? cronText, string? zoneId, DateTimeOffset now)
    {
        if (!ScheduleCron.TryParse(cronText, out var cron, out var cronError) || cron is null)
        {
            return Invalid(cronError ?? ScheduleErrorCodes.CronInvalid);
        }

        if (!ScheduleEvaluator.TryResolveZone(zoneId, out var zone))
        {
            return Invalid(
                string.IsNullOrWhiteSpace(zoneId)
                    ? ScheduleErrorCodes.TimeZoneRequired
                    : ScheduleErrorCodes.TimeZoneUnknown);
        }

        var dueTimes = ScheduleEvaluator.NextOccurrences(cron, zone, now, ScheduleOptions.PreviewCount);
        if (dueTimes.Count == 0)
        {
            return Invalid(ScheduleErrorCodes.CronNeverFires);
        }

        return new SchedulePreview(true, dueTimes, zone, null, null);
    }
}

/// <summary>What one evaluator tick did, summed over every schedule it looked at.</summary>
public sealed record ScheduleTickResult(
    int Queued = 0,
    int SkippedOverlap = 0,
    int SkippedDisabled = 0,
    int SkippedQueueFull = 0,
    int SkippedRefused = 0,
    int Missed = 0,
    int SkippedNonexistent = 0,
    int Raced = 0,
    int Failed = 0)
{
    /// <summary>How many schedules produced any outcome (the tick's "did anything happen").</summary>
    public int Outcomes =>
        Queued + SkippedOverlap + SkippedDisabled + SkippedQueueFull + SkippedRefused + Missed + SkippedNonexistent;

    /// <summary>Adds one schedule's result to the tick's.</summary>
    public ScheduleTickResult Plus(ScheduleTickResult other) => new(
        Queued + other.Queued,
        SkippedOverlap + other.SkippedOverlap,
        SkippedDisabled + other.SkippedDisabled,
        SkippedQueueFull + other.SkippedQueueFull,
        SkippedRefused + other.SkippedRefused,
        Missed + other.Missed,
        SkippedNonexistent + other.SkippedNonexistent,
        Raced + other.Raced,
        Failed + other.Failed);
}

/// <summary>
/// One attempt at one schedule (card Q-018): either the result it committed, or the fact that the unique
/// <c>(ScheduleId, DueAt)</c> index refused it and the whole attempt was rolled back.
/// </summary>
/// <param name="Result">What the attempt did - for a lost attempt, the <c>Raced</c> count it earned.</param>
/// <param name="LostRace">Whether another writer had already recorded one of this window's due times.</param>
internal readonly record struct ScheduleAttempt(ScheduleTickResult Result, bool LostRace);

/// <summary>
/// The schedule feature's server side (card F-024): the settings page's CRUD, the preview, and the one evaluation
/// routine the sweeper ticks.
/// <para>
/// <b>One insert path.</b> A due time is queued through <see cref="RunService.QueueScheduledRunAsync"/>, which
/// stages the run through the same routine - and therefore the same F-028/F-029 gate and project row lock - as the
/// manual form, the API and a webhook delivery. Nothing here writes a <see cref="BuildRun"/> row itself.
/// </para>
/// <para>
/// <b>The evaluation is the card's honesty.</b> For each schedule it computes the due times in
/// <c>(watermark, now]</c> and then, at most once:
/// </para>
/// <list type="bullet">
/// <item><b>queues</b> - only the newest ordinary due time, and only when it is no more than
/// <c>Schedules:MaxDueLatenessSeconds</c> late <em>and</em> the watermark was itself fresh. A Server that was down
/// (a stale watermark) queues nothing for the due times it slept through: they are recorded as <c>missed</c>,
/// which is the card's no-backfill rule;</item>
/// <item><b>skips</b> - with the reason recorded: <c>skipped-overlap</c> (a run of the project is already queued or
/// running and the schedule does not queue anyway), <c>skipped-disabled</c> (the project or the schedule is
/// disabled), <c>skipped-queue-full</c> (queue anyway, but F-029's cap refused it) or <c>skipped-refused</c> (any
/// other refusal, e.g. a Freestyle project with no steps).</item>
/// </list>
/// <para>
/// Older due times in the window are recorded as <c>missed</c>, never queued. The list is bounded by
/// <c>Schedules:MaxRecordedMisses</c> with the exact remainder folded into the oldest kept row, and by
/// <c>Schedules:MissWindowHours</c> behind which a single "earlier misses are not listed" note stands - so a Server
/// that was down for a month costs one bounded pass and still tells the truth about how many it missed.
/// </para>
/// </summary>
public sealed class ScheduleService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    TenantContext tenantContext,
    ISystemDataScope systemDataScope,
    RunService runs,
    ScheduleOptions options,
    TimeProvider timeProvider,
    ILogger<ScheduleService> logger)
{
    /// <summary>How many outcomes the settings page lists per schedule (the card's "last outcomes").</summary>
    public const int ShownOutcomes = 10;

    // ---------------------------------------------------------------------
    // Reads (the settings page)
    // ---------------------------------------------------------------------

    /// <summary>One project's schedules with their next due times and their newest outcomes.</summary>
    public async Task<ScheduleListResult> ListAsync(
        string actingUserId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is null)
        {
            return ScheduleListResult.Forbidden(
                "You are not a member of an organization, so there are no schedules to list.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return ScheduleListResult.Forbidden("You are not a member of the active organization.");
        }

        if (!await context.Projects.AnyAsync(project => project.Id == projectId, cancellationToken))
        {
            // Foreign or unknown project: the same 404, no existence oracle.
            return ScheduleListResult.NotFound();
        }

        var schedules = await context.ProjectSchedules
            .AsNoTracking()
            .Where(schedule => schedule.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        // The occurrence rows are read once for the whole page and ordered in memory: SQLite cannot order
        // DateTimeOffset columns in SQL (the lease/artifact sweeper constraint), and the set is bounded by the
        // retention (Schedules:RetainedOutcomes per schedule).
        var scheduleIds = schedules.Select(schedule => schedule.Id).ToArray();
        var occurrences = scheduleIds.Length == 0
            ? []
            : await context.ScheduleOccurrences
                .AsNoTracking()
                .Where(occurrence => scheduleIds.Contains(occurrence.ScheduleId))
                .ToListAsync(cancellationToken);
        var bySchedule = occurrences.ToLookup(occurrence => occurrence.ScheduleId);

        var now = timeProvider.GetUtcNow();
        return ScheduleListResult.Success(
        [
            .. schedules
                .OrderBy(schedule => schedule.CreatedAt)
                .Select(schedule => ToView(schedule, bySchedule[schedule.Id], now)),
        ]);
    }

    // ---------------------------------------------------------------------
    // Mutations (the settings page's forms)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Adds a schedule. The expression and the zone are validated here, the refusal names the field - and a save
    /// that would never fire is refused, because a stored schedule that can never run is a silent dead end.
    /// </summary>
    public async Task<ScheduleMutationResult> CreateAsync(
        string actingUserId,
        Guid projectId,
        ScheduleInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return ScheduleMutationResult.Forbidden(
                "You are not a member of an organization, so there is no project to schedule.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return ScheduleMutationResult.Forbidden("You are not a member of the active organization.");
        }

        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            return ScheduleMutationResult.NotFound("That project no longer exists.");
        }

        var now = timeProvider.GetUtcNow();
        var preview = SchedulePreview.Compute(input.Cron, input.TimeZone, now);
        if (!preview.IsValid)
        {
            return ScheduleMutationResult.Invalid(preview.ErrorCode!);
        }

        var existing = await context.ProjectSchedules
            .CountAsync(schedule => schedule.ProjectId == projectId, cancellationToken);
        if (existing >= ScheduleErrorCodes.MaxSchedulesPerProject)
        {
            return ScheduleMutationResult.Invalid(ScheduleErrorCodes.ScheduleLimit);
        }

        var cron = input.Cron!.Trim();
        var zone = input.TimeZone!.Trim();

        var schedule = new ProjectSchedule
        {
            OrganizationId = activeOrganizationId.Value,
            ProjectId = project.Id,
            Cron = cron,
            TimeZone = zone,
            Enabled = input.Enabled,
            QueueAnyway = input.QueueAnyway,
            CreatedByUserId = actingUserId,

            // No backfill, by construction: the watermark starts at creation, so a due time that passed before
            // the schedule existed can never be mistaken for one this schedule missed.
            LastEvaluatedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        context.ProjectSchedules.Add(schedule);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException failure) when (DatabaseErrors.IsUniqueViolation(failure, "ProjectSchedules"))
        {
            // The unique (ProjectId, Cron, TimeZone) index, not a pre-flight query, is the guard.
            return ScheduleMutationResult.Invalid(ScheduleErrorCodes.ScheduleDuplicate);
        }

        logger.LogInformation(
            "Schedule created. OrganizationId={OrganizationId} ProjectId={ProjectId} ScheduleId={ScheduleId} Cron={Cron} TimeZone={TimeZone} Enabled={Enabled} QueueAnyway={QueueAnyway} ByUserId={UserId}",
            activeOrganizationId,
            project.Id,
            schedule.Id,
            schedule.Cron,
            schedule.TimeZone,
            schedule.Enabled,
            schedule.QueueAnyway,
            actingUserId);
        return ScheduleMutationResult.Success(schedule.Id);
    }

    /// <summary>
    /// Flips one of the schedule's two switches - <c>enabled</c> or <c>queue-anyway</c> - for a member. The flag
    /// name is a closed set: an unknown one is a field error, never a no-op that looks like success.
    /// </summary>
    public async Task<ScheduleMutationResult> SetFlagAsync(
        string actingUserId,
        Guid projectId,
        Guid scheduleId,
        string? flag,
        bool value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        var normalized = flag?.Trim().ToLowerInvariant();
        if (normalized is not ("enabled" or "queue-anyway"))
        {
            return ScheduleMutationResult.Invalid(ScheduleErrorCodes.FieldInvalid);
        }

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return ScheduleMutationResult.Forbidden(
                "You are not a member of an organization, so there is no schedule to change.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return ScheduleMutationResult.Forbidden("You are not a member of the active organization.");
        }

        var schedule = await context.ProjectSchedules.SingleOrDefaultAsync(
            candidate => candidate.Id == scheduleId && candidate.ProjectId == projectId,
            cancellationToken);
        if (schedule is null)
        {
            return ScheduleMutationResult.NotFound(ScheduleErrorCodes.MessageFor(ScheduleErrorCodes.ScheduleNotFound)!);
        }

        if (normalized == "enabled")
        {
            schedule.Enabled = value;
        }
        else
        {
            schedule.QueueAnyway = value;
        }

        schedule.UpdatedAt = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Schedule changed. OrganizationId={OrganizationId} ProjectId={ProjectId} ScheduleId={ScheduleId} Flag={Flag} Value={Value} ByUserId={UserId}",
            activeOrganizationId,
            projectId,
            schedule.Id,
            normalized,
            value,
            actingUserId);
        return ScheduleMutationResult.Success(schedule.Id);
    }

    /// <summary>
    /// Deletes a schedule and, by cascade, its due-time ledger. Runs it already queued stay: a schedule's history
    /// of outcomes explains what happened, it does not own the runs that happened.
    /// </summary>
    public async Task<ScheduleMutationResult> DeleteAsync(
        string actingUserId,
        Guid projectId,
        Guid scheduleId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return ScheduleMutationResult.Forbidden(
                "You are not a member of an organization, so there is no schedule to delete.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return ScheduleMutationResult.Forbidden("You are not a member of the active organization.");
        }

        var schedule = await context.ProjectSchedules.SingleOrDefaultAsync(
            candidate => candidate.Id == scheduleId && candidate.ProjectId == projectId,
            cancellationToken);
        if (schedule is null)
        {
            return ScheduleMutationResult.NotFound(ScheduleErrorCodes.MessageFor(ScheduleErrorCodes.ScheduleNotFound)!);
        }

        context.ProjectSchedules.Remove(schedule);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Schedule deleted. OrganizationId={OrganizationId} ProjectId={ProjectId} ScheduleId={ScheduleId} Cron={Cron} ByUserId={UserId}",
            activeOrganizationId,
            projectId,
            schedule.Id,
            schedule.Cron,
            actingUserId);
        return ScheduleMutationResult.Success(schedule.Id);
    }

    // ---------------------------------------------------------------------
    // The evaluator (the sweeper's tick and the tests' seam)
    // ---------------------------------------------------------------------

    /// <summary>
    /// One evaluator tick over every schedule of every organization - the sweeper's unit of work, exposed so tests
    /// drive a tick instead of waiting for real time. Each schedule is evaluated in its own transaction under its
    /// own row lock; one schedule's failure is logged and does not stop the others.
    /// </summary>
    public async Task<ScheduleTickResult> EvaluateDueSchedulesAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        // Cross-tenant discovery (the sweeper serves every organization). The rows are materialised inside the
        // system scope and only ids are kept: nothing tenant-owned is read outside its tenant afterwards.
        List<(Guid Id, Guid OrganizationId)> pending;
        await using (var discovery = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            using var scope = systemDataScope.BeginScope();
            pending =
            [
                .. (await discovery.SystemQueryAsync<ProjectSchedule>(_ => true))
                    .Select(schedule => (schedule.Id, schedule.OrganizationId)),
            ];
        }

        var result = new ScheduleTickResult();
        foreach (var organization in pending.GroupBy(schedule => schedule.OrganizationId))
        {
            // The schedule row selects the tenant for everything that follows (the webhook-delivery discipline):
            // the project read, the run insert and the occurrence write all go through the tenant filter.
            tenantContext.OrganizationId = organization.Key;

            foreach (var (scheduleId, _) in organization)
            {
                try
                {
                    result = result.Plus(await EvaluateOneAsync(scheduleId, now, cancellationToken));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception failure)
                {
                    // One unreadable row must not stop the fleet's schedules; the next tick retries it.
                    result = result.Plus(new ScheduleTickResult(Failed: 1));
                    logger.LogError(
                        failure,
                        "Schedule evaluation failed. OrganizationId={OrganizationId} ScheduleId={ScheduleId}",
                        organization.Key,
                        scheduleId);
                }
            }
        }

        if (result.Outcomes > 0 || result.Failed > 0 || result.Raced > 0)
        {
            logger.LogInformation(
                "Schedule tick: {Queued} queued, {SkippedOverlap} overlap skip(s), {SkippedDisabled} disabled skip(s), "
                + "{SkippedQueueFull} queue-full skip(s), {SkippedRefused} refused, {Missed} missed, "
                + "{SkippedNonexistent} nonexistent, {Raced} raced, {Failed} failed.",
                result.Queued,
                result.SkippedOverlap,
                result.SkippedDisabled,
                result.SkippedQueueFull,
                result.SkippedRefused,
                result.Missed,
                result.SkippedNonexistent,
                result.Raced,
                result.Failed);
        }

        return result;
    }

    /// <summary>
    /// One schedule, from the watermark to the committed ledger row - transactionally, under the schedule's row
    /// lock, so the run and the record that explains it either both exist or neither does.
    /// <para>
    /// <b>A lost race is re-evaluated once (card Q-018).</b> The unique <c>(ScheduleId, DueAt)</c> index refuses the
    /// whole attempt, and by the time that refusal is reported the writer that caused it has committed - so a second
    /// attempt reads its rows as already recorded and decides what is left of the window. That is what keeps the
    /// candidate that ran into the refusal from being dropped: it is either the winner's own row (counted as
    /// <c>Raced</c>) or still owed a decision, and the second attempt is where it gets one. Settling without
    /// re-evaluating moved the watermark to <c>now</c> and swallowed the candidate silently.
    /// </para>
    /// <para>
    /// The retry is bounded at one: a second refusal means another writer is committing into this window faster
    /// than one tick can decide it, so the tick settles instead - and that settle stops at the last recorded due
    /// time rather than at <c>now</c>, which leaves the window for the next tick instead of skipping it.
    /// </para>
    /// </summary>
    private async Task<ScheduleTickResult> EvaluateOneAsync(
        Guid scheduleId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var first = await EvaluateAttemptAsync(scheduleId, now, cancellationToken);
        if (!first.LostRace)
        {
            return first.Result;
        }

        var second = await EvaluateAttemptAsync(scheduleId, now, cancellationToken);
        if (!second.LostRace)
        {
            return second.Result;
        }

        // Two refusals in a row: stop retrying (the next tick continues) and report the loss without a `Failed`
        // count. Neither attempt changed anything - both were rolled back - so the second attempt's result is the
        // one that describes this tick.
        await SettleAfterRaceAsync(scheduleId, now, cancellationToken);
        return second.Result;
    }

    /// <summary>
    /// One attempt at one schedule: the whole decision, from the row lock to the commit, in one transaction. It
    /// either commits and returns its result, or rolls back and reports <see cref="ScheduleAttempt.LostRace"/>
    /// (the unique index refused a due time another writer had already recorded).
    /// </summary>
    private async Task<ScheduleAttempt> EvaluateAttemptAsync(
        Guid scheduleId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        static ScheduleAttempt Done(ScheduleTickResult result) => new(result, LostRace: false);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (await context.LockScheduleForEvaluationAsync(scheduleId, cancellationToken) == 0)
        {
            // The schedule was deleted between discovery and the lock.
            return Done(new ScheduleTickResult());
        }

        // Re-read under the lock: a concurrent tick that committed first has already moved the watermark, and this
        // read is what makes "the second one finds nothing to do" true rather than a timing hope.
        var schedule = await context.ProjectSchedules.SingleOrDefaultAsync(
            candidate => candidate.Id == scheduleId,
            cancellationToken);
        if (schedule is null)
        {
            return Done(new ScheduleTickResult());
        }

        var project = await context.Projects
            .AsNoTracking()
            .Where(candidate => candidate.Id == schedule.ProjectId)
            .Select(candidate => new { candidate.Id, candidate.Disabled })
            .SingleOrDefaultAsync(cancellationToken);
        if (project is null)
        {
            // The project's cascade should have removed the schedule; nothing to evaluate if it has not.
            return Done(new ScheduleTickResult());
        }

        if (!ScheduleCron.TryParse(schedule.Cron, out var cron, out _)
            || cron is null
            || !ScheduleEvaluator.TryResolveZone(schedule.TimeZone, out var zone))
        {
            // A row written out of band (the forms refuse both). Advancing the watermark keeps it from being
            // re-logged every tick; nothing is queued and nothing pretends to be an outcome.
            logger.LogWarning(
                "Schedule skipped: its expression or time zone does not resolve. OrganizationId={OrganizationId} ProjectId={ProjectId} ScheduleId={ScheduleId} Cron={Cron} TimeZone={TimeZone}",
                schedule.OrganizationId,
                schedule.ProjectId,
                schedule.Id,
                schedule.Cron,
                schedule.TimeZone);
            schedule.LastEvaluatedAt = now;
            schedule.UpdatedAt = now;
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Done(new ScheduleTickResult(Failed: 1));
        }

        var watermark = schedule.LastEvaluatedAt ?? schedule.CreatedAt;
        if (watermark >= now)
        {
            // Nothing has happened since the last evaluation (and a clock that went backwards never replays).
            return Done(new ScheduleTickResult());
        }

        // The window: no older than the missed-time horizon, so a month of downtime costs one bounded pass.
        var windowTruncated = now - watermark > options.MissWindow;
        var windowStart = windowTruncated ? now - options.MissWindow : watermark;

        // A stale watermark means nothing was evaluating this schedule - the Server was down, or the tick was hung
        // past the lateness bound. Nothing is queued on that first evaluation: every due time in the window is
        // recorded as missed, which is the card's "no backfill, but say how many".
        var recovering = now - watermark > options.MaxDueLateness;

        var found = ScheduleEvaluator.Enumerate(cron, zone, windowStart, now);
        DateTimeOffset? cappedAt = null;
        if (found.Count >= ScheduleEvaluator.MaxEntriesPerScan)
        {
            // Unreachable for a 5-field expression in a 24-hour window (a per-minute cron is 1440 due times), but
            // the cap exists so a pathological window cannot become an unbounded read. Stopping the watermark at
            // the last recorded instant means the next tick continues where this one stopped - nothing is skipped.
            windowTruncated = true;
            cappedAt = found[^1].At;
            logger.LogWarning(
                "Schedule evaluation hit its entry cap; the next tick continues from the last recorded due time. OrganizationId={OrganizationId} ProjectId={ProjectId} ScheduleId={ScheduleId}",
                schedule.OrganizationId,
                schedule.ProjectId,
                schedule.Id);
        }

        var pending = found.ToList();

        // Review M1/M2: the self-healing half of the unique (schedule, due time) guard. A due time that a writer
        // outside this tick's row lock already recorded is *handled*: no run is queued for it, no second row is
        // inserted for it, and the watermark moves past it. Without this read such a writer would leave the
        // schedule re-computing - and re-losing - the same due time on every tick, forever.
        var alreadyRecorded = (await context.ScheduleOccurrences
                .Where(occurrence => occurrence.ScheduleId == schedule.Id)
                .Select(occurrence => occurrence.DueAt)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        // At most one due time per tick can queue a run: the newest ordinary one, and only if it is recent enough
        // to be "this tick's" rather than one the Server slept through.
        var raced = 0;
        ScheduleDue? candidate = null;
        if (!recovering)
        {
            var newestOrdinary = pending.LastOrDefault(due => due.Kind == ScheduleDueKind.Occurrence);
            if (newestOrdinary is not null && now - newestOrdinary.At <= options.MaxDueLateness)
            {
                pending.Remove(newestOrdinary);
                if (alreadyRecorded.Contains(newestOrdinary.At))
                {
                    // Answered by another writer already: nothing to queue and nothing to record.
                    raced++;
                }
                else
                {
                    candidate = newestOrdinary;
                }
            }
        }

        // Everything else in the window is recorded, never queued - minus what another writer already recorded.
        // The list of rows is bounded; the count is not.
        var missTotal = 0;
        var remaining = new List<ScheduleDue>(pending.Count);
        foreach (var due in pending)
        {
            if (due.Kind == ScheduleDueKind.Occurrence)
            {
                // Counted for the page even when another writer already recorded the row: the due time was still
                // missed in this window, and `LastMissedCount` states the window, not this tick's writes.
                missTotal++;
            }

            if (alreadyRecorded.Contains(due.At))
            {
                raced++;
                continue;
            }

            remaining.Add(due);
        }

        var tick = new ScheduleTickResult(Raced: raced);
        var misses = remaining.Where(due => due.Kind == ScheduleDueKind.Occurrence).ToList();
        var nonexistent = remaining.Where(due => due.Kind == ScheduleDueKind.Nonexistent).ToList();
        var collapsed = Math.Max(0, misses.Count - options.MaxRecordedMisses);
        var recordedMisses = misses.Skip(collapsed).ToList();
        var recordedNonexistent = nonexistent.Count > options.MaxRecordedMisses
            ? nonexistent.Skip(nonexistent.Count - options.MaxRecordedMisses).ToList()
            : nonexistent;
        var collapsedNonexistent = nonexistent.Count - recordedNonexistent.Count;

        for (var index = 0; index < recordedMisses.Count; index++)
        {
            var isOldest = index == 0;
            context.ScheduleOccurrences.Add(new ScheduleOccurrence
            {
                OrganizationId = schedule.OrganizationId,
                ScheduleId = schedule.Id,
                DueAt = recordedMisses[index].At,
                Outcome = ScheduleOutcomes.Missed,
                CollapsedDueTimes = isOldest ? collapsed : 0,
                Detail = isOldest && windowTruncated ? ScheduleOutcomes.TruncatedDetail : null,
                RecordedAt = now,
            });
            tick = tick.Plus(new ScheduleTickResult(Missed: 1));
        }

        for (var index = 0; index < recordedNonexistent.Count; index++)
        {
            context.ScheduleOccurrences.Add(new ScheduleOccurrence
            {
                OrganizationId = schedule.OrganizationId,
                ScheduleId = schedule.Id,
                DueAt = recordedNonexistent[index].At,
                Outcome = ScheduleOutcomes.SkippedNonexistent,
                CollapsedDueTimes = index == 0 ? collapsedNonexistent : 0,
                RecordedAt = now,
            });
            tick = tick.Plus(new ScheduleTickResult(SkippedNonexistent: 1));
        }

        try
        {
            if (candidate is not null)
            {
                // Review M1: this call stages the run through the one insert path, which *saves* - and a save
                // flushes the missed rows added above with it. So the unique-index refusal can surface from inside
                // this call, not only from the SaveChanges below; one catch has to cover both, or a tick that has
                // both a missed backlog and a due time to queue would report `Failed` instead of `Raced`.
                tick = tick.Plus(
                    await DecideAsync(context, schedule, project.Disabled, candidate, now, cancellationToken));
            }

            schedule.LastEvaluatedAt = cappedAt ?? now;
            schedule.LastMissedCount = missTotal;
            schedule.LastMissedWindowTruncated = windowTruncated;
            schedule.UpdatedAt = now;

            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException failure) when (DatabaseErrors.IsUniqueViolation(failure, "ScheduleOccurrences"))
        {
            // A writer outside this tick's row lock committed one of these due times between the read above and
            // this save. The whole transaction rolls back - including any run it staged - so one due time can
            // never queue two runs.
            await transaction.RollbackAsync(cancellationToken);
            logger.LogInformation(
                "Schedule tick lost a due time to a concurrent scheduler. OrganizationId={OrganizationId} ProjectId={ProjectId} ScheduleId={ScheduleId}",
                schedule.OrganizationId,
                schedule.ProjectId,
                schedule.Id);

            // Review M2: the winner's rows stand for this window, so the losing attempt's count is one `Raced` per
            // due time it lost - never a `Failed`. What happens next is the caller's decision (one re-evaluation,
            // then a settle): Q-018 found that settling here, without re-reading, dropped the candidate.
            return new ScheduleAttempt(new ScheduleTickResult(Raced: Math.Max(1, raced)), LostRace: true);
        }

        await PruneAsync(context, schedule.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Done(tick);
    }

    /// <summary>
    /// What happens at one due time: the project/schedule switches first, then the overlap policy, then the run.
    /// The order is the card's: a disabled project never even asks about overlap, and "queue anyway" still meets
    /// F-029's cap (which is checked inside the one insert path).
    /// </summary>
    private async Task<ScheduleTickResult> DecideAsync(
        ZenkinsDbContext context,
        ProjectSchedule schedule,
        bool projectDisabled,
        ScheduleDue due,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!schedule.Enabled || projectDisabled)
        {
            // F-028's cross-card contract: the refusal one trigger source keeps in its own log is spelled
            // `skipped-disabled` - the same word the shared gate declares.
            context.ScheduleOccurrences.Add(new ScheduleOccurrence
            {
                OrganizationId = schedule.OrganizationId,
                ScheduleId = schedule.Id,
                DueAt = due.At,
                Outcome = ScheduleOutcomes.SkippedDisabled,
                Detail = projectDisabled ? ProjectTriggerRefusals.ProjectDisabled : null,
                RecordedAt = now,
            });
            logger.LogInformation(
                "Schedule skipped: disabled. OrganizationId={OrganizationId} ProjectId={ProjectId} ScheduleId={ScheduleId} ScheduleEnabled={ScheduleEnabled} ProjectDisabled={ProjectDisabled}",
                schedule.OrganizationId,
                schedule.ProjectId,
                schedule.Id,
                schedule.Enabled,
                projectDisabled);
            return new ScheduleTickResult(SkippedDisabled: 1);
        }

        // The overlap policy (F-024): "any run of the project is queued or running" - including a claimed but not
        // yet accepted one, which is still Queued. Read before this tick stages anything, so it is the state the
        // due time actually met.
        var inFlight = await FindInFlightRunAsync(context, schedule.ProjectId, cancellationToken);
        if (inFlight is not null && !schedule.QueueAnyway)
        {
            context.ScheduleOccurrences.Add(new ScheduleOccurrence
            {
                OrganizationId = schedule.OrganizationId,
                ScheduleId = schedule.Id,
                DueAt = due.At,
                Outcome = ScheduleOutcomes.SkippedOverlap,
                RunNumber = inFlight.Value,
                RecordedAt = now,
            });
            logger.LogInformation(
                "Schedule skipped: a run is in flight. OrganizationId={OrganizationId} ProjectId={ProjectId} ScheduleId={ScheduleId} InFlightRunNumber={RunNumber}",
                schedule.OrganizationId,
                schedule.ProjectId,
                schedule.Id,
                inFlight.Value);
            return new ScheduleTickResult(SkippedOverlap: 1);
        }

        // The one insert path: the same staging routine (and the same gate) every other trigger uses.
        var staged = await runs.QueueScheduledRunAsync(context, schedule.ProjectId, cancellationToken);
        if (staged.Status == RunOperationStatus.Success && staged.RunId is { } runId)
        {
            var number = await context.BuildRuns
                .AsNoTracking()
                .Where(run => run.Id == runId)
                .Select(run => (int?)run.Number)
                .SingleOrDefaultAsync(cancellationToken);

            context.ScheduleOccurrences.Add(new ScheduleOccurrence
            {
                OrganizationId = schedule.OrganizationId,
                ScheduleId = schedule.Id,
                DueAt = due.At,
                Outcome = ScheduleOutcomes.Queued,
                RunId = runId,
                RunNumber = number,
                RecordedAt = now,
            });
            return new ScheduleTickResult(Queued: 1);
        }

        var (outcome, detail) = MapRefusal(staged);
        context.ScheduleOccurrences.Add(new ScheduleOccurrence
        {
            OrganizationId = schedule.OrganizationId,
            ScheduleId = schedule.Id,
            DueAt = due.At,
            Outcome = outcome,
            Detail = detail,
            RecordedAt = now,
        });
        logger.LogInformation(
            "Schedule skipped: the trigger was refused. OrganizationId={OrganizationId} ProjectId={ProjectId} ScheduleId={ScheduleId} Refusal={Refusal}",
            schedule.OrganizationId,
            schedule.ProjectId,
            schedule.Id,
            detail ?? "unknown");

        return outcome == ScheduleOutcomes.SkippedQueueFull
            ? new ScheduleTickResult(SkippedQueueFull: 1)
            : new ScheduleTickResult(SkippedRefused: 1);
    }

    /// <summary>The project's oldest queued-or-running run's number, or null when the project is idle.</summary>
    private static async Task<int?> FindInFlightRunAsync(
        ZenkinsDbContext context,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        // Statuses are translated; the ordering is not (SQLite cannot order DateTimeOffset in SQL), so the
        // handful of active rows is read and ordered here.
        var active = await context.BuildRuns
            .AsNoTracking()
            .Where(run => run.ProjectId == projectId
                && (run.Status == BuildRunStatus.Queued || run.Status == BuildRunStatus.Running))
            .Select(run => new { run.Number, run.CreatedAt })
            .ToListAsync(cancellationToken);

        if (active.Count == 0)
        {
            return null;
        }

        return active
            .OrderBy(run => run.CreatedAt)
            .ThenBy(run => run.Number)
            .First()
            .Number;
    }

    /// <summary>F-028/F-029's refusal codes, mapped into the schedule's vocabulary (one cause, one spelling).</summary>
    private static (string Outcome, string? Detail) MapRefusal(RunTriggerResult staged)
    {
        var code = staged.FieldErrors?.FirstOrDefault()?.Code;
        return code switch
        {
            ProjectTriggerRefusals.ProjectDisabled => (ScheduleOutcomes.SkippedDisabled, ProjectTriggerRefusals.ProjectDisabled),
            ProjectTriggerRefusals.QueueFull => (ScheduleOutcomes.SkippedQueueFull, null),
            null => (ScheduleOutcomes.SkippedRefused, null),
            _ => (ScheduleOutcomes.SkippedRefused, code),
        };
    }

    /// <summary>
    /// Review M2 and Q-018: stops a schedule from recomputing - and re-losing - a due time another writer already
    /// recorded, without ever skipping a due time this tick did not decide.
    /// <para>
    /// <b>The stop point is the newest due time already in the ledger, not <c>now</c>.</b> A writer can only refuse
    /// this tick on a due time it has itself committed (that is what the unique index means), so the ledger holds
    /// at least one row at or before <c>now</c>; moving the watermark there makes the next tick's window start where
    /// the recorded decision ended. Advancing to <c>now</c> instead moved it past every due time in the window,
    /// including the candidate that ran into the refusal and was rolled back with it - which is how a due time could
    /// end up neither queued nor recorded (measured: <c>newestDueWas=12:01:00Z</c>, watermark <c>12:01:30Z</c>, no
    /// run, one ledger row). With the stop point at the last recorded due time, a candidate newer than that is still
    /// in the next window and gets its decision; a candidate that <em>is</em> the recorded row is answered by it, and
    /// the window starts after it, so nothing is recomputed forever.
    /// </para>
    /// <para>
    /// A fresh context and transaction - the caller's change tracker still holds the rows the rollback discarded -
    /// and only forward: a watermark that already moved (another tick won) is left alone. A failure here is logged
    /// and retried by the next tick; it never turns a lost due time into a `Failed` count.
    /// </para>
    /// </summary>
    private async Task SettleAfterRaceAsync(
        Guid scheduleId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            if (await context.LockScheduleForEvaluationAsync(scheduleId, cancellationToken) == 0)
            {
                return;
            }

            var schedule = await context.ProjectSchedules.SingleOrDefaultAsync(
                candidate => candidate.Id == scheduleId,
                cancellationToken);
            if (schedule is null)
            {
                return;
            }

            // The last decision the ledger already holds at or before this tick's instant. Read and ordered here:
            // the handful of rows is bounded by Schedules:RetainedOutcomes, and SQLite cannot order
            // DateTimeOffset in SQL. No row at all cannot happen after a unique refusal, so `now` is only the
            // defensive answer to a ledger that was pruned empty.
            var recorded = await context.ScheduleOccurrences
                .Where(occurrence => occurrence.ScheduleId == scheduleId && occurrence.DueAt <= now)
                .Select(occurrence => occurrence.DueAt)
                .ToListAsync(cancellationToken);
            var stopAt = recorded.Count == 0 ? now : recorded.Max();

            if (schedule.LastEvaluatedAt is { } watermark && watermark >= stopAt)
            {
                return;
            }

            schedule.LastEvaluatedAt = stopAt;
            schedule.UpdatedAt = now;
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            logger.LogWarning(
                failure,
                "Advancing a schedule's watermark after a lost due time failed; the next tick retries it. ScheduleId={ScheduleId}",
                scheduleId);
        }
    }

    /// <summary>
    /// Keeps the newest <c>Schedules:RetainedOutcomes</c> rows of one schedule (the card's "audit of the last N"),
    /// so a per-minute schedule cannot grow the table without limit. Removed through the change tracker rather
    /// than <c>ExecuteDelete</c>, so the tenant guard still applies to the delete.
    /// </summary>
    private async Task PruneAsync(ZenkinsDbContext context, Guid scheduleId, CancellationToken cancellationToken)
    {
        var rows = await context.ScheduleOccurrences
            .Where(occurrence => occurrence.ScheduleId == scheduleId)
            .ToListAsync(cancellationToken);
        if (rows.Count <= options.RetainedOutcomes)
        {
            return;
        }

        var stale = rows
            .OrderByDescending(occurrence => occurrence.DueAt)
            .ThenByDescending(occurrence => occurrence.RecordedAt)
            .Skip(options.RetainedOutcomes)
            .ToList();
        context.ScheduleOccurrences.RemoveRange(stale);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Projects one schedule into the page's view (next due times resolved at read time).</summary>
    private static ScheduleView ToView(
        ProjectSchedule schedule,
        IEnumerable<ScheduleOccurrence> occurrences,
        DateTimeOffset now)
    {
        IReadOnlyList<DateTimeOffset> next = [];
        if (ScheduleCron.TryParse(schedule.Cron, out var cron, out _)
            && cron is not null
            && ScheduleEvaluator.TryResolveZone(schedule.TimeZone, out var zone))
        {
            next = ScheduleEvaluator.NextOccurrences(cron, zone, now, ScheduleOptions.PreviewCount);
        }

        return new ScheduleView(
            schedule.Id,
            schedule.ProjectId,
            schedule.Cron,
            schedule.TimeZone,
            schedule.Enabled,
            schedule.QueueAnyway,
            schedule.LastEvaluatedAt,
            schedule.CreatedAt,
            next,
            [
                .. occurrences
                    .OrderByDescending(occurrence => occurrence.DueAt)
                    .Take(ShownOutcomes)
                    .Select(occurrence => new ScheduleOutcomeView(
                        occurrence.DueAt,
                        occurrence.Outcome,
                        occurrence.RunId,
                        occurrence.RunNumber,
                        occurrence.CollapsedDueTimes,
                        occurrence.Detail)),
            ],
            schedule.LastMissedCount,
            schedule.LastMissedWindowTruncated);
    }

    /// <summary>Any membership (Owner or Member) of the active organization may manage schedules.</summary>
    private static Task<bool> IsMemberAsync(
        ZenkinsDbContext context,
        string actingUserId,
        CancellationToken cancellationToken) =>
        context.OrganizationMembers.AnyAsync(
            member => member.UserId == actingUserId,
            cancellationToken);
}
