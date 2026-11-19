using Zenkins.Server.Localization;
using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Artifacts;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Runs;

/// <summary>
/// One project's retention policy (card F-038): the newest N builds per branch and/or the builds younger than D
/// days. Both values are nullable because <b>unset is a rule</b>.
/// </summary>
public sealed record RetentionPolicy(int? Keep, int? Days)
{
    /// <summary>No rule is set: the project keeps every build, exactly as before card F-038.</summary>
    public bool IsUnset => Keep is null && Days is null;

    /// <summary>Whether any rule is set at all (the sweeper's candidate-project predicate).</summary>
    public bool IsSet => !IsUnset;
}

/// <summary>One build the next sweep would delete, with the sentence that says why.</summary>
public sealed record RetentionCandidate(Guid RunId, int Number, string Branch, string Reason);

/// <summary>
/// What a policy means for one project's current history - the settings card's counts and the sweeper's work list.
/// <para>
/// <see cref="WouldDelete"/> is not "the policy is tight": it is the number of rows a sweep would remove if it ran
/// now, so the preview and the sweep can never disagree about what "the next sweep" means - they are the same
/// evaluation.
/// </para>
/// </summary>
public sealed record RetentionEvaluation(
    RetentionPolicy Policy,
    int TotalRuns,
    int PinnedRuns,
    int NonTerminalRuns,
    int NewestPerBranch,
    IReadOnlyList<RetentionCandidate> Candidates)
{
    /// <summary>How many builds survive the policy (everything the sweep would not delete).</summary>
    public int Retained => TotalRuns - Candidates.Count;

    /// <summary>How many builds the next sweep would delete.</summary>
    public int WouldDelete => Candidates.Count;
}

/// <summary>What a sweep (or one project's evaluation) actually did.</summary>
/// <param name="ProjectsEvaluated">Projects with a policy that were looked at.</param>
/// <param name="Deleted">Runs whose rows and bytes are gone.</param>
/// <param name="Failed">Runs whose deletion threw; logged with the run id, retried on the next sweep.</param>
public sealed record RetentionSweepResult(int ProjectsEvaluated, int Deleted, int Failed)
{
    public static readonly RetentionSweepResult None = new(0, 0, 0);
}

/// <summary>Outcome of a retention policy save (card F-038).</summary>
public enum RetentionMutationStatus
{
    Success = 0,
    NotFound = 1,
    Forbidden = 2,
    InvalidInput = 3,
}

/// <summary>One refusal, with the sentence the settings card renders.</summary>
public sealed record RetentionMutationResult(
    RetentionMutationStatus Status,
    RetentionPolicy? Policy = null,
    string? ErrorCode = null,
    string? Message = null)
{
    public static RetentionMutationResult Success(RetentionPolicy policy) =>
        new(RetentionMutationStatus.Success, policy);

    public static RetentionMutationResult NotFound() => new(RetentionMutationStatus.NotFound);

    public static RetentionMutationResult Forbidden(string message) =>
        new(RetentionMutationStatus.Forbidden, null, null, message);

    public static RetentionMutationResult Invalid(string errorCode, string message) =>
        new(RetentionMutationStatus.InvalidInput, null, errorCode, message);
}

/// <summary>Outcome of a pin toggle (card F-038).</summary>
public enum RunPinStatus
{
    Success = 0,
    NotFound = 1,
    Forbidden = 2,
}

/// <summary>One pin toggle, with the state it left behind.</summary>
public sealed record RunPinResult(RunPinStatus Status, bool Pinned = false, string? Message = null)
{
    public static RunPinResult Success(bool pinned) => new(RunPinStatus.Success, pinned);

    public static RunPinResult NotFound() => new(RunPinStatus.NotFound);

    public static RunPinResult Forbidden(string message) => new(RunPinStatus.Forbidden, false, message);
}

/// <summary>The stable codes the settings card renders for a refused retention save (card F-038).</summary>
public static class RetentionErrorCodes
{
    /// <summary>The keep count is not a number.</summary>
    public const string KeepInvalid = "keep-invalid";

    /// <summary>The day window is not a number.</summary>
    public const string DaysInvalid = "days-invalid";

    /// <summary>One of the two values is outside what this product will store.</summary>
    public const string OutOfRange = "retention-out-of-range";

