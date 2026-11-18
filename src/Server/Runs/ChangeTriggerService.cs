using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;
using Zenkins.Server.Webhooks;

namespace Zenkins.Server.Polling;

/// <summary>
/// What one change trigger did (card F-025): a run queued, the change already triggered, or the queue's own
/// refusal.
/// </summary>
/// <param name="Outcome">A <see cref="PollOutcomes"/> value (<c>queued</c>, <c>duplicate</c>) or the refusal
/// code the queue answered with (<c>queue-full</c>, <c>project-disabled</c>).</param>
/// <param name="RunId">The run the change queued, or - for a duplicate - the run the earlier trigger queued.</param>
/// <param name="TriggeredBy">The trigger type of the <b>first</b> trigger for this change (a duplicate knows
/// who got there first: <c>Webhook</c> or <c>Poll</c>).</param>
public sealed record ChangeTriggerResult(
    string Outcome,
    Guid? RunId = null,
    string? TriggeredBy = null,
    RunOperationStatus Status = RunOperationStatus.Success)
{
    /// <summary>A run was queued for this change.</summary>
    public static ChangeTriggerResult Queued(Guid runId) => new(PollOutcomes.Queued, runId);

    /// <summary>This change was already triggered (by the other path, or an earlier tick).</summary>
    public static ChangeTriggerResult Duplicate(Guid? runId, string? triggeredBy) =>
        new(PollOutcomes.Duplicate, runId, triggeredBy);

    /// <summary>
    /// The queue refused it. <paramref name="status"/> keeps the queue's own distinction between the project
    /// gate's refusal (F-028/F-029: a recorded, accepted delivery) and a request that was never valid (a
    /// rollback, no record, a retry can build) - the webhook has always answered those two differently.
    /// </summary>
    public static ChangeTriggerResult Refused(string code, RunOperationStatus status = RunOperationStatus.Refused) =>
        new(code, Status: status);
}

