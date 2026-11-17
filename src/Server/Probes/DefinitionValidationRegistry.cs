using Zenkins.Shared.Pipelines;

namespace Zenkins.Server.Probes;

/// <summary>One in-flight definition-only validation (card F-022). Mutable and guarded by the registry's lock.</summary>
public sealed class DefinitionValidationEntry
{
    public required Guid Id { get; init; }

    public required Guid OrganizationId { get; init; }

    public required Guid ProjectId { get; init; }

    /// <summary>
    /// Which subject this task describes (<see cref="DefinitionValidationKinds"/>). An inline-script
    /// task carries <see cref="Script"/>; a repository-branch task carries the repository snapshot
    /// (<see cref="RepositoryUrl"/>, <see cref="Branch"/>, <see cref="Commit"/>, <see cref="PipelinePath"/>)
    /// and no script.
    /// </summary>
    public string Kind { get; init; } = DefinitionValidationKinds.InlineScript;

    /// <summary>The Lua body to describe (inline tasks only).</summary>
    public string? Script { get; init; }

    /// <summary>The repository to read (repository-branch tasks only).</summary>
    public string? RepositoryUrl { get; init; }

    /// <summary>The branch to check out (repository-branch tasks only).</summary>
    public string? Branch { get; init; }

    /// <summary>The commit to pin - the evidence commit a verdict is about (repository-branch tasks only).</summary>
    public string? Commit { get; init; }

    /// <summary>The project's pipeline path at request time (repository-branch tasks only).</summary>
    public string? PipelinePath { get; init; }

    /// <summary>The tags a run queued from this observation carries (a webhook's required tags).</summary>
    public string RequiredTags { get; init; } = string.Empty;

    public required DateTimeOffset CreatedAt { get; init; }

    public Guid? WorkerId { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public bool Settled { get; set; }

    /// <summary>The outcome word the Worker reported (<see cref="DefinitionValidationOutcomes"/>), or null.</summary>
    public string? Outcome { get; set; }

    /// <summary>Inline tasks: whether the script's definition phase produced a spec.</summary>
    public bool Valid { get; set; }

    public string? SpecJson { get; set; }

    public string? Diagnostic { get; set; }

    public string? FailureReason { get; set; }
}

/// <summary>One settled validation, as the Pipelines tab renders it.</summary>
public sealed record DefinitionValidationView(
    Guid Id,
    bool Valid,
    string? SpecJson,
    IReadOnlyList<string> StepNames,
    string? Diagnostic,
    string? FailureReason,
    bool NoWorker)
{
    public static DefinitionValidationView From(DefinitionValidationEntry entry) => new(
        entry.Id,
        entry.Valid,
        entry.SpecJson,
        ReadStepNames(entry.SpecJson),
        entry.Diagnostic,
        entry.FailureReason,
        NoWorker: entry.FailureReason == DefinitionValidationFailureReasons.NoWorker);

    private static IReadOnlyList<string> ReadStepNames(string? specJson) =>
        PipelineSpecJson.TryRead(specJson, out var spec, out _) && spec is not null
            ? [.. spec.Steps.Select(step => step.Name)]
            : [];
}

/// <summary>
/// The reasons the Server itself can attach to a validation (a Worker reports only valid/invalid).
/// </summary>
public static class DefinitionValidationFailureReasons
{
    public const string NoWorker = "no-worker";
}

/// <summary>
/// The in-memory queue of definition-only validations (card F-022). It reuses the shape card I-005 gave
/// the Git probe - a transient request the Server queues, a Worker claims and reports, a bounded caller
/// wait - because the property that matters is the same: <b>it is not a run</b>. No <c>BuildRun</c> row,
/// no build slot, no run work directory, no history entry; the only trace is the result the page shows.
/// <para>
/// The registry holds scripts in memory (bounded by <see cref="PipelineScriptLimits.MaxScriptBytes"/>
/// each and a small entry cap), so a page can validate text the user has not saved yet. Nothing here is
/// durable: a Server restart simply drops a pending validation, and the page says no result arrived.
/// </para>
/// </summary>
public sealed class DefinitionValidationRegistry
{
    /// <summary>
    /// The most pending/settled entries kept. A settled entry is pruned before a pending one, so a busy
    /// discovery pass cannot evict the task a Worker is about to claim.
    /// </summary>
    public const int MaxEntries = 256;

