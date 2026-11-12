using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Server.Auth;

/// <summary>Who is signed in, and which organization the current request/circuit acts as.</summary>
/// <param name="UserId">Identity user id.</param>
/// <param name="Email">Account email, for display in the shell.</param>
/// <param name="Organization">Default organization, or <see langword="null"/> for a user with no membership.</param>
public sealed record UserSessionInfo(string UserId, string? Email, UserOrganization? Organization)
{
    /// <summary>Whether the account is attached to an organization yet (F-001 shows an explicit state otherwise).</summary>
    public bool HasOrganization => Organization is not null;
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

        var organization = await directory.FindDefaultForUserAsync(userId);
        tenantContext.OrganizationId = organization?.OrganizationId;

        return new UserSessionInfo(userId, principal.FindFirstValue(ClaimTypes.Email), organization);
    }
}
