using Zenkins.Shared.Probes;

namespace Zenkins.Server.Probes;

/// <summary>The lifecycle of one in-memory probe (card I-005).</summary>
public static class GitProbeStatus
{
    /// <summary>Created; no worker holds it. The only state a claim can take.</summary>
    public const string Pending = "Pending";

    /// <summary>A worker holds the probe under a lease and owes a report.</summary>
    public const string Leased = "Leased";

    /// <summary>A worker reported; <see cref="GitProbeSnapshot.Outcome"/> is the answer.</summary>
    public const string Completed = "Completed";

    /// <summary>The Server settled it with <see cref="GitProbeOutcomes.NoWorker"/>: nobody claimed it in time.</summary>
    public const string NoWorker = "NoWorker";
}

/// <summary>
/// One probe as callers observe it (an immutable copy, never the mutable entry). Records keep the
/// registry's lock out of every reader.
/// </summary>
/// <param name="Id">The probe's id: what the claim hands out and the report addresses.</param>
/// <param name="OrganizationId">The owning tenant (asserted on every claim and report).</param>
/// <param name="ProjectId">The project whose credential the probe uses.</param>
/// <param name="RepositoryUrl">The repository snapshot the probe was requested for.</param>
/// <param name="Status">One of <see cref="GitProbeStatus"/>.</param>
/// <param name="RequestCount">How many callers this probe serves (including the first) - the dedupe evidence.</param>
/// <param name="Attempts">How many leases were handed out (a re-offer after an expired lease counts).</param>
/// <param name="WorkerId">The current (or last) leaseholder.</param>
/// <param name="LeaseExpiresAt">When the current lease lapses; null when unleased.</param>
/// <param name="Outcome">The settled outcome, null while in flight.</param>
/// <param name="Refs">The refs when <paramref name="Outcome"/> is <c>refs</c>.</param>
/// <param name="Message">The worker's (or the Server's) diagnostic, if any.</param>
/// <param name="RequestedAt">When the first caller asked.</param>
/// <param name="CompletedAt">When the probe settled (reported or abandoned).</param>
public sealed record GitProbeSnapshot(
    Guid Id,
    Guid OrganizationId,
    Guid ProjectId,
    string RepositoryUrl,
    string Status,
    int RequestCount,
    int Attempts,
    Guid? WorkerId,
    DateTimeOffset? LeaseExpiresAt,
    string? Outcome,
    GitProbeRefs? Refs,
    string? Message,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt)
{
    /// <summary>Whether the probe has an answer (a success <b>or</b> a typed failure).</summary>
    public bool IsSettled => Status is GitProbeStatus.Completed or GitProbeStatus.NoWorker;

    /// <summary>Whether the answer is the refs payload.</summary>
    public bool Succeeded => IsSettled && GitProbeOutcomes.IsSuccess(Outcome);
}

/// <summary>What one registry operation did, around the snapshot it produced.</summary>
/// <param name="Snapshot">The probe, or null when the id is unknown.</param>
/// <param name="Joined">Whether this call joined an existing probe instead of creating one (dedupe).</param>
/// <param name="SettledByThisCall">Whether <b>this</b> call is the one that settled it (so it owns the
/// project's last-outcome write; every other caller just reads).</param>
/// <param name="ErrorCode">A <see cref="GitProbeErrorCodes"/> value when the call was refused.</param>
public sealed record GitProbeRegistryResult(
    GitProbeSnapshot? Snapshot,
    bool Joined = false,
    bool SettledByThisCall = false,
    string? ErrorCode = null);

