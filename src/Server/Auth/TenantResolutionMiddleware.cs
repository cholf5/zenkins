using System.Security.Claims;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Server.Auth;

/// <summary>
/// Selects the request's tenant from the organization state persisted for the authenticated user
/// (F-002). Runs after authentication and before the Blazor endpoint renders, so every
/// tenant-filtered read in a static SSR request already sees the right organization.
/// <para>
/// The active organization comes from <see cref="UserOrganizationDirectory.ResolveAsync"/>: the
/// server-side selection if the membership still exists, otherwise a valid membership, otherwise no
/// organization. It is never derived from a route value, header, query string or cookie - the switch
/// endpoint is the only thing that writes the selection and it rejects non-members.
/// </para>
/// <para>
/// Interactive circuits have their own DI scope and do not pass through this middleware after the
/// initial request; <see cref="UserSession"/> establishes their tenant when the shell (or a page)
/// resolves the signed-in user, sharing <see cref="UserOrganizationDirectory"/> with this middleware.
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
                var organizations = await directory.ResolveAsync(userId, context.RequestAborted);
                tenantContext.OrganizationId = organizations.Active?.OrganizationId;
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