    private readonly Lock _gate = new();
    private readonly List<DefinitionValidationEntry> _entries = [];
    private readonly Dictionary<Guid, DefinitionValidationView> _lastByProject = [];

    /// <summary>Remembers the project's most recent settled validation for the tab to render.</summary>
    public void Remember(DefinitionValidationView view, Guid projectId)
    {
        lock (_gate)
        {
            _lastByProject[projectId] = view;
        }
    }

    /// <summary>The project's most recent validation, or null when none has settled on this Server.</summary>
    public DefinitionValidationView? LastFor(Guid projectId)
    {
        lock (_gate)
        {
            return _lastByProject.TryGetValue(projectId, out var view) ? view : null;
        }
    }

    /// <summary>Queues one script for a Worker and returns the entry the caller waits on.</summary>
    public DefinitionValidationEntry Enqueue(Guid organizationId, Guid projectId, string script, DateTimeOffset now)
    {
        var entry = new DefinitionValidationEntry
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ProjectId = projectId,
            Script = script,
            CreatedAt = now,
        };

        lock (_gate)
        {
            Add(entry);
        }

        return entry;
    }

    /// <summary>
    /// Queues (or joins) one branch observation for a Worker: read <paramref name="branch"/>'s tree at
    /// <paramref name="commit"/> and answer has-pipeline / no-pipeline.
    /// <para>
    /// <b>Memoised per (project, branch, commit) while in flight.</b> An unsettled task for the same key
    /// is shared, so ten pushes to one branch cannot spawn ten clones. A <em>settled</em> task is not
    /// reused: its verdict is already on the branch row (and a settled task left behind by a failed write
    /// must not block the retry that would fix it). That is also what keeps a discovery sweep from
    /// turning into a per-branch probe storm - the sweep asks for the same key a push already asked for.
    /// </para>
    /// </summary>
    /// <returns>The entry and whether this call created it (false: joined or already answered).</returns>
    public (DefinitionValidationEntry Entry, bool Created) EnqueueObservation(
        Guid organizationId,
        Guid projectId,
        string repositoryUrl,
        string branch,
        string commit,
        string pipelinePath,
        string requiredTags,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            var existing = _entries.FirstOrDefault(entry =>
                entry.Kind == DefinitionValidationKinds.RepositoryBranch
                && !entry.Settled
                && entry.ProjectId == projectId
                && string.Equals(entry.Branch, branch, StringComparison.Ordinal)
                && string.Equals(entry.Commit, commit, StringComparison.Ordinal));
            if (existing is not null)
            {
                return (existing, false);
            }

            var created = new DefinitionValidationEntry
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                ProjectId = projectId,
                Kind = DefinitionValidationKinds.RepositoryBranch,
                RepositoryUrl = repositoryUrl,
                Branch = branch,
                Commit = commit,
                PipelinePath = pipelinePath,
                RequiredTags = requiredTags,
                CreatedAt = now,
            };
            Add(created);
            return (created, true);
        }
    }

    /// <summary>
    /// Unsettled observations older than <paramref name="maxAge"/>. The retry sweeper abandons them (a
    /// task nobody claimed or answered cannot stay in the queue forever) and moves the branch's retry
    /// clock, so "no Worker" backs off instead of becoming a request storm.
    /// </summary>
    public IReadOnlyList<DefinitionValidationEntry> StaleObservations(DateTimeOffset now, TimeSpan maxAge)
    {
        lock (_gate)
        {
            return
            [
                .. _entries.Where(entry =>
                    entry.Kind == DefinitionValidationKinds.RepositoryBranch
                    && !entry.Settled
                    && now - entry.CreatedAt >= maxAge),
            ];
        }
    }

    /// <summary>Adds an entry and prunes to the cap, settled entries first.</summary>
    private void Add(DefinitionValidationEntry entry)
    {
        _entries.Add(entry);
        while (_entries.Count > MaxEntries)
        {
            var settled = _entries.FindIndex(candidate => candidate.Settled);
            _entries.RemoveAt(settled < 0 ? 0 : settled);
        }
    }

    /// <summary>One entry by id, or null when it was pruned.</summary>
    public DefinitionValidationEntry? Find(Guid id)
    {
        lock (_gate)
        {
            return _entries.SingleOrDefault(entry => entry.Id == id);
        }
    }

    /// <summary>Every entry, for the tests' observation seam.</summary>
    public IReadOnlyList<DefinitionValidationEntry> Snapshots
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>
    /// Leases the oldest pending validation of one organization to a Worker. An expired lease is
    /// re-offered (a lost Worker must not strand the page); a settled entry never is.
    /// </summary>
    public DefinitionValidationEntry? TryLease(
        Guid organizationId,
        Guid workerId,
        DateTimeOffset now,
        TimeSpan lease)
    {
        lock (_gate)
        {
            var next = _entries
                .Where(entry => entry.OrganizationId == organizationId
                    && !entry.Settled
                    && (entry.LeaseExpiresAt is null || entry.LeaseExpiresAt <= now))
                .OrderBy(entry => entry.CreatedAt)
                .ThenBy(entry => entry.Id)
                .FirstOrDefault();
            if (next is null)
            {
                return null;
            }

            next.WorkerId = workerId;
            next.LeaseExpiresAt = now + lease;
            return next;
        }
    }

    /// <summary>
    /// Records one Worker's answer. The caller has already validated the payload; the lease and the
    /// organization are re-checked here, so a foreign or lapsed Worker changes nothing.
    /// </summary>
    public (DefinitionValidationEntry? Entry, string? ErrorCode) TryReport(
        Guid organizationId,
        Guid validationId,
        Guid workerId,
        string outcome,
        string? specJson,
        string? diagnostic,
        string? failureReason,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            var entry = _entries.SingleOrDefault(candidate => candidate.Id == validationId);
            if (entry is null || entry.OrganizationId != organizationId)
            {
                return (null, DefinitionValidationErrorCodes.NotFound);
            }

            if (entry.Settled)
            {
                return (entry, DefinitionValidationErrorCodes.AlreadySettled);
            }

            if (entry.WorkerId != workerId
                || entry.LeaseExpiresAt is not { } lease
                || lease <= now)
            {
                return (entry, DefinitionValidationErrorCodes.LeaseLost);
            }

            entry.Settled = true;
            entry.Outcome = outcome;
            entry.Valid = string.Equals(outcome, DefinitionValidationOutcomes.Valid, StringComparison.Ordinal);
            entry.SpecJson = specJson;
            entry.Diagnostic = diagnostic;
            entry.FailureReason = failureReason;
            entry.WorkerId = workerId;
            return (entry, null);
        }
    }

    /// <summary>
    /// Settles a validation nobody claimed as "no worker" - the bounded-wait answer, so a request
    /// always ends rather than hanging. A still-live lease keeps it pending until the hard ceiling.
    /// </summary>
    public (bool SettledByThisCall, DefinitionValidationEntry? Entry) TryAbandon(
        Guid id,
        DateTimeOffset now,
        bool force)
    {
        lock (_gate)
        {
            var entry = _entries.SingleOrDefault(candidate => candidate.Id == id);
            if (entry is null)
            {
                return (false, null);
            }

            if (entry.Settled || (!force && entry.LeaseExpiresAt is { } lease && lease > now))
            {
                return (false, entry);
            }

            entry.Settled = true;
            entry.Valid = false;
            entry.FailureReason = DefinitionValidationFailureReasons.NoWorker;
            entry.Diagnostic = force
                ? "No Worker answered this validation before the wait ended."
                : "No Worker claimed this validation before the wait ended.";
            return (true, entry);
        }
    }
}

/// <summary>The stable error codes the worker report endpoint answers with.</summary>
public static class DefinitionValidationErrorCodes
{
    public const string NotFound = "validation-not-found";
    public const string AlreadySettled = "validation-settled";
    public const string LeaseLost = "validation-lease-lost";
    public const string OutcomeInvalid = "validation-outcome-invalid";
    public const string DiagnosticInvalid = "validation-diagnostic-invalid";
    public const string SpecInvalid = "validation-spec-invalid";
}
