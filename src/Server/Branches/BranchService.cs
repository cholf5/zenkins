using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Projects;
using Zenkins.Shared.Projects;

namespace Zenkins.Server.Branches;

/// <summary>
/// The identity rules of a branch job (card F-023). A branch name is the key of
/// <see cref="BranchJob"/>, and it must be a value a run can actually store:
/// <see cref="BuildRun.GitBranch"/> is bounded by <see cref="ProjectValidation.MaxDefaultBranchLength"/>
/// (200) while a probe may report a full ref of up to 255 characters. The bound is therefore checked
/// <b>before</b> anything is written, so an over-long branch is a recorded refusal instead of a database
/// error - and it is never truncated, because a truncated name would be a different branch.
/// </summary>
public static class BranchIdentity
{
    /// <summary>The longest branch name a run or a branch job can store (the run column's bound).</summary>
    public const int MaxLength = ProjectValidation.MaxDefaultBranchLength;

    /// <summary>
    /// Whether <paramref name="branch"/> can be stored as a branch identity at all. An empty string is
    /// never a branch: the product uses "no branch" as the Freestyle discriminator on a run, so a branch
    /// job with an empty name would collide with that meaning.
    /// </summary>
    public static bool IsStorable(string? branch) => branch is { Length: > 0 } && branch.Length <= MaxLength;
}

/// <summary>One branch's newest run, as the branch list shows it (F-036's number included).</summary>
public sealed record BranchRunView(
    Guid RunId,
    int Number,
    string Status,
    string? GitCommit,
    DateTimeOffset CreatedAt);

/// <summary>
/// One branch job as the Branches tab renders it (card F-023). It is assembled from the branch registry
/// row (when there is one) and the project's runs, so a branch with runs and no registry row - every
/// branch a project had before someone switched it to discovery - still appears.
/// </summary>
public sealed record BranchView(
    string Branch,
    string PipelineState,
    string? LastCommit,
    string? ObservedCommit,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? OrphanedAt,
    string? OrphanedEvidence,
    bool HasRecord,
    BranchRunView? LastRun,
    string? PendingCommit = null,

    /// <summary>
    /// How many times the pending tree read had to be re-requested (card F-023). Zero with a pending
    /// commit means "the first request is out"; a growing number is what the page shows as
    /// <c>(retry N)</c>, so "never tried" and "tried five times, nobody answered" cannot render the same.
    /// </summary>
    int ObservationAttempts = 0,

    /// <summary>When the pending read was last requested (the retry clock the page can state).</summary>
    DateTimeOffset? PendingSince = null)
{
    /// <summary>Whether the branch is marked deleted upstream (history retained, nothing deleted).</summary>
    public bool IsOrphaned => OrphanedAt is not null;

    /// <summary>Whether the branch is listed as having no pipeline file (and is never queued).</summary>
    public bool IsNoPipeline =>
        string.Equals(PipelineState, BranchPipelineStates.NoPipeline, StringComparison.Ordinal);
}

/// <summary>
/// The branch registry of a multibranch project (card F-023): reading the branch list, recording a
/// branch a trigger or a run observed, and the evidence-backed pipeline state that gates queueing.
/// <para>
/// <b>Evidence only.</b> A state is written together with the commit a worker actually read
/// (<see cref="BranchJob.ObservedCommit"/>). Nothing in this service infers <see cref="BranchPipelineStates.NoPipeline"/>
/// from a missing run, a missing row or a failed probe - "I did not see it" is never "it is not there".
/// </para>
/// <para>
/// <b>The run's snapshot is the authority.</b> Reads union the registry with the project's runs, so a
/// branch that only exists in run history (including every row written before this card) is listed
/// rather than dropped. Writes never touch a run and never delete a branch job.
/// </para>
/// </summary>
public sealed class BranchService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    ILogger<BranchService> logger)
{
    /// <summary>
    /// Every branch of one project: the registry rows and the branches the project's runs name, with
    /// each branch's newest run (by build number - the creation order F-036 stamps under the project
    /// lock). Ordered by branch name, ordinal: git's own ref ordering, so <c>Main</c> and <c>main</c> sit
    /// apart exactly as they do in the repository.
    /// <para>
    /// The read is tenant-filtered and project-scoped, so a foreign project id answers an empty list and
    /// a same-named branch in another project of the same organization can never leak in.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<BranchView>> GetBranchesAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (!await context.Projects.AnyAsync(project => project.Id == projectId, cancellationToken))
        {
            return [];
        }

