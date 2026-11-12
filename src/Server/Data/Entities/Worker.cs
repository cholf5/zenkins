using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// The well-known worker statuses shown on the workers page (PRD §3.4.2). Stored as a string so the
/// model stays provider-neutral; the check constraint in <see cref="ZenkinsDbContext"/> keeps
/// out-of-band writes honest.
/// </summary>
public static class WorkerStatus
{
    public const string Online = "Online";

    public const string Offline = "Offline";
}

/// <summary>
/// A registered build worker (PRD §3.4). Tenant-owned: a worker always belongs to exactly one
/// organization - the one whose registration token minted it (F-004).
/// <para>
/// Re-registering with the same name inside the same organization updates this row instead of
/// creating a second one (container restarts must not duplicate workers); the registration flow also
/// revokes the worker's previous active tokens, so a restart replaces identity, never duplicates it.
/// </para>
/// </summary>
public sealed class Worker : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>Unique inside the organization (unique index, enforced by the database).</summary>
    public required string Name { get; set; }

    /// <summary>Windows / Linux / macOS (as the worker reported it).</summary>
    public required string Os { get; set; }

    /// <summary>x64 / Arm64 / ... (as the worker reported it).</summary>
    public required string Architecture { get; set; }

    /// <summary>Version string of the worker build.</summary>
    public required string Version { get; set; }

    /// <summary>Comma-separated scheduling tags (PRD §3.4.4).</summary>
    public required string Tags { get; set; }

    public int CpuCount { get; set; }

    /// <summary>Maximum concurrent tasks the worker accepts; PRD §3.4.5 default is 1.</summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>Online while heartbeats keep arriving; staleness marking is F-010's concern.</summary>
    public string Status { get; set; } = WorkerStatus.Online;

    /// <summary>Registration counts as the first heartbeat; every heartbeat refreshes this.</summary>
    public DateTimeOffset? LastHeartbeatAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
