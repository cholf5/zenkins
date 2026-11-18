using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Zenkins.Server.Environments;

/// <summary>
/// The real POST endpoints for organization environment variables (card F-034): create, edit and
/// delete. The page only renders forms; these routes are the enforcement.
/// <para>
/// <b>Form-bound like F-002/F-003/F-012</b>, so <c>UseAntiforgery</c> validates every call and a direct
/// POST from curl hits the same server-side Owner check. The route id is authoritative; no form model
/// carries a variable id or an organization id.
/// </para>
/// <para>
/// <b>Outcomes.</b> Success is a PRG redirect with <c>?notice=</c>; a foreign or missing id is a
/// bodyless <c>404</c>; a Member's write is a <c>403</c> with no state change; validation failures
/// redirect back with a stable <c>?error=</c> code. Only the submitted <em>name</em> is echoed into the
/// redirect - the value never enters a URL or an access log, even though it is configuration rather
/// than a credential.
/// </para>
/// </summary>
public static class OrganizationEnvironmentEndpoints
{
    /// <summary>The page that owns the forms and every redirect target.</summary>
    public const string ListPath = "/organizations/environment";

    /// <summary>Maps the three mutations. Every route requires an authenticated user.</summary>
    public static IEndpointRouteBuilder MapOrganizationEnvironmentEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/organizations/environment").RequireAuthorization();

        group.MapPost("/create", CreateAsync);
        group.MapPost("/{variableId:guid}/update", UpdateAsync);
        group.MapPost("/{variableId:guid}/delete", DeleteAsync);

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        [FromForm] EnvironmentVariableForm form,
        HttpContext httpContext,
        OrganizationEnvironmentService variables)
    {
        if (FindUserId(httpContext) is not { } userId)
        {
            return Results.Unauthorized();
        }

        // F-019's shape: a body whose fields all fail to bind arrives as null, and the service - not a
        // null reference - answers it with the page's own PRG error.
        var result = await variables.CreateAsync(
            userId,
            form?.ToInput() ?? EmptyInput(),
            httpContext.RequestAborted);

        return result.Status switch
        {
            OrganizationEnvironmentStatus.Success => Results.Redirect($"{ListPath}?notice=created"),
            OrganizationEnvironmentStatus.Forbidden => Forbidden(result),
            OrganizationEnvironmentStatus.NotFound => Results.NotFound(),
            _ => ValidationRedirect(result, form?.Name),
        };
    }

    private static async Task<IResult> UpdateAsync(
        Guid variableId,
        [FromForm] EnvironmentVariableForm form,
        HttpContext httpContext,
        OrganizationEnvironmentService variables)
    {
        if (FindUserId(httpContext) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await variables.UpdateAsync(
            userId,
            variableId,
            form?.ToInput() ?? EmptyInput(),
            httpContext.RequestAborted);

        return result.Status switch
        {
            OrganizationEnvironmentStatus.Success => Results.Redirect($"{ListPath}?notice=updated"),
            OrganizationEnvironmentStatus.Forbidden => Forbidden(result),
            OrganizationEnvironmentStatus.NotFound => Results.NotFound(),
            _ => ValidationRedirect(result, form?.Name),
        };
    }

    private static async Task<IResult> DeleteAsync(
        Guid variableId,
        [FromForm] DeleteEnvironmentVariableForm form,
        HttpContext httpContext,
        OrganizationEnvironmentService variables)
    {
        if (FindUserId(httpContext) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await variables.DeleteAsync(userId, variableId, httpContext.RequestAborted);
        return result.Status switch
        {
            OrganizationEnvironmentStatus.Success => Results.Redirect($"{ListPath}?notice=deleted"),
            OrganizationEnvironmentStatus.Forbidden => Forbidden(result),
            _ => Results.NotFound(),
        };
    }

    private static IResult Forbidden(OrganizationEnvironmentMutationResult result) =>
        Results.Text(
            result.Message ?? "You are not allowed to perform this action.",
            "text/plain",
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// PRG back to the page with the field error and the submitted name. The value is deliberately not
    /// carried in the query: a redirect lands in access logs and browser history.
    /// </summary>
    private static IResult ValidationRedirect(
        OrganizationEnvironmentMutationResult result,
        string? name)
    {
        var code = result.FieldErrors?.FirstOrDefault()?.Code
            ?? OrganizationEnvironmentErrorCodes.NameInvalid;
        var query = QueryString.Create(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["error"] = code,
            ["name"] = name,
        });

        return Results.Redirect($"{ListPath}{query}");
    }

    private static string? FindUserId(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>The stand-in for a form that bound null (a POST carrying no recognised field).</summary>
    private static OrganizationEnvironmentInput EmptyInput() => new(Name: null, Value: null);

    /// <summary>Create/edit form: the name plus the plain value. No id: the route owns the target.</summary>
    public sealed class EnvironmentVariableForm
    {
        public string? Name { get; set; }

        public string? Value { get; set; }

        public OrganizationEnvironmentInput ToInput() => new(Name, Value);
    }

    /// <summary>
    /// Empty on purpose: the parameter exists so the delete POST is form-bound and therefore covered by
    /// <c>UseAntiforgery</c>. The page's own delete form is the confirmation.
    /// </summary>
    public sealed class DeleteEnvironmentVariableForm
    {
    }
}
