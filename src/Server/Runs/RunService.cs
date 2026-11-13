using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Git;
using Zenkins.Server.LogStreaming;
using Zenkins.Server.Projects;
using Zenkins.Server.Workers;
using Zenkins.Shared.LogStreaming;
using Zenkins.Shared.RunCancellation;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Runs;

/// <summary>Outcome of a run operation. The HTTP layer maps each value to one status code.</summary>
public enum RunOperationStatus
{
    Success = 0,

    /// <summary>The presented worker token is missing, unknown or revoked. Always a bare 401.</summary>
    InvalidToken = 1,

    /// <summary>The project or run id does not exist in the active tenant (also the foreign case).</summary>
    NotFound = 2,

    /// <summary>The caller is not a member of the active organization.</summary>
    Forbidden = 3,

    /// <summary>The payload failed validation; nothing was written.</summary>
    InvalidInput = 4,

    /// <summary>An illegal state transition, a lease that is not the caller's, or an expired lease.</summary>
    Conflict = 5,
}

/// <summary>One field-level validation failure of the trigger form.</summary>
public sealed record RunFieldError(string Field, string Code);

/// <summary>Field names the trigger form's errors render against.</summary>
public static class RunFields
{
    public const string Command = "command";
    public const string RequiredTags = "requiredTags";
    public const string Timeout = "timeoutMinutes";
}

/// <summary>The raw trigger-form values, before trimming and validation.</summary>
public sealed record RunTriggerInput(string? Command, string? RequiredTags, string? TimeoutMinutes = null);

/// <summary>Result of triggering a run. <see cref="RunId"/> is the redirect target on success.</summary>
public sealed record RunTriggerResult(
    RunOperationStatus Status,
    Guid? RunId = null,
    string? Message = null,
    IReadOnlyList<RunFieldError>? FieldErrors = null)
{
    public static RunTriggerResult Success(Guid runId) => new(RunOperationStatus.Success, runId);

    public static RunTriggerResult NotFound() => new(RunOperationStatus.NotFound);

    public static RunTriggerResult Forbidden(string message) =>
        new(RunOperationStatus.Forbidden, Message: message);

    public static RunTriggerResult Invalid(params RunFieldError[] errors) =>
        new(RunOperationStatus.InvalidInput, FieldErrors: errors);
}

/// <summary>A run as the pages display it. Records keep disposed contexts out of the UI.</summary>
public sealed record RunView(
    Guid Id,
    Guid ProjectId,
    string Status,
    string TriggerType,
    string? TriggerUserEmail,
    string? WorkerName,
    string[] Command,
    string[] RequiredTags,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int? ExitCode,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int? TimeoutSeconds = null,
    DateTimeOffset? DeadlineAt = null);

/// <summary>The failure reasons the server itself records (the worker's exit code needs none).</summary>
public static class RunFailureReasons
{
    /// <summary>F-010: the worker stopped heartbeating while the run was still running.</summary>
    public const string WorkerOffline = "worker-offline";

    /// <summary>F-009 (PM ruling): the run passed its deadline plus the grace period without any
    /// terminal report from the worker. Honest wording: the result is unknown - the server never
    /// fabricates a Failed for a run it did not see end.</summary>
    public const string RunDeadlineExceededResultUnknown = "run-deadline-exceeded-result-unknown";
}

/// <summary>What a user cancel did. <see cref="RunCancelOutcome.AlreadyFinished"/> is the friendly
/// no-op: a terminal run cannot be canceled again, and the page says so instead of erroring.</summary>
public enum RunCancelOutcome
{
    /// <summary>The run was queued and is now <c>Canceled</c> (immediate, atomic).</summary>
    Canceled = 0,

    /// <summary>The run was running; the worker has been asked to cancel it.</summary>
    CancelRequested = 1,

    /// <summary>The run had already reached a terminal state; nothing was changed.</summary>
    AlreadyFinished = 2,
}

/// <summary>Outcome of the user-facing cancel action (the run page's cancel button).</summary>
public sealed record RunCancelResult(RunOperationStatus Status, RunCancelOutcome Outcome, string? Message = null)
{
    public static RunCancelResult Canceled() => new(RunOperationStatus.Success, RunCancelOutcome.Canceled);

    public static RunCancelResult CancelRequested() => new(RunOperationStatus.Success, RunCancelOutcome.CancelRequested);

    public static RunCancelResult AlreadyFinished() => new(RunOperationStatus.Success, RunCancelOutcome.AlreadyFinished);

    public static RunCancelResult Forbidden(string message) =>
        new(RunOperationStatus.Forbidden, RunCancelOutcome.AlreadyFinished, message);

    public static RunCancelResult NotFound() => new(RunOperationStatus.NotFound, RunCancelOutcome.AlreadyFinished);
}

/// <summary>A queued run handed to a worker by the claim endpoint.</summary>
public sealed record RunClaimResult(
    RunOperationStatus Status,
    Guid? RunId = null,
    Guid? ProjectId = null,
    IReadOnlyList<string>? Command = null,
    DateTimeOffset? LeaseExpiresAt = null)
{
    /// <summary>The 204 answer: nothing eligible right now. Same for every other rejection reason -
    /// a worker polls again, it never learns why it got nothing.</summary>
    public static RunClaimResult NoRun() => new(RunOperationStatus.Success);
}

