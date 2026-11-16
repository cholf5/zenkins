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
/// values, so the page can show a field-level error without losing the user's input. A body that
/// binds to <em>nothing</em> - every field name lost, or none submitted at all - is the same
/// <c>?error=</c> redirect as an empty submission (F-019), not a null-reference 500.
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

        // F-020: the Freestyle steps editor's save. Same form-post + antiforgery shape as the other
        // mutations; the drafts travel as one JSON field the page serializes.
        group.MapPost("/{projectId:guid}/freestyle/steps", SaveFreestyleStepsAsync);

        // F-021: the parameters editor's save - the same shape, both kinds.
        group.MapPost("/{projectId:guid}/parameters", SaveParametersAsync);

        // F-028: the reversible trigger brake, and F-029: the queue controls. Same form-post shape
        // (antiforgery through UseAntiforgery) and the same 404/403 answers as every other project
        // mutation; the PRG target is the settings tab, which owns the forms and renders the codes.
        group.MapPost("/{projectId:guid}/disable", DisableProjectAsync);
        group.MapPost("/{projectId:guid}/enable", EnableProjectAsync);
        group.MapPost("/{projectId:guid}/limits", SaveLimitsAsync);

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

        // F-019: a body whose fields all fail to bind arrives as null, not as an empty form. The
        // service, not a guard, validates it, so the answer stays the page's own PRG error
        // (?error=name-required) and nothing is stored.
        var result = await projects.CreateAsync(
            userId,
            form?.ToInput() ?? EmptyInput(),
            httpContext.RequestAborted);
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

        // F-019: same shape as create - a lost body is the page's PRG error, never a null reference.
        var result = await projects.UpdateAsync(
            userId,
            projectId,
            form?.ToInput() ?? EmptyInput(),
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

    private static async Task<IResult> SaveFreestyleStepsAsync(
        Guid projectId,
        [FromForm] FreestyleStepsForm form,
        HttpContext httpContext,
        ProjectService projects)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // F-019's rule in miniature: a lost body is the page's PRG error, never a null reference.
        var result = await projects.SaveFreestyleStepsAsync(
            userId,
            projectId,
            form?.ToInput() ?? new FreestyleStepsInput(null),
            httpContext.RequestAborted);

        // The PRG target is the route that owns the editor (the Freestyle project's definition tab),
        // so a refusal lands on the page that can show the code - the overview reads neither `error`
        // nor the steps' notice.
        return result.Status switch
        {
            ProjectMutationStatus.Success =>
                Results.Redirect($"/projects/{projectId}/pipelines?notice=steps-saved"),
            ProjectMutationStatus.Forbidden => Forbidden(result),
            ProjectMutationStatus.NotFound => Results.NotFound(),
            _ => Results.Redirect($"/projects/{projectId}/pipelines?error={ProjectErrorCodes.StepsInvalid}"),
        };
    }

    private static async Task<IResult> SaveParametersAsync(
        Guid projectId,
        [FromForm] ProjectParametersForm form,
        HttpContext httpContext,
        ProjectService projects)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // F-019's rule in miniature: a lost body is the page's PRG error, never a null reference.
        var result = await projects.SaveParametersAsync(
            userId,
            projectId,
            form?.ToInput() ?? new ProjectParametersInput(null),
            httpContext.RequestAborted);

        // Same PRG target rule as the steps save: the Parameters tab renders the code, the overview
        // does not.
        return result.Status switch
        {
            ProjectMutationStatus.Success =>
                Results.Redirect($"/projects/{projectId}/parameters?notice=parameters-saved"),
            ProjectMutationStatus.Forbidden => Forbidden(result),
            ProjectMutationStatus.NotFound => Results.NotFound(),
            _ => Results.Redirect($"/projects/{projectId}/parameters?error={ProjectErrorCodes.ParametersInvalid}"),
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

    /// <summary>
    /// Card F-028: disables the project - every trigger refuses, the queue is canceled, history stays.
    /// A Member may do this (it is a brake, not a delete), and a form that carries no reason is refused
    /// with the field-level code the settings tab renders.
    /// </summary>
    private static async Task<IResult> DisableProjectAsync(
        Guid projectId,
        [FromForm] DisableProjectForm form,
        HttpContext httpContext,
        ProjectService projects)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // F-019's rule in miniature: a lost body is the page's PRG error, never a null reference.
        var result = await projects.SetDisabledAsync(
            userId,
            projectId,
            disabled: true,
            form?.Reason,
            httpContext.RequestAborted);

        return result.Status switch
        {
            ProjectMutationStatus.Success =>
                Results.Redirect($"/projects/{projectId}/settings?notice=project-disabled"),
            ProjectMutationStatus.Forbidden => Forbidden(result),
            ProjectMutationStatus.NotFound => Results.NotFound(),
            _ => Results.Redirect($"/projects/{projectId}/settings?error={FirstCode(result)}"),
        };
    }

    /// <summary>
    /// Card F-028: re-enables the project. Nothing is replayed - canceled runs stay canceled and no
    /// refused trigger is retried; the next trigger queues a run like any other.
    /// </summary>
    private static async Task<IResult> EnableProjectAsync(
        Guid projectId,
        [FromForm] EnableProjectForm form,
        HttpContext httpContext,
        ProjectService projects)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await projects.SetDisabledAsync(
            userId,
            projectId,
            disabled: false,
            reason: null,
            httpContext.RequestAborted);

        return result.Status switch
        {
            ProjectMutationStatus.Success =>
                Results.Redirect($"/projects/{projectId}/settings?notice=project-enabled"),
            ProjectMutationStatus.Forbidden => Forbidden(result),
            ProjectMutationStatus.NotFound => Results.NotFound(),
            _ => Results.Redirect($"/projects/{projectId}/settings?error={FirstCode(result)}"),
        };
    }

    /// <summary>
    /// Card F-029: saves the three queue controls. Blank means the default (unlimited / zero), so the
    /// form can express "behave exactly as before" without a second toggle.
    /// </summary>
    private static async Task<IResult> SaveLimitsAsync(
        Guid projectId,
        [FromForm] ProjectLimitsForm form,
        HttpContext httpContext,
        ProjectService projects)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // F-019's rule in miniature: a lost body is the page's PRG error, never a null reference.
        var result = await projects.SaveLimitsAsync(
            userId,
            projectId,
            form?.ToInput() ?? new ProjectLimitsInput(null, null, null),
            httpContext.RequestAborted);

        return result.Status switch
        {
            ProjectMutationStatus.Success =>
                Results.Redirect($"/projects/{projectId}/settings?notice=limits-saved"),
            ProjectMutationStatus.Forbidden => Forbidden(result),
            ProjectMutationStatus.NotFound => Results.NotFound(),
            _ => Results.Redirect($"/projects/{projectId}/settings?error={FirstCode(result)}"),
        };
    }

    /// <summary>The first field error's code, or the name field's when none was reported (F-019).</summary>
    private static string FirstCode(ProjectMutationResult result) =>
        result.FieldErrors?.FirstOrDefault()?.Code ?? ProjectErrorCodes.NameInvalid;

    private static IResult Forbidden(ProjectMutationResult result) =>
        Results.Text(
            result.Message ?? "You are not allowed to perform this action.",
            "text/plain",
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// PRG back to the form with the first field error and the submitted values. One code at a time is
    /// enough for a field-level message; the service still validates every field, so nothing invalid
    /// is written. The form is nullable because a body that bound to nothing has no values to echo
    /// (F-019); the code is then the first required field's.
    /// </summary>
    private static IResult ValidationRedirect(string formPath, ProjectMutationResult result, ProjectForm? form)
    {
        var code = result.FieldErrors?.FirstOrDefault()?.Code ?? ProjectErrorCodes.NameInvalid;
        var query = QueryString.Create(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["error"] = code,
            ["name"] = form?.Name,
            ["kind"] = form?.Kind,
            ["repositoryUrl"] = form?.RepositoryUrl,
            ["defaultBranch"] = form?.DefaultBranch,
            ["pipelinePath"] = form?.PipelinePath,
        });

        return Results.Redirect($"{formPath}{query}");
    }

    private static string? FindUserId(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>
    /// The stand-in for a form that bound null (a POST carrying no field the model knows): empty
    /// values, so the service's validation - not a null reference - answers the request (F-019).
    /// </summary>
    private static ProjectInput EmptyInput() =>
        new(Name: null, Kind: null, RepositoryUrl: null, DefaultBranch: null, PipelinePath: null);

    /// <summary>
    /// Card F-028's disable form: the reason is the only field, and it is required - the person whose
    /// trigger is refused reads it, so "disabled" alone would leave them guessing.
    /// </summary>
    public sealed class DisableProjectForm
    {
        public string? Reason { get; set; }
    }

    /// <summary>Empty on purpose: the parameter makes the enable POST form-bound (antiforgery).</summary>
    public sealed class EnableProjectForm
    {
    }

    /// <summary>
    /// Card F-029's limits form. All three fields are optional text: blank is "the default". They are
    /// not bound as numbers so a typo reaches the service's validation and comes back as a field-level
    /// code instead of a binding failure.
    /// </summary>
    public sealed class ProjectLimitsForm
    {
        public string? MaxConcurrentRuns { get; set; }

        public string? QuietPeriodSeconds { get; set; }

        public string? MaxQueuedRuns { get; set; }

        public ProjectLimitsInput ToInput() => new(MaxConcurrentRuns, QuietPeriodSeconds, MaxQueuedRuns);
    }

    /// <summary>
    /// Empty on purpose: the parameter exists so the delete POST is form-bound and therefore covered
    /// by <c>UseAntiforgery</c>. The confirmation page is the confirmation; the only request payload
    /// required is the antiforgery token.
    /// </summary>
    public sealed class DeleteProjectForm
    {
    }

    /// <summary>
    /// The editable fields plus the pipeline path and (since F-020) the kind. Deliberately no
    /// <c>ProjectId</c>: the route is the only source of the target project. The kind binds from a
    /// radio group; a form that omits it keeps the historical Pipeline default (see
    /// <see cref="ProjectValidation.ValidateKind"/>).
    /// </summary>
    public sealed class ProjectForm
    {
        public string? Name { get; set; }

        public string? Kind { get; set; }

        public string? RepositoryUrl { get; set; }

        public string? DefaultBranch { get; set; }

        public string? PipelinePath { get; set; }

        public ProjectInput ToInput() => new(Name, Kind, RepositoryUrl, DefaultBranch, PipelinePath);
    }

    /// <summary>
    /// The Freestyle steps editor's payload (F-020): one JSON field holding the draft list. The field
    /// is serialized by the page (which owns the dynamic row list) and parsed, built and validated
    /// server-side by <see cref="FreestyleSteps.BuildSpecJson"/> - the browser never authors a spec.
    /// </summary>
    public sealed class FreestyleStepsForm
    {
        public string? StepsJson { get; set; }

        public FreestyleStepsInput ToInput() => new(StepsJson);
    }

    /// <summary>
    /// The parameters editor's payload (F-021): one JSON field holding the draft list, parsed and
    /// validated server-side by <see cref="ProjectParameters.BuildDefinitionsJson"/>.
    /// </summary>
    public sealed class ProjectParametersForm
    {
        public string? ParametersJson { get; set; }

        public ProjectParametersInput ToInput() => new(ParametersJson);
    }
}
