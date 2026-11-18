using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Projects;

namespace Zenkins.Server.Runs;

/// <summary>The bounds and the retry policy of the downstream dispatcher (card F-026).</summary>
public static class DownstreamTriggerLimits
{
    /// <summary>
    /// How many links deep a chain may go before it stops. The runtime half of the loop protection: a chain can
    /// loop through branch policies (A main -> B main -> A main) without the graph ever containing a cycle, so the
    /// save-time check cannot see it. Twenty is deliberately shallow - a legitimate chain is a handful of jobs.
    /// </summary>
    public const int MaxHops = 20;

    /// <summary>How many rows one dispatch pass works. Bounded so one broken chain cannot starve the rest.</summary>
    public const int BatchSize = 20;

    /// <summary>
    /// How many times a <b>transient</b> refusal (a full queue, a database blip) is retried before the row settles
    /// as a failed trigger. A permanent refusal (disabled, missing project, no credential, an invalid downstream)
    /// settles on the first pass - the card's "not retried forever".
    /// </summary>
    public const int MaxAttempts = 5;
}

/// <summary>
/// Stages the downstream triggers one terminal upstream run owes (card F-026), <b>on the caller's context</b> so
/// they commit with the terminal status itself.
/// <para>
/// <b>Why this is the card's core.</b> The failure mode this feature has to answer is a Server dying between "the
/// upstream finished" and "the downstream was queued". Writing the obligation as a row in the same transaction
/// turns an in-memory callback into a durable fact: either both are committed or neither is, and a committed row is
/// worked off after commit (or by the next start) until it settles.
/// </para>
/// <para>
/// <b>A rule that does not hold still leaves a row.</b> An <c>onlyOnSuccess</c> link whose upstream failed is
/// recorded settled with <c>skipped-upstream-failed</c>, because "the chain did nothing" is exactly the thing the
/// upstream run page must be able to explain.
/// </para>
/// </summary>
public static class DownstreamTriggers
{
    /// <summary>Adds one outbox row per downstream link of <paramref name="upstream"/>'s project. Does not save.</summary>
    public static async Task StageAsync(
        ZenkinsDbContext context,
        BuildRun upstream,
        string terminalStatus,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(upstream);

        var links = await context.ProjectLinks
            .AsNoTracking()
            .Where(link => link.UpstreamProjectId == upstream.ProjectId)
            .ToListAsync(cancellationToken);
        if (links.Count == 0)
        {
            return;
        }

        var succeeded = string.Equals(terminalStatus, BuildRunStatus.Success, StringComparison.Ordinal);
        foreach (var link in links)
        {
            var message = new OutboxMessage
            {
                OrganizationId = upstream.OrganizationId,
                Kind = OutboxKinds.DownstreamTrigger,
                UpstreamRunId = upstream.Id,
                LinkId = link.Id,
                Status = OutboxStatuses.Pending,
            };

            if (!succeeded && link.OnlyOnSuccess)
            {
                // The rule did not hold: the row is settled immediately, with the reason the page renders.
                message.Status = OutboxStatuses.Settled;
                message.Outcome = DownstreamOutcomes.SkippedUpstreamFailed;
                message.Reason = "the upstream run failed and this link only triggers on success";
                message.SettledAt = now;
            }

            context.OutboxMessages.Add(message);
        }
    }
}

