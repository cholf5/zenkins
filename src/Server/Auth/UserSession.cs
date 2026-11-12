using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Server.Auth;

/// <summary>Who is signed in, and which organization the current request/circuit acts as.</summary>
/// <param name="UserId">Identity user id.</param>
/// <param name="Email">Account email, for display in the shell.</param>
/// <param name="Organization">Active organization, or <see langword="null"/> for a user with no membership.</param>
/// <param name="Organizations">Every organization the user belongs to (the switcher offers exactly these).</param>
public sealed record UserSessionInfo(
    string UserId,
    string? Email,
    UserOrganization? Organization,
    IReadOnlyList<UserOrganization> Organizations)
{
    /// <summary>Whether the account is attached to an organization yet (F-001 shows an explicit state otherwise).</summary>
    public bool HasOrganization => Organization is not null;

    /// <summary>Whether the account owns its active organization (F-002's management guard).</summary>
    public bool CanManageActiveOrganization => Organization?.Role == OrganizationRole.Owner;
}

/// <summary>
/// Per-request / per-circuit view of the signed-in user. Resolving it also establishes the scoped
/// tenant (<see cref="TenantContext"/>), which is how interactive circuits - whose DI scope is not
/// the HTTP request's scope - get a tenant at all. The result is cached for the lifetime of the
/// scope.
/// <para>
/// This is a UI-facing service: it depends on <see cref="AuthenticationStateProvider"/>, so it is
/// only valid inside a Blazor render (the HTTP middleware path uses
/// <see cref="TenantResolutionMiddleware"/> instead and shares
/// <see cref="UserOrganizationDirectory"/> with it).
/// </para>
/// <para>
/// <b>F-003 hazard:</b> a page's own <c>OnInitializedAsync</c> may run before the shell layout's.
/// Any page that reads tenant-owned data must therefore resolve this service itself first (as the
/// organization and project pages do), instead of relying on the layout having selected the tenant.
/// </para>
/// </summary>
public sealed class UserSession(
    AuthenticationStateProvider authenticationStateProvider,
    UserOrganizationDirectory directory,
    TenantContext tenantContext)
{
    private Task<UserSessionInfo?>? _resolve;

    /// <summary>The signed-in user, or <see langword="null"/> when the request is anonymous.</summary>
    public Task<UserSessionInfo?> GetAsync() => _resolve ??= ResolveAsync();

    private async Task<UserSessionInfo?> ResolveAsync()
    {
        var authenticationState = await authenticationStateProvider.GetAuthenticationStateAsync();
        var principal = authenticationState.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }

        // Resolves the persisted server-side selection (falling back to a valid membership) and
        // returns only the caller's memberships - never a value taken from the request.
        var resolved = await directory.ResolveAsync(userId);
        tenantContext.OrganizationId = resolved.Active?.OrganizationId;

        return new UserSessionInfo(
            userId,
            principal.FindFirstValue(ClaimTypes.Email),
            resolved.Active,
            resolved.Memberships);
    }
}
