using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Zenkins.Server.Snippets;

/// <summary>
/// The snippet library's form endpoints (card F-035): create, edit, delete. Form-bound like every other
/// project/organization mutation, so <c>UseAntiforgery</c> validates each call; the route id is authoritative
/// and the service enforces membership in the active organization. Outcomes follow the repo's shape: a PRG
/// redirect with a stable <c>?notice=</c>/<c>?error=</c> code, a bodyless 404 for a foreign or missing id, and
/// a 403 for a non-member - no state changes on either refusal.
/// <para>
/// <b>No runtime route exists.</b> A snippet is text an editor copies; nothing fetches it per step or per run,
/// which is what makes "editing or deleting a snippet never changes a saved project or a run" true by
/// construction (there is no reference to follow).
/// </para>
/// </summary>
public static class StepSnippetEndpoints
{
    /// <summary>The page that owns the create/edit/delete forms and every redirect target.</summary>
    public const string ListPath = "/snippets";

    /// <summary>Maps the three mutation endpoints.</summary>
    public static IEndpointRouteBuilder MapStepSnippetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/snippets").RequireAuthorization();

        group.MapPost("/create", CreateAsync);
        group.MapPost("/{snippetId:guid}/edit", EditAsync);
        group.MapPost("/{snippetId:guid}/delete", DeleteAsync);

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        [FromForm] SnippetForm form,
        HttpContext httpContext,
        StepSnippetService snippets)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await snippets.CreateAsync(
            userId,
            form?.ToInput() ?? new SnippetInput(null, null, null, null),
            httpContext.RequestAborted);

        return result.Status switch
        {
            SnippetMutationStatus.Success => Results.Redirect($"{ListPath}?notice=created"),
            SnippetMutationStatus.Forbidden => Results.Forbid(),
            SnippetMutationStatus.NotFound => Results.NotFound(),
            _ => Results.Redirect($"{ListPath}?error={FirstCode(result)}"),
        };
    }

    private static async Task<IResult> EditAsync(
        Guid snippetId,
        [FromForm] SnippetForm form,
        HttpContext httpContext,
        StepSnippetService snippets)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await snippets.UpdateAsync(
            userId,
            snippetId,
            form?.ToInput() ?? new SnippetInput(null, null, null, null),
            httpContext.RequestAborted);

        return result.Status switch
        {
            SnippetMutationStatus.Success => Results.Redirect($"{ListPath}?notice=updated"),
            SnippetMutationStatus.Forbidden => Results.Forbid(),
            // A foreign id and a missing one are the same answer - the tenant filter has already decided.
            SnippetMutationStatus.NotFound => Results.NotFound(),
            _ => Results.Redirect($"{ListPath}?error={FirstCode(result)}"),
        };
    }

    private static async Task<IResult> DeleteAsync(
        Guid snippetId,
        HttpContext httpContext,
        StepSnippetService snippets)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await snippets.DeleteAsync(userId, snippetId, httpContext.RequestAborted);

        return result.Status switch
        {
            SnippetMutationStatus.Success => Results.Redirect($"{ListPath}?notice=deleted"),
            SnippetMutationStatus.Forbidden => Results.Forbid(),
            SnippetMutationStatus.NotFound => Results.NotFound(),
            _ => Results.Redirect($"{ListPath}?error={FirstCode(result)}"),
        };
    }

    private static string? FindUserId(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static string FirstCode(SnippetMutationResult result) =>
        result.FieldErrors is { Count: > 0 } errors ? errors[0].Code : SnippetErrorCodes.NameRequired;
}

/// <summary>The snippet form's raw fields, bound from the page's form.</summary>
public sealed class SnippetForm
{
    public string? Name { get; set; }

    public string? Kind { get; set; }

    public string? Body { get; set; }

    public string? Description { get; set; }

    public SnippetInput ToInput() => new(Name, Kind, Body, Description);
}
