using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Zenkins.Server.Runs;

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

        // F-042: the explicit move into another folder (or back to the root). Deliberately its own route:
        // the edit form never moves a project, and this route refuses a name collision instead of
        // renaming the project to fit.
        group.MapPost("/{projectId:guid}/move", MoveAsync);

        // F-020: the Freestyle steps editor's save. Same form-post + antiforgery shape as the other
        // mutations; the drafts travel as one JSON field the page serializes.
        group.MapPost("/{projectId:guid}/freestyle/steps", SaveFreestyleStepsAsync);

        // F-021: the parameters editor's save - the same shape, both kinds.
        group.MapPost("/{projectId:guid}/parameters", SaveParametersAsync);

        // F-026: the chain edges, declared from the project's settings tab. Same form-post +
        // antiforgery shape as every other project mutation; the service owns the graph check (a
        // cycle is refused with the path that would close it) and the tenant-filtered lookups.
        group.MapPost("/{projectId:guid}/links", SaveLinkAsync);
        group.MapPost("/{projectId:guid}/links/{linkId:guid}/delete", DeleteLinkAsync);

        // F-022: the inline Pipeline editor's save and its definition-only Validate action. Both are
        // the same form-post + antiforgery shape as every other project mutation; the page that owns
        // the editor renders the notice/error and the validation's step list.
        group.MapPost("/{projectId:guid}/inline/script", SaveInlineScriptAsync);
        group.MapPost("/{projectId:guid}/inline/validate", ValidateInlineScriptAsync);

        // F-028: the reversible trigger brake, and F-029: the queue controls. Same form-post shape
        // (antiforgery through UseAntiforgery) and the same 404/403 answers as every other project
        // mutation; the PRG target is the settings tab, which owns the forms and renders the codes.
        group.MapPost("/{projectId:guid}/disable", DisableProjectAsync);
        group.MapPost("/{projectId:guid}/enable", EnableProjectAsync);
        group.MapPost("/{projectId:guid}/limits", SaveLimitsAsync);

        // F-025: the polling switch and interval, from the project's settings card.
        group.MapPost("/{projectId:guid}/polling", SavePollingAsync);

        // F-038: the retention policy (keep N builds and/or D days), from the settings card. One form, two
        // submits: the preview button names itself and answers "what would the next sweep delete?" with the
        // same evaluation the sweep itself runs, without storing anything.
        group.MapPost("/{projectId:guid}/retention", SaveRetentionAsync);
        // F-031: the explicit "a failed post step fails the run" switch, from the settings tab.
        group.MapPost("/{projectId:guid}/post-failure", SavePostFailureAsync);

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

    private static async Task<IResult> MoveAsync(
        Guid projectId,
        [FromForm] ProjectMoveForm form,
        HttpContext httpContext,
        ProjectService projects)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await projects.MoveToFolderAsync(
            userId,
            projectId,
            form?.FolderId,
            httpContext.RequestAborted);

        return result.Status switch
        {
            // The success lands where the project now lives, so the person sees it in its new place.
            ProjectMutationStatus.Success =>
                Results.Redirect($"{FolderPathFor(form?.FolderId)}?notice=project-moved"),
            ProjectMutationStatus.Forbidden => Forbidden(result),
            ProjectMutationStatus.NotFound => Results.NotFound(),

            // A refusal lands back on the page that owns the form, with the code and the existing name.
            _ => Results.Redirect($"{FolderPathFor(form?.FromFolderId)}{ErrorQuery(result)}"),
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

    /// <summary>
    /// Card F-026: declares one upstream/downstream edge from the settings tab. <c>Direction</c> says which
    /// side the form was on - the same row either way. A refusal (a cycle, a duplicate, a self-link, a branch
    /// policy that is neither known policy) is a PRG back to the tab with the service's own sentence in the
    /// query, so the page can name the path that would close; nothing was written.
    /// </summary>
    private static async Task<IResult> SaveLinkAsync(
        Guid projectId,
        [FromForm] ProjectLinkForm form,
        HttpContext httpContext,
        ProjectLinkService links)
    {
        if (FindUserId(httpContext) is null)
        {
            return Results.Unauthorized();
        }

        // F-019's rule in miniature: a lost body is the page's PRG error, never a null reference.
        if (form?.TargetProjectId is not { } targetProjectId || targetProjectId == Guid.Empty)
        {
            return Results.Redirect($"/projects/{projectId}/settings?links=target-required");
        }

        var result = await links.SaveAsync(
            projectId,
            targetProjectId,
            isDownstream: !string.Equals(form.Direction, "upstream", StringComparison.Ordinal),
            onlyOnSuccess: form.OnlyOnSuccess,
            branchPolicy: form.BranchPolicy,
            httpContext.RequestAborted);

        return result.Status switch
        {
            ProjectLinkStatus.Success =>
                Results.Redirect($"/projects/{projectId}/settings?links=saved"),

            // A foreign or missing project is the same bodyless 404 every tenant-owned row answers - and it
            // is what an edge pointing outside the organization looks like from here.
            ProjectLinkStatus.NotFound => Results.NotFound(),

            // A cycle, a duplicate or an unknown policy is a well-formed request the graph refused: the
            // service's sentence carries the path, so the reader can act on it instead of guessing.
            _ => Results.Redirect(
                $"/projects/{projectId}/settings?links=refused&reason={Uri.EscapeDataString(result.Message ?? string.Empty)}"),
        };
    }

    /// <summary>
    /// Card F-026: removes one edge named on the settings tab. The service resolves both ids through the
    /// tenant filter, so a link of another organization - or one that does not touch this project - is the
    /// same bodyless 404, and no row is touched.
    /// </summary>
    private static async Task<IResult> DeleteLinkAsync(
        Guid projectId,
        Guid linkId,
        HttpContext httpContext,
        ProjectLinkService links)
    {
        if (FindUserId(httpContext) is null)
        {
            return Results.Unauthorized();
        }

        var deleted = await links.DeleteAsync(projectId, linkId, httpContext.RequestAborted);
        return deleted
            ? Results.Redirect($"/projects/{projectId}/settings?links=deleted")
            : Results.NotFound();
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

    private static async Task<IResult> SaveInlineScriptAsync(
        Guid projectId,
        [FromForm] InlineScriptForm form,
        HttpContext httpContext,
        ProjectService projects)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await projects.SaveInlineScriptAsync(
            userId,
            projectId,
            form?.Script,
            httpContext.RequestAborted);

        return result.Status switch
        {
            ProjectMutationStatus.Success =>
                Results.Redirect($"/projects/{projectId}/pipelines?notice=script-saved"),
            ProjectMutationStatus.Forbidden => Forbidden(result),
            ProjectMutationStatus.NotFound => Results.NotFound(),
            _ => Results.Redirect(
                $"/projects/{projectId}/pipelines?error={result.FieldErrors?.FirstOrDefault()?.Code ?? ProjectErrorCodes.InlineScriptRequired}"),
        };
    }

    /// <summary>
    /// F-022's Validate: run the posted script's definition phase on a Worker and show the resulting
    /// step list. It is deliberately <b>not</b> a save: the text the person is looking at is what gets
    /// validated, and saving alone never claims a script is valid. The request creates no run row and
    /// takes no build slot (see <see cref="Probes.DefinitionValidationService"/>); a repository project
    /// has no script to validate and is refused with the editor's own stable code.
    /// </summary>
    private static async Task<IResult> ValidateInlineScriptAsync(
        Guid projectId,
        [FromForm] InlineScriptForm form,
        HttpContext httpContext,
        ProjectService projects,
        Probes.DefinitionValidationService validations)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await validations.ValidateInlineScriptAsync(
            userId,
            projectId,
            form?.Script,
            httpContext.RequestAborted);

        return result.Status switch
        {
            Probes.DefinitionValidationStatus.Success =>
                Results.Redirect($"/projects/{projectId}/pipelines?validated={result.ValidationId}"),
            Probes.DefinitionValidationStatus.Forbidden =>
                Results.Redirect($"/projects/{projectId}/pipelines?error={ProjectErrorCodes.PipelineSourceInvalid}"),
            Probes.DefinitionValidationStatus.NotFound => Results.NotFound(),
            Probes.DefinitionValidationStatus.InvalidInput =>
                Results.Redirect($"/projects/{projectId}/pipelines?error={result.ErrorCode ?? ProjectErrorCodes.InlineScriptRequired}"),
            _ => Results.Redirect($"/projects/{projectId}/pipelines?error={ProjectErrorCodes.InlineScriptRequired}"),
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
    /// Card F-031: flips the "a failed post step fails the run" switch. A settings-tab form, so the PRG
    /// target is that tab with a notice; authorization and tenancy answers stay hard.
    /// </summary>
    private static async Task<IResult> SavePostFailureAsync(
        Guid projectId,
        [FromForm] PostFailureForm form,
        HttpContext httpContext,
        ProjectService projects)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var enabled = form?.Enabled ?? false;
        var result = await projects.SetPostFailureFailsRunAsync(
            userId,
            projectId,
            enabled,
            httpContext.RequestAborted);

        return result.Status switch
        {
            ProjectMutationStatus.Success =>
                Results.Redirect($"/projects/{projectId}/settings?notice={(enabled ? "post-failure-on" : "post-failure-off")}"),
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

    /// <summary>Card F-025: saves the polling card (switch + interval).</summary>
    private static async Task<IResult> SavePollingAsync(
        Guid projectId,
        [FromForm] ProjectPollingForm form,
        HttpContext httpContext,
        ProjectService projects)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // F-019's rule in miniature: a lost body is the page's PRG error, never a null reference.
        var result = await projects.SavePollingAsync(
            userId,
            projectId,
            form?.ToInput() ?? new ProjectPollingInput(null, null),
            httpContext.RequestAborted);

        return result.Status switch
        {
            ProjectMutationStatus.Success =>
                Results.Redirect($"/projects/{projectId}/settings?notice=polling-saved"),
            ProjectMutationStatus.Forbidden => Forbidden(result),
            ProjectMutationStatus.NotFound => Results.NotFound(),
            _ => Results.Redirect($"/projects/{projectId}/settings?error={FirstCode(result)}"),
        };
    }

    /// <summary>
    /// Card F-038: saves the two retention rules, or previews what they would delete. Blank means "the rule is
    /// not set" - the form expresses "keep everything" without a second toggle, and a body whose fields are
    /// missing entirely is the same "keep everything" (F-019's rule: a lost body is a PRG answer, not a crash).
    /// </summary>
    private static async Task<IResult> SaveRetentionAsync(
        Guid projectId,
        [FromForm] ProjectRetentionForm form,
        HttpContext httpContext,
        RetentionService retention)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        if (!TryReadOptionalCount(form?.RetentionKeep, out var keep))
        {
            return Results.Redirect($"/projects/{projectId}/settings?retentionError={RetentionErrorCodes.KeepInvalid}");
        }

        if (!TryReadOptionalCount(form?.RetentionDays, out var days))
        {
            return Results.Redirect($"/projects/{projectId}/settings?retentionError={RetentionErrorCodes.DaysInvalid}");
        }

        // The preview echoes the two values back so the card computes the same numbers the save would store. A
        // present-but-empty parameter is "this rule is unset", which is why the echo is not simply omitted.
        if (string.Equals(form?.Action, "preview", StringComparison.Ordinal))
        {
            var query = QueryString.Create(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["retentionPreviewKeep"] = form?.RetentionKeep ?? string.Empty,
                ["retentionPreviewDays"] = form?.RetentionDays ?? string.Empty,
            });
            return Results.Redirect($"/projects/{projectId}/settings{query}");
        }

        var result = await retention.SavePolicyAsync(
            userId,
            projectId,
            keep,
            days,
            httpContext.RequestAborted);

        return result.Status switch
        {
            RetentionMutationStatus.Success =>
                Results.Redirect($"/projects/{projectId}/settings?notice=retention-saved"),
            RetentionMutationStatus.Forbidden => ForbiddenText(result.Message),
            RetentionMutationStatus.NotFound => Results.NotFound(),
            _ => Results.Redirect(
                $"/projects/{projectId}/settings?retentionError={result.ErrorCode ?? RetentionErrorCodes.OutOfRange}"),
        };
    }

    /// <summary>Reads one retention field: blank is "unset", anything not a non-negative integer is refused.</summary>
    private static bool TryReadOptionalCount(string? text, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static IResult ForbiddenText(string? message) =>
        Results.Text(message ?? "You are not allowed to perform this action.", "text/plain",
            statusCode: StatusCodes.Status403Forbidden);

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

            // F-022: the source is small enough to echo back (the script is not: it can be 64 KiB and
            // the URL is not the place for it; the editor starts empty after a refused create).
            ["pipelineSource"] = form?.PipelineSource,

            // F-023: the discovery choice is a word, so the refused create/edit keeps it selected.
            ["branchDiscovery"] = form?.BranchDiscovery,

            // F-042: the folder the project was being created in, so a refused create keeps the choice.
            ["folder"] = form?.FolderId,

            // F-042: the existing project the name collided with, so the message names it (TC-002).
            ["conflict"] = result.ConflictName,
        });

        return Results.Redirect($"{formPath}{query}");
    }

    private static string? FindUserId(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>The page that lists one folder (or the root listing when the value is blank/not a guid).</summary>
    private static string FolderPathFor(string? folderId) =>
        Guid.TryParse(folderId?.Trim(), out var id) ? $"/folders/{id}" : "/projects";

    /// <summary>A refusal's PRG query: the stable code, plus the existing project's name when it collided.</summary>
    private static string ErrorQuery(ProjectMutationResult result) =>
        QueryString.Create(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["error"] = result.FieldErrors?.FirstOrDefault()?.Code ?? ProjectErrorCodes.NameInvalid,
            ["conflict"] = result.ConflictName,
        }).ToString();

    /// <summary>
    /// The stand-in for a form that bound null (a POST carrying no field the model knows): empty
    /// values, so the service's validation - not a null reference - answers the request (F-019).
    /// </summary>
    private static ProjectInput EmptyInput() =>
        new(
            Name: null,
            Kind: null,
            RepositoryUrl: null,
            DefaultBranch: null,
            PipelinePath: null,
            BranchDiscovery: null,
            FolderId: null);

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
    /// Card F-031's switch form. The checkbox posts <c>true</c> when ticked and nothing when not, which binds
    /// to <see langword="false"/> - the same reading the page showed.
    /// </summary>
    public sealed class PostFailureForm
    {
        public bool Enabled { get; set; }
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
    /// Card F-026's chain form: the other project, which side of this one it sits on, whether a failed
    /// upstream still triggers, and the branch policy. The field names are the form's own, so the page and
    /// this binding cannot drift.
    /// </summary>
    public sealed class ProjectLinkForm
    {
        /// <summary>The other end of the edge. Absent when the select was not submitted at all (F-019).</summary>
        public Guid? TargetProjectId { get; set; }

        /// <summary><c>downstream</c> (this project's terminal run queues the target) or <c>upstream</c>.</summary>
        public string? Direction { get; set; }

        /// <summary>
        /// Whether only a successful upstream triggers. The unchecked form posts <c>false</c> through its
        /// hidden twin, so this default only matters for a body that omitted the flag entirely.
        /// </summary>
        public bool OnlyOnSuccess { get; set; } = true;

        /// <summary>One of <see cref="ProjectLinkBranchPolicies"/>; blank means the default.</summary>
        public string? BranchPolicy { get; set; }
    }

    /// <summary>
    /// Card F-038's retention form: the two rules as text (blank is unset - "keep everything"), plus the action
    /// that distinguishes the preview submit from the save submit. The fields are not bound as numbers so a typo
    /// reaches the handler's own check and comes back as a stable code instead of a binding failure.
    /// </summary>
    public sealed class ProjectRetentionForm
    {
        /// <summary>The "keep the newest N builds of each branch" rule; blank means not set.</summary>
        public string? RetentionKeep { get; set; }

        /// <summary>The "keep builds newer than D days" rule; blank means not set.</summary>
        public string? RetentionDays { get; set; }

        /// <summary><c>preview</c> when the preview button submitted the form, otherwise null (the save).</summary>
        public string? Action { get; set; }
    }

    /// <summary>Card F-025's polling form: the checkbox and the interval, both as the browser sends them.</summary>
    public sealed class ProjectPollingForm
    {
        public string? PollingEnabled { get; set; }

        public string? PollIntervalMinutes { get; set; }

        public ProjectPollingInput ToInput() => new(PollingEnabled, PollIntervalMinutes);
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
    /// F-042's move form (on a folder page or the root listing): the target folder, plus the folder the
    /// form was rendered on - purely to choose the PRG target of a refusal. Both are folder ids or blank
    /// (the root), and the endpoint builds the path itself, so the value can never become an open redirect.
    /// </summary>
    public sealed class ProjectMoveForm
    {
        public string? FolderId { get; set; }

        public string? FromFolderId { get; set; }
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

        /// <summary>F-022: the create form's definition source (Pipeline kind only).</summary>
        public string? PipelineSource { get; set; }

        /// <summary>F-022: the create form's inline Lua body, posted only for the inline source.</summary>
        public string? InlineScript { get; set; }

        /// <summary>F-023: the branch-discovery selector (repository Pipeline projects only). Blank
        /// keeps the historical one-branch behaviour.</summary>
        public string? BranchDiscovery { get; set; }

        /// <summary>F-042: the create form's parent folder (blank = the organization's root).</summary>
        public string? FolderId { get; set; }

        public ProjectInput ToInput() => new(
            Name,
            Kind,
            RepositoryUrl,
            DefaultBranch,
            PipelinePath,
            PipelineSource,
            InlineScript,
            BranchDiscovery,
            FolderId);
    }

    /// <summary>
    /// F-022's inline editor payload: the Lua body, on its own. The save endpoint stores it; the
    /// validate endpoint runs its definition phase without storing anything.
    /// </summary>
    public sealed class InlineScriptForm
    {
        public string? Script { get; set; }
    }

    /// <summary>
    /// The Freestyle steps editor's payload (F-020): one JSON field holding the draft list. The field
    /// is serialized by the page (which owns the dynamic row list) and parsed, built and validated
    /// server-side by <see cref="FreestyleSteps.BuildSpecJson"/> - the browser never authors a spec.
    /// </summary>
    public sealed class FreestyleStepsForm
    {
        public string? StepsJson { get; set; }

        /// <summary>Card F-031: the three post lists, each its own JSON array like the main one.</summary>
        public string? AlwaysStepsJson { get; set; }

        public string? SuccessStepsJson { get; set; }

        public string? FailureStepsJson { get; set; }

        public FreestyleStepsInput ToInput() => new(
            StepsJson,
            AlwaysStepsJson,
            SuccessStepsJson,
            FailureStepsJson);
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
