using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Zenkins.Server.Projects;

/// <summary>
/// Real POST endpoints for project create, edit and delete (card F-003).
/// <para>
/// They follow F-002's shape for the same reason: a plain HTML form must work without a Blazor
/// circuit, and a direct POST from curl/a script must hit the same server-side validation and
/// authorization. Each handler binds a <c>[FromForm]</c> model, so <c>UseAntiforgery</c> validates
/// the token automatically.
/// </para>
/// <para>
/// <b>The route id is authoritative.</b> The form models carry no project id, so a forged
/// <c>ProjectId</c> field cannot redirect an edit or a delete to another project - and even if it
/// could, the service resolves every id through the tenant-filtered context.
/// </para>
/// <para>
/// <b>Outcomes.</b> Success is a PRG redirect with <c>?notice=</c>; a foreign or missing project id
/// is <c>404</c>; a Member's delete is <c>403</c> with no state change; validation failures and a
/// duplicate name redirect back to the form with a stable <c>?error=</c> code plus the submitted
/// values, so the page can show a field-level error without losing the user's input.
/// </para>
/// </summary>
public static class ProjectEndpoints
{
    /// <summary>List page (also the delete success target).</summary>
    public const string ListPath = "/projects";

    /// <summary>Create form page.</summary>
    public const string NewPath = "/projects/new";

    /// <summary>Maps the three project mutation endpoints. All require authentication.</summary>
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/projects").RequireAuthorization();

        group.MapPost("/create", CreateAsync);
        group.MapPost("/{projectId:guid}/edit", UpdateAsync);
        group.MapPost("/{projectId:guid}/delete", DeleteAsync);

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        [FromForm] ProjectForm form,
        HttpContext httpContext,
        ProjectService projects)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await projects.CreateAsync(userId, form.ToInput(), httpContext.RequestAborted);
        return result.Status switch
        {
            ProjectMutationStatus.Success =>
                Results.Redirect($"/projects/{result.ProjectId}?notice=created"),
            ProjectMutationStatus.Forbidden => Forbidden(result),
            ProjectMutationStatus.NotFound => Results.NotFound(),
            _ => ValidationRedirect(NewPath, result, form),
        };
    }

    private static async Task<IResult> UpdateAsync(
        Guid projectId,
        [FromForm] ProjectForm form,
        HttpContext httpContext,
        ProjectService projects)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await projects.UpdateAsync(
            userId,
            projectId,
            form.ToInput(),
            httpContext.RequestAborted);

        return result.Status switch
        {
            ProjectMutationStatus.Success =>
                Results.Redirect($"/projects/{result.ProjectId}?notice=updated"),
            ProjectMutationStatus.Forbidden => Forbidden(result),
            ProjectMutationStatus.NotFound => Results.NotFound(),
            _ => ValidationRedirect($"/projects/{projectId}/edit", result, form),
        };
    }

    private static async Task<IResult> DeleteAsync(
        Guid projectId,
        [FromForm] DeleteProjectForm form,
        HttpContext httpContext,
        ProjectService projects)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await projects.DeleteAsync(userId, projectId, httpContext.RequestAborted);
        return result.Status switch
        {
            ProjectMutationStatus.Success => Results.Redirect($"{ListPath}?notice=deleted"),

            // Authorization failures are a hard rejection, never a friendly redirect: the UI is not
            // the enforcement point, so a Member's direct POST must see the status code.
            ProjectMutationStatus.Forbidden => Forbidden(result),
            ProjectMutationStatus.NotFound => Results.NotFound(),
            _ => Results.NotFound(),
        };
    }

    private static IResult Forbidden(ProjectMutationResult result) =>
        Results.Text(
            result.Message ?? "You are not allowed to perform this action.",
            "text/plain",
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// PRG back to the form with the first field error and the submitted values. One code at a time is
    /// enough for a field-level message; the service still validates every field, so nothing invalid
    /// is written.
    /// </summary>
    private static IResult ValidationRedirect(string formPath, ProjectMutationResult result, ProjectForm form)
    {
        var code = result.FieldErrors?.FirstOrDefault()?.Code ?? ProjectErrorCodes.NameInvalid;
        var query = QueryString.Create(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["error"] = code,
            ["name"] = form.Name,
            ["repositoryUrl"] = form.RepositoryUrl,
            ["defaultBranch"] = form.DefaultBranch,
            ["pipelinePath"] = form.PipelinePath,
        });

        return Results.Redirect($"{formPath}{query}");
    }

    private static string? FindUserId(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>
    /// Empty on purpose: the parameter exists so the delete POST is form-bound and therefore covered
    /// by <c>UseAntiforgery</c>. The confirmation page is the confirmation; the only request payload
    /// required is the antiforgery token.
    /// </summary>
    public sealed class DeleteProjectForm
    {
    }

    /// <summary>
    /// The three editable fields plus the pipeline path. Deliberately no <c>ProjectId</c>: the route
    /// is the only source of the target project.
    /// </summary>
    public sealed class ProjectForm
    {
        public string? Name { get; set; }

        public string? RepositoryUrl { get; set; }

        public string? DefaultBranch { get; set; }

        public string? PipelinePath { get; set; }

        public ProjectInput ToInput() => new(Name, RepositoryUrl, DefaultBranch, PipelinePath);
    }
}
