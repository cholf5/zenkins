using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Projects;
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
            entry.LeaseExpiresAt!.Value));

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

        var valid = input.Outcome == DefinitionValidationOutcomes.Valid;
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

        if (input.FailureReason is { } reason && !PipelineFailureReasons.IsWorkerReportable(reason))
        {
            return DefinitionValidationReportResult.Invalid(DefinitionValidationErrorCodes.OutcomeInvalid);
        }

        var reported = registry.TryReport(
            workerToken.OrganizationId,
            validationId,
            workerId,
            valid,
            valid ? input.SpecJson : null,
            input.Diagnostic,
            input.FailureReason,
            DateTimeOffset.UtcNow);

        if (reported.Entry is null)
        {
            return DefinitionValidationReportResult.NotFound();
        }

        return reported.ErrorCode is { } errorCode
            ? DefinitionValidationReportResult.Conflict(errorCode)
            : DefinitionValidationReportResult.Success();
    }

    private sealed record ProjectFacts(string Kind, string PipelineSource);
}
