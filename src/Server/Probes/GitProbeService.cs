using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Git;
using Zenkins.Server.Workers;
using Zenkins.Shared.Probes;
using Zenkins.Shared.Projects;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Probes;

/// <summary>Outcome of one requesting call (the feature asking "what heads exist?").</summary>
public enum GitProbeRequestStatus
{
    /// <summary>The probe settled: <see cref="GitProbeRequestResult.Outcome"/> is the answer, success or typed failure.</summary>
    Success = 0,

    /// <summary>The project does not exist in the active organization (also the foreign case).</summary>
    NotFound = 1,

    /// <summary>The project cannot be probed (no repository) or the request was malformed.</summary>
    InvalidInput = 2,

    /// <summary>No organization is active, so there is no project to probe.</summary>
    Forbidden = 3,
}

/// <summary>
/// One settled probe, as the requesting feature sees it. <see cref="Outcome"/> is always set on
/// <see cref="GitProbeRequestStatus.Success"/> and is never guessed: <c>refs</c> means the refs are
/// real, every other value is a failure a caller must record - never "nothing changed".
/// </summary>
/// <param name="Status">Whether the request could be answered at all.</param>
/// <param name="ProbeId">The probe's id (the same one every joined caller sees).</param>
/// <param name="Outcome">A <see cref="GitProbeOutcomes"/> value.</param>
/// <param name="Refs">The heads and remote HEAD when <paramref name="Outcome"/> is <c>refs</c>.</param>
/// <param name="Message">The worker's diagnostic (a failure's explanation), if any.</param>
/// <param name="RequestCount">How many callers this one probe served (dedupe evidence).</param>
/// <param name="ErrorCode">A <see cref="GitProbeErrorCodes"/> value for <see cref="GitProbeRequestStatus.InvalidInput"/>.</param>
public sealed record GitProbeRequestResult(
    GitProbeRequestStatus Status,
    Guid? ProbeId = null,
    string? Outcome = null,
    GitProbeRefs? Refs = null,
    string? Message = null,
    int RequestCount = 0,
    string? ErrorCode = null)
{
    public static GitProbeRequestResult Success(GitProbeSnapshot probe) => new(
        GitProbeRequestStatus.Success,
        probe.Id,
        probe.Outcome,
        probe.Refs,
        probe.Message,
        probe.RequestCount);

    public static GitProbeRequestResult NotFound() => new(GitProbeRequestStatus.NotFound);

    public static GitProbeRequestResult Invalid(string errorCode) =>
        new(GitProbeRequestStatus.InvalidInput, ErrorCode: errorCode);

    public static GitProbeRequestResult Forbidden() => new(GitProbeRequestStatus.Forbidden);
}

/// <summary>Outcome of a worker's probe claim.</summary>
public enum GitProbeClaimStatus
{
    /// <summary>A probe was leased to the calling worker.</summary>
    Success = 0,

    /// <summary>
    /// Nothing is claimable for this worker: no pending probe, the worker is not online, not
    /// probe-capable, or disabled. Every reason collapses into this one value (the run claim's
    /// "never learn why" rule), so a claim is not a capability oracle.
    /// </summary>
    NoProbe = 1,

    /// <summary>The presented token is unknown or revoked: a bare 401.</summary>
    InvalidToken = 2,
}

/// <summary>The claim's answer: what the worker must probe, and until when it may.</summary>
public sealed record GitProbeClaimResult(
    GitProbeClaimStatus Status,
    Guid? ProbeId = null,
    Guid? ProjectId = null,
    string? RepositoryUrl = null,
    DateTimeOffset? LeaseExpiresAt = null)
{
    public static GitProbeClaimResult Success(GitProbeSnapshot probe) => new(
        GitProbeClaimStatus.Success,
        probe.Id,
        probe.ProjectId,
        probe.RepositoryUrl,
        probe.LeaseExpiresAt);

    public static GitProbeClaimResult NoProbe() => new(GitProbeClaimStatus.NoProbe);

    public static GitProbeClaimResult InvalidToken() => new(GitProbeClaimStatus.InvalidToken);
}

/// <summary>Outcome of a worker's probe report.</summary>
public enum GitProbeReportStatus
{
    /// <summary>Recorded: the probe is settled and the project's last outcome is updated.</summary>
    Success = 0,

    /// <summary>Missing/unknown/revoked worker token: a bare 401.</summary>
    InvalidToken = 1,

    /// <summary>Unknown probe id, or a probe of another organization (indistinguishable by design).</summary>
    NotFound = 2,

