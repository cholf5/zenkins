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
    /// Identity user id. Stored as a plain column with no navigation or foreign key because I-002
    /// does not model <c>User</c>; F-001 adds the ASP.NET Identity table and must add the foreign key.
    /// </summary>
    public required string UserId { get; set; }

    public OrganizationRole Role { get; set; } = OrganizationRole.Member;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