/// <summary>Outcome of the accepted/completed reporting endpoints.</summary>
public sealed record RunReportResult(
    RunOperationStatus Status,
    string? ErrorCode = null,
    string? RunStatus = null)
{
    public static RunReportResult Success(string runStatus) =>
        new(RunOperationStatus.Success, RunStatus: runStatus);

    public static RunReportResult InvalidToken() => new(RunOperationStatus.InvalidToken);

    public static RunReportResult Invalid(string errorCode) => new(RunOperationStatus.InvalidInput, errorCode);

    public static RunReportResult NotFound() => new(RunOperationStatus.NotFound);

    public static RunReportResult Conflict(string errorCode) => new(RunOperationStatus.Conflict, errorCode);
}

/// <summary>
/// Build runs, queue and scheduler (card F-006, PRD §3.5).
/// <para>
/// <b>Tenant boundary.</b> Page/trigger methods read the organization from
/// <see cref="ITenantContext"/> only; run lookups go through the tenant-filtered context, so a
/// foreign id is indistinguishable from a missing one (404, never 403). Worker-API methods
/// (claim/accepted/completed) authenticate with a worker token through
/// <see cref="WorkerService.AuthenticateWorkerAsync"/> - authentication before any payload
/// validation, the token row selects the tenant, every auth failure is a bare 401.
/// </para>
/// <para>
/// <b>Atomic claim (the pull model).</b> A claim never assigns: it leases. Candidates are the
/// tenant's <c>Queued</c> runs whose lease is free or expired, FIFO by creation; the first one
/// whose single-statement compare-and-set wins (<see cref="ZenkinsDbContext.TryLeaseBuildRunAsync"/>,
/// the guarded <c>ExecuteUpdate</c>) is returned. The UPDATE's WHERE restates the eligibility, so
/// two concurrent claims can never both win the same run under either provider's write locking.
/// </para>
/// <para>
/// <b>State transitions.</b> Only <see cref="BuildRunStateMachine"/> (through this service) changes
/// <see cref="BuildRun.Status"/>: <c>accepted</c> is the single <c>Queued → Running</c> edge,
/// <c>completed</c> maps exit 0 to <c>Success</c> and anything else to <c>Failed</c>. Illegal
/// transitions are <see cref="RunOperationStatus.Conflict"/> (409). The claim itself is not a
/// transition - a leased run stays <c>Queued</c> until accepted, which is what makes the lease
/// sweeper safe: it requeues only <c>Queued</c> runs with an expired lease, so a run that was
/// accepted (no lease anymore) is unreachable for it.
/// </para>
/// </summary>
public sealed class RunService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    TenantContext tenantContext,
    ISystemDataScope systemDataScope,
    WorkerService workerService,
    RunLogIngest runLogIngest,
    RunOptions options,
    WorkerMonitorOptions workerMonitorOptions,
    IHubContext<WorkerLogHub> workerLogHubs,
    ILogger<RunService> logger)
{
    /// <summary>
    /// Upper bound on the candidates one claim request walks. Each candidate costs one CAS
    /// statement; with every lease contended, a worker re-polls instead of spinning here.
    /// </summary>
    private const int MaxClaimCandidates = 64;

    // ---------------------------------------------------------------------
    // Trigger and reads (cookie-authenticated pages and forms)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Creates a <c>Queued</c> run for a project of the active organization. Any member may
    /// trigger (PRD §3.1.2). The command is parsed as one-argument-per-line (no shell quoting
    /// interpretation); required tags must exist among the organization's workers, so a typo
    /// cannot queue a run no worker will ever take.
    /// </summary>
    public async Task<RunTriggerResult> TriggerAsync(
        string actingUserId,
        Guid projectId,
        RunTriggerInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return RunTriggerResult.Forbidden(
                "You are not a member of an organization, so there is no project to run.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await context.OrganizationMembers.AnyAsync(
                member => member.UserId == actingUserId,
                cancellationToken))
        {
            return RunTriggerResult.Forbidden("You are not a member of the active organization.");
        }

        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            // Foreign or unknown id: the same 404, no existence oracle.
            return RunTriggerResult.NotFound();
        }

        var argvError = RunValidation.ValidateArguments(input.Command, out var argv);
        if (argvError is not null)
        {
            return RunTriggerResult.Invalid(new RunFieldError(RunFields.Command, argvError));
        }

        var tagsError = RunValidation.ValidateRequiredTags(input.RequiredTags, out var requiredTags);
        if (tagsError is not null)
        {
            return RunTriggerResult.Invalid(new RunFieldError(RunFields.RequiredTags, tagsError));
        }

        var timeoutError = RunValidation.ValidateTimeoutMinutes(input.TimeoutMinutes, out var timeoutMinutes);
        if (timeoutError is not null)
        {
            return RunTriggerResult.Invalid(new RunFieldError(RunFields.Timeout, timeoutError));
        }

        if (requiredTags.Length > 0)
        {
            var knownTags = (await context.Workers.ToListAsync(cancellationToken))
                .SelectMany(worker => RunValidation.ParseTags(worker.Tags))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = RunValidation.ParseTags(requiredTags)
                .FirstOrDefault(tag => !knownTags.Contains(tag));
            if (missing is not null)
            {
                return RunTriggerResult.Invalid(
                    new RunFieldError(RunFields.RequiredTags, RunErrorCodes.TagUnknown));
            }
        }

        var run = new BuildRun
        {
            // The tenant comes from the active request; the data layer rejects anything else.
            OrganizationId = activeOrganizationId.Value,
            ProjectId = project.Id,
            TriggerType = BuildRunTriggerType.Manual,
            TriggerUserId = actingUserId,
            RequiredTags = requiredTags,
            CommandJson = RunValidation.EncodeCommandJson(argv),
            TimeoutSeconds = timeoutMinutes is { } minutes ? minutes * 60 : null,
        };
        context.BuildRuns.Add(run);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Run queued. OrganizationId={OrganizationId} ProjectId={ProjectId} RunId={RunId} ByUserId={UserId}",
            activeOrganizationId,
            project.Id,
            run.Id,
            actingUserId);
        return RunTriggerResult.Success(run.Id);
    }

    /// <summary>Runs of one project in the active organization, newest first.</summary>
    public async Task<IReadOnlyList<RunView>> GetProjectRunsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var runs = await context.BuildRuns
            .Where(run => run.ProjectId == projectId)
            .ToListAsync(cancellationToken);
        return (await ToViewsAsync(context, runs, cancellationToken))
            .OrderByDescending(run => run.CreatedAt)
            .ThenBy(run => run.Id)
            .ToArray();
    }

    /// <summary>
    /// One run of the active organization, or <see langword="null"/> when the id belongs to another
    /// organization or does not exist. Both cases are deliberately indistinguishable.
    /// </summary>
    public async Task<RunView?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = await context.BuildRuns.SingleOrDefaultAsync(
            candidate => candidate.Id == runId,
            cancellationToken);
        return run is null ? null : (await ToViewsAsync(context, [run], cancellationToken))[0];
    }

    /// <summary>Whether the run exists in the active organization (download endpoint's 404 gate).</summary>
    public async Task<bool> RunExistsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.BuildRuns.AnyAsync(run => run.Id == runId, cancellationToken);
    }

    /// <summary>
    /// Streams one run's persisted log ordered by <see cref="Entities.BuildLog.Seq"/> (F-008's
    /// download). The database read is streamed - the caller writes each line as it arrives, so
    /// memory stays bounded no matter how large the log grew. Lines never leave the active
    /// organization: the query runs through the tenant filter, and callers gate the 404 beforehand.
    /// </summary>
    public async IAsyncEnumerable<LogLineMessage> StreamRunLogAsync(
        Guid runId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = context.BuildLogs
            .AsNoTracking()
            .Where(log => log.RunId == runId)
            .OrderBy(log => log.Seq)
            .Select(log => new LogLineMessage(log.Seq, log.Stream, log.Message, log.Timestamp));

        await foreach (var line in rows.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            yield return line;
        }
    }

    // ---------------------------------------------------------------------
    // Worker API (token-authenticated; F-004's authentication pattern)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Atomically leases the next eligible queued run to the calling worker: worker
    /// <c>Online</c>, a free concurrency slot (every run still assigned to the worker occupies
    /// one - running, or leased and not yet swept), required tags covered by the worker's tags,
    /// FIFO by creation. No eligible run - or every lease lost to a concurrent claim - is a
    /// 204-shaped <see cref="RunClaimResult.NoRun"/>: a worker polls again and never learns why
    /// it got nothing.
    /// </summary>
    public async Task<RunClaimResult> ClaimAsync(
        string? plaintextToken,
        CancellationToken cancellationToken = default)
    {
        // 1. Authenticate before anything else (F-004 pattern): bare 401 with no tenant detail.
        var workerToken = await AuthenticateAsync(plaintextToken, cancellationToken);
        if (workerToken is null)
        {
            return new RunClaimResult(RunOperationStatus.InvalidToken);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;

        // 2. Eligibility of the worker itself: Online and a free concurrency slot.
        var worker = await context.Workers.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == workerToken.WorkerId,
            cancellationToken);
        if (worker is null || worker.Status != WorkerStatus.Online)
        {
            return RunClaimResult.NoRun();
        }

        // A queued-but-leased run still occupies one of the worker's slots until the sweeper
        // clears the expired lease, so claiming cannot outrun MaxConcurrency through leases.
        var assigned = await context.BuildRuns.CountAsync(
            run => run.WorkerId == worker.Id
                && (run.Status == BuildRunStatus.Running || run.Status == BuildRunStatus.Queued),
            cancellationToken);
        if (assigned >= worker.MaxConcurrency)
        {
            return RunClaimResult.NoRun();
        }

        // 3. The candidate queue: Queued and unleased (never leased, or requeued by the sweeper),
        //    FIFO by creation. Ordered in memory - a small list per tenant, and SQLite cannot
        //    ORDER BY DateTimeOffset columns. An expired lease is NOT claimable here: the lease
        //    sweeper is the single authority on expiry, and SQLite cannot compare DateTimeOffset
        //    values in SQL, so the claim never needs a time predicate (see TryLeaseBuildRunAsync).
        var queued = await context.BuildRuns
            .AsNoTracking()
            .Where(run => run.Status == BuildRunStatus.Queued && run.LeaseExpiresAt == null)
            .ToListAsync(cancellationToken);
        var candidates = queued
            .OrderBy(run => run.CreatedAt)
            .ThenBy(run => run.Id)
            .Where(run => RunValidation.WorkerTagsCover(run.RequiredTags, worker.Tags))
            .Take(MaxClaimCandidates)
            .ToArray();

        // 4. Compare-and-set loop: the first candidate whose single-statement lease wins is the
        //    answer. A lost race means another claim took exactly that run - move to the next.
        var leaseExpiresAt = now.AddSeconds(options.LeaseSeconds);
        foreach (var candidate in candidates)
        {
            var leased = await context.TryLeaseBuildRunAsync(
                candidate.Id,
                worker.Id,
                leaseExpiresAt,
                now,
                cancellationToken);
            if (leased == 1)
            {
                logger.LogInformation(
                    "Run leased. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} LeaseExpiresAt={LeaseExpiresAt}",
                    workerToken.OrganizationId,
                    candidate.Id,
                    worker.Id,
                    leaseExpiresAt);
                return new RunClaimResult(
                    RunOperationStatus.Success,
                    candidate.Id,
                    candidate.ProjectId,
                    RunValidation.DecodeCommandJson(candidate.CommandJson),
                    leaseExpiresAt);
            }
        }

        return RunClaimResult.NoRun();
    }

    /// <summary>
    /// Reports that the worker accepted a run it holds a valid lease for: the single
    /// <c>Queued → Running</c> transition, start time recorded, lease cleared (after acceptance
    /// there is no lease - cancellation F-009 and offline detection F-010 own the rest).
    /// <para>
    /// <b>F-013's accepted-report extension.</b> <paramref name="gitCommit"/> carries the exact commit
    /// the worker checked out (<c>git rev-parse HEAD</c>), or <see langword="null"/> for a run with no
    /// repository. It is validated as an object name (never a branch label, never a revision
    /// expression) and stamped through the guarded compare-and-set
    /// <see cref="ZenkinsDbContext.TryStampGitCommitAsync"/> - once. The column is never derived from
    /// the branch and never rewritten: a re-delivered accept, or a second accept, leaves the first
    /// value in place.
    /// </para>
    /// <para>
    /// <b>The transition and the commit are one transaction (review M6).</b> They are two statements -
    /// an entity update for the state and a guarded <c>ExecuteUpdate</c> for the commit - so without a
    /// transaction an interruption between them would leave the run <c>Running</c> with
    /// <c>GitCommit</c> NULL. That state is unrecoverable by design: <c>Running → Running</c> is not a
    /// legal transition, so every retried accepted report is a 409 and the missing commit could never be
    /// filled in. Committing both together means an interrupted attempt changes nothing at all - the run
    /// stays <c>Queued</c> with its lease intact - so the worker's retry completes it.
    /// </para>
    /// </summary>
    public async Task<RunReportResult> AcceptAsync(
        string? plaintextToken,
        Guid runId,
        string? gitCommit,
        CancellationToken cancellationToken = default)
    {
        var (workerToken, worker, context) = await AuthenticateWithWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || worker is null)
        {
            return RunReportResult.InvalidToken();
        }

        try
        {
            var run = await context.BuildRuns.SingleOrDefaultAsync(
                candidate => candidate.Id == runId,
                cancellationToken);
            if (run is null)
            {
                // Foreign or unknown id: the same 404, no existence oracle.
                return RunReportResult.NotFound();
            }

            // Authentication and run ownership are settled above; the commit shape is validated before
            // the transition so a malformed report cannot leave a half-accepted run behind.
            if (gitCommit is not null && !GitCommitValidation.IsValid(gitCommit))
            {
                return RunReportResult.Invalid(GitCommitValidation.InvalidCode);
            }

            var now = DateTimeOffset.UtcNow;

            // Only a run this worker holds a live lease for can be accepted. The transition guard
            // comes first so an already-accepted run reports invalid-transition (not the misleading
            // lease-expired its cleared lease would produce).
            if (run.WorkerId != worker.Id)
            {
                return RunReportResult.Conflict(RunErrorCodes.RunNotAssignedToWorker);
            }

            if (!BuildRunStateMachine.CanTransition(run.Status, BuildRunStatus.Running))
            {
                return RunReportResult.Conflict(RunErrorCodes.InvalidTransition);
            }

            if (run.LeaseExpiresAt is null || run.LeaseExpiresAt <= now)
            {
                return RunReportResult.Conflict(RunErrorCodes.LeaseExpired);
            }

            run.Status = BuildRunStatus.Running;
            run.StartedAt = now;
            run.LeaseExpiresAt = null;

            // F-009: the execution deadline is frozen at accept - the run's per-run override or
            // the server default. The deadline sweeper never touches the run before this instant,
            // and a later configuration change never moves an accepted run's deadline.
            var timeout = run.TimeoutSeconds is { } seconds
                ? TimeSpan.FromSeconds(seconds)
                : options.DefaultTimeout;
            run.DeadlineAt = now + timeout;

            // F-013 + review M6: the status transition and the commit stamp are ONE unit of work.
            // They are two statements (an entity update and a guarded ExecuteUpdate), so without a
            // transaction an interruption between them would leave the run Running with
            // GitCommit NULL - and the run's own state guard (Running -> Running is not a legal
            // transition, so every retry is a 409) would make that state permanently unrecoverable.
            // Inside one transaction either both land or neither does; a failed attempt leaves the
            // run Queued and still leased, so the worker's retried accepted report completes it.
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            await context.SaveChangesAsync(cancellationToken);

            string? recordedCommit = null;
            if (gitCommit is not null)
            {
                // The CAS's WHERE is "GitCommit IS NULL", so a second report (or a re-delivery of this
                // one) updates zero rows and the first value - the commit this run actually built -
                // survives. A zero-row answer inside this path means a concurrent accept already
                // stamped it; the recorded value is read back for the log rather than assumed.
                var stamped = await context.TryStampGitCommitAsync(run.Id, gitCommit, now, cancellationToken);
                recordedCommit = stamped == 1
                    ? gitCommit
                    : await context.BuildRuns
                        .Where(candidate => candidate.Id == run.Id)
                        .Select(candidate => candidate.GitCommit)
                        .SingleOrDefaultAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Run accepted. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} DeadlineAt={DeadlineAt} GitCommit={GitCommit}",
                workerToken.OrganizationId,
                run.Id,
                worker.Id,
                run.DeadlineAt,
                recordedCommit);
            return RunReportResult.Success(BuildRunStatus.Running);
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    /// <summary>
    /// Reports the exit code of a run this worker executes: exit 0 → <c>Success</c>, anything else
    /// → <c>Failed</c>, end time recorded (duration is derived from start and end on display).
    /// </summary>
    public async Task<RunReportResult> CompleteAsync(
        string? plaintextToken,
        Guid runId,
        int? exitCode,
        CancellationToken cancellationToken = default)
    {
        // Authentication before payload validation (F-004 review fix): a tokenless caller gets
        // 401 even with an invalid payload, so 400s only ever reach an authenticated worker.
        var (workerToken, worker, context) = await AuthenticateWithWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || worker is null)
        {
            return RunReportResult.InvalidToken();
        }

        try
        {
            if (exitCode is null)
            {
                return RunReportResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            var run = await context.BuildRuns.SingleOrDefaultAsync(
                candidate => candidate.Id == runId,
                cancellationToken);
            if (run is null)
            {
                return RunReportResult.NotFound();
            }

            // Only the executing worker can complete the run, and only from Running.
            if (run.WorkerId != worker.Id)
            {
                return RunReportResult.Conflict(RunErrorCodes.RunNotAssignedToWorker);
            }

            var target = exitCode.Value == 0 ? BuildRunStatus.Success : BuildRunStatus.Failed;
            if (!BuildRunStateMachine.CanTransition(run.Status, target))
            {
                return RunReportResult.Conflict(RunErrorCodes.InvalidTransition);
            }

            var now = DateTimeOffset.UtcNow;
            run.Status = target;
            run.ExitCode = exitCode;
            run.EndedAt = now;
            await context.SaveChangesAsync(cancellationToken);

            // The run is terminal: flush the ingest tail and evict its buffer, so a run recorded
            // as finished never leaves lines behind nor keeps server memory (F-008's completed
            // path mirrors F-007's cleanup-before-report discipline on the server side).
            await runLogIngest.CompleteRunAsync(run.Id);

            logger.LogInformation(
                "Run completed. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} Status={Status} ExitCode={ExitCode}",
                workerToken.OrganizationId,
                run.Id,
                worker.Id,
                target,
                exitCode);
            return RunReportResult.Success(target);
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------------
    // Lease sweeper (background service and tests)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Returns every claimed-but-never-accepted run whose lease expired to the queue (clears the
    /// lease columns). Spans tenants, hence the system scope. The expiry check happens in memory
    /// (SQLite cannot compare DateTimeOffset values in SQL), and each requeue is then an exact
    /// compare-and-set on the expired lease value - so an accepted/Running run (no lease) is
    /// unreachable no matter how late the sweep fires, and a lease stamped after the read is never
    /// clobbered. Returns the number of requeued runs.
    /// </summary>
    public async Task<int> SweepExpiredLeasesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        using var scope = systemDataScope.BeginScope();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Queued runs that still carry a lease (accepted runs have none; terminal runs never requeue).
        var leased = await context.BuildRuns
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(run => run.Status == BuildRunStatus.Queued && run.LeaseExpiresAt != null)
            .Select(run => new { run.Id, run.LeaseExpiresAt })
            .ToListAsync(cancellationToken);

        var requeued = 0;
        foreach (var run in leased)
        {
            if (run.LeaseExpiresAt is not { } leaseExpiresAt || leaseExpiresAt > now)
            {
                continue;
            }

            requeued += await context.RequeueExpiredBuildRunLeaseAsync(
                run.Id,
                leaseExpiresAt,
                now,
                cancellationToken);
        }

        if (requeued > 0)
        {
            logger.LogInformation("Lease sweeper requeued {Count} expired run(s).", requeued);
        }

        return requeued;
    }

    // ---------------------------------------------------------------------
    // User cancel (F-009): the run page's cancel button
    // ---------------------------------------------------------------------

    /// <summary>
    /// Cancels a run on behalf of an organization member (Owner or Member - the trigger right).
    /// <list type="bullet">
    /// <item><c>Queued</c>: a guarded compare-and-set flips it to <c>Canceled</c> atomically. Under
    /// a concurrent claim storm exactly one of the two statements can win the row (both WHEREs
    /// require <c>Status == Queued</c>); a run canceled after a claim but before its accept makes
    /// the leaseholder's accepted report a 409, so the process is never started.</item>
    /// <item><c>Running</c>: the run is marked cancel-requested (reason
    /// <c>canceled-by-user &lt;user&gt;</c>, first request wins) and the worker is signaled over
    /// its hub connection (the heartbeat response is the fallback). The run stays Running until
    /// the worker kills the tree and reports through the canceled endpoint.</item>
    /// <item>Terminal: a friendly no-op - nothing changes, the page says the run had finished.
    /// Double-cancel is the same no-op.</item>
    /// </list>
    /// A foreign-organization run is indistinguishable from a missing one (404, no existence
    /// oracle); a non-member gets 403.
    /// </summary>
    public async Task<RunCancelResult> CancelAsync(
        string actingUserId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is null)
        {
            return RunCancelResult.Forbidden(
                "You are not a member of an organization, so there is no run to cancel.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await context.OrganizationMembers.AnyAsync(
                member => member.UserId == actingUserId,
                cancellationToken))
        {
            return RunCancelResult.Forbidden("You are not a member of the active organization.");
        }

        var run = await context.BuildRuns.SingleOrDefaultAsync(
            candidate => candidate.Id == runId,
            cancellationToken);
        if (run is null)
        {
            // Foreign or unknown id: the same 404, no existence oracle.
            return RunCancelResult.NotFound();
        }

        var actor = await context.Users
            .Where(user => user.Id == actingUserId)
            .Select(user => user.Email)
            .FirstOrDefaultAsync(cancellationToken);
        var reason = RunCancelReasons.ForUser(actor ?? actingUserId);
        var now = DateTimeOffset.UtcNow;

        if (run.Status is BuildRunStatus.Queued or BuildRunStatus.Running)
        {
            // Still non-terminal: the only two states with outgoing edges.
            if (run.Status == BuildRunStatus.Queued)
            {
                var canceled = await context.TryCancelQueuedBuildRunAsync(runId, reason, now, cancellationToken);
                if (canceled == 1)
                {
                    logger.LogInformation(
                        "Queued run canceled. OrganizationId={OrganizationId} RunId={RunId} ByUserId={UserId}",
                        run.OrganizationId,
                        runId,
                        actingUserId);
                    return RunCancelResult.Canceled();
                }

                // Lost the race: the run left Queued between the read and the CAS. Re-read and
                // fall through - it is either Running now (cancel-request path) or terminal
                // (the friendly no-op).
                var reread = await context.BuildRuns.SingleOrDefaultAsync(
                    candidate => candidate.Id == runId,
                    cancellationToken);
                if (reread is null)
                {
                    // The run's project (and with it the run) is gone; nothing left to cancel.
                    return RunCancelResult.NotFound();
                }

                run = reread;
            }

            if (run.Status == BuildRunStatus.Running)
            {
                // First request wins (CancelRequestedAt IS NULL in the CAS): a double cancel or a
                // user cancel racing the deadline sweeper records one reason and never overwrites.
                await context.TryRequestBuildRunCancelAsync(runId, reason, now, cancellationToken);
                if (run.WorkerId is { } workerId)
                {
                    await BroadcastCancelAsync(workerId, runId, reason, cancellationToken);
                }

                logger.LogInformation(
                    "Running run cancel requested. OrganizationId={OrganizationId} RunId={RunId} ByUserId={UserId}",
                    run.OrganizationId,
                    runId,
                    actingUserId);
                return RunCancelResult.CancelRequested();
            }
        }

        // Terminal (including a run that turned terminal during the race above): friendly no-op.
        return RunCancelResult.AlreadyFinished();
    }

    // ---------------------------------------------------------------------
    // Worker canceled report (F-009)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Records the terminal state of a run its worker killed on a server cancel request. The
    /// worker's outcome selects the mapping (the server owns it): <c>canceled</c> →
    /// <c>Canceled</c> with the recorded cancel reason (<c>canceled-by-user &lt;user&gt;</c>),
    /// <c>timeout</c> → <c>Failed</c> with reason <c>timeout</c>. One endpoint for both so a kill
    /// never needs a second action.
    /// <para>
    /// Guards: worker token (bare 401), tenant-filtered run lookup (foreign id 404), own-run check
    /// (409), a pending cancel request (409 <c>cancel-not-requested</c> - the worker may only kill
    /// what the server asked it to kill), and the compare-and-set on <c>Status == Running</c>
    /// (409 - the report racing a Lost transition loses and answers honestly).
    /// </para>
    /// </summary>
    public async Task<RunReportResult> ReportCanceledAsync(
        string? plaintextToken,
        Guid runId,
        string? outcome,
        CancellationToken cancellationToken = default)
    {
        // Authentication before payload validation (the accepted/completed pattern).
        var (workerToken, worker, context) = await AuthenticateWithWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || worker is null)
        {
            return RunReportResult.InvalidToken();
        }

        try
        {
            var mappedOutcome = outcome switch
            {
                null or "" or RunCanceledOutcomes.Canceled => RunCanceledOutcomes.Canceled,
                RunCanceledOutcomes.Timeout => RunCanceledOutcomes.Timeout,
                _ => null,
            };
            if (mappedOutcome is null)
            {
                return RunReportResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            var run = await context.BuildRuns.SingleOrDefaultAsync(
                candidate => candidate.Id == runId,
                cancellationToken);
            if (run is null)
            {
                return RunReportResult.NotFound();
            }

            if (run.WorkerId != worker.Id)
            {
                return RunReportResult.Conflict(RunErrorCodes.RunNotAssignedToWorker);
            }

            if (run.CancelRequestedAt is null)
            {
                return RunReportResult.Conflict(RunErrorCodes.CancelNotRequested);
            }

            var (target, failureReason) = mappedOutcome == RunCanceledOutcomes.Timeout
                ? (BuildRunStatus.Failed, RunCancelReasons.Timeout)
                : (BuildRunStatus.Canceled, run.CancelReason ?? RunCancelReasons.CanceledByUser);

            var now = DateTimeOffset.UtcNow;
            var reported = await context.TryReportCanceledBuildRunAsync(
                runId,
                worker.Id,
                target,
                failureReason,
                now,
                cancellationToken);
            if (reported != 1)
            {
                // The run left Running between the read and the CAS (a Lost transition won).
                return RunReportResult.Conflict(RunErrorCodes.InvalidTransition);
            }

            // Terminal: flush the ingest tail and evict the run's buffer (the same completed-path
            // discipline F-008 established; closes the Canceled-run gap its log recorded).
            await runLogIngest.CompleteRunAsync(run.Id);

            logger.LogInformation(
                "Run canceled by its worker. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} Status={Status} Reason={Reason}",
                workerToken.OrganizationId,
                runId,
                worker.Id,
                target,
                failureReason);
            return RunReportResult.Success(target);
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------------
    // Deadline sweep (F-009) and offline sweep (F-010) - background services
    // ---------------------------------------------------------------------

    /// <summary>The deadline sweep's effects: how many runs got a timeout cancel request, how many
    /// were honestly marked Lost after the grace period.</summary>
    public sealed record DeadlineSweepResult(int CancelRequested, int Lost);

    /// <summary>
    /// The deadline sweeper (F-009): every <c>Running</c> run past its <see cref="Entities.BuildRun.DeadlineAt"/>
    /// gets a cancel request (reason <c>timeout</c>, signaled like a user cancel); a run still
    /// Running after the grace period (deadline + 2 × <c>Workers:OfflineAfterSeconds</c>, the PM
    /// ruling that closes F-007's online-but-unreportable gap) is marked <c>Lost</c> with reason
    /// <c>run-deadline-exceeded-result-unknown</c> - never Failed, never requeued: the result is
    /// unknown, not a failure. Before the deadline the sweep changes nothing.
    /// <para>
    /// Spans tenants (system scope). The expiry comparison happens in memory - SQLite cannot
    /// compare DateTimeOffset values in SQL (the lease sweep's discovery) - and each per-run write
    /// is a compare-and-set, so a run that leaves Running during the sweep is never touched.
    /// </para>
    /// </summary>
    public async Task<DeadlineSweepResult> SweepDeadlinesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var grace = TimeSpan.FromSeconds(2 * workerMonitorOptions.OfflineAfterSeconds);
        using var scope = systemDataScope.BeginScope();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var running = await context.BuildRuns
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(run => run.Status == BuildRunStatus.Running)
            .Select(run => new { run.Id, run.WorkerId, run.DeadlineAt, run.CancelRequestedAt })
            .ToListAsync(cancellationToken);

        var result = new DeadlineSweepResult(0, 0);
        foreach (var run in running)
        {
            if (run.DeadlineAt is not { } deadline || deadline > now)
            {
                continue;
            }

            if (deadline + grace > now)
            {
                // Within the grace period: ask the worker to kill the run (timeout). The CAS only
                // records the request when none is pending and the run is still Running.
                var requested = await context.TryRequestBuildRunCancelAcrossTenantsAsync(
                    run.Id,
                    RunCancelReasons.Timeout,
                    now,
                    cancellationToken);
                if (requested == 1)
                {
                    result = result with { CancelRequested = result.CancelRequested + 1 };
                    if (run.WorkerId is { } workerId)
                    {
                        await BroadcastCancelAsync(workerId, run.Id, RunCancelReasons.Timeout, cancellationToken);
                    }
                }
            }
            else
            {
                // Grace over, no terminal report: the honest unknown. A Lost run never requeues.
                var lost = await context.TryTerminateRunningBuildRunAsync(
                    run.Id,
                    BuildRunStatus.Lost,
                    RunFailureReasons.RunDeadlineExceededResultUnknown,
                    now,
                    cancellationToken);
                if (lost == 1)
                {
                    result = result with { Lost = result.Lost + 1 };
                    await runLogIngest.CompleteRunAsync(run.Id);
                }
            }
        }

        if (result.CancelRequested > 0 || result.Lost > 0)
        {
            logger.LogInformation(
                "Deadline sweep: {CancelRequested} timeout cancel request(s), {Lost} run(s) marked Lost after the grace period.",
                result.CancelRequested,
                result.Lost);
        }

        return result;
    }

    /// <summary>
    /// The offline sweep (F-010): every Online worker whose last heartbeat is older than the
    /// threshold is flipped Offline through a compare-and-set, and only the sweep that wins a
    /// worker's flip marks that worker's <c>Running</c> runs <c>Lost</c> (reason
    /// <c>worker-offline</c>) - the exactly-once guarantee for concurrent ticks. Claimed-but-not-
    /// accepted leases are deliberately left alone: the lease sweeper requeues them (they never
    /// executed, so a requeue is not a retry), and a Lost marking there would be a lie.
    /// <para>
    /// Spans tenants (system scope); the staleness comparison happens in memory (SQLite cannot
    /// compare DateTimeOffset values in SQL). A worker with no heartbeat timestamp is never
    /// flipped - unknown data must not produce an Offline. The flip's compare-and-set also carries
    /// the snapshot's heartbeat value as its staleness evidence: a fresh heartbeat that lands
    /// between the snapshot read and the flip leaves zero rows, the tick skips that worker's runs
    /// entirely (no fake Offline, no wrong Lost), and the next tick re-evaluates it from a fresh
    /// snapshot.
    /// </para>
    /// </summary>
    public async Task<OfflineSweepResult> SweepOfflineWorkersAsync(
        TimeSpan offlineAfter,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        using var scope = systemDataScope.BeginScope();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var onlineWorkers = await context.Workers
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(worker => worker.Status == WorkerStatus.Online)
            .Select(worker => new { worker.Id, worker.OrganizationId, worker.LastHeartbeatAt })
            .ToListAsync(cancellationToken);

        var result = new OfflineSweepResult(0, 0);
        foreach (var worker in onlineWorkers)
        {
            if (worker.LastHeartbeatAt is not { } lastHeartbeat || lastHeartbeat > now - offlineAfter)
            {
                // Inside the threshold (or no heartbeat data at all): no flapping on one beat.
                continue;
            }

            var offlined = await context.TryMarkWorkerOfflineAsync(
                worker.Id,
                lastHeartbeat,
                now,
                cancellationToken);
            if (offlined != 1)
            {
                // Another sweep already flipped the worker, or a fresh heartbeat landed after the
                // snapshot read (the CAS's observed-heartbeat evidence no longer matches): this
                // tick skips the worker and its runs - the next tick re-evaluates from a fresh
                // snapshot.
                continue;
            }

            result = result with { WorkersOfflined = result.WorkersOfflined + 1 };

            // Exactly the runs still Running on the lost worker become Lost - the claimed-but-
            // unaccepted lease stays Queued for the lease sweeper (F-010's decision).
            var runningIds = await context.BuildRuns
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(run => run.WorkerId == worker.Id && run.Status == BuildRunStatus.Running)
                .Select(run => run.Id)
                .ToListAsync(cancellationToken);
            foreach (var runId in runningIds)
            {
                var lost = await context.TryTerminateRunningBuildRunAsync(
                    runId,
                    BuildRunStatus.Lost,
                    RunFailureReasons.WorkerOffline,
                    now,
                    cancellationToken);
                if (lost == 1)
                {
                    result = result with { RunsLost = result.RunsLost + 1 };
                    await runLogIngest.CompleteRunAsync(runId);
                }
            }

            logger.LogInformation(
                "Worker {WorkerId} (organization {OrganizationId}) went Offline after a stale heartbeat; "
                + "{RunsLost} running run(s) marked Lost.",
                worker.Id,
                worker.OrganizationId,
                result.RunsLost);
        }

        return result;
    }

    /// <summary>
    /// Pushes the cancelRun signal to one worker's hub group. Fire-and-forget by design: a missed
    /// signal is recovered by the heartbeat response's cancelRequested fallback (F-009's decision),
    /// so a broadcast failure is logged, never thrown into the cancel path.
    /// </summary>
    private async Task BroadcastCancelAsync(
        Guid workerId,
        Guid runId,
        string? reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await workerLogHubs.Clients
                .Group(WorkerLogGroups.ForWorker(workerId))
                .SendAsync("cancelRun", runId, reason, cancellationToken);
        }
        catch (Exception failure)
        {
            logger.LogWarning(
                failure,
                "Broadcasting the cancel signal for run {RunId} to worker {WorkerId} failed; "
                + "the heartbeat fallback will deliver it.",
                runId,
                workerId);
        }
    }

    // ---------------------------------------------------------------------
    // Shared plumbing
    // ---------------------------------------------------------------------

    /// <summary>
    /// F-004's authentication: resolve the worker token through the system scope (setting the
    /// tenant), then re-read worker and token through the tenant filter - the re-read rechecks
    /// revocation, which may have happened between the two lookups.
    /// </summary>
    private async Task<WorkerToken?> AuthenticateAsync(
        string? plaintextToken,
        CancellationToken cancellationToken) =>
        await workerService.AuthenticateWorkerAsync(plaintextToken, cancellationToken);

    /// <summary>
    /// The full reporting preamble: token authentication, then the tenant-side re-read of the
    /// token (revocation) and the worker row. The caller owns (and must dispose) the context.
    /// </summary>
    private async Task<(WorkerToken? Token, Worker? Worker, ZenkinsDbContext Context)>
        AuthenticateWithWorkerAsync(string? plaintextToken, CancellationToken cancellationToken)
    {
        var workerToken = await AuthenticateAsync(plaintextToken, cancellationToken);
        if (workerToken is null)
        {
            return (null, null, null!);
        }

        var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var worker = await context.Workers.SingleOrDefaultAsync(
            candidate => candidate.Id == workerToken.WorkerId,
            cancellationToken);
        var tokenRow = await context.WorkerTokens.SingleOrDefaultAsync(
            candidate => candidate.Id == workerToken.Id && candidate.RevokedAt == null,
            cancellationToken);
        if (worker is null || tokenRow is null)
        {
            await context.DisposeAsync();
            return (null, null, null!);
        }

        return (workerToken, worker, context);
    }

    /// <summary>
    /// Projects runs into views, resolving the display names (trigger user's email, worker's
    /// name) in two batched lookups so a page render costs a fixed number of queries.
    /// </summary>
    private static async Task<List<RunView>> ToViewsAsync(
        ZenkinsDbContext context,
        IReadOnlyList<BuildRun> runs,
        CancellationToken cancellationToken)
    {
        if (runs.Count == 0)
        {
            return [];
        }

        var userIds = runs
            .Where(run => run.TriggerUserId is not null)
            .Select(run => run.TriggerUserId!)
            .Distinct()
            .ToArray();
        var emails = await context.Users
            .Where(user => userIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.Email, cancellationToken);

        var workerIds = runs
            .Where(run => run.WorkerId is not null)
            .Select(run => run.WorkerId!.Value)
            .Distinct()
            .ToArray();
        var workerNames = await context.Workers
            .Where(worker => workerIds.Contains(worker.Id))
            .ToDictionaryAsync(worker => worker.Id, worker => worker.Name, cancellationToken);

        return runs.Select(run => new RunView(
            run.Id,
            run.ProjectId,
            run.Status,
            run.TriggerType,
            run.TriggerUserId is not null && emails.TryGetValue(run.TriggerUserId, out var email)
                ? email
                : null,
            run.WorkerId is not null && workerNames.TryGetValue(run.WorkerId.Value, out var name)
                ? name
                : null,
            RunValidation.DecodeCommandJson(run.CommandJson),
            RunValidation.ParseTags(run.RequiredTags),
            run.StartedAt,
            run.EndedAt,
            run.ExitCode,
            run.FailureReason,
            run.CreatedAt,
            run.UpdatedAt,
            run.TimeoutSeconds,
            run.DeadlineAt)).ToList();
    }
}