    /// <summary>The payload broke the shared contract; nothing was written.</summary>
    InvalidInput = 3,

    /// <summary>This worker no longer holds a live lease, or the probe was already settled.</summary>
    Conflict = 4,
}

/// <summary>The report's answer (the status plus a stable code for the two refusal kinds).</summary>
public sealed record GitProbeReportResult(
    GitProbeReportStatus Status,
    string? ErrorCode = null)
{
    public static GitProbeReportResult Success() => new(GitProbeReportStatus.Success);

    public static GitProbeReportResult InvalidToken() => new(GitProbeReportStatus.InvalidToken);

    public static GitProbeReportResult NotFound() => new(GitProbeReportStatus.NotFound);

    public static GitProbeReportResult Invalid(string errorCode) =>
        new(GitProbeReportStatus.InvalidInput, errorCode);

    public static GitProbeReportResult Conflict(string errorCode) =>
        new(GitProbeReportStatus.Conflict, errorCode);
}

/// <summary>One report's raw values, before validation.</summary>
public sealed record GitProbeReportInput(
    string? Outcome,
    IReadOnlyList<GitProbeRef>? Heads,
    string? HeadSha,
    string? HeadBranch,
    string? Message);

/// <summary>
/// The probe primitive of card I-005: ask the pull channel for a repository's heads without cloning,
/// without the Server running git and without an inbound port on any Worker.
/// <para>
/// <b>Three callers, one vocabulary.</b> A requesting feature (F-023, F-025, or a test) calls
/// <see cref="RequestAsync"/>; a Worker claims through <see cref="ClaimAsync"/> and answers through
/// <see cref="ReportAsync"/>; the material it needs is fetched through
/// <see cref="FetchMaterialForProbeAsync"/>. No caller ever hands in an organization id - the tenant
/// comes from <see cref="ITenantContext"/> (a feature) or the worker token row (a Worker), exactly like
/// every other tenant-owned path.
/// </para>
/// <para>
/// <b>Dedupe and the bounded wait.</b> <see cref="GitProbeRegistry"/> joins every caller of one
/// (project, repository) onto one in-flight probe; this service bounds how long the caller waits, and
/// settles the probe as <c>no-worker</c> when nobody claims it - so "there is no probe-capable Worker" is
/// an answer with a timestamp, not a hang.
/// </para>
/// <para>
/// <b>Nothing here is a run.</b> A probe writes no <c>BuildRun</c>, takes no build slot, owns no lease in
/// the run table and appears on no dashboard, run list or history page. The only persisted trace is the
/// project's last probe outcome (which the project page states), plus the log lines.
/// </para>
/// </summary>
public sealed class GitProbeService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    ITenantContext tenantContext,
    GitProbeRegistry registry,
    WorkerService workers,
    GitCredentialService credentials,
    ProbeOptions options,
    ILogger<GitProbeService> logger)
{
    /// <summary>How much of a worker's message the project row keeps (the column's bound).</summary>
    private const int ProjectMessageLength = 200;

    /// <summary>
    /// Asks for the project's repository heads and waits (bounded) for the answer. Consecutive callers
    /// inside the dedupe window share one probe and therefore one <c>git ls-remote</c>.
    /// </summary>
    public async Task<GitProbeRequestResult> RequestAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (tenantContext.OrganizationId is not { } organizationId)
        {
            return GitProbeRequestResult.Forbidden();
        }

        // The project read is its own short unit of work: the wait below can last as long as one lease,
        // and holding a database connection open for it would be a connection held across a poll loop.
        Project? project;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            project = await context.Projects
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);
        }

        if (project is null)
        {
            // Foreign or unknown id: one answer, no existence oracle.
            return GitProbeRequestResult.NotFound();
        }

        // F-020/F-022: a project with no repository (Freestyle, or an inline Pipeline) has nothing to
        // probe. The shared predicate answers "has a repository"; an empty URL is still a separate
        // fact (a row written out of band), and both are the same refusal.
        if (!ProjectSources.HasRepository(project.Kind, project.PipelineSource)
            || string.IsNullOrWhiteSpace(project.RepositoryUrl))
        {
            return GitProbeRequestResult.Invalid(GitProbeErrorCodes.ProjectHasNoRepository);
        }

        var joined = registry.JoinOrStart(
            organizationId,
            project.Id,
            project.RepositoryUrl,
            options.DedupeWindow,
            options.Retention);
        var probe = joined.Snapshot!;
        if (joined.Joined)
        {
            logger.LogInformation(
                "Git probe shared. OrganizationId={OrganizationId} ProjectId={ProjectId} ProbeId={ProbeId} RequestCount={RequestCount}",
                organizationId,
                project.Id,
                probe.Id,
                probe.RequestCount);
        }

        // A joined probe that already settled (a fresh answer inside the dedupe window) is returned
        // without waiting: that is what makes the window useful to a poller.
        if (probe.IsSettled)
        {
            return GitProbeRequestResult.Success(probe);
        }

        // Two bounds, one promise: the caller waits at most `Wait` for a claimant (that is the card's
        // "no-worker after a bounded wait"), plus at most one lease for a claimant that may still report.
        // Beyond that the probe is settled as no-worker even if a silent worker holds a lease, so a
        // request always ends with a typed answer.
        var startedAt = DateTimeOffset.UtcNow;
        var claimDeadline = startedAt + options.Wait;
        var hardDeadline = claimDeadline + options.Lease;
        while (!cancellationToken.IsCancellationRequested)
        {
            var snapshot = registry.Find(probe.Id);
            if (snapshot is null)
            {
                // Pruned or never there: nothing to report about this probe any more.
                return GitProbeRequestResult.NotFound();
            }

            if (snapshot.IsSettled)
            {
                return GitProbeRequestResult.Success(snapshot);
            }

            var now = DateTimeOffset.UtcNow;
            if (now >= claimDeadline)
            {
                var abandoned = registry.TryAbandon(
                    probe.Id,
                    now,
                    options.Wait,
                    force: now >= hardDeadline);
                if (abandoned.SettledByThisCall && abandoned.Snapshot is { } settled)
                {
                    await RecordProjectOutcomeAsync(settled, cancellationToken);
                    return GitProbeRequestResult.Success(settled);
                }

                if (abandoned.Snapshot is null)
                {
                    return GitProbeRequestResult.NotFound();
                }

                // A live lease (and the hard ceiling not reached yet): keep waiting for the report. The
                // next pass either sees it, or reaches the ceiling and settles the probe above.
            }

            try
            {
                await Task.Delay(options.PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The caller gave up (a shutdown, a canceled request). The probe stays in flight: the
                // Worker may still answer it, and a later caller inside the window collects that answer.
                return GitProbeRequestResult.NotFound();
            }
        }

        return GitProbeRequestResult.NotFound();
    }

    /// <summary>
    /// Leases the next claimable probe to the calling Worker. Eligibility mirrors the run claim's
    /// discipline (online, not disabled) plus the card's capability flag: a Worker that never said it can
    /// probe is never handed one, so an older Worker binary keeps building runs untouched.
    /// </summary>
    public async Task<GitProbeClaimResult> ClaimAsync(
        string? plaintextToken,
        CancellationToken cancellationToken = default)
    {
        // Authenticate before anything else: a bare 401, no tenant detail (the run claim's rule).
        var workerToken = await workers.AuthenticateWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || workerToken.WorkerId is not { } workerId)
        {
            return GitProbeClaimResult.InvalidToken();
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var worker = await context.Workers
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == workerId, cancellationToken);
        if (worker is null
            || worker.Status != WorkerStatus.Online
            || worker.IsDisabled
            || !worker.CanProbe)
        {
            // No probe for you - and no hint of which of those reasons it was.
            return GitProbeClaimResult.NoProbe();
        }

        var probe = registry.TryLease(
            workerToken.OrganizationId,
            worker.Id,
            DateTimeOffset.UtcNow,
            options.Lease);
        return probe is null ? GitProbeClaimResult.NoProbe() : GitProbeClaimResult.Success(probe);
    }

    /// <summary>
    /// Records one Worker's answer. The payload is validated against the shared contract before it can
    /// reach the registry, and the result is written to the project's last-outcome columns in the same
    /// call - so a failure is always visible, and never becomes "no change".
    /// </summary>
    public async Task<GitProbeReportResult> ReportAsync(
        string? plaintextToken,
        Guid probeId,
        GitProbeReportInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Authenticate before validating the payload (the F-004/F-006 rule), so a tokenless caller
        // cannot probe the contract through a 400.
        var workerToken = await workers.AuthenticateWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || workerToken.WorkerId is not { } workerId)
        {
            return GitProbeReportResult.InvalidToken();
        }

        if (GitProbeRules.ValidateReport(
                input.Outcome,
                input.Heads,
                input.HeadSha,
                input.HeadBranch,
                input.Message) is { } validationError)
        {
            return GitProbeReportResult.Invalid(validationError);
        }

        var outcome = input.Outcome!;
        var refs = GitProbeOutcomes.IsSuccess(outcome)
            ? new GitProbeRefs(
                [.. (input.Heads ?? []).OrderBy(head => head.Name, StringComparer.Ordinal)],
                input.HeadSha,
                input.HeadBranch)
            : null;

        var reported = registry.TryReport(
            workerToken.OrganizationId,
            probeId,
            workerId,
            outcome,
            refs,
            input.Message,
            DateTimeOffset.UtcNow);

        if (reported.Snapshot is null)
        {
            return GitProbeReportResult.NotFound();
        }

        if (reported.ErrorCode is { } errorCode)
        {
            return GitProbeReportResult.Conflict(errorCode);
        }

        await RecordProjectOutcomeAsync(reported.Snapshot, cancellationToken);
        return GitProbeReportResult.Success();
    }

    /// <summary>
    /// The material for one probe's <c>git ls-remote</c> (card I-005's credential path): the project's
    /// assigned credential, delivered only to the Worker that currently holds that probe's lease.
    /// <para>
    /// <b>Scoped to the probe, not to the project's runs.</b> The answer needs a live probe lease held by
    /// the calling Worker, so a probe id can never fetch a run's material and a run id can never reach
    /// this path (the two routes take different ids and resolve different leases). Every miss - unknown
    /// probe, another tenant's probe, another worker's lease, an expired lease, no credential configured -
    /// is the same bodyless 404.
    /// </para>
    /// </summary>
    public async Task<GitCredentialFetchResult> FetchMaterialForProbeAsync(
        string? plaintextToken,
        Guid probeId,
        CancellationToken cancellationToken = default)
    {
        var workerToken = await workers.AuthenticateWorkerAsync(plaintextToken, cancellationToken);
        if (workerToken is null || workerToken.WorkerId is not { } workerId)
        {
            return GitCredentialFetchResult.InvalidToken();
        }

        var probe = registry.Find(probeId);
        var now = DateTimeOffset.UtcNow;
        if (probe is null
            || probe.OrganizationId != workerToken.OrganizationId
            || probe.Status != GitProbeStatus.Leased
            || probe.WorkerId != workerId
            || probe.LeaseExpiresAt is not { } expiry
            || expiry <= now)
        {
            logger.LogInformation(
                "Git probe credential request resolved no material. ProbeId={ProbeId} WorkerId={WorkerId}",
                probeId,
                workerId);
            return GitCredentialFetchResult.NotFound();
        }

        return await credentials.FetchMaterialForProjectAsync(
            probe.ProjectId,
            new GitCredentialFetchScope(workerId, ProbeId: probe.Id),
            cancellationToken);
    }

    /// <summary>
    /// Writes the probe's outcome onto the project - the card's "last outcome on the project". A project
    /// deleted while the probe was in flight has nothing to record; the log line already carries the
    /// answer.
    /// </summary>
    private async Task RecordProjectOutcomeAsync(
        GitProbeSnapshot probe,
        CancellationToken cancellationToken)
    {
        // The guarded statement writes the three probe columns and nothing else, so a probe does not look
        // like a project edit on the overview (its "Updated" timestamp is the operator's, not the poller's).
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var updated = await context.TryRecordProjectProbeOutcomeAsync(
            probe.ProjectId,
            probe.Outcome!,
            probe.CompletedAt ?? DateTimeOffset.UtcNow,
            Truncate(probe.Message),
            cancellationToken);
        if (updated == 0)
        {
            logger.LogInformation(
                "Git probe outcome not recorded: the project is gone. OrganizationId={OrganizationId} ProjectId={ProjectId} ProbeId={ProbeId} Outcome={Outcome}",
                probe.OrganizationId,
                probe.ProjectId,
                probe.Id,
                probe.Outcome);
            return;
        }

        // The probe's own log line: which project, which probe, which answer. The refs themselves are
        // not logged (a branch name is the caller's business, and a log is not a data store).
        logger.LogInformation(
            "Git probe settled on the project. OrganizationId={OrganizationId} ProjectId={ProjectId} ProbeId={ProbeId} Outcome={Outcome} Heads={HeadCount} Succeeded={Succeeded}",
            probe.OrganizationId,
            probe.ProjectId,
            probe.Id,
            probe.Outcome,
            probe.Refs?.Heads.Count ?? 0,
            probe.Succeeded);
    }

    private static string? Truncate(string? message) =>
        message is { Length: > ProjectMessageLength }
            ? message[..ProjectMessageLength]
            : message;
}