    /// <summary>The sentence for a code, or null when the code is not one of ours.</summary>
    /// <summary>The message the settings card renders for a code, in the UI's language.</summary>
    public static string? MessageFor(string? code, Localizer L10n) => code switch
    {
        KeepInvalid => L10n["The keep count must be a whole number of builds (blank keeps every build)."],
        DaysInvalid => L10n["The day window must be a whole number of days (blank keeps every build)."],
        OutOfRange =>
            L10n["The keep count must be between 0 and {0} and the day window between 0 and {1}.", RetentionLimits.MaxKeep, RetentionLimits.MaxDays],
        _ => null,
    };
}

/// <summary>The bounds a retention policy may carry (card F-038).</summary>
public static class RetentionLimits
{
    /// <summary>The most builds one branch may be told to keep. Zero is legal: keep only the newest.</summary>
    public const int MaxKeep = 1000;

    /// <summary>The widest day window, in days (ten years).</summary>
    public const int MaxDays = 3650;
}

/// <summary>
/// Card F-038: the retention policy, the candidate query behind it, and the one deletion path that removes a run
/// for real - rows and bytes together.
/// <para>
/// <b>Two rules, unioned.</b> A build survives if it is within the newest <see cref="Project.RetentionKeep"/> of
/// its branch <b>or</b> younger than <see cref="Project.RetentionDays"/>; it is deleted only when both rules say so.
/// With one rule unset only the other decides, and with both unset nothing is deleted - the whole sweep is a no-op,
/// which is how every project behaved before this card.
/// </para>
/// <para>
/// <b>Three things are never deleted</b>, and each of them is a separate reason so the page can say which:
/// a pinned build (both rules exempt it), a run that is still <c>Queued</c> or <c>Running</c>, and the newest build
/// of each branch (a project always keeps its last result). A run whose downstream chain obligation is still
/// pending is also spared: F-026's "exactly once" would otherwise lose the trigger that made the run history.
/// </para>
/// <para>
/// <b>Deletion is rows + bytes.</b> The artifact store directory is removed after the rows commit, so a crash
/// between the two leaves an orphaned directory that <see cref="ArtifactSweeper"/> already removes on its own
/// pass - the two halves are both idempotent, which is what makes a partial failure a retry and not a leak. The
/// worker's <c>work/{run-id}</c> tree is the worker's own disk (the Server cannot reach it): it is removed by the
/// worker's age-based sweep (<c>ZENKINS_WORK_RETENTION_HOURS</c>).
/// </para>
/// <para>
/// <b>The tenant boundary is the request's.</b> Reads and deletes go through the tenant-filtered context, so a
/// foreign project or run id is the same answer as a missing one. The background sweep runs in the system scope and
/// names each row's organization explicitly - it is the one caller that must see every tenant.
/// </para>
/// </summary>
public sealed class RetentionService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    ITenantContext tenantContext,
    ISystemDataScope systemDataScope,
    ArtifactStore artifactStore,
    Localizer L10n,
    ILogger<RetentionService> logger)
{
    // ---------------------------------------------------------------------
    // Policy read / save
    // ---------------------------------------------------------------------

    /// <summary>One project's policy, or <see langword="null"/> for a foreign or missing project.</summary>
    public async Task<RetentionPolicy?> GetPolicyAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Projects
            .AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => new RetentionPolicy(project.RetentionKeep, project.RetentionDays))
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Saves the two rules. Members may edit, like every other project setting; a foreign or missing project is a
    /// 404 before any role is considered, and a non-member is 403.
    /// </summary>
    public async Task<RetentionMutationResult> SavePolicyAsync(
        string actingUserId,
        Guid projectId,
        int? keep,
        int? days,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is null)
        {
            return RetentionMutationResult.Forbidden(
                "You are not a member of an organization, so there is no project to configure.");
        }

        if (keep is < 0 || days is < 0 || keep > RetentionLimits.MaxKeep || days > RetentionLimits.MaxDays)
        {
            return RetentionMutationResult.Invalid(
                RetentionErrorCodes.OutOfRange,
                RetentionErrorCodes.MessageFor(RetentionErrorCodes.OutOfRange, L10n)!);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            return RetentionMutationResult.NotFound();
        }

        if (!await context.OrganizationMembers.AnyAsync(
                member => member.UserId == actingUserId,
                cancellationToken))
        {
            return RetentionMutationResult.Forbidden("You are not a member of the active organization.");
        }

        project.RetentionKeep = keep;
        project.RetentionDays = days;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Retention policy saved. OrganizationId={OrganizationId} ProjectId={ProjectId} Keep={Keep} Days={Days} ByUserId={UserId}",
            project.OrganizationId,
            project.Id,
            keep,
            days,
            actingUserId);

        return RetentionMutationResult.Success(new RetentionPolicy(keep, days));
    }

    // ---------------------------------------------------------------------
    // Pin
    // ---------------------------------------------------------------------

    /// <summary>
    /// Pins or unpins one run (card F-038). A Member action with the same guards as the run page's other actions:
    /// the tenant filter makes a foreign run a 404 (no existence oracle), a non-member gets 403, and only the
    /// <see cref="BuildRun.Pinned"/> column moves. Pinning never changes the run's status, and unpinning never
    /// deletes - the next sweep decides.
    /// </summary>
    public async Task<RunPinResult> SetPinnedAsync(
        string actingUserId,
        Guid runId,
        bool pinned,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is null)
        {
            return RunPinResult.Forbidden("You are not a member of an organization, so there is no run to pin.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await context.OrganizationMembers.AnyAsync(
                member => member.UserId == actingUserId,
                cancellationToken))
        {
            return RunPinResult.Forbidden("You are not a member of the active organization.");
        }

        var run = await context.BuildRuns.SingleOrDefaultAsync(
            candidate => candidate.Id == runId,
            cancellationToken);
        if (run is null)
        {
            // Foreign or unknown id: the same 404, no existence oracle.
            return RunPinResult.NotFound();
        }

        if (run.Pinned != pinned)
        {
            run.Pinned = pinned;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Run pin changed. OrganizationId={OrganizationId} RunId={RunId} Pinned={Pinned} ByUserId={UserId}",
                run.OrganizationId,
                run.Id,
                pinned,
                actingUserId);
        }

        return RunPinResult.Success(run.Pinned);
    }

    // ---------------------------------------------------------------------
    // Evaluation (the preview and the sweep share this)
    // ---------------------------------------------------------------------

    /// <summary>
    /// What <paramref name="policy"/> means for this project's history, evaluated at <paramref name="now"/>.
    /// <see langword="null"/> for a foreign or missing project.
    /// </summary>
    public async Task<RetentionEvaluation?> EvaluateAsync(
        Guid projectId,
        RetentionPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var organizationId = await context.Projects
            .AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => (Guid?)project.OrganizationId)
            .SingleOrDefaultAsync(cancellationToken);
        if (organizationId is null)
        {
            return null;
        }

        return await EvaluateCoreAsync(context, organizationId.Value, projectId, policy, now, cancellationToken);
    }

    /// <summary>
    /// The candidate query and the rules, over an already-open context (so the sweeper can evaluate and delete in
    /// one unit of work). Bounded to one project.
    /// </summary>
    /// <remarks>
    /// The organization is a parameter, not the ambient tenant: the daily sweep runs in the system scope (where the
    /// query filters answer nothing) and the after-terminal evaluation runs in a request scope. Naming the row's
    /// organization explicitly - it comes from a row the caller already resolved - gives both callers the same
    /// query, and the boundary is still not the client's to choose.
    /// </remarks>
    private static async Task<RetentionEvaluation> EvaluateCoreAsync(
        ZenkinsDbContext context,
        Guid organizationId,
        Guid projectId,
        RetentionPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var runs = await context.BuildRuns
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(run => run.OrganizationId == organizationId && run.ProjectId == projectId)
            .Select(run => new
            {
                run.Id,
                run.Number,
                run.Status,
                run.Pinned,
                run.CreatedAt,
                run.GitBranch,
            })
            .ToListAsync(cancellationToken);

        // F-026: a run whose chain obligation has not been dispatched yet is not history to trim - deleting it
        // would cascade the pending row away and lose a downstream trigger that was promised exactly once.
        var pendingUpstreamRuns = await context.OutboxMessages
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(message => message.OrganizationId == organizationId
                && message.Status == OutboxStatuses.Pending)
            .Select(message => message.UpstreamRunId)
            .ToListAsync(cancellationToken);
        var owed = pendingUpstreamRuns.ToHashSet();

        var candidates = new List<RetentionCandidate>();
        var newestPerBranch = 0;

        if (policy.IsUnset)
        {
            // Both rules unset: nothing is a candidate. The sweep is a no-op by construction, not by luck.
            return new RetentionEvaluation(
                policy,
                runs.Count,
                runs.Count(run => run.Pinned),
                runs.Count(run => !IsTerminal(run.Status)),
                0,
                candidates);
        }

        var terminal = runs
            .Where(run => IsTerminal(run.Status))
            .OrderByDescending(run => run.Number)
            .ToList();

        foreach (var branch in terminal.GroupBy(run => run.GitBranch, StringComparer.Ordinal))
        {
            var rank = 0;
            foreach (var run in branch)
            {
                // The newest build of a branch is that branch's last result, whatever the rules say - and the
                // project's newest build is the newest of some branch, so a project can never be emptied.
                if (rank++ == 0)
                {
                    newestPerBranch++;
                    continue;
                }

                if (run.Pinned)
                {
                    continue;
                }

                if (owed.Contains(run.Id))
                {
                    continue;
                }

                // rank starts at 1 for the newest build of the branch, so this run's position is rank - 1.
                var keptByCount = policy.Keep is { } keep && rank - 1 < keep;
                var keptByAge = policy.Days is { } days
                    && (now - run.CreatedAt) <= TimeSpan.FromDays(days);
                if (keptByCount || keptByAge)
                {
                    continue;
                }

                var reasons = new List<string>(2);
                if (policy.Keep is { } keepText)
                {
                    reasons.Add($"beyond the newest {keepText} of branch '{run.GitBranch}'");
                }

                if (policy.Days is { } daysText)
                {
                    reasons.Add($"older than {daysText} day(s)");
                }

                candidates.Add(new RetentionCandidate(
                    run.Id,
                    run.Number,
                    run.GitBranch,
                    string.Join(" and ", reasons)));
            }
        }

        return new RetentionEvaluation(
            policy,
            runs.Count,
            runs.Count(run => run.Pinned),
            runs.Count(run => !IsTerminal(run.Status)),
            newestPerBranch,
            candidates);
    }

    /// <summary>Whether a run status is terminal (the closed set the run state machine defines).</summary>
    private static bool IsTerminal(string status) =>
        status is BuildRunStatus.Success or BuildRunStatus.Failed or BuildRunStatus.Canceled
            or BuildRunStatus.Lost;

    // ---------------------------------------------------------------------
    // Sweeps
    // ---------------------------------------------------------------------

    /// <summary>
    /// Evaluates one project's policy and deletes what it names (card F-038's "after each run reaches a terminal
    /// state"). Missing or unset policy is a no-op. Never throws for one run's failure: the run id is logged and the
    /// next sweep retries.
    /// </summary>
    public async Task<RetentionSweepResult> SweepProjectAsync(
        Guid projectId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var project = await context.Projects
            .AsNoTracking()
            .Where(candidate => candidate.Id == projectId)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.OrganizationId,
                candidate.RetentionKeep,
                candidate.RetentionDays,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (project is null)
        {
            return RetentionSweepResult.None;
        }

        var policy = new RetentionPolicy(project.RetentionKeep, project.RetentionDays);
        if (policy.IsUnset)
        {
            return RetentionSweepResult.None;
        }

        var evaluation = await EvaluateCoreAsync(
            context, project.OrganizationId, projectId, policy, now, cancellationToken);
        return await DeleteCandidatesAsync(context, project.OrganizationId, evaluation, cancellationToken);
    }

    /// <summary>
    /// The daily sweep: every project that has a policy, across every tenant (the system scope is what lets it
    /// see them; the rows' own organization is what authorizes each delete).
    /// </summary>
    public async Task<RetentionSweepResult> SweepAllAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        using var scope = systemDataScope.BeginScope();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var projects = await context.SystemQueryAsync<Project>(
            project => project.RetentionKeep != null || project.RetentionDays != null,
            cancellationToken);

        var evaluated = 0;
        var deleted = 0;
        var failed = 0;
        foreach (var project in projects)
        {
            evaluated++;
            var policy = new RetentionPolicy(project.RetentionKeep, project.RetentionDays);
            try
            {
                var evaluation = await EvaluateCoreAsync(
                    context, project.OrganizationId, project.Id, policy, now, cancellationToken);
                var result = await DeleteCandidatesAsync(context, project.OrganizationId, evaluation, cancellationToken);
                deleted += result.Deleted;
                failed += result.Failed;
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                // One project's failure never stops the sweep: the next tick finds the same candidates.
                failed++;
                logger.LogError(
                    failure,
                    "Retention sweep failed for one project; the next sweep will retry it. OrganizationId={OrganizationId} ProjectId={ProjectId}",
                    project.OrganizationId,
                    project.Id);
            }
        }

        if (deleted > 0 || failed > 0)
        {
            logger.LogInformation(
                "Retention sweep finished. Projects={Projects} Deleted={Deleted} Failed={Failed}",
                evaluated,
                deleted,
                failed);
        }

        return new RetentionSweepResult(evaluated, deleted, failed);
    }

    private async Task<RetentionSweepResult> DeleteCandidatesAsync(
        ZenkinsDbContext context,
        Guid organizationId,
        RetentionEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        var deleted = 0;
        var failed = 0;
        foreach (var candidate in evaluation.Candidates)
        {
            try
            {
                if (await DeleteRunAsync(context, organizationId, candidate.RunId, cancellationToken))
                {
                    deleted++;
                    logger.LogInformation(
                        "Build deleted by retention. OrganizationId={OrganizationId} RunId={RunId} Number={Number} Keep={Keep} Days={Days} Reason={Reason}",
                        organizationId,
                        candidate.RunId,
                        candidate.Number,
                        evaluation.Policy.Keep,
                        evaluation.Policy.Days,
                        candidate.Reason);
                }
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                // A partial failure is logged with the run id and retried by the next sweep; it is never fatal to
                // the pass around it (the other candidates still go).
                failed++;
                logger.LogError(
                    failure,
                    "Retention could not delete one build; the next sweep will retry it. OrganizationId={OrganizationId} RunId={RunId}",
                    organizationId,
                    candidate.RunId);
            }
        }

        return new RetentionSweepResult(1, deleted, failed);
    }

    // ---------------------------------------------------------------------
    // The one deletion path (shared with project deletion's bytes half)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Removes one run for real: the rows (the run and everything that cascades from it) and the artifact store
    /// directory. Idempotent - a run that is already gone answers <see langword="false"/> and the caller's sweep
    /// moves on.
    /// <para>
    /// <b>References that carry no foreign key are cleared, not left dangling.</b> A webhook delivery, a schedule
    /// occurrence, a change ledger row, an outbox row's downstream run and a re-run's origin all keep their row -
    /// they are dedupe and audit records - but stop naming a run that no longer exists, so no page can render a
    /// link to a deleted build (AC-5's "no ghosts").
    /// </para>
    /// </summary>
    public async Task<bool> DeleteRunAsync(
        ZenkinsDbContext context,
        Guid organizationId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var run = await context.BuildRuns
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == runId && candidate.OrganizationId == organizationId,
                cancellationToken);
        if (run is null)
        {
            return false;
        }

        // Every statement names the organization the run belongs to (the row above proved the caller may touch
        // it), and ignores the ambient filter for the same reason the candidate query does: the daily sweep runs
        // in the system scope. Inside a request the filter and this predicate agree.
        await context.Set<WebhookDelivery>()
            .IgnoreQueryFilters()
            .Where(delivery => delivery.RunId == runId && delivery.OrganizationId == organizationId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(delivery => delivery.RunId, (Guid?)null),
                cancellationToken);
        await context.ScheduleOccurrences
            .IgnoreQueryFilters()
            .Where(occurrence => occurrence.RunId == runId && occurrence.OrganizationId == organizationId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(occurrence => occurrence.RunId, (Guid?)null),
                cancellationToken);
        await context.ChangeTriggers
            .IgnoreQueryFilters()
            .Where(trigger => trigger.RunId == runId && trigger.OrganizationId == organizationId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(trigger => trigger.RunId, (Guid?)null),
                cancellationToken);
        await context.OutboxMessages
            .IgnoreQueryFilters()
            .Where(message => message.DownstreamRunId == runId && message.OrganizationId == organizationId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(message => message.DownstreamRunId, (Guid?)null),
                cancellationToken);
        await context.BuildRuns
            .IgnoreQueryFilters()
            .Where(later => later.TriggeredByRunId == runId && later.OrganizationId == organizationId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(later => later.TriggeredByRunId, (Guid?)null),
                cancellationToken);
        await context.BuildRuns
            .IgnoreQueryFilters()
            .Where(later => later.RebuiltFromRunId == runId && later.OrganizationId == organizationId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(later => later.RebuiltFromRunId, (Guid?)null),
                cancellationToken);

        // The rows: steps, logs, artifacts, outbox obligations and API idempotency keys all cascade with the run.
        context.BuildRuns.Remove(run);
        await context.SaveChangesAsync(cancellationToken);

        // The bytes, after the rows: a crash in between leaves an orphaned directory that the artifact sweeper
        // already removes on its own pass, so the two halves never have to be transactional with each other.
        TryDeleteArtifactDirectory(organizationId, runId);
        return true;
    }

    private void TryDeleteArtifactDirectory(Guid organizationId, Guid runId)
    {
        try
        {
            artifactStore.DeleteRunDirectory(organizationId, runId);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Never fatal: the rows are gone, so the directory is now an orphan and F-016's sweeper removes it.
            logger.LogWarning(
                failure,
                "The artifact directory of a deleted build could not be removed; the artifact sweeper will retry it. OrganizationId={OrganizationId} RunId={RunId}",
                organizationId,
                runId);
        }
    }
}
