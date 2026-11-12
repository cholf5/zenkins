using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Tenancy;

/// <summary>
/// Scoped, request-local holder of the selected organization. The authentication pipeline sets
/// <see cref="OrganizationId"/> once per request (F-001); tests re-point it deliberately to prove
/// that query filters and the change tracker follow the current request instead of freezing the
/// first tenant.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    private Guid? _organizationId;
    private int _tenantRevision;

    /// <inheritdoc />
    public Guid? OrganizationId
    {
        get => _organizationId;

        // Internal on purpose: only the request pipeline (and tests, via InternalsVisibleTo) may
        // select a tenant. It must never be settable from a component or a query parameter handler.
        internal set
        {
            if (_organizationId == value)
            {
                return;
            }

            _organizationId = value;
            _tenantRevision++;
        }
    }

    /// <inheritdoc />
    public int TenantRevision => _tenantRevision;
}
