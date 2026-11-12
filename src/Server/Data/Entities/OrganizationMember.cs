using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// Membership of a user in an organization (PRD §3.1.2). Tenant-scoped and uniquely keyed by
/// <c>(OrganizationId, UserId)</c>.
/// </summary>
public sealed class OrganizationMember : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>
    /// Identity user id (F-001 added the foreign key to the Identity users table). The row is deleted
    /// with the account, never with the organization's other data.
    /// </summary>
    public required string UserId { get; set; }

    /// <summary>Optional navigation for member listings (F-002); the id above is the source of truth.</summary>
    public User? User { get; set; }

    public OrganizationRole Role { get; set; } = OrganizationRole.Member;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
