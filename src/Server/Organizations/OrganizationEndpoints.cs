using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Zenkins.Server.Auth;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Server.Organizations;

/// <summary>
/// HTTP form endpoints for organization switching and management (card F-002).
/// <para>
/// These are real POSTs, not interactive-circuit event handlers, because that is the boundary where
/// the server can be asked to do something directly by a client that never rendered the UI (curl, a
/// script, a forged form). Each handler therefore re-establishes identity from the cookie and
/// re-validates the membership/role server-side; nothing is trusted from the form except the
/// requested target and the new value.
/// </para>
/// <para>
/// <b>Antiforgery:</b> every handler binds a <c>[FromForm]</c> model, so <c>UseAntiforgery</c>
/// validates the token automatically; a POST without it is a 400. The pages emit the request token
/// in a <c>&lt;meta&gt;</c> tag (App.razor) and a tiny script copies it into the form on submit.
/// </para>
/// <para>
/// <b>Authorization outcomes:</b> a non-Owner attempting a management action gets 403 regardless of
/// the UI; a non-member attempting a switch gets 403 and the stored selection is untouched.
/// Validation failures (last Owner, unknown email, empty name) redirect back to the page with a
/// stable <c>?error=</c> code so the server-rendered page can explain them; they never report success.
/// </para>
/// </summary>
public static class OrganizationEndpoints
{
    /// <summary>Page that owns the rename form.</summary>
    public const string SettingsPath = "/organizations/settings";

    /// <summary>Page that owns the member list and its forms.</summary>
    public const string MembersPath = "/organizations/members";

    /// <summary>Maps the five organization POST endpoints. All require authentication.</summary>
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/organizations").RequireAuthorization();

        group.MapPost("/switch", SwitchAsync);
        group.MapPost("/settings", RenameAsync);
        group.MapPost("/members/invite", InviteAsync);
        group.MapPost("/members/role", ChangeRoleAsync);
        group.MapPost("/members/remove", RemoveAsync);

        return endpoints;
    }

    /// <summary>
    /// Switches the active organization. The membership check happens inside
    /// <see cref="UserOrganizationDirectory.SwitchAsync"/>; a forged or stale target is a 403 and
    /// leaves the previous selection in place.
    /// </summary>
    private static async Task<IResult> SwitchAsync(
        [FromForm] SwitchOrganizationForm form,
        HttpContext httpContext,
        UserOrganizationDirectory directory)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        if (!Guid.TryParse(form.OrganizationId, out var organizationId))
        {
            return Results.Text("The organization id is not valid.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        var selected = await directory.SwitchAsync(userId, organizationId, httpContext.RequestAborted);
        if (selected is null)
        {
            // Not a member: reject, never fall through to "switch anyway".
            return Results.Text(
                "You are not a member of that organization, so the active organization was not changed.",
                "text/plain",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.Redirect(ReturnUrlSanitizer.Sanitize(form.ReturnUrl, "/"));
    }

    /// <summary>Renames the active organization (Owner-only, enforced by the service).</summary>
    private static async Task<IResult> RenameAsync(
        [FromForm] RenameOrganizationForm form,
        HttpContext httpContext,
        OrganizationManagementService management)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await management.RenameActiveOrganizationAsync(
            userId,
            form.Name,
            httpContext.RequestAborted);

        return MutationResult(result, SettingsPath, "renamed");
    }

    /// <summary>Invites an existing account by email (Owner-only, enforced by the service).</summary>
    private static async Task<IResult> InviteAsync(
        [FromForm] InviteMemberForm form,
        HttpContext httpContext,
        OrganizationManagementService management)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await management.InviteAsync(userId, form.Email, httpContext.RequestAborted);

        return MutationResult(result, MembersPath, "invited");
    }

    /// <summary>Changes a member's role (Owner-only; last-Owner invariant in the service transaction).</summary>
    private static async Task<IResult> ChangeRoleAsync(
        [FromForm] ChangeRoleForm form,
        HttpContext httpContext,
        OrganizationManagementService management)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        if (!Guid.TryParse(form.MembershipId, out var membershipId))
        {
            return RedirectWithError(MembersPath, "member-not-found");
        }

        if (!Enum.TryParse<OrganizationRole>(form.Role, ignoreCase: true, out var role) || !Enum.IsDefined(role))
        {
            return RedirectWithError(MembersPath, "invalid-role");
        }

        var result = await management.ChangeRoleAsync(
            userId,
            membershipId,
            role,
            httpContext.RequestAborted);

        return MutationResult(result, MembersPath, "role-changed");
    }

    /// <summary>Removes a member (Owner-only; last-Owner invariant in the service transaction).</summary>
    private static async Task<IResult> RemoveAsync(
        [FromForm] RemoveMemberForm form,
        HttpContext httpContext,
        OrganizationManagementService management)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        if (!Guid.TryParse(form.MembershipId, out var membershipId))
        {
            return RedirectWithError(MembersPath, "member-not-found");
        }

        var result = await management.RemoveMemberAsync(
            userId,
            membershipId,
            httpContext.RequestAborted);

        return MutationResult(result, MembersPath, "member-removed");
    }

    private static IResult MutationResult(
        OrganizationMutationResult result,
        string returnPath,
        string successNotice)
    {
        switch (result.Status)
        {
            case OrganizationMutationStatus.Success:
                return Results.Redirect($"{returnPath}?notice={successNotice}");

            // Authorization failures are never redirected to a friendly page: the caller must see a
            // hard rejection, because the UI is not the enforcement point.
            case OrganizationMutationStatus.Forbidden:
                return Results.Text(
                    result.Message ?? "Only an organization Owner can perform this action.",
                    "text/plain",
                    statusCode: StatusCodes.Status403Forbidden);

            case OrganizationMutationStatus.NotFound:
                return Results.NotFound();

            default:
                // PRG: redirect back with a stable code so the page can render the message. A failed
                // mutation must never look like success, and the response carries the code in Location.
                return RedirectWithError(returnPath, result.ErrorCode ?? "invalid");
        }
    }

    private static IResult RedirectWithError(string returnPath, string errorCode) =>
        Results.Redirect($"{returnPath}?error={Uri.EscapeDataString(errorCode)}");

    private static string? FindUserId(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

    public sealed class SwitchOrganizationForm
    {
        public string? OrganizationId { get; set; }

        public string? ReturnUrl { get; set; }
    }

    public sealed class RenameOrganizationForm
    {
        public string? Name { get; set; }
    }

    public sealed class InviteMemberForm
    {
        public string? Email { get; set; }
    }

    public sealed class ChangeRoleForm
    {
        public string? MembershipId { get; set; }

        public string? Role { get; set; }
    }

    public sealed class RemoveMemberForm
    {
        public string? MembershipId { get; set; }
    }
}
