namespace Zenkins.Server.Data.Entities;

/// <summary>
/// The tenant root (PRD §3.1.1). Not <see cref="Zenkins.Shared.Tenancy.ITenantOwned"/>: it *is*
/// the tenant, so it must stay readable while a tenant is being selected.
/// </summary>
public sealed class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
