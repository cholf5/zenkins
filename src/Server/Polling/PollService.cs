using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Branches;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Projects;
using Zenkins.Server.Probes;
using Zenkins.Server.Runs;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Probes;
using Zenkins.Shared.Projects;

namespace Zenkins.Server.Polling;

/// <summary>What one project's poll did (card F-025): the recorded outcome and how many runs it queued.</summary>
/// <param name="Outcome">A <see cref="PollOutcomes"/> value (or a <see cref="GitProbeOutcomes"/> failure word).</param>
/// <param name="RunsQueued">Runs this poll queued (0 for every skip, duplicate or failure).</param>
public sealed record PollProjectResult(string Outcome, int RunsQueued = 0);

/// <summary>
/// One project's poll (card F-025): ask I-005 for the repository's heads <b>once</b>, compare each head with
/// the branch's last known head, and hand a moved head to the shared change-trigger path.
/// <para>
/// <b>Built on F-023's probe, not on a second sampler.</b> There is no Server-side git and no second way to
/// ask a repository (PRD §8.1): the poll is one <see cref="GitProbeService.RequestAsync"/> per project per
/// tick, exactly the call F-023's discovery makes - which is also what keeps "one probe per repository per
/// tick" true when both passes run in the same interval.
/// </para>
/// <para>
/// <b>A failure is not "no change".</b> An unreachable remote, a refused credential or no worker at all is
/// stored as the poll's outcome (never as "the head did not move"), the branch heads are left untouched, and
/// the project's failure streak grows so the next poll waits longer (1x, 2x, 4x … capped). One successful
/// probe resets it.
/// </para>
/// <para>
/// <b>Every tick records an outcome.</b> A poll that queues nothing because the project is disabled, at its
/// queue cap or at its concurrency limit stores the skip reason on the project (and on the branch head where
/// one exists) - an operator asking "why did nothing build?" reads it on the settings card instead of
/// guessing.
/// </para>
/// </summary>
public sealed class PollService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    GitProbeService probes,
    ChangeTriggerService changes,
    BranchService branches,
    DefinitionValidationService observations,
    PollOptions options,
    TimeProvider timeProvider,
    ILogger<PollService> logger)
{
    /// <summary>Polls one project of the active tenant and returns what it decided.</summary>
    public async Task<PollProjectResult> PollProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        var project = await context.Projects
            .AsNoTracking()
            .Where(candidate => candidate.Id == projectId)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Kind,
                candidate.PipelineSource,
                candidate.RepositoryUrl,
                candidate.DefaultBranch,
                candidate.BranchDiscovery,
                candidate.Disabled,
                candidate.GitCredentialId,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (project is null)
        {
            return new PollProjectResult(PollOutcomes.NoRepository);
        }

        // F-020/F-022: a project with no repository has nothing to poll, and its settings card says so
        // instead of this pass failing every interval.
        if (!ProjectSources.HasRepository(project.Kind, project.PipelineSource)
            || string.IsNullOrWhiteSpace(project.RepositoryUrl))
        {
            await RecordAsync(context, projectId, PollOutcomes.NoRepository, message: null, failureStreak: null, now, cancellationToken);
            return new PollProjectResult(PollOutcomes.NoRepository);
        }

        // F-028: a disabled project queues nothing, from any source. This is a recorded skip - and no probe
        // is spent on it (an operator who disabled a project does not want it polled).
        if (project.Disabled)
        {
            await RecordAsync(context, projectId, PollOutcomes.SkippedDisabled, message: null, failureStreak: null, now, cancellationToken);
            return new PollProjectResult(PollOutcomes.SkippedDisabled);
        }

        // F-024's overlap rule, read through the same query the schedule reactor reads: a project that is
        // executing a run does not get a second one stacked on it. The head stays where it was, so the tick
        // after the busy run finishes sees the newest commit - and the skip is recorded, never silent.
        // Merely queued runs are the queue cap's business (F-029), not this one: a poll of a project whose
        // runs are all waiting must still build what it just observed.
        if (await InFlightRuns.FindOldestAsync(context, projectId, includeQueued: false, cancellationToken) is { } inFlight)
        {
            await RecordAsync(context, projectId, PollOutcomes.SkippedOverlap, message: $"run #{inFlight}", failureStreak: null, now, cancellationToken);
            return new PollProjectResult(PollOutcomes.SkippedOverlap);
        }

        // One probe per repository per tick - the same I-005 call F-023's discovery makes.
        var probe = await probes.RequestAsync(projectId, cancellationToken, options.ProbeWait);
        if (probe.Status == GitProbeRequestStatus.InvalidInput)
        {
            await RecordAsync(context, projectId, PollOutcomes.NoRepository, message: probe.ErrorCode, failureStreak: null, now, cancellationToken);
            return new PollProjectResult(PollOutcomes.NoRepository);
        }

        if (probe.Status != GitProbeRequestStatus.Success)
        {
            // The project vanished (another tenant's id, or a concurrent delete): nothing to record.
            return new PollProjectResult(PollOutcomes.Unchanged);
        }

        if (!GitProbeOutcomes.IsSuccess(probe.Outcome) || probe.Refs is null)
        {
            // A failure is recorded verbatim - never as "no change" - and the backoff grows.
            var failureOutcome = ClassifyFailure(probe.Outcome, project.GitCredentialId);
            await RecordAsync(context, projectId, failureOutcome, probe.Message, failureStreak: null, now, cancellationToken);
            logger.LogInformation(
                "Poll could not read the remote. ProjectId={ProjectId} Outcome={Outcome}",
                projectId,
                probe.Outcome);
            return new PollProjectResult(failureOutcome);
        }

        // Which heads this project cares about: the tracked branch in single mode, every head in F-023's
        // multibranch mode (the card's AC-1).
        var heads = ReadHeads(probe.Refs, project.DefaultBranch, project.BranchDiscovery);
        var outcomes = new List<string>(heads.Count + 1);
        var queued = 0;

        foreach (var (branch, sha) in heads)
        {
            var decision = await PollBranchAsync(
                context, projectId, branch, sha, project.BranchDiscovery, now, cancellationToken);
            outcomes.Add(decision);
            if (string.Equals(decision, PollOutcomes.Queued, StringComparison.Ordinal))
            {
                queued++;
            }
        }

        if (queued > 0)
        {
            logger.LogInformation(
                "Poll queued run(s). ProjectId={ProjectId} Queued={Queued} Heads={Heads}",
                projectId,
                queued,
                heads.Count);
        }

        // A successful probe resets the backoff (AC-5) and records what the tick decided.
        var outcome = PollOutcomes.Aggregate(outcomes) ?? PollOutcomes.Unchanged;
        await RecordAsync(context, projectId, outcome, message: null, failureStreak: 0, now, cancellationToken);
        return new PollProjectResult(outcome, queued);
    }

    /// <summary>
    /// The head list this project polls: the tracked branch only in single mode, every <c>refs/heads</c> in
    /// multibranch mode. A branch a run can never store (over-long, empty) is skipped, the same bound the
    /// webhook and discovery paths apply.
    /// </summary>
    private static List<(string Branch, string Sha)> ReadHeads(
        GitProbeRefs refs,
        string? defaultBranch,
        string branchDiscovery)
    {
        var heads = new List<(string Branch, string Sha)>();
        var multibranch = BranchDiscoveryModes.IsMultibranch(branchDiscovery);

        foreach (var head in refs.Heads)
        {
            if (!head.Name.StartsWith(GitProbeRules.HeadPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var branch = head.Name[GitProbeRules.HeadPrefix.Length..];
            if (!Zenkins.Server.Branches.BranchIdentity.IsStorable(branch))
            {
                continue;
            }

            if (!multibranch && !string.Equals(branch, defaultBranch, StringComparison.Ordinal))
            {
                continue;
            }

            heads.Add((branch, head.Sha));
        }

        return heads;
    }

    /// <summary>
    /// Compares one observed head with its stored head and triggers the shared change path when it moved.
    /// Returns the recorded outcome for this branch.
    /// </summary>
    private async Task<string> PollBranchAsync(
        ZenkinsDbContext context,
        Guid projectId,
        string branch,
        string sha,
        string branchDiscovery,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var head = await context.BranchHeads.SingleOrDefaultAsync(
            candidate => candidate.ProjectId == projectId && candidate.Branch == branch,
            cancellationToken);

        if (head is null)
        {
            // A head this project has never polled. If a trigger already queued a run for exactly this
            // commit (a push arrived first, or polling was switched on after a webhook had been running),
            // the poll's answer is `duplicate` - not "new": the change is known, and the branch starts from
            // the head it already has.
            var alreadyTriggered = await context.ChangeTriggers
                .AsNoTracking()
                .AnyAsync(
                    trigger => trigger.ProjectId == projectId
                        && trigger.Branch == branch
                        && trigger.Commit == sha,
                    cancellationToken);

            // First sighting: record the head, queue nothing. Polling starts from "what is there now" -
            // enabling it on a repository with fifty branches must not queue fifty runs, and there is no
            // earlier head a change could be measured against.
            head = new BranchHead
            {
                OrganizationId = context.CurrentOrganizationId,
                ProjectId = projectId,
                Branch = branch,
                Sha = sha,
                FirstSeenAt = now,
                LastSeenAt = now,
                LastOutcome = alreadyTriggered ? PollOutcomes.Duplicate : PollOutcomes.FirstSighting,
                LastOutcomeAt = now,
            };
            context.BranchHeads.Add(head);
            await context.SaveChangesAsync(cancellationToken);
            return head.LastOutcome!;
        }

        head.LastSeenAt = now;

        if (string.Equals(head.Sha, sha, StringComparison.Ordinal))
        {
            head.LastOutcome = PollOutcomes.Unchanged;
            head.LastOutcomeAt = now;
            await context.SaveChangesAsync(cancellationToken);
            return PollOutcomes.Unchanged;
        }

        // F-023's branch verdict - the delivery path's own rule, applied to the second source of the same
        // observation: **unknown is never queued**, and neither is a branch that has no pipeline in it.
        // Without this a multibranch project with polling on would queue a run for every documentation
        // change and the worker would answer `pipeline-missing`, which is a failed run for a branch that
        // was never supposed to build. Only evidence about *this* commit counts: a branch whose earlier
        // answer spoke about an older head asks for a fresh tree read instead of inheriting it.
        if (BranchDiscoveryModes.IsMultibranch(branchDiscovery))
        {
            var (state, observedCommit) = await branches.GetVerdictAsync(projectId, branch, cancellationToken);
            if (!string.Equals(state, BranchPipelineStates.HasPipeline, StringComparison.Ordinal))
            {
                var verdictOutcome = string.Equals(state, BranchPipelineStates.NoPipeline, StringComparison.Ordinal)
                    && string.Equals(observedCommit, sha, StringComparison.Ordinal)
                        ? PollOutcomes.NoPipeline
                        : PollOutcomes.AwaitingObservation;

                // The head stays where it was: nothing was built, so the next tick must see the same move
                // again (and record the same honest answer) until an observation settles the branch.
                head.LastOutcome = verdictOutcome;
                head.LastOutcomeAt = now;
                await context.SaveChangesAsync(cancellationToken);

                // ...and the loop has to close, not just report. A branch left `unknown` with nothing asking
                // for a tree read stays unknown forever: polling would only re-ask the same unanswered
                // question, and the next push is exactly what this feature exists without. The delivery
                // path asks the definition registry for that read; this is the same call, best effort and
                // after the row committed (a Worker answer must never be overwritten by a later marker).
                if (string.Equals(verdictOutcome, PollOutcomes.AwaitingObservation, StringComparison.Ordinal))
                {
                    try
                    {
                        // No tag requirement: a poll has no webhook row to read one from, and reading a
                        // tree needs no trigger-level tags (the delivery path passes webhook.RequiredTags
                        // because it has them, not because the read needs them). When F-033 lands its
                        // requirement vocabulary, this is the line that must either carry the run's own
                        // requirement or say out loud that a tree read needs none - not grow a second
                        // source of tags here.
                        await observations.EnqueueBranchObservationAsync(
                            projectId, branch, sha, requiredTags: null, cancellationToken);
                    }
                    catch (Exception failure)
                    {
                        logger.LogWarning(
                            failure,
                            "Branch observation not requested at poll. ProjectId={ProjectId} Branch={Branch}",
                            projectId,
                            branch);
                    }
                }

                return verdictOutcome;
            }
        }

        // The head moved. Hand it to the one shared change-trigger path - the same one a webhook delivery
        // calls - so the (project, branch, commit) ledger decides whether this is a new change or a
        // duplicate of one the other path already queued.
        var trigger = await changes.TriggerAsync(
            context,
            projectId,
            branch,
            sha,
            BuildRunTriggerType.Poll,
            requiredTags: [],
            cancellationToken);

        // A refusal is recorded as a *skip with the reason*, not as the queue's bare code: the card tells an
        // operator "skipped: the queue is at its cap", and the head stays where it was.
        var decision = SkipOutcomeFor(trigger.Outcome);
        head.LastOutcome = decision;
        head.LastOutcomeAt = now;

        switch (decision)
        {
            case PollOutcomes.Queued:
                head.Sha = sha;
                head.LastRunAt = now;
                head.LastRunCommit = sha;
                break;

            case PollOutcomes.Duplicate:
                // The commit is already queued by the other path: the head is current, nothing to queue.
                head.Sha = sha;
                break;

            default:
                // Refused (disabled / queue full): the head stays where it was, so the next tick sees the
                // newest commit again and can still build it.
                break;
        }

        await context.SaveChangesAsync(cancellationToken);
        return decision;
    }

    /// <summary>
    /// Maps the shared trigger's refusal codes onto the poll vocabulary, so a project-gate refusal is a
    /// recorded skip with its reason rather than the queue's internal word.
    /// </summary>
    private static string SkipOutcomeFor(string outcome) => outcome switch
    {
        ProjectTriggerRefusals.QueueFull => PollOutcomes.SkippedQueueFull,
        ProjectTriggerRefusals.ProjectDisabled => PollOutcomes.SkippedDisabled,
        _ => outcome,
    };

    /// <summary>
    /// Maps a probe failure onto the poll vocabulary. A rejected credential with <b>no credential assigned</b>
    /// is <see cref="PollOutcomes.MissingCredential"/>: the fix is on the settings card, and the card must say
    /// that instead of failing every interval with the same opaque word (AC-7).
    /// </summary>
    private static string ClassifyFailure(string? probeOutcome, Guid? gitCredentialId) => probeOutcome switch
    {
        GitProbeOutcomes.AuthRejected when gitCredentialId is null => PollOutcomes.MissingCredential,
        { } outcome => outcome,
        _ => PollOutcomes.Unchanged,
    };

    /// <summary>
    /// Records one tick's outcome on the project: the time, the outcome, the probe's message, and - when the
    /// caller says so - one more failure on the backoff curve (or a reset to zero).
    /// </summary>
    private async Task RecordAsync(
        ZenkinsDbContext context,
        Guid projectId,
        string outcome,
        string? message,
        int? failureStreak,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            return;
        }

        project.LastPollAt = now;
        project.LastPollOutcome = outcome;
        project.LastPollMessage = message is { Length: > 200 } ? message[..200] : message;

        if (failureStreak is { } streak)
        {
            project.PollFailureStreak = streak;
        }
        else if (message is not null || !PollOutcomes.All.Contains(outcome))
        {
            // A probe failure word is the only "not a fixed outcome" case that also grows the backoff.
            project.PollFailureStreak = Math.Min(project.PollFailureStreak + 1, 16);
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
