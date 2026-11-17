using Zenkins.Shared.Pipelines;

namespace Zenkins.Server.Probes;

/// <summary>One in-flight definition-only validation (card F-022). Mutable and guarded by the registry's lock.</summary>
public sealed class DefinitionValidationEntry
{
    public required Guid Id { get; init; }

    public required Guid OrganizationId { get; init; }

    public required Guid ProjectId { get; init; }

    public required string Script { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public Guid? WorkerId { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public bool Settled { get; set; }

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
    /// <summary>The most pending/settled entries kept, oldest pruned first.</summary>
    public const int MaxEntries = 64;

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
            _entries.Add(entry);
            while (_entries.Count > MaxEntries)
            {
                _entries.RemoveAt(0);
            }
        }

        return entry;
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
        bool valid,
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
            entry.Valid = valid;
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