/// <summary>
/// The <b>one</b> change-trigger path (card F-025): every source that can observe "this (project, branch,
/// commit) changed" calls this, so the webhook, the poller - and later the schedule and the upstream
/// reactor - cannot drift apart on deduplication, on the pinned commit or on the queue's own gate.
/// <para>
/// <b>Deduplication is a row, not a convention.</b> <see cref="ChangeTrigger"/> is keyed by
/// <c>(project, branch, commit)</c> under a unique index, and this method claims that row before it queues.
/// Two triggers for the same commit - a push and a poll, or two concurrent polls - therefore produce exactly
/// one run: the loser reads the winner's row and answers <c>duplicate</c> with the winner's run id. F-017's
/// per-delivery dedupe is a different question ("have I seen this delivery id?") and stays where it is; the
/// two keys answer different things and both are needed.
/// </para>
/// <para>
/// <b>Written with the run.</b> The ledger row and the run share the caller's transaction (the delivery's, or
/// this service's own when the poller calls it). A refused queue - a disabled project, a full queue - removes
/// the row again, so the next poll or push can still build that commit: nothing is ever claimed about a run
/// that does not exist.
/// </para>
/// </summary>
public sealed class ChangeTriggerService(
    RunService runs,
    ILogger<ChangeTriggerService> logger)
{
    /// <summary>
    /// Triggers one change: claim the ledger row, then queue the run through the queue's one gate.
    /// </summary>
    /// <param name="context">The caller's unit of work (its tenant is the change's tenant).</param>
    /// <param name="projectId">The project the change belongs to.</param>
    /// <param name="branch">The branch the change was observed on.</param>
    /// <param name="commit">The commit to build (pinned on the run, exactly like a webhook push).</param>
    /// <param name="triggerType">A <see cref="BuildRunTriggerType"/> value: which path observed it.</param>
    /// <param name="requiredTags">The tags the trigger requires of a worker (a webhook's, or none).</param>
    public async Task<ChangeTriggerResult> TriggerAsync(
        ZenkinsDbContext context,
        Guid projectId,
        string branch,
        string commit,
        string triggerType,
        IReadOnlyList<string> requiredTags,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requiredTags);

        var organizationId = context.CurrentOrganizationId;
        if (organizationId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A change trigger needs an active organization: the caller selects the tenant (the webhook "
                + "row, or the sweeper's scope) before it triggers.");
        }

        // Own transaction when the caller has none (the poller); otherwise the caller's, so a delivery and
        // the run it queued still commit together.
        var ownsTransaction = context.Database.CurrentTransaction is null;
        await using var ownedTransaction = ownsTransaction
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var transaction = context.Database.CurrentTransaction!;

        // 1. Claim (project, branch, commit). The unique index is the real guard: two concurrent triggers
        //    both pass the read below, and the loser lands in the catch.
        var existing = await FindAsync(context, projectId, branch, commit, cancellationToken);
        if (existing is not null)
        {
            return ChangeTriggerResult.Duplicate(existing.RunId, existing.TriggerType);
        }

        var trigger = new ChangeTrigger
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            Branch = branch,
            Commit = commit,
            TriggerType = triggerType,
        };
        context.ChangeTriggers.Add(trigger);

        await transaction.CreateSavepointAsync("change_trigger_claim", cancellationToken);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.ReleaseSavepointAsync("change_trigger_claim", cancellationToken);
        }
        catch (DbUpdateException failure) when (DatabaseErrors.IsUniqueViolation(failure, "ChangeTriggers"))
        {
            // Another trigger won the race. Roll back to the savepoint (PostgreSQL aborts the whole
            // transaction otherwise), then read the winner outside the failed insert.
            await transaction.RollbackToSavepointAsync("change_trigger_claim", cancellationToken);
            context.Entry(trigger).State = EntityState.Detached;

            var winner = await FindAsync(context, projectId, branch, commit, cancellationToken);
            if (winner is null)
            {
                // The winner's row vanished between the violation and this read: nothing to report about.
                if (ownsTransaction)
                {
                    await ownedTransaction!.CommitAsync(cancellationToken);
                }

                return ChangeTriggerResult.Duplicate(null, triggerType);
            }

            return ChangeTriggerResult.Duplicate(winner.RunId, winner.TriggerType);
        }

        // 2. Queue the run through the one queue path (the same F-028/F-029 gate, snapshots and numbering
        //    every trigger source uses).
        var queued = await runs.QueueWebhookRunAsync(
            context,
            projectId,
            branch,
            commit,
            requiredTags,
            cancellationToken,
            triggerType);

        if (queued.Status != RunOperationStatus.Success || queued.RunId is null)
        {
            var code = queued.FieldErrors?.FirstOrDefault()?.Code ?? PollOutcomes.Unchanged;
            logger.LogInformation(
                "Change trigger refused by the queue. OrganizationId={OrganizationId} ProjectId={ProjectId} Branch={Branch} Commit={Commit} TriggerType={TriggerType} Refusal={Refusal}",
                organizationId,
                projectId,
                branch,
                commit,
                triggerType,
                code);

            if (ownsTransaction)
            {
                // Own transaction: rolling it back removes the ledger row with it, so a later tick or push
                // can still build this commit.
                await ownedTransaction!.RollbackAsync(cancellationToken);
                return ChangeTriggerResult.Refused(code);
            }

            // The caller's transaction owns the delivery record: remove only the ledger row and save, so
            // the delivery still commits and a later trigger can retry the commit.
            context.ChangeTriggers.Remove(trigger);
            await context.SaveChangesAsync(cancellationToken);
            return ChangeTriggerResult.Refused(code);
        }

        trigger.RunId = queued.RunId;
        await context.SaveChangesAsync(cancellationToken);

        if (ownsTransaction)
        {
            await ownedTransaction!.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Change triggered a run. OrganizationId={OrganizationId} ProjectId={ProjectId} Branch={Branch} Commit={Commit} TriggerType={TriggerType} RunId={RunId}",
            organizationId,
            projectId,
            branch,
            commit,
            triggerType,
            queued.RunId);

        return ChangeTriggerResult.Queued(queued.RunId.Value);
    }

    /// <summary>
    /// A <b>webhook</b> delivery's trigger (card F-025): queues the run exactly as F-017 always has, but first
    /// answers "did a poll already trigger this commit?" from the shared ledger - and records the ledger row
    /// when no poll did, so a poll that observes the same commit afterwards answers <c>duplicate</c>.
    /// <para>
    /// <b>Why the webhook keeps its own queue call.</b> A delivery is already deduplicated by F-017's
    /// per-delivery key (the delivery id or the body hash): two pushes that carry the same commit under two
    /// delivery ids are two deliveries and queue two runs today, and that is a contract the webhook tests pin.
    /// The card's rule is the <em>cross-path</em> one - polling must never add a second run for a commit a push
    /// already built - so the ledger decides exactly that case and leaves the delivery contract alone.
    /// </para>
    /// </summary>
    public async Task<ChangeTriggerResult> TriggerFromWebhookAsync(
        ZenkinsDbContext context,
        Guid projectId,
        string branch,
        string commit,
        IReadOnlyList<string> requiredTags,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requiredTags);

        var organizationId = context.CurrentOrganizationId;
        if (organizationId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A webhook trigger needs an active organization: the delivery selects the tenant from the "
                + "webhook row before it triggers.");
        }

        var existing = await FindAsync(context, projectId, branch, commit, cancellationToken);
        if (existing is { } already && string.Equals(already.TriggerType, BuildRunTriggerType.Poll, StringComparison.Ordinal))
        {
            // A poll got there first: the run it queued is this push's run.
            return ChangeTriggerResult.Duplicate(already.RunId, already.TriggerType);
        }

        var queued = await runs.QueueWebhookRunAsync(
            context,
            projectId,
            branch,
            commit,
            requiredTags,
            cancellationToken);

        if (queued.Status != RunOperationStatus.Success || queued.RunId is null)
        {
            var code = queued.FieldErrors?.FirstOrDefault()?.Code ?? PollOutcomes.Unchanged;
            return ChangeTriggerResult.Refused(code, queued.Status);
        }

        if (existing is null)
        {
            // Record the change so a later poll of the same commit answers `duplicate`. Best effort inside
            // the delivery's transaction: the run is already queued, and a lost row only means the poll
            // would queue... nothing - its own dedupe compares the head it already knows.
            context.ChangeTriggers.Add(new ChangeTrigger
            {
                OrganizationId = organizationId,
                ProjectId = projectId,
                Branch = branch,
                Commit = commit,
                TriggerType = BuildRunTriggerType.Webhook,
                RunId = queued.RunId,
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        return ChangeTriggerResult.Queued(queued.RunId.Value);
    }

    /// <summary>The ledger row for one change, or null.</summary>
    private static Task<ChangeTriggerTriggerRow?> FindAsync(
        ZenkinsDbContext context,
        Guid projectId,
        string branch,
        string commit,
        CancellationToken cancellationToken) =>
        context.ChangeTriggers
            .AsNoTracking()
            .Where(trigger => trigger.ProjectId == projectId
                && trigger.Branch == branch
                && trigger.Commit == commit)
            .Select(trigger => new ChangeTriggerTriggerRow(trigger.TriggerType, trigger.RunId))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The two facts a duplicate answer needs (read without tracking, so it never joins a save).</summary>
    private sealed record ChangeTriggerTriggerRow(string TriggerType, Guid? RunId);
}