        var records = await context.BranchJobs
            .AsNoTracking()
            .Where(branch => branch.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        // Only the columns the list needs - never the frozen spec or a log line. An inline or Freestyle
        // run carries an empty branch (the product's discriminator), which is not a branch identity and
        // is therefore not listed here.
        var runs = await context.BuildRuns
            .AsNoTracking()
            .Where(run => run.ProjectId == projectId && run.GitBranch != string.Empty)
            .Select(run => new
            {
                run.Id,
                run.GitBranch,
                run.Number,
                run.Status,
                run.GitCommit,
                run.CreatedAt,
                HasDefinition = run.PipelineSpecJson != null,
            })
            .ToListAsync(cancellationToken);

        var lastRunByBranch = new Dictionary<string, (BranchRunView View, bool HasDefinition)>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            // The projection above cannot order per branch in SQL, and one project's run list is small
            // enough to fold in memory (the runs page already loads it). Highest number wins; the
            // created-at tiebreak only matters for a row no writer numbered.
            if (lastRunByBranch.TryGetValue(run.GitBranch, out var current)
                && (current.View.Number > run.Number
                    || (current.View.Number == run.Number && current.View.CreatedAt >= run.CreatedAt)))
            {
                continue;
            }

            lastRunByBranch[run.GitBranch] = (
                new BranchRunView(run.Id, run.Number, run.Status, run.GitCommit, run.CreatedAt),
                run.HasDefinition);
        }

        var byBranch = new Dictionary<string, BranchView>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (!BranchIdentity.IsStorable(record.Branch))
            {
                // Defensive: a row written out of band with an impossible identity is not listed as a
                // branch (it cannot join a run). It is not deleted either - reads never delete.
                logger.LogWarning(
                    "Branch job skipped. OrganizationId={OrganizationId} ProjectId={ProjectId} BranchJobId={BranchJobId} Reason=identity",
                    record.OrganizationId,
                    record.ProjectId,
                    record.Id);
                continue;
            }

