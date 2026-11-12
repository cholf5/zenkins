namespace Zenkins.Server.Data.Entities;

/// <summary>
/// First-run bootstrap guard (F-001). Exactly one row can ever exist, enforced by the primary key on
/// the fixed <see cref="SingletonId"/> value rather than by an application-level count check: two
/// concurrent first registrations both try to insert this row inside their transaction and the
/// database lets exactly one commit succeed.
/// <para>
/// Not tenant-owned: the marker records a deployment-wide fact ("the starter organization has been
/// created"), not an organization's data. It is written inside the same transaction as the first
/// <see cref="User"/>, <see cref="Organization"/> and Owner <see cref="OrganizationMember"/>.
/// </para>
/// </summary>
public sealed class BootstrapMarker
{
    /// <summary>The only key this table may ever contain.</summary>
    public const string SingletonId = "bootstrap";

    /// <summary>Always <see cref="SingletonId"/>; the primary key is what makes the bootstrap race-safe.</summary>
    public string Id { get; set; } = SingletonId;

    /// <summary>The starter organization created by the bootstrap.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>
    /// The first user, who owns <see cref="OrganizationId"/>. Deliberately a plain string with no
    /// foreign key: deleting that account must not cascade the marker away and silently re-open the
    /// bootstrap (the organization and its history stay; a replacement Owner is F-002's job).
    /// </summary>
    public required string UserId { get; set; }

    /// <summary>When the bootstrap transaction committed (UTC).</summary>
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
}
