namespace Zenkins.Shared.Tenancy;

/// <summary>
/// Marks an entity that belongs to exactly one <see cref="Zenkins.Shared.Tenancy.ITenantContext"/> tenant.
/// <para>
/// Every entity implementing this interface is tenant-scoped: the data layer applies a global query
/// filter and rejects writes that do not match the active organization. Implementations live in
/// <c>Zenkins.Server</c>; the marker lives here so the Worker can reason about tenant ownership
/// without referencing ASP.NET Core or Entity Framework.
/// </para>
/// </summary>
public interface ITenantOwned
{
    /// <summary>
    /// Owning organization. Set by the data layer on insert and never mutable afterwards.
    /// </summary>
    Guid OrganizationId { get; set; }
}