/// <summary>
/// The Server's probe queue, lease and dedupe table (card I-005) - <b>in memory, deliberately</b>.
/// <para>
/// <b>Why memory instead of a table.</b> The product is single-Server (PRD §3.2 deployment model), and a
/// probe is a short-lived question whose answer nobody replays: if this process dies mid-probe, the
/// caller that was waiting is gone too, and F-023/F-025 ask again - a lost probe is re-requested, never
/// replayed. A table would buy durability the feature has no use for while adding a second write path
/// inside every claim. What <em>must</em> survive a restart is the outcome's visibility on the project,
/// and that is persisted (<c>Project.LastProbeOutcome</c>); this registry owns only
/// the in-flight part. A future multi-Server deployment (parking lot) would have to persist this table -
/// recorded here so that change starts at the right place.
/// </para>
/// <para>
/// <b>One probe per (project, repository).</b> <see cref="JoinOrStart"/> returns an existing in-flight
/// probe to every caller that asks while it runs, and a freshly completed one to callers inside the
/// dedupe window, so three features asking at once produce one git conversation, not three. A settled
/// <c>no-worker</c> probe is never joined: the next caller wants a fresh attempt, not a cached "nobody
/// was there".
/// </para>
/// <para>
/// <b>Leases, not slots.</b> A probe occupies no build slot: the build claim counts
/// <c>BuildRuns</c>, and this registry is not part of that count. Its lease exists only to bound how
/// long a silent worker may hold a probe before another worker may take it over, and it is re-offerable
/// after expiry (a crashed worker must not wedge a probe for its whole dedupe window).
/// </para>
/// <para>
/// <b>Concurrency.</b> One lock guards every mutation; the work under it is a dictionary lookup and a
/// few field writes, so the critical section is microseconds and no caller ever awaits inside it. Every
/// reader gets an immutable <see cref="GitProbeSnapshot"/>.
/// </para>
/// </summary>
public sealed class GitProbeRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, MutableProbe> _probes = [];
    private readonly ILogger<GitProbeRegistry> _logger;

    public GitProbeRegistry(ILogger<GitProbeRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// The dedupe-or-create call every requesting feature makes: join the live probe of this
    /// (project, repository) if one exists inside the window, otherwise start one.
    /// </summary>
    /// <param name="organizationId">The owning tenant (the probe is never cross-tenant).</param>
    /// <param name="projectId">The project asking.</param>
    /// <param name="repositoryUrl">The repository snapshot (part of the dedupe key).</param>
    /// <param name="dedupeWindow">How long a completed probe stays joinable (its answer's freshness).</param>
    /// <param name="retention">How long a settled probe is remembered at all (pruning bound).</param>
    public GitProbeRegistryResult JoinOrStart(
        Guid organizationId,
        Guid projectId,
        string repositoryUrl,
        TimeSpan dedupeWindow,
        TimeSpan retention)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            Prune(now, retention);

            // Newest first: by construction at most one probe of a pair is joinable at a time, and if two
            // ever were, the newer one's answer is the current one.
            var existing = _probes.Values
                .OrderByDescending(probe => probe.RequestedAt)
                .ThenBy(probe => probe.Id)
                .FirstOrDefault(probe =>
                probe.ProjectId == projectId
                && probe.OrganizationId == organizationId
                && string.Equals(probe.RepositoryUrl, repositoryUrl, StringComparison.Ordinal)
                && IsJoinable(probe, now, dedupeWindow));

            if (existing is not null)
            {
                existing.RequestCount++;
                return new GitProbeRegistryResult(existing.ToSnapshot(), Joined: true);
            }

            var created = new MutableProbe
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                ProjectId = projectId,
                RepositoryUrl = repositoryUrl,
                RequestedAt = now,
            };
            _probes.Add(created.Id, created);
            _logger.LogInformation(
                "Git probe requested. OrganizationId={OrganizationId} ProjectId={ProjectId} ProbeId={ProbeId}",
                organizationId,
                projectId,
                created.Id);
            return new GitProbeRegistryResult(created.ToSnapshot());
        }
    }

    /// <summary>
    /// Leases the oldest claimable probe of <paramref name="organizationId"/> to
    /// <paramref name="workerId"/>, or answers null when there is none. A probe whose lease lapsed is
    /// re-offered (an attempt was lost, not the probe); a probe another worker holds a live lease on is
    /// never stolen.
    /// </summary>
    public GitProbeSnapshot? TryLease(
        Guid organizationId,
        Guid workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration)
    {
        lock (_gate)
        {
            var candidate = _probes.Values
                .Where(probe => probe.OrganizationId == organizationId && IsClaimable(probe, now))
                .OrderBy(probe => probe.RequestedAt)
                .ThenBy(probe => probe.Id)
                .FirstOrDefault();
            if (candidate is null)
            {
                return null;
            }

            candidate.Status = GitProbeStatus.Leased;
            candidate.WorkerId = workerId;
            candidate.Attempts++;
            candidate.LeaseExpiresAt = now + leaseDuration;
            candidate.StartedAt ??= now;

            _logger.LogInformation(
                "Git probe leased. OrganizationId={OrganizationId} ProbeId={ProbeId} WorkerId={WorkerId} Attempt={Attempt}",
                organizationId,
                candidate.Id,
                workerId,
                candidate.Attempts);
            return candidate.ToSnapshot();
        }
    }

    /// <summary>
    /// Records a worker's answer for a probe it holds. Refusals are explicit: a probe of another tenant
    /// or an unknown id is <see cref="NotFound"/>-shaped (no existence oracle), while another worker's
    /// live lease, an expired lease and an already settled probe are conflicts.
    /// </summary>
    public GitProbeRegistryResult TryReport(
        Guid organizationId,
        Guid probeId,
        Guid workerId,
        string outcome,
        GitProbeRefs? refs,
        string? message,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_probes.TryGetValue(probeId, out var probe)
                || probe.OrganizationId != organizationId)
            {
                return new GitProbeRegistryResult(null);
            }

            if (probe.IsSettled)
            {
                return new GitProbeRegistryResult(probe.ToSnapshot(), ErrorCode: GitProbeErrorCodes.AlreadySettled);
            }

            if (probe.WorkerId != workerId)
            {
                return new GitProbeRegistryResult(probe.ToSnapshot(), ErrorCode: GitProbeErrorCodes.LeaseLost);
            }

            if (probe.LeaseExpiresAt is { } expiry && expiry <= now)
            {
                return new GitProbeRegistryResult(probe.ToSnapshot(), ErrorCode: GitProbeErrorCodes.LeaseLost);
            }

            Settle(probe, GitProbeStatus.Completed, outcome, refs, message, now);
            _logger.LogInformation(
                "Git probe reported. OrganizationId={OrganizationId} ProjectId={ProjectId} ProbeId={ProbeId} WorkerId={WorkerId} Outcome={Outcome} Heads={HeadCount}",
                organizationId,
                probe.ProjectId,
                probe.Id,
                workerId,
                outcome,
                refs?.Heads.Count ?? 0);
            return new GitProbeRegistryResult(probe.ToSnapshot(), SettledByThisCall: true);
        }
    }

    /// <summary>
    /// The requesting caller's deadline: settle an unclaimed (or lease-expired) probe as
    /// <see cref="GitProbeOutcomes.NoWorker"/>, so "nobody is there" is recorded once and is visible
    /// instead of the caller waiting forever.
    /// <para>
    /// A probe a worker holds under a live lease is left alone - the worker's report may still land. Only
    /// when <paramref name="force"/> is set (the caller's hard ceiling: the bounded wait plus one lease) is
    /// such a probe settled too, with a message that says a worker claimed it and never answered. That is
    /// the only case where a report arriving later is refused (409) - and it is the honest answer to "no
    /// answer arrived in time".
    /// </para>
    /// </summary>
    public GitProbeRegistryResult TryAbandon(Guid probeId, DateTimeOffset now, TimeSpan waited, bool force = false)
    {
        lock (_gate)
        {
            if (!_probes.TryGetValue(probeId, out var probe) || probe.IsSettled)
            {
                return new GitProbeRegistryResult(probe?.ToSnapshot());
            }

            if (!force && !IsClaimable(probe, now))
            {
                // Someone holds a live lease: the probe is still being answered.
                return new GitProbeRegistryResult(probe.ToSnapshot());
            }

            var message = probe.Attempts == 0
                ? $"No probe-capable Worker claimed this probe within {waited.TotalSeconds:0.#}s "
                    + "(no such Worker registered, or none came back in time)."
                : $"A Worker claimed this probe but reported no answer within {waited.TotalSeconds:0.#}s "
                    + $"({probe.Attempts} lease attempt(s)); the probe was abandoned.";
            Settle(probe, GitProbeStatus.NoWorker, GitProbeOutcomes.NoWorker, refs: null, message, now);
            _logger.LogWarning(
                "Git probe not claimed by any Worker. OrganizationId={OrganizationId} ProjectId={ProjectId} ProbeId={ProbeId} WaitedSeconds={WaitedSeconds} Attempts={Attempts}",
                probe.OrganizationId,
                probe.ProjectId,
                probe.Id,
                waited.TotalSeconds,
                probe.Attempts);
            return new GitProbeRegistryResult(probe.ToSnapshot(), SettledByThisCall: true);
        }
    }

    /// <summary>One probe by id, or null. Reads never take the tenant into account; callers assert it.</summary>
    public GitProbeSnapshot? Find(Guid probeId)
    {
        lock (_gate)
        {
            return _probes.TryGetValue(probeId, out var probe) ? probe.ToSnapshot() : null;
        }
    }

    /// <summary>
    /// Every probe currently held, oldest first. Diagnostics and tests read this; no product decision is
    /// made from it (the queue decisions above are the ones under the lock).
    /// </summary>
    public IReadOnlyList<GitProbeSnapshot> Snapshots
    {
        get
        {
            lock (_gate)
            {
                return [.. _probes.Values
                    .OrderBy(probe => probe.RequestedAt)
                    .ThenBy(probe => probe.Id)
                    .Select(probe => probe.ToSnapshot())];
            }
        }
    }

    /// <summary>How many probes the registry holds (tests and diagnostics).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _probes.Count;
            }
        }
    }

    /// <summary>
    /// Whether a claim may take this probe: still pending, or leased by a worker whose lease has lapsed
    /// (the re-offer). A completed/abandoned probe never returns to the queue.
    /// </summary>
    private static bool IsClaimable(MutableProbe probe, DateTimeOffset now) => probe.Status switch
    {
        GitProbeStatus.Pending => true,
        GitProbeStatus.Leased => probe.LeaseExpiresAt is { } expiry && expiry <= now,
        _ => false,
    };

    /// <summary>
    /// Whether a new caller joins this probe: everything in flight, plus a probe that just completed
    /// inside the dedupe window (its answer is still fresh). A <c>no-worker</c> probe is never joined.
    /// </summary>
    private static bool IsJoinable(MutableProbe probe, DateTimeOffset now, TimeSpan dedupeWindow) =>
        probe.Status switch
        {
            GitProbeStatus.Pending or GitProbeStatus.Leased => true,
            GitProbeStatus.Completed => probe.CompletedAt is { } completed
                && completed + dedupeWindow > now,
            _ => false,
        };

    private static void Settle(
        MutableProbe probe,
        string status,
        string outcome,
        GitProbeRefs? refs,
        string? message,
        DateTimeOffset now)
    {
        probe.Status = status;
        probe.Outcome = outcome;
        probe.Refs = refs;
        probe.Message = message;
        probe.CompletedAt = now;
        probe.LeaseExpiresAt = null;
    }

    /// <summary>
    /// Forgets settled probes older than the retention window. Runs under the same lock as every other
    /// mutation, so the registry stays bounded no matter how many callers came and went.
    /// </summary>
    private void Prune(DateTimeOffset now, TimeSpan retention)
    {
        if (_probes.Count == 0)
        {
            return;
        }

        var expired = _probes.Values
            .Where(probe => probe.IsSettled
                && probe.CompletedAt is { } completed
                && completed + retention <= now)
            .Select(probe => probe.Id)
            .ToArray();
        foreach (var id in expired)
        {
            _probes.Remove(id);
        }
    }

    /// <summary>The mutable entry. Never leaves the lock - callers only see <see cref="ToSnapshot"/>.</summary>
    private sealed class MutableProbe
    {
        public required Guid Id { get; init; }

        public required Guid OrganizationId { get; init; }

        public required Guid ProjectId { get; init; }

        public required string RepositoryUrl { get; init; }

        public string Status { get; set; } = GitProbeStatus.Pending;

        public int RequestCount { get; set; } = 1;

        public int Attempts { get; set; }

        public Guid? WorkerId { get; set; }

        public DateTimeOffset? LeaseExpiresAt { get; set; }

        public DateTimeOffset? StartedAt { get; set; }

        public string? Outcome { get; set; }

        public GitProbeRefs? Refs { get; set; }

        public string? Message { get; set; }

        public required DateTimeOffset RequestedAt { get; init; }

        public DateTimeOffset? CompletedAt { get; set; }

        public bool IsSettled => Status is GitProbeStatus.Completed or GitProbeStatus.NoWorker;

        public GitProbeSnapshot ToSnapshot() => new(
            Id,
            OrganizationId,
            ProjectId,
            RepositoryUrl,
            Status,
            RequestCount,
            Attempts,
            WorkerId,
            LeaseExpiresAt,
            Outcome,
            Refs,
            Message,
            RequestedAt,
            CompletedAt);
    }
}
