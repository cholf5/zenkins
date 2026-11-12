namespace Zenkins.Shared.Tenancy;

/// <summary>
/// Request-scoped active tenant. The value comes from the authenticated user's selected
/// organization (see F-001); it must never be derived from a route value or query string alone.
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// Active organization, or <see langword="null"/> when the request has no tenant selected
    /// (anonymous request, or a user who is not a member of any organization).
    /// </summary>
    Guid? OrganizationId { get; }

    /// <summary>
    /// Monotonically increasing counter, incremented every time the selected organization actually
    /// changes. Decorators and replacements must forward/increment it; the data layer uses it to
    /// detect a tenant switch without depending on a concrete <see cref="ITenantContext"/> type.
    /// </summary>
    int TenantRevision { get; }
}