/// <summary>
/// The dispatcher's per-pass work (card F-026): settle every due obligation - queue a downstream run, or record why
/// not - and never lose one to a crash.
/// <para>
/// <b>Idempotent by construction.</b> The row is the unit: it stays <c>pending</c> until the whole pass (queue +
/// settle) commits, so a crash in between re-dispatches it. A row that already settled is skipped, which is what
/// makes a second tick add nothing (TC-001).
/// </para>
/// <para>
/// <b>Two kinds of refusal, two behaviours.</b> A <em>permanent</em> one (disabled project, missing project or
/// link, no credential, an invalid downstream, the hop cap) settles the row with its outcome on the first pass - the
/// card's "not retried forever". A <em>transient</em> one (a full queue, a database error) backs off and retries,
/// bounded by <see cref="DownstreamTriggerLimits.MaxAttempts"/>, after which it settles as a failed trigger.
/// </para>
/// </summary>
public sealed class DownstreamTriggerService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    ISystemDataScope systemDataScope,
    TenantContext tenantContext,
    RunService runs,
    ILogger<DownstreamTriggerService> logger)
{
    /// <summary>Works one batch of due obligations. Returns how many rows settled.</summary>
    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        var due = await ReadDueAsync(cancellationToken);
        var settled = 0;
        foreach (var row in due)
        {
            try
            {
                settled += await DispatchOneAsync(row.Id, row.OrganizationId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure)
            {
                // One broken obligation must not stop the batch; the row keeps its attempts and backs off.
                logger.LogError(
                    failure,
                    "Downstream trigger dispatch failed. OrganizationId={OrganizationId} OutboxId={OutboxId}",
                    row.OrganizationId,
                    row.Id);
            }
        }

        return settled;
    }

    /// <summary>The due rows, read across tenants through the system scope (the dispatcher has no request).</summary>
    private async Task<IReadOnlyList<(Guid Id, Guid OrganizationId)>> ReadDueAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        using var scope = systemDataScope.BeginScope();
        var now = DateTimeOffset.UtcNow;

        // The status filter runs in SQL; the due check and the ordering happen in memory, because SQLite cannot
        // compare or order DateTimeOffset columns in SQL (the same constraint the claim loop documents). A pending
        // set is small - an organization's unfinished obligations at one instant - so the read is bounded by that,
        // and the batch below caps the work a single pass does.
        var pending = await context.OutboxMessages
            .AsNoTracking()
            .Where(message => message.Status == OutboxStatuses.Pending)
            .Select(message => new { message.Id, message.OrganizationId, message.CreatedAt, message.NextAttemptAt })
            .ToListAsync(cancellationToken);

        return
        [
            .. pending
                .Where(message => message.NextAttemptAt <= now)
                .OrderBy(message => message.CreatedAt)
                .ThenBy(message => message.Id)
                .Take(DownstreamTriggerLimits.BatchSize)
                .Select(message => (message.Id, message.OrganizationId)),
        ];
    }

    private async Task<int> DispatchOneAsync(Guid outboxId, Guid organizationId, CancellationToken cancellationToken)
    {
        // The row carries the tenant: a background pass has no request to inherit one from.
        tenantContext.OrganizationId = organizationId;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var message = await context.OutboxMessages.SingleOrDefaultAsync(
            candidate => candidate.Id == outboxId,
            cancellationToken);
        if (message is null || !string.Equals(message.Status, OutboxStatuses.Pending, StringComparison.Ordinal))
        {
            // Another pass settled it, or it is gone. Nothing to do, and nothing to undo.
            return 0;
        }

        var upstream = await context.BuildRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(run => run.Id == message.UpstreamRunId, cancellationToken);
        var link = await context.ProjectLinks
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == message.LinkId, cancellationToken);
        if (upstream is null || link is null)
        {
            // The upstream run or the edge was deleted between the completion and this pass: settled, not retried.
            return await SettleAsync(
                context,
                message,
                DownstreamOutcomes.SkippedMissing,
                "the link or the upstream run no longer exists",
                null,
                cancellationToken);
        }

        var project = await context.Projects
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == link.DownstreamProjectId, cancellationToken);
        if (project is null)
        {
            return await SettleAsync(
                context,
                message,
                DownstreamOutcomes.SkippedMissing,
                "the downstream project no longer exists",
                null,
                cancellationToken);
        }

        // Card F-028: a disabled project is skipped, with the reason the upstream run page can render.
        if (project.Disabled)
        {
            return await SettleAsync(
                context,
                message,
                DownstreamOutcomes.SkippedDisabled,
                // The operator's own reason when there is one: "disabled" alone sends the reader to the settings
                // tab to find out why, and the reason is right here.
                string.IsNullOrWhiteSpace(project.DisabledReason)
                    ? $"{project.Name} is disabled"
                    : $"{project.Name} is disabled: {project.DisabledReason}",
                null,
                cancellationToken);
        }

        // The runtime half of the loop protection (card F-026): a chain that only loops through branch policies.
        if (upstream.UpstreamHops + 1 > DownstreamTriggerLimits.MaxHops)
        {
            return await SettleAsync(
                context,
                message,
                DownstreamOutcomes.SkippedHopLimit,
                $"the chain reached {DownstreamTriggerLimits.MaxHops} links, so it was stopped here",
                null,
                cancellationToken);
        }

        var needsRepository = ProjectSources.HasRepository(project.Kind, project.PipelineSource);
        var branch = string.Equals(link.BranchPolicy, ProjectLinkBranchPolicies.DefaultBranch, StringComparison.Ordinal)
            ? project.DefaultBranch
            : upstream.GitBranch;

        // A branch the downstream cannot build is a skip, not a failure: the chain did what the link asked and the
        // branch simply is not something this project can run.
        if (needsRepository && (string.IsNullOrWhiteSpace(branch) || string.IsNullOrWhiteSpace(project.RepositoryUrl)))
        {
            return await SettleAsync(
                context,
                message,
                DownstreamOutcomes.SkippedBranch,
                string.IsNullOrWhiteSpace(branch)
                    ? $"{project.Name} has no branch to build for this link"
                    : $"{project.Name} has no repository to build '{branch}' from",
                null,
                cancellationToken);
        }

        // A credential that no longer resolves is a permanent failure, not a retry: waiting will not recreate it.
        if (project.GitCredentialId is { } credentialId
            && !await context.GitCredentials.AnyAsync(credential => credential.Id == credentialId, cancellationToken))
        {
            return await SettleAsync(
                context,
                message,
                DownstreamOutcomes.FailedTrigger,
                $"{project.Name} is assigned a Git credential that no longer exists",
                null,
                cancellationToken);
        }

        // The chain facts the downstream's steps read (card F-026), frozen onto the new run: read from the terminal
        // upstream run now, so a later rename of its project cannot rewrite what these steps were told.
        var upstreamProjectName = await context.Projects
            .AsNoTracking()
            .Where(candidate => candidate.Id == upstream.ProjectId)
            .Select(candidate => candidate.Name)
            .SingleOrDefaultAsync(cancellationToken) ?? "(deleted project)";

        var queued = await runs.QueueUpstreamRunAsync(
            context,
            project.Id,
            upstream.Id,
            upstream.UpstreamHops + 1,
            needsRepository ? branch ?? string.Empty : string.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["UPSTREAM_PROJECT"] = upstreamProjectName,
                ["UPSTREAM_RUN_ID"] = upstream.Id.ToString(),
                ["UPSTREAM_STATUS"] = upstream.Status,
                ["UPSTREAM_BRANCH"] = upstream.GitBranch,
                ["UPSTREAM_COMMIT"] = upstream.GitCommit ?? string.Empty,
            },
            cancellationToken);

        if (queued.Status == RunOperationStatus.Success && queued.RunId is { } downstreamRunId)
        {
            message.DownstreamRunId = downstreamRunId;
            return await SettleAsync(
                context,
                message,
                DownstreamOutcomes.Queued,
                reason: null,
                downstreamRunId,
                cancellationToken);
        }

        if (queued.Status == RunOperationStatus.NotFound)
        {
            return await SettleAsync(
                context,
                message,
                DownstreamOutcomes.SkippedMissing,
                "the downstream project no longer exists",
                null,
                cancellationToken);
        }

        // Everything else is a refusal the queue itself answered: a disabled project, a full queue, a project with
        // no saved steps. A full queue is the transient one - retried with backoff, bounded.
        var reason = queued.Message
            ?? queued.FieldErrors?.FirstOrDefault()?.Code
            ?? "the downstream trigger was refused";

        return await RetryOrFailAsync(context, message, project.Name, reason, cancellationToken);
    }

    /// <summary>
    /// Backs off and retries a refusal a later pass might not see, up to
    /// <see cref="DownstreamTriggerLimits.MaxAttempts"/>; after that the row settles as a failed trigger, so a
    /// chain cannot sit pending forever.
    /// </summary>
    private async Task<int> RetryOrFailAsync(
        ZenkinsDbContext context,
        OutboxMessage message,
        string projectName,
        string reason,
        CancellationToken cancellationToken)
    {
        message.Attempts++;
        if (message.Attempts >= DownstreamTriggerLimits.MaxAttempts)
        {
            return await SettleAsync(
                context,
                message,
                DownstreamOutcomes.FailedTrigger,
                $"{projectName} could not be triggered after {message.Attempts} attempts: {reason}",
                null,
                cancellationToken);
        }

        // Linear backoff over the tick: the queue drains, a disabled project is re-enabled, a database blip ends.
        message.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(5 * message.Attempts);
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Downstream trigger retried. OrganizationId={OrganizationId} OutboxId={OutboxId} Attempts={Attempts} Reason={Reason}",
            message.OrganizationId,
            message.Id,
            message.Attempts,
            reason);
        return 0;
    }

    /// <summary>Settles one row with its outcome and reason - the audit trail the upstream run page renders.</summary>
    private async Task<int> SettleAsync(
        ZenkinsDbContext context,
        OutboxMessage message,
        string outcome,
        string? reason,
        Guid? downstreamRunId,
        CancellationToken cancellationToken)
    {
        message.Status = OutboxStatuses.Settled;
        message.Outcome = outcome;
        message.Reason = reason;
        message.SettledAt = DateTimeOffset.UtcNow;
        if (downstreamRunId is { } runId)
        {
            message.DownstreamRunId = runId;
        }

        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Downstream trigger settled. OrganizationId={OrganizationId} OutboxId={OutboxId} Outcome={Outcome} DownstreamRunId={DownstreamRunId}",
            message.OrganizationId,
            message.Id,
            outcome,
            message.DownstreamRunId);
        return 1;
    }
}

/// <summary>
/// The background half of card F-026: one dispatch pass per <c>Downstream: TickSeconds</c>. It assigns nothing
/// itself - a queued downstream run is claimed by a worker like any other - and it never gives up on a committed
/// obligation except through the documented permanent refusals.
/// <para>
/// <b>Failure policy matches the other sweepers.</b> A pass that throws is logged and retried on the next interval;
/// tests drive <see cref="DispatchOnceAsync"/> instead of waiting on real time.
/// </para>
/// </summary>
public sealed class DownstreamTriggerDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<DownstreamTriggerDispatcher> logger) : BackgroundService
{
    /// <summary>How often the dispatcher looks. Short: the delay is visible on the upstream run page.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchOnceAsync(stoppingToken);
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Downstream dispatch passed failed; retrying at the next tick.");
                try
                {
                    if (!await timer.WaitForNextTickAsync(stoppingToken))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>One pass in its own DI scope (never a shared singleton scope). Test seam.</summary>
    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<DownstreamTriggerService>();
        return await dispatch.DispatchPendingAsync(cancellationToken);
    }
}
