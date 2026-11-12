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
}
