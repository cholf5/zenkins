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
using Zenkins.Shared.Pipelines;
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
    /// <summary>F-014: the branch the run builds (blank means the project's default branch).</summary>
    public const string Branch = "branch";
    public const string RequiredTags = "requiredTags";
    public const string Timeout = "timeoutMinutes";
}

/// <summary>The raw trigger-form values, before trimming and validation.</summary>
public sealed record RunTriggerInput(string? Branch, string? RequiredTags, string? TimeoutMinutes = null);

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
/// <param name="PipelinePath">The pipeline file the run looked for (snapshot at trigger time).</param>
/// <param name="GitBranch">The branch the run built (snapshot at trigger time).</param>
/// <param name="GitCommit">The commit the worker resolved, once it reported one.</param>
/// <param name="Pipeline">The frozen spec, when the definition was reported; null before that.</param>
/// <param name="GitCredentialAssigned">
/// Whether the run's project had a Git credential assigned when the page was read (card F-018).
/// <see langword="null"/> when the project could not be read (a foreign or deleted row), so the page
/// says "unknown" rather than claiming a credential was missing. The value is a configuration fact -
/// never material - and it is what lets the page explain a <c>git-auth-rejected</c> failure instead of
/// showing the bare code.
/// </param>
public sealed record RunView(
    Guid Id,
    Guid ProjectId,
    string Status,
    string TriggerType,
    string? TriggerUserEmail,
    string? WorkerName,
    string PipelinePath,
    string GitBranch,
    string? GitCommit,
    PipelineSpec? Pipeline,
    string[] RequiredTags,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int? ExitCode,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int? TimeoutSeconds = null,
    DateTimeOffset? DeadlineAt = null,
    IReadOnlyList<RunStepView>? Steps = null,
    bool? GitCredentialAssigned = null)
{
    /// <summary>The step names in declaration order - what the run page lists (F-014).</summary>
    public string[] StepNames => Pipeline is { } spec ? [.. spec.Steps.Select(step => step.Name)] : [];

    /// <summary>The pipeline's declared name, or null while no definition was reported.</summary>
    public string? PipelineName => Pipeline?.Name;

    /// <summary>
    /// The run's steps in declaration order (card F-015): each declared step merged with the row the
    /// worker recorded for it, or <see cref="PipelineStepStatus.Pending"/> when it has not been
    /// reached yet. A run page opened mid-run therefore shows completed, current and pending steps.
    /// </summary>
    public IReadOnlyList<RunStepView> StepList => Steps ?? [];
}

/// <summary>
/// One step of a run as the page displays it (card F-015). <see cref="Id"/> is the recorded row's id
/// (also what <see cref="Zenkins.Shared.LogStreaming.LogLineMessage.StepId"/> carries), null for a
/// declared step that has not been reported yet; <see cref="Index"/> is its 1-based position in the
/// frozen spec.
/// </summary>
public sealed record RunStepView(
    Guid? Id,
    int Index,
    string Name,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int? ExitCode,
    string? FailureReason)
{
    /// <summary>Whether the step is declared but not reached (no row recorded).</summary>
    public bool IsPending => Status == PipelineStepStatus.Pending;

    /// <summary>The step's duration, or null while it has no recorded end.</summary>
    public TimeSpan? Duration => StartedAt is { } start && EndedAt is { } end ? end - start : null;
}

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

/// <summary>
/// A worker's runs as the worker detail page shows them (card D-005). <see cref="Current"/> is the
/// worker's <c>Running</c> runs - the same predicate the roster's per-worker count aggregates;
/// <see cref="Recent"/> is everything else the worker has queued or finished, newest first, bounded.
/// </summary>
public sealed record WorkerRunsView(
    IReadOnlyList<RunView> Current,
    IReadOnlyList<RunView> Recent);

/// <summary>A queued run handed to a worker by the claim endpoint.</summary>
public sealed record RunClaimResult(
    RunOperationStatus Status,
    Guid? RunId = null,
    Guid? ProjectId = null,
    string? RepositoryUrl = null,
    string? Branch = null,

    // F-017 (review M1): the exact commit this run must build, when the trigger already named it. Null for
    // a manual run (the worker resolves the branch tip and reports it back) and for a webhook run whose
    // payload carried no usable commit (never happens today - the parser refuses that payload).
    string? Commit = null,
    string? PipelinePath = null,
    DateTimeOffset? LeaseExpiresAt = null)
{
    /// <summary>The 204 answer: nothing eligible right now. Same for every other rejection reason -
    /// a worker polls again, it never learns why it got nothing.</summary>
    public static RunClaimResult NoRun() => new(RunOperationStatus.Success);
}

/// <summary>Outcome of the accepted/completed/definition reporting endpoints.</summary>
/// <param name="DeadlineAt">F-014: the run's frozen deadline, answered by the accept edge so the
/// worker's definition phase runs against the run's own wall clock.</param>
public sealed record RunReportResult(
    RunOperationStatus Status,
    string? ErrorCode = null,
    string? RunStatus = null,
    DateTimeOffset? DeadlineAt = null)
{
    public static RunReportResult Success(string runStatus, DateTimeOffset? deadlineAt = null) =>
        new(RunOperationStatus.Success, RunStatus: runStatus, DeadlineAt: deadlineAt);

    public static RunReportResult InvalidToken() => new(RunOperationStatus.InvalidToken);

    public static RunReportResult Invalid(string errorCode) => new(RunOperationStatus.InvalidInput, errorCode);

    public static RunReportResult NotFound() => new(RunOperationStatus.NotFound);

    public static RunReportResult Conflict(string errorCode) => new(RunOperationStatus.Conflict, errorCode);
}

