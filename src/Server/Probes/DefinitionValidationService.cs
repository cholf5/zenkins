using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Branches;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Git;
using Zenkins.Server.Projects;
using Zenkins.Server.Runs;
using Zenkins.Server.Workers;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Projects;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Probes;

/// <summary>Outcome of one Validate request (the Pipelines tab's action).</summary>
public enum DefinitionValidationStatus
{
    /// <summary>The attempt settled: inspect <see cref="DefinitionValidationRequestResult.View"/>.</summary>
    Success = 0,

    /// <summary>The project id does not exist in the active organization (also the foreign case).</summary>
    NotFound = 1,

    /// <summary>The caller is not a member, or no organization is active.</summary>
    Forbidden = 2,

    /// <summary>The project has no inline script to validate, or the posted text broke a rule.</summary>
    InvalidInput = 3,
}

/// <summary>One Validate request's answer.</summary>
public sealed record DefinitionValidationRequestResult(
    DefinitionValidationStatus Status,
    Guid? ValidationId = null,
    DefinitionValidationView? View = null,
    string? ErrorCode = null)
{
    public bool Valid => View?.Valid == true;

    public static DefinitionValidationRequestResult Success(DefinitionValidationView view) =>
        new(DefinitionValidationStatus.Success, view.Id, view);

    public static DefinitionValidationRequestResult NotFound() => new(DefinitionValidationStatus.NotFound);

    public static DefinitionValidationRequestResult Forbidden() => new(DefinitionValidationStatus.Forbidden);

    public static DefinitionValidationRequestResult Invalid(string errorCode) =>
        new(DefinitionValidationStatus.InvalidInput, ErrorCode: errorCode);
}

/// <summary>Outcome of a Worker's validation claim.</summary>
public enum DefinitionValidationClaimStatus
{
    Success = 0,

    /// <summary>Nothing to validate (or this Worker is not eligible) - no reason disclosed, like the run claim.</summary>
    NoWork = 1,

    /// <summary>Unknown or revoked token: a bare 401.</summary>
    InvalidToken = 2,
}

/// <summary>The claim's answer: the id, the script and the lease.</summary>
public sealed record DefinitionValidationClaimResult(
    DefinitionValidationClaimStatus Status,
    DefinitionValidationClaim? Claim = null)
{
    public static DefinitionValidationClaimResult Success(DefinitionValidationEntry entry) =>
        new(DefinitionValidationClaimStatus.Success, new DefinitionValidationClaim(
            entry.Id,
            entry.Script,
            entry.LeaseExpiresAt!.Value,
            entry.Kind,
            entry.RepositoryUrl,
            entry.Branch,
            entry.Commit,
            entry.PipelinePath));

    public static DefinitionValidationClaimResult NoWork() => new(DefinitionValidationClaimStatus.NoWork);

    public static DefinitionValidationClaimResult InvalidToken() =>
        new(DefinitionValidationClaimStatus.InvalidToken);
}

/// <summary>Outcome of a Worker's validation report.</summary>
public enum DefinitionValidationReportStatus
{
    Success = 0,
    InvalidToken = 1,
    NotFound = 2,
    InvalidInput = 3,
    Conflict = 4,
}

/// <summary>One report's raw values, before validation.</summary>
public sealed record DefinitionValidationReportInput(
    string? Outcome,
    string? SpecJson,
    string? Diagnostic,
    string? FailureReason);

/// <summary>The report's answer (the status plus a stable code for the refusal kinds).</summary>
public sealed record DefinitionValidationReportResult(
    DefinitionValidationReportStatus Status,
    string? ErrorCode = null)
{
    public static DefinitionValidationReportResult Success() => new(DefinitionValidationReportStatus.Success);

    public static DefinitionValidationReportResult InvalidToken() =>
        new(DefinitionValidationReportStatus.InvalidToken);

    public static DefinitionValidationReportResult NotFound() => new(DefinitionValidationReportStatus.NotFound);

    public static DefinitionValidationReportResult Invalid(string code) =>
        new(DefinitionValidationReportStatus.InvalidInput, code);

    public static DefinitionValidationReportResult Conflict(string code) =>
        new(DefinitionValidationReportStatus.Conflict, code);
}

