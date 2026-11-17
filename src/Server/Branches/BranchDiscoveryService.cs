using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Probes;
using Zenkins.Shared.Probes;
using Zenkins.Shared.Projects;

namespace Zenkins.Server.Branches;

/// <summary>What one project's discovery pass did (the facts a sweep logs and a test reads).</summary>
public sealed record BranchDiscoveryResult(
    string? ProbeOutcome = null,
    int Heads = 0,
    int Registered = 0,
    int Awaiting = 0,
    int Orphaned = 0,
    bool Probed = false)
{
    /// <summary>Whether the pass could read the remote's heads (a failure changed nothing).</summary>
    public bool Succeeded => GitProbeOutcomes.IsSuccess(ProbeOutcome);
}

/// <summary>
/// Card F-023's push-free discovery: ask I-005 for one repository's heads (one probe per project per
/// interval, never per branch), register the branches that are new, ask for a tree read where no verdict
/// exists, and mark the branches a <b>successful</b> answer no longer lists as orphaned.
/// <para>
/// <b>A failed probe changes nothing.</b> Every failure outcome is logged and left alone: no branch is
/// registered, no state is rewritten and - the rule that matters - nothing is orphaned. "I could not see
/// the remote" is not "the branch is gone"; only a successful refs answer that omits a branch is
/// evidence, and it only adds a marker (the runs, logs and artifacts stay).
/// </para>
/// <para>
/// <b>Observations, not runs.</b> A head with no verdict gets its tree read by a Worker observation (the
/// definition task family); the run is queued only when that read finds the pipeline file. This pass
/// itself never creates a run, so a repository full of docs-only branches produces no history.
/// </para>
/// </summary>
public sealed class BranchDiscoveryService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    GitProbeService probes,
    DefinitionValidationService observations,
    BranchDiscoveryOptions options,
    ILogger<BranchDiscoveryService> logger)
{
    private const string HeadsPrefix = GitProbeRules.HeadPrefix;

    /// <summary>
    /// One discovery pass for one project of the active tenant. Returns what it saw; a probe failure is
    /// reported in <see cref="BranchDiscoveryResult.ProbeOutcome"/> and changes nothing.
    /// </summary>
    public async Task<BranchDiscoveryResult> DiscoverAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        // The sweep's own budget: a discovery tick must not wait the interactive probe timeout for every
        // project. "No worker" is a recorded answer here, not a hang.
        var probe = await probes.RequestAsync(
            projectId,
            cancellationToken,
            wait: TimeSpan.FromSeconds(options.DiscoveryProbeWaitSeconds));
        if (probe.Status != GitProbeRequestStatus.Success)
        {
            logger.LogInformation(
                "Branch discovery skipped. ProjectId={ProjectId} Status={Status}",
                projectId,
                probe.Status);
            return new BranchDiscoveryResult(ProbeOutcome: probe.Outcome);
        }

        if (!GitProbeOutcomes.IsSuccess(probe.Outcome) || probe.Refs is null)
        {
            // The probe settled with a typed failure (auth, unreachable, timeout, no worker). The
            // project row already states it; nothing here may read it as "no heads".
            logger.LogInformation(
                "Branch discovery read no heads. ProjectId={ProjectId} Outcome={Outcome}",
                projectId,
                probe.Outcome);
            return new BranchDiscoveryResult(ProbeOutcome: probe.Outcome);
        }

        var now = DateTimeOffset.UtcNow;
        var headNames = new HashSet<string>(StringComparer.Ordinal);
        var headShas = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var head in probe.Refs.Heads)
        {
            if (!head.Name.StartsWith(HeadsPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var branch = head.Name[HeadsPrefix.Length..];
            if (!BranchIdentity.IsStorable(branch))
            {
                // Over-long or empty: a branch identity a run could not carry is not registered, and it
                // is not an error - the same refusal the webhook path records.
                continue;
            }

            headNames.Add(branch);
            headShas[branch] = head.Sha;
        }

        var registered = 0;
        var awaiting = new List<(string Branch, string Commit)>();
        var orphaned = 0;

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var project = await context.Projects
                .AsNoTracking()
                .Where(candidate => candidate.Id == projectId)
                .Select(candidate => new
                {
                    candidate.Kind,
                    candidate.PipelineSource,
                    candidate.RepositoryUrl,
                    candidate.BranchDiscovery,
                })
                .SingleOrDefaultAsync(cancellationToken);

            // A project that stopped being a multibranch repository Pipeline has nothing to discover.
            // Its existing rows are left untouched: reads never delete and discovery never deletes.
            if (project is null
                || !ProjectSources.HasRepository(project.Kind, project.PipelineSource)
                || !BranchDiscoveryModes.IsMultibranch(project.BranchDiscovery))
            {
                return new BranchDiscoveryResult(probe.Outcome, headNames.Count, Probed: true);
            }

            var rows = await context.BranchJobs
                .Where(branch => branch.ProjectId == projectId)
                .ToListAsync(cancellationToken);
            var byBranch = rows.ToDictionary(branch => branch.Branch, StringComparer.Ordinal);

            foreach (var (branch, sha) in headShas)
            {
                if (!byBranch.TryGetValue(branch, out var row))
                {
                    row = new BranchJob
                    {
                        OrganizationId = OrganizationOf(context),
                        ProjectId = projectId,
                        Branch = branch,
                        PipelineState = BranchPipelineStates.Unknown,
                        LastCommit = sha,
                        DiscoveredAt = now,
                        LastSeenAt = now,
                        PendingCommit = sha,
                        PendingSince = now,
                    };
                    context.BranchJobs.Add(row);
                    registered++;
                    awaiting.Add((branch, sha));
                    continue;
                }

                row.LastCommit = sha;
                row.LastSeenAt = now;

                // Seeing a branch in a successful answer proves it exists: an old orphan marker clears.
                row.OrphanedAt = null;
                row.OrphanedEvidence = null;

                // One probe answers every head; only heads that need a tree read become observations.
                // A branch whose verdict is has-pipeline is left to the webhook/polling paths - asking
                // again here would queue a second run for a commit a push already built.
                var needsObservation = row.PipelineState == BranchPipelineStates.Unknown
                    || (row.PipelineState == BranchPipelineStates.NoPipeline
                        && !string.Equals(row.ObservedCommit, sha, StringComparison.Ordinal));

                if (needsObservation && !string.Equals(row.PendingCommit, sha, StringComparison.Ordinal))
                {
                    row.PendingCommit = sha;
                    row.PendingSince = now;
                    row.ObservationAttempts = 0;
                    row.PendingTags = string.Empty;
                }

                if (needsObservation && string.Equals(row.PendingCommit, sha, StringComparison.Ordinal))
                {
                    awaiting.Add((branch, sha));
                }
            }

            foreach (var row in rows)
            {
                if (headNames.Contains(row.Branch) || row.OrphanedAt is not null)
                {
                    continue;
                }

                // A successful refs answer that omits the branch is the second (and only other) piece of
                // orphan evidence. Nothing is deleted; the pending observation (if any) is dropped
                // because there is no commit to read.
                row.OrphanedAt = now;
                row.OrphanedEvidence = BranchOrphanEvidence.RefsAbsent;
                row.PendingCommit = null;
                row.PendingSince = null;
                row.ObservationAttempts = 0;
                row.PendingTags = string.Empty;
                orphaned++;
            }

            await context.SaveChangesAsync(cancellationToken);
        }

        // Enqueue after the rows committed (a Worker answer must never be overwritten by a pending marker
        // that commits later).
        foreach (var (branch, commit) in awaiting)
        {
            await observations.EnqueueBranchObservationAsync(
                projectId, branch, commit, requiredTags: null, cancellationToken);
        }

        logger.LogInformation(
            "Branch discovery pass. ProjectId={ProjectId} Outcome={Outcome} Heads={Heads} Registered={Registered} Awaiting={Awaiting} Orphaned={Orphaned}",
            projectId,
            probe.Outcome,
            headNames.Count,
            registered,
            awaiting.Count,
            orphaned);

        return new BranchDiscoveryResult(
            probe.Outcome,
            headNames.Count,
            registered,
            awaiting.Count,
            orphaned,
            Probed: true);
    }

    /// <summary>
    /// The tenant of the active context' query filter. Inside a sweeper scope the tenant is set to the
    /// project's organization before this runs; a row inserted without it would be refused by the
    /// context's own guard, so the value is read from the filter's source rather than guessed.
    /// </summary>
    private static Guid OrganizationOf(ZenkinsDbContext context)
    {
        var organizationId = context.CurrentOrganizationId;
        return organizationId == Guid.Empty
            ? throw new InvalidOperationException(
                "Branch discovery needs an active organization; the sweeper sets it before the pass.")
            : organizationId;
    }
}
