using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Zenkins.Server.Folders;

/// <summary>
/// The real POST endpoints behind the folder forms (card F-042): create, rename, move and delete. The
/// pages only render forms; these routes are the enforcement, exactly like the project forms (F-003).
/// <para>
/// <b>Form-bound</b>, so <c>UseAntiforgery</c> validates every call and a direct POST from curl hits the
/// same server-side membership check. The route id is authoritative: no form model carries a folder id,
/// so a forged field cannot redirect a rename or a delete to another folder.
/// </para>
/// <para>
/// <b>Outcomes.</b> Success is a PRG redirect with <c>?notice=</c>; a foreign or missing folder id is a
/// bare <c>404</c>; validation failures redirect back with a stable <c>?error=</c> code and the submitted
/// name, so the form keeps the user's input. The redirect targets are the pages that own the forms, which
/// are also the pages that render the codes.
/// </para>
/// </summary>
public static class FolderEndpoints
{
    /// <summary>The create form page.</summary>
    public const string NewPath = "/folders/new";

    /// <summary>The root listing (the delete success target: the folder it lived in may be gone too).</summary>
    public const string ProjectsPath = "/projects";

    /// <summary>Maps the four folder mutations. All require authentication.</summary>
    public static IEndpointRouteBuilder MapFolderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/folders").RequireAuthorization();

        group.MapPost("/create", CreateAsync);
        group.MapPost("/{folderId:guid}/rename", RenameAsync);
        group.MapPost("/{folderId:guid}/move", MoveAsync);
        group.MapPost("/{folderId:guid}/delete", DeleteAsync);

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        [FromForm] FolderForm form,
        HttpContext httpContext,
        FolderService folders)
    {
        if (FindUserId(httpContext) is not { } userId)
        {
            return Results.Unauthorized();
        }

        // F-019's shape: a body whose fields all fail to bind arrives as null, and the service - not a
        // null reference - answers it with the page's own PRG error.
        var result = await folders.CreateAsync(
            userId,
            form?.ToInput() ?? new FolderInput(null),
            httpContext.RequestAborted);

        return result.Status switch
        {
            FolderMutationStatus.Success =>
                Results.Redirect($"/folders/{result.FolderId}?notice=created"),
            FolderMutationStatus.Forbidden => Forbidden(result),
            FolderMutationStatus.NotFound => Results.NotFound(),
            _ => CreateValidationRedirect(result, form),
        };
    }

    private static async Task<IResult> RenameAsync(
        Guid folderId,
        [FromForm] FolderForm form,
        HttpContext httpContext,
        FolderService folders)
    {
        if (FindUserId(httpContext) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await folders.UpdateAsync(
            userId,
            folderId,
            form?.ToInput() ?? new FolderInput(null),
            httpContext.RequestAborted);

        return result.Status switch
        {
            FolderMutationStatus.Success => Results.Redirect($"/folders/{folderId}?notice=renamed"),
            FolderMutationStatus.Forbidden => Forbidden(result),
            FolderMutationStatus.NotFound => Results.NotFound(),
            _ => ValidationRedirect($"/folders/{folderId}", result, form?.Name),
        };
    }

    private static async Task<IResult> MoveAsync(
        Guid folderId,
        [FromForm] FolderForm form,
        HttpContext httpContext,
        FolderService folders)
    {
        if (FindUserId(httpContext) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await folders.MoveAsync(
            userId,
            folderId,
            form?.ParentFolderId,
            httpContext.RequestAborted);

        return result.Status switch
        {
            FolderMutationStatus.Success => Results.Redirect($"/folders/{folderId}?notice=moved"),
            FolderMutationStatus.Forbidden => Forbidden(result),
            FolderMutationStatus.NotFound => Results.NotFound(),
            _ => Results.Redirect($"/folders/{folderId}{ErrorQuery(result)}"),
        };
    }

    private static async Task<IResult> DeleteAsync(
        Guid folderId,
        HttpContext httpContext,
        FolderService folders)
    {
        if (FindUserId(httpContext) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await folders.DeleteAsync(userId, folderId, httpContext.RequestAborted);

        return result.Status switch
        {
            FolderMutationStatus.Success => Results.Redirect($"{ProjectsPath}?notice=folder-deleted"),
            FolderMutationStatus.Forbidden => Forbidden(result),
            FolderMutationStatus.NotFound => Results.NotFound(),
            _ => Results.Redirect($"/folders/{folderId}{ErrorQuery(result)}"),
        };
    }

    private static IResult Forbidden(FolderMutationResult result) =>
        Results.Text(
            result.Message ?? "You are not allowed to perform this action.",
            "text/plain",
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>The create form's PRG error, echoing the name and the chosen parent so nothing is lost.</summary>
    private static IResult CreateValidationRedirect(FolderMutationResult result, FolderForm? form)
    {
        var query = QueryString.Create(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["error"] = FirstCode(result),
            ["name"] = form?.Name,
            ["parent"] = form?.ParentFolderId,
            ["description"] = form?.Description,
        });

        return Results.Redirect($"{NewPath}{query}");
    }

    /// <summary>A folder page's PRG error: the stable code, plus the name for the form to keep.</summary>
    private static IResult ValidationRedirect(string path, FolderMutationResult result, string? name)
    {
        var query = QueryString.Create(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["error"] = FirstCode(result),
            ["name"] = name,
        });

        return Results.Redirect($"{path}{query}");
    }

    private static string ErrorQuery(FolderMutationResult result) =>
        QueryString.Create(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["error"] = FirstCode(result),
        }).ToString();

    private static string FirstCode(FolderMutationResult result) =>
        result.FieldErrors is { Count: > 0 } errors ? errors[0].Code : FolderErrorCodes.NameInvalid;

    private static string? FindUserId(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
}

/// <summary>
/// The create/rename form's fields. <see cref="ParentFolderId"/> is blank at the root and a folder id
/// otherwise; a malformed value is refused rather than coerced to the root.
/// </summary>
public sealed class FolderForm
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? ParentFolderId { get; set; }

    public FolderInput ToInput() => new(Name, Description, ParentFolderId);
}