/// <summary>
/// Card F-022's definition-only validation: run an inline script's definition phase on a Worker and
/// return the step list or the diagnostic, <b>without executing a step and without creating a run</b>.
/// <para>
/// <b>Why not a run.</b> An ordinary queued run would take a build slot and write history for something
/// that is not a build. This reuses card I-005's no-run worker task shape instead: a transient in-memory
/// request, a claim/report pair on the worker API, a bounded caller wait and a lease. A Worker answers
/// it on its own validation loop, beside (never inside) the build loops, so a fully busy worker can
/// still validate.
/// </para>
/// <para>
/// <b>What it proves.</b> Exactly the definition phase: compile, prelude, <c>pipeline{…}</c> validation
/// and the same <c>pipeline.lua:(line,col)</c> diagnostic a repository run would produce. The Server
/// re-parses the reported spec with the same envelope a run's frozen spec goes through, so a Worker
/// cannot make a broken script look valid.
/// </para>
/// </summary>
public sealed class DefinitionValidationService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    ITenantContext tenantContext,
    DefinitionValidationRegistry registry,
    DefinitionValidationOptions options,
    WorkerService workers,
    GitCredentialService credentials,
    RunService runs,
    ILogger<DefinitionValidationService> logger)
{
    /// <summary>
    /// Validates one posted script for a project that is a Pipeline with the inline source. The posted
    /// text is used, not the stored one - the honest version of "validate before saving" (saving alone
    /// never claims a script is valid).
    /// </summary>
    public async Task<DefinitionValidationRequestResult> ValidateInlineScriptAsync(
        string actingUserId,
        Guid projectId,
        string? script,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is not { } organizationId)
        {
            return DefinitionValidationRequestResult.Forbidden();
        }

        ProjectFacts? project;
        bool isMember;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            project = await context.Projects
                .AsNoTracking()
                .Where(candidate => candidate.Id == projectId)
                .Select(candidate => new ProjectFacts(candidate.Kind, candidate.PipelineSource))
                .SingleOrDefaultAsync(cancellationToken);
            isMember = await context.OrganizationMembers.AnyAsync(
                member => member.UserId == actingUserId,
                cancellationToken);
        }

        if (project is null)
        {
            return DefinitionValidationRequestResult.NotFound();
        }

        if (!isMember)
        {
            return DefinitionValidationRequestResult.Forbidden();
        }

        // The action belongs to the inline editor. A repository project's definition is a file in its
        // repository (validated when a run defines it); a Freestyle project has no definition phase at
        // all. Neither has a script this action could own.
        if (project.Kind != ProjectKinds.Pipeline || project.PipelineSource != ProjectSources.Inline)
        {
            return DefinitionValidationRequestResult.Invalid(ProjectErrorCodes.PipelineSourceInvalid);
        }

        var scriptError = ProjectValidation.ValidateInlineScript(script, out var normalized);
        if (scriptError is not null)
        {
            return DefinitionValidationRequestResult.Invalid(scriptError);
        }

        var now = DateTimeOffset.UtcNow;
        var entry = registry.Enqueue(organizationId, projectId, normalized, now);
        logger.LogInformation(
            "Inline definition validation queued. OrganizationId={OrganizationId} ProjectId={ProjectId} ValidationId={ValidationId} ScriptBytes={ScriptBytes}",
            organizationId,
            projectId,
            entry.Id,
            System.Text.Encoding.UTF8.GetByteCount(normalized));

        // Two bounds, one promise (the I-005 discipline): wait at most the claim wait for a Worker to
        // pick it up, plus at most one lease for a claimant that may still report. A request therefore
        // always ends with a typed answer - "no worker" is one of them.
        var claimDeadline = now + options.Wait;
        var hardDeadline = claimDeadline + options.Lease;
        while (!cancellationToken.IsCancellationRequested)
        {
            var snapshot = registry.Find(entry.Id);
            if (snapshot is null)
            {
                return DefinitionValidationRequestResult.NotFound();
            }

            if (snapshot.Settled)
            {
                var view = DefinitionValidationView.From(snapshot);
                registry.Remember(view, projectId);
                logger.LogInformation(
                    "Inline definition validation settled. OrganizationId={OrganizationId} ProjectId={ProjectId} ValidationId={ValidationId} Valid={Valid} FailureReason={FailureReason}",
                    organizationId,
                    projectId,
                    entry.Id,
                    view.Valid,
                    view.FailureReason);
                return DefinitionValidationRequestResult.Success(view);
            }

            var current = DateTimeOffset.UtcNow;
            if (current >= claimDeadline)
            {
                var abandoned = registry.TryAbandon(entry.Id, current, force: current >= hardDeadline);
                if (abandoned.SettledByThisCall && abandoned.Entry is { } settled)
                {
                    var view = DefinitionValidationView.From(settled);
                    registry.Remember(view, projectId);
                    logger.LogInformation(
                        "Inline definition validation not claimed. OrganizationId={OrganizationId} ProjectId={ProjectId} ValidationId={ValidationId}",
                        organizationId,
                        projectId,
                        entry.Id);
                    return DefinitionValidationRequestResult.Success(view);
                }
            }

            try
            {
                await Task.Delay(options.PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return DefinitionValidationRequestResult.NotFound();
            }
        }

        return DefinitionValidationRequestResult.NotFound();
    }

    /// <summary>
    /// One settled validation for the Pipelines tab. A specific id (the PRG redirect's
    /// <c>?validated=</c>) is answered only when it names a settled validation of this project; a stale
    /// or forged id shows nothing rather than the project's last result. With no id, the last result is
    /// what the page shows (a direct visit).
    /// </summary>
    public DefinitionValidationView? LastValidation(Guid projectId, Guid? validationId = null)
    {
        if (validationId is { } id)
        {
            var entry = registry.Find(id);
            return entry is { Settled: true } && entry.ProjectId == projectId
                ? DefinitionValidationView.From(entry)
                : null;
        }

        return registry.LastFor(projectId);
    }

    /// <summary>
    /// Leases the next pending validation to the calling Worker. Eligibility mirrors the run claim's
    /// discipline (online, not disabled) - <b>not</b> the build-slot count: a validatation is not a
    /// build, and a Worker whose every slot is busy must still be able to answer one.
    /// </summary>
    public async Task<DefinitionValidationClaimResult> ClaimAsync(
        string? plaintextToken,
        CancellationToken cancellationToken = default)
    {
        var workerToken = await workers.AuthenticateWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || workerToken.WorkerId is not { } workerId)
        {
            return DefinitionValidationClaimResult.InvalidToken();
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var worker = await context.Workers
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == workerId, cancellationToken);
        if (worker is null || worker.Status != WorkerStatus.Online || worker.IsDisabled)
        {
            return DefinitionValidationClaimResult.NoWork();
        }

        var entry = registry.TryLease(
            workerToken.OrganizationId,
            worker.Id,
            DateTimeOffset.UtcNow,
            options.Lease);
        return entry is null ? DefinitionValidationClaimResult.NoWork() : DefinitionValidationClaimResult.Success(entry);
    }

    /// <summary>
    /// Records one Worker's answer. Both halves are re-validated: a <c>valid</c> outcome must carry a
    /// spec the run envelope accepts, an <c>invalid</c> one a bounded diagnostic. A refused or lapsed
    /// lease changes nothing.
    /// </summary>
    public async Task<DefinitionValidationReportResult> ReportAsync(
        string? plaintextToken,
        Guid validationId,
        DefinitionValidationReportInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var workerToken = await workers.AuthenticateWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || workerToken.WorkerId is not { } workerId)
        {
            return DefinitionValidationReportResult.InvalidToken();
        }

        if (!DefinitionValidationOutcomes.IsKnown(input.Outcome))
        {
            return DefinitionValidationReportResult.Invalid(DefinitionValidationErrorCodes.OutcomeInvalid);
        }

        if (input.Diagnostic is { Length: > DefinitionValidationRules.MaxDiagnosticLength })
        {
            return DefinitionValidationReportResult.Invalid(DefinitionValidationErrorCodes.DiagnosticInvalid);
        }

        // The entry's kind decides which outcomes are legal: an inline task answers valid/invalid, a
        // repository-branch task answers has-pipeline/no-pipeline. Reading it first is not a trust
        // boundary (the registry re-checks the lease); it is what keeps one task family from accepting a
        // word that belongs to the other.
        var entry = registry.Find(validationId);
        if (entry is null || entry.OrganizationId != workerToken.OrganizationId)
        {
            return DefinitionValidationReportResult.NotFound();
        }

        var valid = string.Equals(input.Outcome, DefinitionValidationOutcomes.Valid, StringComparison.Ordinal);
        if (entry.Kind == DefinitionValidationKinds.RepositoryBranch)
        {
            if (!DefinitionValidationOutcomes.IsRepositoryReportable(input.Outcome))
            {
                return DefinitionValidationReportResult.Invalid(DefinitionValidationErrorCodes.OutcomeInvalid);
            }

            // "I could not read the tree" is a reportable answer, but it must say why: the retry sweeper
            // and the operator both need the reason, and an unexplained unreadable would be a gap.
            if (string.Equals(input.Outcome, DefinitionValidationOutcomes.Unreadable, StringComparison.Ordinal)
                && input.FailureReason is null
                && string.IsNullOrWhiteSpace(input.Diagnostic))
            {
                return DefinitionValidationReportResult.Invalid(DefinitionValidationErrorCodes.DiagnosticInvalid);
            }
        }
        else
        {
            if (!DefinitionValidationOutcomes.IsInlineOutcome(input.Outcome))
            {
                return DefinitionValidationReportResult.Invalid(DefinitionValidationErrorCodes.OutcomeInvalid);
            }

            if (valid)
            {
                if (!PipelineSpecJson.TryRead(input.SpecJson, out var spec, out _) || spec is null)
                {
                    return DefinitionValidationReportResult.Invalid(DefinitionValidationErrorCodes.SpecInvalid);
                }
            }
            else if (string.IsNullOrWhiteSpace(input.Diagnostic))
            {
                return DefinitionValidationReportResult.Invalid(DefinitionValidationErrorCodes.DiagnosticInvalid);
            }
        }

        if (input.FailureReason is { } reason && !PipelineFailureReasons.IsWorkerReportable(reason))
        {
            return DefinitionValidationReportResult.Invalid(DefinitionValidationErrorCodes.OutcomeInvalid);
        }

        var reported = registry.TryReport(
            workerToken.OrganizationId,
            validationId,
            workerId,
            input.Outcome!,
            valid ? input.SpecJson : null,
            input.Diagnostic,
            input.FailureReason,
            DateTimeOffset.UtcNow);

        if (reported.Entry is null)
        {
            return DefinitionValidationReportResult.NotFound();
        }

        if (reported.ErrorCode is { } errorCode)
        {
            return DefinitionValidationReportResult.Conflict(errorCode);
        }

        // F-023: an observation is the point where a branch verdict becomes durable and, when the answer
        // is "there is a pipeline here", where the run the push asked for is actually queued. It is
        // deliberately best effort: the Worker's report has already been accepted, and a registry/queue
        // failure must not turn it into a retry the Worker cannot act on - the sweeper will ask again.
        if (reported.Entry.Kind == DefinitionValidationKinds.RepositoryBranch)
        {
            await SettleObservationAsync(reported.Entry, cancellationToken);
        }

        return DefinitionValidationReportResult.Success();
    }

    /// <summary>
    /// Queues (or joins) one branch observation for the active project (card F-023): read
    /// <paramref name="branch"/>'s tree at <paramref name="commit"/> and answer whether the project's
    /// pipeline file is there. <b>Nothing is queued here</b> - the answer decides that.
    /// <para>
    /// The project must be a multibranch repository Pipeline; anything else is refused silently (the
    /// caller is a webhook/discovery path that already checked, so a refusal is a wiring fact, not a
    /// user error). The registry memoises per (project, branch, commit), so a retry or a second push for
    /// the same commit joins the task instead of spawning another clone.
    /// </para>
    /// </summary>
    /// <returns>Whether the project was eligible, and whether this call created a new task (a joined or
    /// already-answered task is not new work).</returns>
    public async Task<(bool Enqueued, bool Created)> EnqueueBranchObservationAsync(
        Guid projectId,
        string branch,
        string commit,
        string? requiredTags,
        CancellationToken cancellationToken = default)
    {
        if (tenantContext.OrganizationId is not { } organizationId
            || !BranchIdentity.IsStorable(branch)
            || !GitCommitValidation.IsValid(commit))
        {
            return (false, false);
        }

        ProjectObservationFacts? project;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            project = await context.Projects
                .AsNoTracking()
                .Where(candidate => candidate.Id == projectId)
                .Select(candidate => new ProjectObservationFacts(
                    candidate.Kind,
                    candidate.PipelineSource,
                    candidate.RepositoryUrl,
                    candidate.PipelinePath,
                    candidate.BranchDiscovery))
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (project is null
            || !ProjectSources.HasRepository(project.Kind, project.PipelineSource)
            || string.IsNullOrWhiteSpace(project.RepositoryUrl)
            || !BranchDiscoveryModes.IsMultibranch(project.BranchDiscovery))
        {
            return (false, false);
        }

        var pipelinePath = string.IsNullOrWhiteSpace(project.PipelinePath)
            ? ProjectValidation.DefaultPipelinePath
            : project.PipelinePath;
        var (entry, created) = registry.EnqueueObservation(
            organizationId,
            projectId,
            project.RepositoryUrl,
            branch,
            commit,
            pipelinePath,
            requiredTags ?? string.Empty,
            DateTimeOffset.UtcNow);

        logger.LogInformation(
            "Branch observation requested. OrganizationId={OrganizationId} ProjectId={ProjectId} Branch={Branch} Commit={Commit} ValidationId={ValidationId} Created={Created}",
            organizationId,
            projectId,
            branch,
            commit,
            entry.Id,
            created);
        return (true, created);
    }

    /// <summary>
    /// The credential material for one branch observation, addressed by the task's id (card F-023). The
    /// same answers and secrecy as the run and probe fetches: a live lease held by this Worker, one
    /// bodyless 404 for every miss, <c>no-store</c> on success. The lease behind it is the definition
    /// task's, never a run's or a probe's.
    /// </summary>
    public async Task<GitCredentialFetchResult> FetchMaterialForValidationAsync(
        string? plaintextToken,
        Guid validationId,
        CancellationToken cancellationToken = default)
    {
        var workerToken = await workers.AuthenticateWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || workerToken.WorkerId is not { } workerId)
        {
            return GitCredentialFetchResult.InvalidToken();
        }

        var entry = registry.Find(validationId);
        var now = DateTimeOffset.UtcNow;
        if (entry is null
            || entry.OrganizationId != workerToken.OrganizationId
            || entry.Kind != DefinitionValidationKinds.RepositoryBranch
            || entry.WorkerId != workerId
            || entry.LeaseExpiresAt is not { } expiry
            || expiry <= now)
        {
            logger.LogInformation(
                "Definition credential request resolved no material. ValidationId={ValidationId} WorkerId={WorkerId}",
                validationId,
                workerId);
            return GitCredentialFetchResult.NotFound();
        }

        return await credentials.FetchMaterialForProjectAsync(
            entry.ProjectId,
            new GitCredentialFetchScope(workerId, ValidationId: entry.Id),
            cancellationToken);
    }

    /// <summary>
    /// The one place a branch observation becomes durable (card F-023): record the verdict with the
    /// commit it is evidence about, clear the pending marker, and - only for <c>has-pipeline</c> - queue
    /// the run the push asked for, pinned to that commit.
    /// <para>
    /// <b>A failed read is not a verdict.</b> When the Worker could not check out the commit (or the
    /// registry settled the task as no-worker), the branch keeps its previous state and the pending
    /// marker stays; the retry sweeper asks again after a backoff. Nothing here ever writes
    /// <c>no-pipeline</c> without an observation, and nothing here deletes anything.
    /// </para>
    /// <para>
    /// The queue write goes through <see cref="RunService.QueueWebhookRunAsync"/>, so the run is the same
    /// run a push queues: the same F-028/F-029 gate, the same snapshots, the same numbering. A refusal
    /// (disabled project, full queue) is logged and the delivery is not retried - the project gate owns
    /// that decision.
    /// </para>
    /// </summary>
    private async Task SettleObservationAsync(
        DefinitionValidationEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Branch is not { Length: > 0 } branch || entry.Commit is not { Length: > 0 } commit)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            if (!DefinitionValidationOutcomes.IsRepositoryOutcome(entry.Outcome))
            {
                // The task settled without a verdict (a git failure, or no Worker). Keep the pending
                // marker and move its retry clock; the branch's state is untouched.
                await BranchService.NoteObservationAttemptAsync(
                    context, entry.ProjectId, branch, commit, now, cancellationToken);
                logger.LogInformation(
                    "Branch observation produced no verdict. OrganizationId={OrganizationId} ProjectId={ProjectId} Branch={Branch} Commit={Commit} FailureReason={FailureReason}",
                    entry.OrganizationId,
                    entry.ProjectId,
                    branch,
                    commit,
                    entry.FailureReason);
                return;
            }

            var hasPipeline = string.Equals(
                entry.Outcome, DefinitionValidationOutcomes.HasPipeline, StringComparison.Ordinal);
            var state = hasPipeline
                ? BranchPipelineStates.HasPipeline
                : BranchPipelineStates.NoPipeline;

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await BranchService.RecordObservationAsync(
                context,
                entry.OrganizationId,
                entry.ProjectId,
                branch,
                state,
                commit,
                now,
                cancellationToken);

            var queuedForBranch = false;
            if (hasPipeline)
            {
                var queued = await runs.QueueWebhookRunAsync(
                    context,
                    entry.ProjectId,
                    branch,
                    commit,
                    RunValidation.ParseTags(entry.RequiredTags),
                    cancellationToken);
                queuedForBranch = queued.Status == RunOperationStatus.Success;
                if (queued.Status != RunOperationStatus.Success)
                {
                    logger.LogInformation(
                        "Branch observation queued no run. OrganizationId={OrganizationId} ProjectId={ProjectId} Branch={Branch} Commit={Commit} Status={Status}",
                        entry.OrganizationId,
                        entry.ProjectId,
                        branch,
                        commit,
                        queued.Status);
                }
            }

            // Card F-033: a refused queue leaves the pending marker in place. Clearing it would erase the only
            // durable record of "this branch has a pipeline here and no run was queued" - the marker carries the
            // requirement the run was waiting on.
            if (queuedForBranch)
            {
                await BranchService.ClearPendingObservationAsync(
                    context, entry.ProjectId, branch, commit, now, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Branch observation recorded. OrganizationId={OrganizationId} ProjectId={ProjectId} Branch={Branch} Commit={Commit} State={State}",
                entry.OrganizationId,
                entry.ProjectId,
                branch,
                commit,
                state);
        }
        catch (Exception failure)
        {
            // The Worker's report has already been accepted; a failure here is ours to retry, not its
            // to resend. The pending marker survives, so the sweeper asks again.
            logger.LogWarning(
                failure,
                "Branch observation not recorded. OrganizationId={OrganizationId} ProjectId={ProjectId} Branch={Branch} Commit={Commit}",
                entry.OrganizationId,
                entry.ProjectId,
                branch,
                commit);
        }
    }

    private sealed record ProjectFacts(string Kind, string PipelineSource);

    private sealed record ProjectObservationFacts(
        string Kind,
        string PipelineSource,
        string? RepositoryUrl,
        string? PipelinePath,
        string BranchDiscovery);
}