            lastRunByBranch.TryGetValue(record.Branch, out var lastRun);
            byBranch[record.Branch] = new BranchView(
                record.Branch,
                BranchPipelineStates.IsKnown(record.PipelineState)
                    ? record.PipelineState
                    : BranchPipelineStates.Unknown,
                record.LastCommit,
                record.ObservedCommit,
                record.DiscoveredAt,
                record.LastSeenAt,
                record.OrphanedAt,
                record.OrphanedEvidence,
                HasRecord: true,
                lastRun.View,
                record.PendingCommit,
                record.ObservationAttempts,
                record.PendingSince);
        }

        // Branches known only from history (or from a run queued before the registry row existed). A run
        // whose definition froze proves the pipeline file was there; anything less leaves the state
        // unknown rather than guessing.
        foreach (var (branch, lastRun) in lastRunByBranch)
        {
            if (byBranch.ContainsKey(branch))
            {
                continue;
            }

            byBranch[branch] = new BranchView(
                branch,
                lastRun.HasDefinition ? BranchPipelineStates.HasPipeline : BranchPipelineStates.Unknown,
                lastRun.View.GitCommit,
                lastRun.HasDefinition ? lastRun.View.GitCommit : null,
                lastRun.View.CreatedAt,
                lastRun.View.CreatedAt,
                OrphanedAt: null,
                OrphanedEvidence: null,
                HasRecord: false,
                lastRun.View);
        }

        return
        [
            .. byBranch.Values
                .OrderBy(branch => branch.Branch, StringComparer.Ordinal)
                .ThenBy(branch => branch.Branch.Length),
        ];
    }

    /// <summary>
    /// The verdict a delivery gates on, read through the tenant filter: the pipeline state and the commit
    /// it is evidence about. Unknown (and a null commit) when no registry row exists - and unknown never
    /// means "no pipeline": a delivery asks for an observation instead of guessing.
    /// </summary>
    public async Task<(string State, string? ObservedCommit)> GetVerdictAsync(
        Guid projectId,
        string branch,
        CancellationToken cancellationToken = default)
    {
        if (!BranchIdentity.IsStorable(branch))
        {
            return (BranchPipelineStates.Unknown, null);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.BranchJobs
            .AsNoTracking()
            .Where(job => job.ProjectId == projectId && job.Branch == branch)
            .Select(job => new { job.PipelineState, job.ObservedCommit })
            .SingleOrDefaultAsync(cancellationToken);
        return row is not null && BranchPipelineStates.IsKnown(row.PipelineState)
            ? (row.PipelineState, row.ObservedCommit)
            : (BranchPipelineStates.Unknown, null);
    }

    /// <summary>
    /// Records that a trigger saw <paramref name="branch"/> at <paramref name="commit"/> without
    /// changing its pipeline state (an observation - not a sighting - decides that). Runs in the
    /// caller's unit of work, so a delivery and the branch it discovered commit together.
    /// </summary>
    public static Task RecordSeenAsync(
        ZenkinsDbContext context,
        Guid organizationId,
        Guid projectId,
        string branch,
        string? commit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        string? pendingCommit = null,
        string pendingTags = "") =>
        WriteAsync(
            context, organizationId, projectId, branch, state: null, commit, now, cancellationToken,
            pendingCommit, pendingTags);

    /// <summary>
    /// Records the pipeline state a worker actually observed on <paramref name="branch"/> at
    /// <paramref name="commit"/>. This is the only writer that may produce
    /// <see cref="BranchPipelineStates.NoPipeline"/>.
    /// </summary>
    public static Task RecordObservationAsync(
        ZenkinsDbContext context,
        Guid organizationId,
        Guid projectId,
        string branch,
        string state,
        string? commit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!BranchPipelineStates.IsKnown(state) || state == BranchPipelineStates.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "An observation must carry a known, non-unknown state.");
        }

        return WriteAsync(
            context, organizationId, projectId, branch, state, commit, now, cancellationToken,
            pendingCommit: null, pendingTags: string.Empty);
    }

    /// <summary>
    /// Clears the pending observation when the answer is about the commit it was owed for. A stale answer
    /// (a push moved the head meanwhile) leaves the newer pending request alone.
    /// </summary>
    public static async Task ClearPendingObservationAsync(
        ZenkinsDbContext context,
        Guid projectId,
        string branch,
        string commit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!BranchIdentity.IsStorable(branch))
        {
            return;
        }

        var row = await context.BranchJobs.SingleOrDefaultAsync(
            job => job.ProjectId == projectId && job.Branch == branch,
            cancellationToken);
        if (row is null || !string.Equals(row.PendingCommit, commit, StringComparison.Ordinal))
        {
            return;
        }

        ClearPending(row, now);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Records that a pending observation was attempted and produced no verdict (the Worker could not read
    /// the tree, or nobody answered). The pending marker stays - the retry sweeper will ask again after a
    /// backoff - and the attempt count grows so that backoff grows too.
    /// </summary>
    public static async Task NoteObservationAttemptAsync(
        ZenkinsDbContext context,
        Guid projectId,
        string branch,
        string commit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!BranchIdentity.IsStorable(branch))
        {
            return;
        }

        var row = await context.BranchJobs.SingleOrDefaultAsync(
            job => job.ProjectId == projectId && job.Branch == branch,
            cancellationToken);
        if (row is null || !string.Equals(row.PendingCommit, commit, StringComparison.Ordinal))
        {
            return;
        }

        row.ObservationAttempts++;
        row.PendingSince = now;
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Marks an existing branch job orphaned, with the evidence that proves it
    /// (<see cref="BranchOrphanEvidence"/>). <b>Never inserts and never deletes</b>: a branch nobody
    /// registered has nothing to mark, and its runs (if any) stay visible through the run snapshot
    /// union. An already-orphaned row keeps its first evidence and timestamp - a second delete event
    /// is not new information.
    /// </summary>
    public static async Task MarkOrphanedAsync(
        ZenkinsDbContext context,
        Guid projectId,
        string branch,
        string evidence,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!BranchOrphanEvidence.IsKnown(evidence))
        {
            throw new ArgumentOutOfRangeException(nameof(evidence), evidence, "Unknown orphan evidence.");
        }

        if (!BranchIdentity.IsStorable(branch))
        {
            return;
        }

        var row = await context.BranchJobs.SingleOrDefaultAsync(
            job => job.ProjectId == projectId && job.Branch == branch,
            cancellationToken);
        if (row is null || row.OrphanedAt is not null)
        {
            return;
        }

        row.OrphanedAt = now;
        row.OrphanedEvidence = evidence;
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The one write path for the branch registry: update the row when it exists, insert it otherwise.
    /// <para>
    /// <b>Race-safe without a provider-specific upsert.</b> Two deliveries racing to register the same
    /// new branch both pass the read, so the loser's insert hits the unique <c>(ProjectId, Branch)</c>
    /// index. The insert runs inside a savepoint: the unique violation rolls back to it (PostgreSQL
    /// otherwise aborts the caller's whole transaction) and the loser updates the winner's row instead.
    /// </para>
    /// <para>
    /// <b>Seen revives an orphan; only an observation changes the pipeline state.</b> A branch that
    /// reappears after an upstream delete is live again (the marker clears), but a sighting never
    /// overwrites what a worker read - a <c>no-pipeline</c> verdict stays until new evidence replaces it.
    /// </para>
    /// </summary>
    private static async Task WriteAsync(
        ZenkinsDbContext context,
        Guid organizationId,
        Guid projectId,
        string branch,
        string? state,
        string? commit,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string? pendingCommit,
        string pendingTags)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!BranchIdentity.IsStorable(branch))
        {
            // Never write a branch identity a run could not carry; callers that must answer the sender
            // (the webhook path) check the bound first and record a refusal instead.
            return;
        }

        var existingTransaction = context.Database.CurrentTransaction;
        await using var ownedTransaction = existingTransaction is null
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var transaction = existingTransaction ?? ownedTransaction!;

        var row = await context.BranchJobs
            .SingleOrDefaultAsync(job => job.ProjectId == projectId && job.Branch == branch, cancellationToken);
        if (row is null)
        {
            var created = new BranchJob
            {
                OrganizationId = organizationId,
                ProjectId = projectId,
                Branch = branch,
                PipelineState = BranchPipelineStates.Unknown,
                DiscoveredAt = now,
                LastSeenAt = now,
            };
            Apply(created, state, commit, now, pendingCommit, pendingTags);
            context.BranchJobs.Add(created);

            await transaction.CreateSavepointAsync("branch_job_insert", cancellationToken);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                await transaction.ReleaseSavepointAsync("branch_job_insert", cancellationToken);
            }
            catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "BranchJobs"))
            {
                await transaction.RollbackToSavepointAsync("branch_job_insert", cancellationToken);
                context.Entry(created).State = EntityState.Detached;
                var winner = await context.BranchJobs
                    .SingleOrDefaultAsync(
                        job => job.ProjectId == projectId && job.Branch == branch,
                        cancellationToken);
                if (winner is null)
                {
                    // The winner's row vanished between the insert and this read: nothing to record.
                    return;
                }

                Apply(winner, state, commit, now, pendingCommit, pendingTags);
                await context.SaveChangesAsync(cancellationToken);
            }
        }
        else
        {
            Apply(row, state, commit, now, pendingCommit, pendingTags);
            await context.SaveChangesAsync(cancellationToken);
        }

        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Folds one sighting/observation into a row. <paramref name="state"/> null means "seen only":
    /// the pipeline state and its evidence commit are untouched.
    /// </summary>
    private static void Apply(
        BranchJob row,
        string? state,
        string? commit,
        DateTimeOffset now,
        string? pendingCommit,
        string pendingTags)
    {
        row.LastSeenAt = now;
        if (commit is { Length: > 0 })
        {
            row.LastCommit = commit;
        }

        if (state is not null)
        {
            row.PipelineState = state;
            row.ObservedCommit = commit;
        }

        // The pending marker: a request for a commit the row has no verdict about. A new commit starts a
        // fresh retry clock; a repeated request for the same commit keeps it, so a push storm cannot reset
        // the backoff.
        if (pendingCommit is null)
        {
            // A plain sighting (no state, no request) means nothing is owed. An observation's answer only
            // clears a pending marker for the commit it is about - a late answer about an older head must
            // not drop the read the newer head is waiting for.
            if (state is null || string.Equals(row.PendingCommit, commit, StringComparison.Ordinal))
            {
                ClearPending(row, now);
            }
        }
        else if (!string.Equals(row.PendingCommit, pendingCommit, StringComparison.Ordinal))
        {
            row.PendingCommit = pendingCommit;
            row.PendingSince = now;
            row.ObservationAttempts = 0;
            row.PendingTags = pendingTags;
        }

        // Seeing a branch again is proof it exists again: an orphan marker is cleared, never history.
        row.OrphanedAt = null;
        row.OrphanedEvidence = null;
    }

    /// <summary>The pending observation is answered or no longer needed: the row forgets it.</summary>
    private static void ClearPending(BranchJob row, DateTimeOffset now)
    {
        row.PendingCommit = null;
        row.PendingSince = null;
        row.ObservationAttempts = 0;
        row.PendingTags = string.Empty;
        row.LastSeenAt = now;
    }
}
