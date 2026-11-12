using System.Security.Claims;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Server.Auth;

/// <summary>
/// Selects the request's tenant from the authenticated user's organization memberships (F-001).
/// Runs after authentication and before the Blazor endpoint renders, so every tenant-filtered read
/// in a static SSR request already sees the right organization.
/// <para>
/// Interactive circuits have their own DI scope and do not pass through this middleware after the
/// initial request; <see cref="UserSession"/> establishes their tenant when the shell resolves the
/// signed-in user, sharing <see cref="UserOrganizationDirectory"/> with this middleware.
/// </para>
/// <para>
/// The organization is derived from the principal - never from a route value, header or query
/// string - so a caller cannot choose someone else's tenant. F-002 replaces the "default
/// organization" rule with an explicit selection, keeping the same guarantee.
/// </para>
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        TenantContext tenantContext,
        UserOrganizationDirectory directory)
    {
        if (IsPageRequest(context.Request) && context.User.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrEmpty(userId))
            {
                var organization = await directory.FindDefaultForUserAsync(userId, context.RequestAborted);
                tenantContext.OrganizationId = organization?.OrganizationId;
            }
        }

        await next(context);
    }

    /// <summary>
    /// Framework assets and static files cannot render tenant data, so they skip the two database
    /// reads this middleware would otherwise perform on every fingerprinted script and image.
    /// </summary>
    private static bool IsPageRequest(HttpRequest request) =>
        !request.Path.StartsWithSegments("/_", StringComparison.OrdinalIgnoreCase)
        && !Path.HasExtension(request.Path.Value);
}