/// <summary>
/// One step transition a worker reports (card F-015): the step's 1-based position in the frozen spec,
/// its name (checked against that spec), the state it reached and - when nothing exited - the closed
/// reason. Timestamps are the server's: the run's own <c>StartedAt</c>/<c>EndedAt</c> follow the same
/// discipline, so a worker clock skew cannot make a step appear to end before it started.
/// </summary>
public sealed record RunStepReport(
    int Index,
    string? Name,
    string? Status,
    int? ExitCode = null,
    string? FailureReason = null);

/// <summary>The answer of a step report: the recorded row's id (which the worker stamps on the step's
/// log lines), its state and the run's state.</summary>
public sealed record RunStepResult(
    RunOperationStatus Status,
    Guid? StepId = null,
    string? ErrorCode = null,
    string? RunStatus = null,
    string? StepStatus = null)
{
    public static RunStepResult Success(Guid stepId, string stepStatus, string runStatus) =>
        new(RunOperationStatus.Success, stepId, RunStatus: runStatus, StepStatus: stepStatus);

    public static RunStepResult InvalidToken() => new(RunOperationStatus.InvalidToken);

    public static RunStepResult Invalid(string errorCode) => new(RunOperationStatus.InvalidInput, ErrorCode: errorCode);

    public static RunStepResult NotFound() => new(RunOperationStatus.NotFound);

    public static RunStepResult Conflict(string errorCode) => new(RunOperationStatus.Conflict, ErrorCode: errorCode);
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
/// The claim's whole unit of work - the worker-row lock, the concurrency-slot count and the
/// compare-and-set - runs in one transaction (B-002): the lock serializes a worker's own racing
/// claims, so the slot check cannot be passed twice before either lease commits.
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

    /// <summary>The most current runs the worker detail page lists (card D-005). The roster's count
    /// is uncapped - it counts, it does not render.</summary>
    private const int MaxCurrentRunsShown = 20;

    /// <summary>The most recent runs the worker detail page lists (card D-005).</summary>
    private const int MaxRecentRunsShown = 10;

    // ---------------------------------------------------------------------
    // Trigger and reads (cookie-authenticated pages and forms)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Creates a <c>Queued</c> run for a project of the active organization. Any member may trigger
    /// (PRD §3.1.2). F-014 replaced the argv command with a branch: blank means the project's default
    /// branch, and the run snapshots the branch and the pipeline path it will use so a later project
    /// edit cannot move it. Required tags must exist among the organization's workers, so a typo cannot
    /// queue a run no worker will ever take.
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

        var branchError = RunValidation.ValidateBranch(input.Branch, out var requestedBranch);
        if (branchError is not null)
        {
            return RunTriggerResult.Invalid(new RunFieldError(RunFields.Branch, branchError));
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

        var run = BuildQueuedRun(
            activeOrganizationId.Value,
            project,
            requestedBranch.Length > 0 ? requestedBranch : project.DefaultBranch,
            requiredTags,
            timeoutMinutes is { } minutes ? minutes * 60 : null,
            BuildRunTriggerType.Manual,
            actingUserId,
            gitCommit: null);
        context.BuildRuns.Add(run);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Run queued. OrganizationId={OrganizationId} ProjectId={ProjectId} RunId={RunId} Branch={Branch} PipelinePath={PipelinePath} ByUserId={UserId}",
            activeOrganizationId,
            project.Id,
            run.Id,
            run.GitBranch,
            run.PipelinePath,
            actingUserId);
        return RunTriggerResult.Success(run.Id);
    }

    /// <summary>
    /// Queues a run for a push delivery (card F-017), <b>on the caller's context and inside the caller's
    /// unit of work</b>. The webhook decides <em>which</em> branch and commit; this is the same construction
    /// routine the manual trigger uses (one routine, one queue, one state machine), so a webhook run is
    /// never a special case downstream.
    /// <para>
    /// <b>Why it takes a context.</b> The delivery's replay record and the run it creates must commit
    /// together (review m6): a crash between "record the delivery" and "queue the run" would otherwise
    /// lose that push forever - a retry would answer <c>duplicate</c> with no run behind it. The caller
    /// owns the transaction, and this method stages the run without saving it.
    /// </para>
    /// <para>
    /// <b>Differs from <see cref="TriggerAsync"/> in exactly three ways, each deliberate.</b> There is no
    /// acting user (nobody signed in) and therefore no membership check - the caller proved possession of
    /// the webhook's secret, and the webhook row selected the tenant before this call. Required tags are
    /// <b>not</b> checked against the organization's workers: a push to a branch whose tag set no worker
    /// covers yet stays <c>Queued</c>, which is the honest answer (the AC's "no eligible worker keeps the
    /// run Queued") rather than a rejected delivery. And <see cref="BuildRun.GitCommit"/> is recorded at
    /// creation from the pushed <c>after</c>, because the webhook already names the commit - the claim
    /// hands that same commit to the worker to pin (review M1), so the recorded commit and the built tree
    /// cannot drift apart when the branch moves between push and clone.
    /// </para>
    /// <para>
    /// <b>The repository is the project's, never the payload's.</b> Nothing on this path reads a
    /// <c>clone_url</c>: the claim hands the worker <c>project.RepositoryUrl</c>, so a valid signature
    /// cannot redirect a build to another repository.
    /// </para>
    /// </summary>
    /// <returns>
    /// <see cref="RunTriggerResult.Success"/> with the staged run's id (<b>not yet saved</b>), or the
    /// reason nothing was staged - in which case the caller rolls its transaction back.
    /// </returns>
    public async Task<RunTriggerResult> QueueWebhookRunAsync(
        ZenkinsDbContext context,
        Guid projectId,
        string branch,
        string gitCommit,
        IReadOnlyList<string> requiredTags,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requiredTags);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            // The webhook row selects the tenant; reaching here without one is a wiring fault, not a
            // user error - refuse rather than write an untagged run.
            return RunTriggerResult.Forbidden("No organization is selected for this delivery.");
        }

        // The branch came from `ref` and the commit from `after`; both are re-validated here so a
        // malformed payload cannot reach the queue through a future caller of this method.
        if (RunValidation.ValidateBranch(branch, out var normalizedBranch) is not null
            || normalizedBranch.Length == 0)
        {
            return RunTriggerResult.Invalid(new RunFieldError(RunFields.Branch, RunErrorCodes.BranchInvalid));
        }

        if (!GitCommitValidation.IsValid(gitCommit))
        {
            return RunTriggerResult.Invalid(
                new RunFieldError(RunFields.Branch, GitCommitValidation.InvalidCode));
        }

        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            return RunTriggerResult.NotFound();
        }

        var run = BuildQueuedRun(
            activeOrganizationId.Value,
            project,
            normalizedBranch,
            string.Join(',', requiredTags),
            timeoutSeconds: null,
            BuildRunTriggerType.Webhook,
            triggerUserId: null,
            gitCommit);
        context.BuildRuns.Add(run);

        logger.LogInformation(
            "Run queued by webhook. OrganizationId={OrganizationId} ProjectId={ProjectId} RunId={RunId} Branch={Branch} Commit={Commit} PipelinePath={PipelinePath}",
            activeOrganizationId,
            project.Id,
            run.Id,
            run.GitBranch,
            run.GitCommit,
            run.PipelinePath);
        return RunTriggerResult.Success(run.Id);
    }

    /// <summary>
    /// The one run construction routine both trigger sources use: the tenant from the active request (the
    /// data layer rejects anything else), the branch and pipeline path snapshotted so a later project edit
    /// cannot move this run, and the trigger source's own attribution. Adding and saving is the caller's
    /// step, so each source keeps its own unit of work (the manual trigger saves one row; a webhook saves
    /// its replay record and the run together).
    /// </summary>
    private static BuildRun BuildQueuedRun(
        Guid organizationId,
        Project project,
        string branch,
        string requiredTags,
        int? timeoutSeconds,
        string triggerType,
        string? triggerUserId,
        string? gitCommit) =>
        new()
        {
            OrganizationId = organizationId,
            ProjectId = project.Id,
            TriggerType = triggerType,
            TriggerUserId = triggerUserId,
            RequiredTags = requiredTags,

            // F-014 snapshots: the branch the trigger chose and the project's pipeline path. Both are
            // copies, so editing the project later never moves this run.
            GitBranch = branch,
            PipelinePath = project.PipelinePath,
            TimeoutSeconds = timeoutSeconds,

            // F-017: a webhook delivery already names the commit; a manual trigger leaves it to the
            // worker's accepted report (F-013).
            GitCommit = gitCommit,
        };

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

    // ---------------------------------------------------------------------
    // Worker page reads (card D-005)
    // ---------------------------------------------------------------------

    /// <summary>
    /// How many <see cref="WorkerRunsView.Current"/>-style runs each worker of the active
    /// organization holds, in one read. Card D-005's decision: the roster's per-worker count comes
    /// from the same query shape as the detail page's current-runs list - the worker's
    /// <c>Running</c> runs - never from a denormalised column that could drift from it.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, int>> GetCurrentRunCountsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.BuildRuns
            .AsNoTracking()
            .Where(run => run.WorkerId != null && run.Status == BuildRunStatus.Running)
            .Select(run => new { WorkerId = run.WorkerId!.Value })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(row => row.WorkerId)
            .ToDictionary(group => group.Key, group => group.Count());
    }

    /// <summary>
    /// The runs the worker detail page lists (card D-005): the worker's current runs (<c>Running</c>,
    /// the same predicate <see cref="GetCurrentRunCountsAsync"/> aggregates for the roster) and its
    /// recent runs (everything else - queued, finished, canceled, lost - newest first), both capped.
    /// The worker id resolves through the tenant filter, so a foreign worker's runs are simply empty.
    /// </summary>
    public async Task<WorkerRunsView> GetWorkerRunsAsync(
        Guid workerId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // In-memory ordering on purpose (SQLite cannot ORDER BY DateTimeOffset columns) - the same
        // shape GetProjectRunsAsync uses. A worker's Running runs are bounded by MaxConcurrency, so
        // the current read is small by construction.
        var currentRows = await context.BuildRuns
            .AsNoTracking()
            .Where(run => run.WorkerId == workerId && run.Status == BuildRunStatus.Running)
            .ToListAsync(cancellationToken);
        var recentRows = await context.BuildRuns
            .AsNoTracking()
            .Where(run => run.WorkerId == workerId && run.Status != BuildRunStatus.Running)
            .ToListAsync(cancellationToken);

        var runs = currentRows
            .OrderByDescending(run => run.CreatedAt)
            .ThenBy(run => run.Id)
            .Take(MaxCurrentRunsShown)
            .Concat(recentRows
                .OrderByDescending(run => run.CreatedAt)
                .ThenBy(run => run.Id)
                .Take(MaxRecentRunsShown))
            .ToList();
        var views = await ToViewsAsync(context, runs, cancellationToken);
        return new WorkerRunsView(
            views.Where(run => run.Status == BuildRunStatus.Running).ToArray(),
            views.Where(run => run.Status != BuildRunStatus.Running).ToArray());
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
            .Select(log => new LogLineMessage(log.Seq, log.Stream, log.Message, log.Timestamp, log.StepId));

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
    /// <para>
    /// <b>The cap is part of the claim (B-002).</b> The slot count and the lease run inside one
    /// transaction that opens by locking the caller's own worker row
    /// (<see cref="ZenkinsDbContext.LockWorkerForClaimAsync"/>), so concurrent claims of one
    /// worker serialize instead of interleaving the count with the lease - the enforced
    /// <c>MaxConcurrency</c> is the data layer's, not the client's good behaviour. The lease
    /// compare-and-set keeps its F-006 semantics unchanged.
    /// </para>
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

        // 2. Eligibility of the worker itself: Online, not disabled (card D-005: the disabled flag is
        //    a server fact enforced here, so a worker cannot bypass it by ignoring the page - a
        //    disabled worker's claim gets the same not-assigned answer as any other rejection).
        //    Runs already in flight are untouched: nothing below blocks the accepted/completed
        //    reports of a running run.
        var worker = await context.Workers.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == workerToken.WorkerId,
            cancellationToken);
        if (worker is null || worker.Status != WorkerStatus.Online || worker.IsDisabled)
        {
            return RunClaimResult.NoRun();
        }

        // 3. B-002: the concurrency cap and the lease are ONE unit of work. The slot check used to
        //    be check-then-act - count in one statement, lease in another - so two racing claims of
        //    one valid worker token could both pass the count before either lease committed and
        //    admit more running runs than the worker's reported MaxConcurrency. The claim therefore
        //    runs in one transaction, opened by locking the worker's own row:
        //    - LockWorkerForClaimAsync takes the provider's row lock (PostgreSQL) or the database's
        //      single write lock (SQLite) and holds it to commit, so two claims of one worker
        //      serialize here instead of interleaving count and lease;
        //    - inside the lock the count below can only ever be raised by this claim itself (a
        //      requeue, cancel or completion only releases slots), so count + one lease cannot
        //      cross the cap;
        //    - the compare-and-set itself is unchanged: the same guarded statement leases the same
        //      candidate exactly once, with the same lease semantics (F-006 untouched).
        //    The early returns below dispose the transaction - the lock writes nothing.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var locked = await context.LockWorkerForClaimAsync(worker.Id, cancellationToken);
        if (locked == 0)
        {
            // The worker row is gone (deleted between the read and the lock): nothing to admit.
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

        // 4. The candidate queue: Queued and unleased (never leased, or requeued by the sweeper),
        //    FIFO by creation. Ordered in memory - a small list per tenant, and SQLite cannot
        //    ORDER BY DateTimeOffset columns. An expired lease is NOT claimable here: the lease
        //    sweeper is the single authority on expiry, and SQLite cannot compare DateTimeOffset
        //    values in SQL, so the claim never needs a time predicate (see TryLeaseBuildRunAsync).
        //    F-014: the project is included because the pipeline layer needs its repository URL.
        var queued = await context.BuildRuns
            .AsNoTracking()
            .Include(run => run.Project)
            .Where(run => run.Status == BuildRunStatus.Queued && run.LeaseExpiresAt == null)
            .ToListAsync(cancellationToken);
        var candidates = queued
            .OrderBy(run => run.CreatedAt)
            .ThenBy(run => run.Id)
            .Where(run => RunValidation.WorkerTagsCover(run.RequiredTags, worker.Tags))
            .Take(MaxClaimCandidates)
            .ToArray();

        // 5. Compare-and-set loop: the first candidate whose single-statement lease wins is the
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
                // The slot was reserved and the run leased inside one transaction: commit is what
                // makes both visible together, and it releases the worker-row lock for the next
                // claim.
                await transaction.CommitAsync(cancellationToken);

                logger.LogInformation(
                    "Run leased. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} LeaseExpiresAt={LeaseExpiresAt}",
                    workerToken.OrganizationId,
                    candidate.Id,
                    worker.Id,
                    leaseExpiresAt);

                // F-014: the claim carries what the pipeline layer executes - the repository, the
                // branch this run snapshotted and the pipeline path it snapshotted. F-017 adds the exact
                // commit when the trigger already named one (a webhook push): the worker pins that commit
                // instead of trusting the branch tip, which may have moved by now.
                return new RunClaimResult(
                    RunOperationStatus.Success,
                    candidate.Id,
                    candidate.ProjectId,
                    candidate.Project.RepositoryUrl,
                    candidate.GitBranch,
                    candidate.GitCommit,
                    candidate.PipelinePath,
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
            // AsNoTracking on purpose: the transition below is a single guarded UPDATE, so the
            // tracked entity must never be able to write a stale status afterwards.
            var run = await context.BuildRuns.AsNoTracking().SingleOrDefaultAsync(
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

            // F-017 (review M1): a webhook run already records the commit the push named, and the claim
            // handed that same commit to this worker to pin. A report that disagrees is refused - never
            // silently overwritten (the column is write-once) and never silently accepted (the run would
            // claim to have built one tree while the worker built another). The check runs BEFORE any
            // write, so a refused report leaves the run exactly as it was.
            if (run.GitCommit is not null
                && gitCommit is not null
                && !string.Equals(run.GitCommit, gitCommit, StringComparison.Ordinal))
            {
                return RunReportResult.Conflict(RunErrorCodes.GitCommitMismatch);
            }

            if (run.LeaseExpiresAt is null || run.LeaseExpiresAt <= now)
            {
                return RunReportResult.Conflict(RunErrorCodes.LeaseExpired);
            }

            // F-009: the execution deadline is frozen at accept - the run's per-run override or the
            // server default. The deadline sweeper never touches the run before this instant, and a
            // later configuration change never moves an accepted run's deadline.
            var timeout = run.TimeoutSeconds is { } seconds
                ? TimeSpan.FromSeconds(seconds)
                : options.DefaultTimeout;
            var deadlineAt = now + timeout;

            // F-013 + review M6: the transition and the commit stamp are ONE unit of work, or an
            // interruption between them would leave the run Running with GitCommit NULL - a state its
            // own guard (Running -> Running is not a legal transition, so every retry is a 409) makes
            // permanently unrecoverable. A failed attempt therefore changes nothing at all: the run
            // stays Queued with its lease intact, so the worker's retry completes it.
            //
            // F-014 closes F-013's check-then-act window: the transition itself is now the guarded
            // compare-and-set TryAcceptBuildRunAsync (WHERE restates Queued + this worker + a live
            // lease), so two concurrent accepted reports for one run cannot both win - the loser
            // updates zero rows and answers a conflict, exactly as the queued cancel does.
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            var accepted = await context.TryAcceptBuildRunAsync(
                run.Id,
                worker.Id,
                now,
                deadlineAt,
                now,
                cancellationToken);
            if (accepted == 0)
            {
                await transaction.RollbackAsync(cancellationToken);

                // Why did the CAS lose? A run that is no longer Queued was accepted (or canceled)
                // concurrently - a conflict either way; one that is still Queued lost its lease.
                var currentStatus = await context.BuildRuns
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == run.Id)
                    .Select(candidate => candidate.Status)
                    .SingleOrDefaultAsync(cancellationToken);
                return currentStatus == BuildRunStatus.Queued
                    ? RunReportResult.Conflict(RunErrorCodes.LeaseExpired)
                    : RunReportResult.Conflict(RunErrorCodes.InvalidTransition);
            }

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
                deadlineAt,
                recordedCommit);

            // F-014: the answer carries the frozen deadline, so the worker's definition phase runs
            // against the run's own wall clock instead of a worker-side guess.
            return RunReportResult.Success(BuildRunStatus.Running, deadlineAt);
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    /// <summary>
    /// Reports the definition of a run this worker executes (card F-014): the commit it resolved and
    /// the validated spec it froze - sent once, before the first step starts, so the run page can show
    /// the step list of a run that is only just beginning.
    /// <para>
    /// <b>Server-side gate, Worker-side strictness.</b> The Worker owns the field-path validation (it is
    /// the only side holding the Lua table); this method re-checks the envelope the issue asks for - the
    /// JSON parses, the schema version is known, the step list is 1..200 and the payload is under the
    /// size cap - so a buggy or hostile worker cannot store something the page would then have to
    /// trust.
    /// </para>
    /// <para>
    /// <b>Idempotent.</b> Both writes are compare-and-sets (the commit's <c>IS NULL</c>, the spec's
    /// <c>IS NULL</c>): a re-delivered report after a lost response updates nothing and still answers
    /// success while the run is Running with a spec - it must not turn a completed report into a 409.
    /// </para>
    /// </summary>
    public async Task<RunReportResult> ReportDefinitionAsync(
        string? plaintextToken,
        Guid runId,
        string? gitCommit,
        string? pipelineSpecJson,
        CancellationToken cancellationToken = default)
    {
        var (workerToken, worker, context) = await AuthenticateWithWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || worker is null)
        {
            return RunReportResult.InvalidToken();
        }

        try
        {
            // F-014: two reports share this endpoint. The commit-only one (a null spec) lands right
            // after the clone, so a run whose definition fails still records what it checked out; the
            // second one adds the frozen spec before the first step starts.
            var hasSpec = !string.IsNullOrWhiteSpace(pipelineSpecJson);
            if (gitCommit is null && !hasSpec)
            {
                return RunReportResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            if (gitCommit is not null && !GitCommitValidation.IsValid(gitCommit))
            {
                return RunReportResult.Invalid(GitCommitValidation.InvalidCode);
            }

            if (hasSpec && !PipelineSpecJson.TryRead(pipelineSpecJson, out _, out var specError))
            {
                logger.LogWarning(
                    "Run definition refused. RunId={RunId} WorkerId={WorkerId} Reason={Reason}",
                    runId,
                    worker.Id,
                    specError);
                return RunReportResult.Invalid(RunErrorCodes.PipelineSpecInvalid);
            }

            var run = await context.BuildRuns.AsNoTracking().SingleOrDefaultAsync(
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

            if (run.Status != BuildRunStatus.Running)
            {
                // A definition can only be frozen on a running run; anything else is a stale or
                // misdirected report.
                return RunReportResult.Conflict(RunErrorCodes.InvalidTransition);
            }

            // F-017 (review M1): the definition report is where a worker normally states the commit it
            // checked out. A webhook run was created with the pushed commit, and the claim told the worker
            // to pin exactly that; a report naming a different commit means the worker built something
            // else, which is a conflict, not a detail to absorb. Nothing is written on the refusal path -
            // no commit rewrite, no frozen spec - so the run keeps its recorded commit and stays
            // re-reportable.
            if (run.GitCommit is not null
                && gitCommit is not null
                && !string.Equals(run.GitCommit, gitCommit, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Run definition refused: the reported commit disagrees with the run's. RunId={RunId} WorkerId={WorkerId}",
                    run.Id,
                    worker.Id);
                return RunReportResult.Conflict(RunErrorCodes.GitCommitMismatch);
            }

            var now = DateTimeOffset.UtcNow;
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            if (gitCommit is not null)
            {
                // The same once-only stamp the accept report uses (F-013): the first value - the commit
                // this run actually built - survives.
                await context.TryStampGitCommitAsync(run.Id, gitCommit, now, cancellationToken);
            }

            if (!hasSpec)
            {
                // Commit only: nothing was defined yet, so no spec is frozen.
                await transaction.CommitAsync(cancellationToken);
                logger.LogInformation(
                    "Run commit recorded. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} GitCommit={GitCommit}",
                    workerToken.OrganizationId,
                    run.Id,
                    worker.Id,
                    gitCommit);
                return RunReportResult.Success(BuildRunStatus.Running, run.DeadlineAt);
            }

            var frozen = await context.FreezePipelineDefinitionAsync(
                run.Id,
                worker.Id,
                pipelineSpecJson!,
                now,
                cancellationToken);
            if (frozen == 0)
            {
                await transaction.RollbackAsync(cancellationToken);

                // The spec column is write-once. A re-delivery of the SAME report (a retry after a lost
                // response) is the idempotent success; a DIFFERENT spec for the same run is refused -
                // answering 200 would silently accept a second, contradictory definition.
                var existing = await context.BuildRuns
                    .AsNoTracking()
                    .Where(candidate => candidate.Id == run.Id)
                    .Select(candidate => candidate.PipelineSpecJson)
                    .SingleOrDefaultAsync(cancellationToken);
                if (existing is null)
                {
                    return RunReportResult.Conflict(RunErrorCodes.InvalidTransition);
                }

                return string.Equals(existing, pipelineSpecJson, StringComparison.Ordinal)
                    ? RunReportResult.Success(BuildRunStatus.Running, run.DeadlineAt)
                    : RunReportResult.Conflict(RunErrorCodes.PipelineSpecFrozen);
            }

            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Run definition frozen. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} GitCommit={GitCommit} SpecBytes={SpecBytes}",
                workerToken.OrganizationId,
                run.Id,
                worker.Id,
                gitCommit,
                pipelineSpecJson!.Length);
            return RunReportResult.Success(BuildRunStatus.Running, run.DeadlineAt);
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    /// <summary>
    /// Records one step transition of a run this worker executes (card F-015, PRD §3.3.5). Every step
    /// is reported before it starts (<c>Running</c>) and again when it ends (<c>Success</c>/
    /// <c>Failed</c>/<c>Skipped</c>), so a run page opened mid-run shows completed, current and pending
    /// steps; a skipped step is reported too, with its reason, so the page never shows a step that
    /// silently vanished.
    /// <para>
    /// <b>Guards, in this order.</b> The token (bare 401), the payload's closed vocabulary (400), the
    /// run's existence through the tenant filter (404 - a foreign id and a missing one are the same
    /// answer), the run's ownership and state (409), the frozen spec (409: a step only exists inside a
    /// definition), the position and name against that spec (400), then ordering across the rows already
    /// recorded (409). A position that already has a row is never inserted twice: the same report
    /// re-delivered is idempotent, and a terminal step refuses a further transition.
    /// </para>
    /// <para>
    /// <b>Timestamps are the server's.</b> Started/ended instants follow the run's own discipline
    /// (<c>BuildRun.StartedAt</c> is stamped at accept), so a skewed worker clock can never make a step
    /// end before it started. A skipped step gets no start time: nothing ran.
    /// </para>
    /// </summary>
    public async Task<RunStepResult> ReportStepAsync(
        string? plaintextToken,
        Guid runId,
        RunStepReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        // Authentication before payload validation (the accepted/completed discipline).
        var (workerToken, worker, context) = await AuthenticateWithWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || worker is null)
        {
            return RunStepResult.InvalidToken();
        }

        try
        {
            // 1. The payload's shape: a status outside the closed vocabulary, a failure reason that does
            //    not belong to it, or an exit code a non-exiting state claims are all contract mismatches.
            var status = report.Status;
            if (!PipelineStepStatus.IsReportable(status) || report.Index < 1)
            {
                return RunStepResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            if (!PipelineStepFailureReasons.IsLegal(status!, report.FailureReason))
            {
                return RunStepResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            if (status == PipelineStepStatus.Success && report.ExitCode is not (null or 0))
            {
                return RunStepResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            if (status is PipelineStepStatus.Running or PipelineStepStatus.Skipped && report.ExitCode is not null)
            {
                return RunStepResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            // 2. The run: this worker's, still Running, and carrying the frozen spec the step belongs to.
            //    The lookup goes through the tenant query filter, so a foreign run id is the same 404.
            var run = await context.BuildRuns
                .AsNoTracking()
                .Where(candidate => candidate.Id == runId)
                .Select(candidate => new
                {
                    candidate.Id,
                    candidate.OrganizationId,
                    candidate.WorkerId,
                    candidate.Status,
                    candidate.PipelineSpecJson,
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (run is null)
            {
                return RunStepResult.NotFound();
            }

            if (run.WorkerId != worker.Id)
            {
                return RunStepResult.Conflict(RunErrorCodes.RunNotAssignedToWorker);
            }

            if (run.Status != BuildRunStatus.Running)
            {
                return RunStepResult.Conflict(RunErrorCodes.InvalidTransition);
            }

            if (!PipelineSpecJson.TryRead(run.PipelineSpecJson, out var spec, out _) || spec is null)
            {
                // A step only exists inside a definition: without one there is nothing to index into.
                return RunStepResult.Conflict(RunErrorCodes.StepUnknown);
            }

            if (report.Index > spec.Steps.Length
                || !string.Equals(report.Name, spec.Steps[report.Index - 1].Name, StringComparison.Ordinal))
            {
                // The position must exist in the frozen spec and agree with it; that is what makes
                // (RunId, Index) an ordering authority rather than a bucket of worker-chosen rows.
                return RunStepResult.Invalid(RunErrorCodes.StepUnknown);
            }

            var declaredName = spec.Steps[report.Index - 1].Name;

            // 3. Ordering and idempotence, from the rows already recorded for this run.
            var existing = await context.BuildSteps
                .Where(step => step.RunId == runId)
                .ToListAsync(cancellationToken);
            var row = existing.SingleOrDefault(candidate => candidate.Index == report.Index);
            var highestIndex = existing.Count == 0 ? 0 : existing.Max(candidate => candidate.Index);
            var now = DateTimeOffset.UtcNow;

            if (row is null)
            {
                if (report.Index <= highestIndex)
                {
                    return RunStepResult.Conflict(RunErrorCodes.StepOutOfOrder);
                }

                row = new BuildStep
                {
                    // The run's own tenant, which the filter has already proved is the active one.
                    OrganizationId = run.OrganizationId,
                    RunId = runId,
                    Index = report.Index,
                    Name = declaredName,
                    Status = status!,
                    ExitCode = report.ExitCode,
                    FailureReason = report.FailureReason,

                    // A skipped step never ran, so it has no start instant; a coalesced terminal report
                    // (a single Success/Failed without a preceding Running) starts and ends now.
                    StartedAt = status == PipelineStepStatus.Skipped ? null : now,
                    EndedAt = status == PipelineStepStatus.Skipped ? now
                        : PipelineStepStatus.IsTerminal(status) ? now : null,
                };
                context.BuildSteps.Add(row);
            }
            else if (string.Equals(row.Status, status, StringComparison.Ordinal))
            {
                // A re-delivery of the same report (a lost response): the stored row is the answer.
                return RunStepResult.Success(row.Id, row.Status, run.Status);
            }
            else if (PipelineStepStatus.IsTerminal(row.Status))
            {
                return RunStepResult.Conflict(RunErrorCodes.StepAlreadyFinished);
            }
            else
            {
                // Running -> terminal (or Running -> Running, a retried progress report): the first
                // recorded start instant survives.
                row.Status = status!;
                row.ExitCode = report.ExitCode;
                row.FailureReason = report.FailureReason;
                row.StartedAt ??= status == PipelineStepStatus.Skipped ? null : now;
                if (PipelineStepStatus.IsTerminal(status))
                {
                    row.EndedAt = now;
                }
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "BuildSteps"))
            {
                // Two concurrent reports of the same position: one row wins the unique (RunId, Index)
                // index. The loser answers what is stored instead of inventing a second record.
                context.ChangeTracker.Clear();
                var stored = await context.BuildSteps
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        candidate => candidate.RunId == runId && candidate.Index == report.Index,
                        cancellationToken);
                if (stored is null)
                {
                    throw;
                }

                return string.Equals(stored.Status, status, StringComparison.Ordinal)
                    ? RunStepResult.Success(stored.Id, stored.Status, run.Status)
                    : RunStepResult.Conflict(RunErrorCodes.StepOutOfOrder);
            }

            logger.LogInformation(
                "Run step recorded. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} StepIndex={StepIndex} StepStatus={StepStatus} ExitCode={ExitCode}",
                run.OrganizationId,
                runId,
                worker.Id,
                report.Index,
                row.Status,
                row.ExitCode);
            return RunStepResult.Success(row.Id, row.Status, run.Status);
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    /// <summary>
    /// Reports the end of a run this worker executes (card F-014 extends F-006's exit code with a
    /// closed failure reason):    /// <list type="bullet">
    /// <item>exit 0, no reason → <c>Success</c>.</item>
    /// <item>a non-zero exit (optionally with <c>step-failed</c>) → <c>Failed</c> with the code recorded
    /// - the code is the record, and the reason adds which phase produced it.</item>
    /// <item>no exit code but a reason (<c>pipeline-missing</c>, <c>git-unavailable</c>, …) →
    /// <c>Failed</c> with no invented code: nothing ran, so there is no code to record.</item>
    /// </list>
    /// End time is recorded; duration is derived from start and end on display.
    /// </summary>
    public async Task<RunReportResult> CompleteAsync(
        string? plaintextToken,
        Guid runId,
        int? exitCode,
        string? failureReason,
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
            if (exitCode is null && failureReason is null)
            {
                return RunReportResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            // The vocabulary is closed: a reason the server does not know is a contract mismatch, not
            // something to store and hope the UI can render.
            if (failureReason is not null && !PipelineFailureReasons.IsWorkerReportable(failureReason))
            {
                return RunReportResult.Invalid(RunErrorCodes.FieldInvalid);
            }

            // A successful exit with a failure reason contradicts itself; refuse it rather than guess
            // which half the worker meant.
            if (exitCode == 0 && failureReason is not null)
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

            var target = exitCode == 0 && failureReason is null
                ? BuildRunStatus.Success
                : BuildRunStatus.Failed;
            if (!BuildRunStateMachine.CanTransition(run.Status, target))
            {
                return RunReportResult.Conflict(RunErrorCodes.InvalidTransition);
            }

            var now = DateTimeOffset.UtcNow;
            run.Status = target;
            run.ExitCode = exitCode;
            run.FailureReason = failureReason;
            run.EndedAt = now;
            await context.SaveChangesAsync(cancellationToken);

            // The run is terminal: flush the ingest tail and evict its buffer, so a run recorded
            // as finished never leaves lines behind nor keeps server memory (F-008's completed
            // path mirrors F-007's cleanup-before-report discipline on the server side).
            await runLogIngest.CompleteRunAsync(run.Id);

            logger.LogInformation(
                "Run completed. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} Status={Status} ExitCode={ExitCode} FailureReason={FailureReason}",
                workerToken.OrganizationId,
                run.Id,
                worker.Id,
                target,
                exitCode,
                failureReason);
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
                // (the friendly no-op). The re-read must bypass the change tracker: this context
                // already tracks the row from the read above, and the queued-cancel CAS
                // (ExecuteUpdate) writes the database without touching tracked state, so a tracked
                // re-read hands back the stale Queued snapshot - the Running branch never runs, the
                // cancel is dropped behind a 302, and the run executes on with no recorded cancel
                // request (the windows gate recorded exactly that race).
                var reread = await context.BuildRuns
                    .AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken);
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

        // F-015: every recorded step of every run on the page, in one read (the run page shows
        // completed, current and pending steps for a run that is still executing).
        var runIds = runs.Select(run => run.Id).ToArray();
        var stepRows = await context.BuildSteps
            .AsNoTracking()
            .Where(step => runIds.Contains(step.RunId))
            .ToListAsync(cancellationToken);
        var stepsByRun = stepRows.ToLookup(step => step.RunId);

        // F-018: whether each run's project has a Git credential assigned, in one batched read. The
        // run page needs it to say "no credential was assigned" instead of leaving a failed clone
        // unexplained; the tenant filter keeps another organization's project out of the dictionary.
        //
        // "Assigned" means the credential row really resolves, not merely that the column is set: the
        // foreign key makes a dangling reference impossible, and this condition keeps the run page's
        // wording honest even against a database that predates the constraint. A project whose reference
        // does not resolve reads as "no credential", which is what its runs actually got.
        var projectIds = runs.Select(run => run.ProjectId).Distinct().ToArray();
        var credentialAssigned = await context.Projects
            .Where(project => projectIds.Contains(project.Id))
            .Select(project => new
            {
                project.Id,
                Assigned = project.GitCredentialId != null
                    && context.GitCredentials.Any(
                        credential => credential.Id == project.GitCredentialId.Value),
            })
            .ToDictionaryAsync(row => row.Id, row => row.Assigned, cancellationToken);

        return runs.Select(run =>
        {
            var pipeline = ReadPipeline(run.PipelineSpecJson);
            return new RunView(
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
                run.PipelinePath,
                run.GitBranch,
                run.GitCommit,
                pipeline,
                RunValidation.ParseTags(run.RequiredTags),
                run.StartedAt,
                run.EndedAt,
                run.ExitCode,
                run.FailureReason,
                run.CreatedAt,
                run.UpdatedAt,
                run.TimeoutSeconds,
                run.DeadlineAt,
                BuildStepViews(pipeline, [.. stepsByRun[run.Id]]),
                credentialAssigned.TryGetValue(run.ProjectId, out var assigned) ? assigned : null);
        }).ToList();
    }

    /// <summary>
    /// The steps a run page lists (card F-015): every step of the frozen spec in declaration order,
    /// merged with the row the worker recorded for that position, or <c>Pending</c> when it has not been
    /// reached. Recorded positions the spec does not cover (a spec that no longer parses, or a step
    /// reported before a spec was frozen) are appended rather than hidden - a step that produced output
    /// must not disappear from its own page.
    /// </summary>
    private static RunStepView[] BuildStepViews(
        PipelineSpec? pipeline,
        IReadOnlyList<BuildStep> recorded)
    {
        var views = new List<RunStepView>();
        var byIndex = recorded.ToDictionary(step => step.Index);

        if (pipeline is { } spec)
        {
            for (var index = 1; index <= spec.Steps.Length; index++)
            {
                var name = spec.Steps[index - 1].Name;
                views.Add(byIndex.TryGetValue(index, out var step)
                    ? ToStepView(step)
                    : new RunStepView(null, index, name, PipelineStepStatus.Pending, null, null, null, null));
            }
        }

        views.AddRange(recorded
            .Where(step => views.All(view => view.Index != step.Index))
            .OrderBy(step => step.Index)
            .Select(ToStepView));

        return [.. views];
    }

    private static RunStepView ToStepView(BuildStep step) => new(
        step.Id,
        step.Index,
        step.Name,
        step.Status,
        step.StartedAt,
        step.EndedAt,
        step.ExitCode,
        step.FailureReason);

    /// <summary>
    /// The frozen spec as the page reads it (card F-014). A row whose JSON no longer parses is rendered
    /// as "no definition" rather than failing the page: the run's status and log are still the record.
    /// </summary>
    private static PipelineSpec? ReadPipeline(string? pipelineSpecJson) =>
        PipelineSpecJson.TryRead(pipelineSpecJson, out var spec, out _) ? spec : null;
}
