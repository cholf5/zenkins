using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Projects;
using Zenkins.Server.Workers;
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
}

/// <summary>The raw trigger-form values, before trimming and validation.</summary>
public sealed record RunTriggerInput(string? Command, string? RequiredTags);

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
    DateTimeOffset UpdatedAt);

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
    RunOptions options,
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
    /// </summary>
    public async Task<RunReportResult> AcceptAsync(
        string? plaintextToken,
        Guid runId,
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
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Run accepted. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId}",
                workerToken.OrganizationId,
                run.Id,
                worker.Id);
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
            run.UpdatedAt)).ToList();
    }
}
