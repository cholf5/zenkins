using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// The well-known build-run states (PRD §3.5). Stored as a string so the model stays
/// provider-neutral; the check constraint in <see cref="ZenkinsDbContext"/> keeps out-of-band
/// writes honest. Transitions between states are validated in exactly one place
/// (<c>BuildRunStateMachine</c>); illegal transitions are a conflict (409).
/// </summary>
public static class BuildRunStatus
{
    /// <summary>Waiting for an eligible worker to claim it (or for its lease to expire).</summary>
    public const string Queued = "Queued";

    /// <summary>Accepted by a worker and executing there.</summary>
    public const string Running = "Running";

    /// <summary>Finished with exit code 0. Terminal.</summary>
    public const string Success = "Success";

    /// <summary>Finished with a non-zero exit code. Terminal.</summary>
    public const string Failed = "Failed";

    /// <summary>Canceled by a user before or during execution (F-009). Terminal.</summary>
    public const string Canceled = "Canceled";

    /// <summary>The worker disappeared without reporting a result (F-010). Terminal.</summary>
    public const string Lost = "Lost";
}

/// <summary>How a run entered the queue. An open set: new trigger sources add values.</summary>
public static class BuildRunTriggerType
{
    /// <summary>Triggered by an organization member from the project page (this card).</summary>
    public const string Manual = "Manual";
}

/// <summary>
/// One execution of a project's command (PRD §3.5, card F-006). Tenant-owned like every other
/// organization row. A v0.2 run is a manually specified argv command executed inside the worker's
/// per-run work directory (F-007) - <c>Pipeline*</c> fields stay out until Phase 3 and Git fields
/// until Phase 4.
/// <para>
/// <b>Claim/lease.</b> A claim does not change <see cref="Status"/>: it stamps
/// <see cref="WorkerId"/> and <see cref="LeaseExpiresAt"/> while the run stays
/// <see cref="BuildRunStatus.Queued"/>. The worker must report <c>accepted</c> inside the lease
/// window; the lease sweeper requeues an expired, never-accepted lease. After <c>accepted</c>
/// there is no lease - <see cref="LeaseExpiresAt"/> is cleared and cancellation (F-009) and
/// offline detection (F-010) own the rest.
/// </para>
/// </summary>
public sealed class BuildRun : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>The project this run executes for. Cascade-deleted with it (F-003 defers the
    /// cascade decision to this card: build history is not meaningful without its project).</summary>
    public Guid ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    public string Status { get; set; } = BuildRunStatus.Queued;

    /// <summary>The <see cref="BuildRunTriggerType"/> discriminator.</summary>
    public required string TriggerType { get; set; }

    /// <summary>
    /// The Identity user id of the member who triggered the run (manual triggers). Deliberately no
    /// foreign key: build history must survive account deletion.
    /// </summary>
    public string? TriggerUserId { get; set; }

    /// <summary>Comma-separated tags a claimant worker must cover (empty when unrestricted).</summary>
    public required string RequiredTags { get; set; } = string.Empty;

    /// <summary>The command as a JSON array of argv strings. Never a shell line: there is no
    /// quoting interpretation anywhere in the pipeline (PRD §8 constraint 6).</summary>
    public required string CommandJson { get; set; }

    /// <summary>The worker the run is leased to (claim) or executing on (accepted). Null when
    /// queued and never claimed.</summary>
    public Guid? WorkerId { get; set; }

    public Worker? Worker { get; set; }

    /// <summary>When the current lease expires; null when the run is not leased. Cleared on
    /// acceptance - a running run has no lease.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>Set when the run is accepted (Queued → Running).</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Set when the run reaches a terminal state. Duration is derived on display.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>The process exit code reported by the worker (Success ⇔ 0).</summary>
    public int? ExitCode { get; set; }

    /// <summary>Machine-readable reason for an abnormal end (canceled-by-user, timeout,
    /// worker-offline). Null for a plain non-zero exit - the exit code is the reason.</summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Per-run timeout override in seconds (the trigger form collects minutes); <c>null</c> when
    /// the trigger used the server default (<c>Runs:DefaultTimeoutMinutes</c>, F-009). The
    /// effective timeout is frozen into <see cref="DeadlineAt"/> at accept time, so a later
    /// configuration change never moves an existing run's deadline.
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// The execution deadline, frozen at accept time: <c>StartedAt + effective timeout</c> where
    /// the effective timeout is this run's <see cref="TimeoutSeconds"/> override or the server
    /// default. Null until accepted (a queued run has no deadline). The deadline sweeper
    /// (F-009) never touches a run before this instant.
    /// </summary>
    public DateTimeOffset? DeadlineAt { get; set; }

    /// <summary>
    /// Set when a cancellation was requested for the still-running run (F-009): a user cancel
    /// (<see cref="CancelReason"/> = <c>canceled-by-user …</c>) or the deadline sweeper's timeout
    /// request. The worker kills the process tree on the signal and reports through the
    /// <c>canceled</c> endpoint; until that report the run stays <c>Running</c>.
    /// </summary>
    public DateTimeOffset? CancelRequestedAt { get; set; }

    /// <summary>Why the cancellation was requested (<c>canceled-by-user &lt;user&gt;</c> or
    /// <c>timeout</c>, F-009). Becomes the run's <see cref="FailureReason"/> when the worker
    /// reports the kill - the server, not the worker, owns the wording.</summary>
    public string? CancelReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
