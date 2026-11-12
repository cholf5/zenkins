using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Tenancy;

/// <summary>
/// Scoped, request-local holder of the selected organization. The authentication pipeline sets
/// <see cref="OrganizationId"/> once per request (F-001); tests re-point it deliberately to prove
/// that query filters follow the current request instead of freezing the first tenant.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    /// <inheritdoc />
    public Guid? OrganizationId { get; set; }
}
