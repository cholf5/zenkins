using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Zenkins.Server.Workers;

namespace Zenkins.Server.Git;

/// <summary>
/// The Git credential HTTP surface (card F-013): Owner-only management for people, and one
/// run-scoped fetch for workers.
/// <para>
/// <b>No value-returning user route.</b> The three mutations accept a value and store ciphertext;
/// nothing on the user side reads one back - not a page, not a JSON endpoint. The single decrypting
/// route is the worker's <c>GET /api/workers/runs/{runId}/git-credential</c>, which is bearer-
/// authenticated, run-scoped and answers only the worker currently assigned to that run.
/// </para>
/// <para>
/// <b>Why the management routes answer JSON instead of a PRG redirect.</b> There is no credential page
/// in this card (the project settings page that consumes <c>Project.GitCredentialId</c> is a later
/// card); a redirect to a page that does not exist would be a lie. The status codes are the contract:
/// 200 with the id on success, 403 for a non-Owner, 404 for a missing/foreign id, 400 with a stable
/// code for invalid input. The endpoints are form-bound, so <c>UseAntiforgery</c> still validates every
/// call.
/// </para>
/// <para>
/// <b>The worker route is exempt from cookie auth</b> (like the run endpoints): a bearer token
/// authenticates and selects the tenant. A failure is one bodyless 404 - cross-tenant, foreign-run and
/// unknown-run are indistinguishable, and the server log carries one neutral line for all of them.
/// </para>
/// </summary>
public static class GitCredentialEndpoints
{
    /// <summary>The management group's prefix (the future project-settings page targets it).</summary>
    public const string ManagementPath = "/git-credentials";

    /// <summary>The worker fetch route template.</summary>
    public const string WorkerFetchPath = "/api/workers/runs/{runId:guid}/git-credential";

    /// <summary>Maps the management endpoints and the worker fetch.</summary>
    public static IEndpointRouteBuilder MapGitCredentialEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var management = endpoints.MapGroup(ManagementPath).RequireAuthorization();
        management.MapPost("/create", CreateAsync);
        management.MapPost("/{credentialId:guid}/rotate", RotateAsync);
        management.MapPost("/{credentialId:guid}/delete", DeleteAsync);

        endpoints.MapGet(WorkerFetchPath, FetchMaterialAsync);
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        [FromForm] CreateGitCredentialForm form,
        HttpContext httpContext,
        GitCredentialService credentials)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await credentials.CreateAsync(userId, form.ToInput(), httpContext.RequestAborted);
        return result.Status switch
        {
            GitCredentialMutationStatus.Success => Results.Json(
                new GitCredentialMutationResponse(result.GitCredentialId!.Value)),
            GitCredentialMutationStatus.Forbidden => Forbidden(result),
            GitCredentialMutationStatus.NotFound => Results.NotFound(),
            _ => Invalid(result),
        };
    }

    private static async Task<IResult> RotateAsync(
        Guid credentialId,
        [FromForm] RotateGitCredentialForm form,
        HttpContext httpContext,
        GitCredentialService credentials)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await credentials.RotateAsync(
            userId,
            credentialId,
            form.ToInput(),
            httpContext.RequestAborted);
        return result.Status switch
        {
            GitCredentialMutationStatus.Success => Results.Json(
                new GitCredentialMutationResponse(result.GitCredentialId!.Value)),
            GitCredentialMutationStatus.Forbidden => Forbidden(result),
            GitCredentialMutationStatus.NotFound => Results.NotFound(),
            _ => Invalid(result),
        };
    }

    private static async Task<IResult> DeleteAsync(
        Guid credentialId,
        [FromForm] DeleteGitCredentialForm form,
        HttpContext httpContext,
        GitCredentialService credentials)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await credentials.DeleteAsync(userId, credentialId, httpContext.RequestAborted);
        return result.Status switch
        {
            GitCredentialMutationStatus.Success => Results.Json(
                new GitCredentialMutationResponse(result.GitCredentialId!.Value)),
            GitCredentialMutationStatus.Forbidden => Forbidden(result),
            _ => Results.NotFound(),
        };
    }

    private static async Task<IResult> FetchMaterialAsync(
        Guid runId,
        HttpContext httpContext,
        GitCredentialService credentials)
    {
        var result = await credentials.FetchMaterialForRunAsync(
            WorkerEndpoints.BearerToken(httpContext),
            runId,
            httpContext.RequestAborted);

        return result.Status switch
        {
            // The body is a secret: explicitly uncacheable, so neither an intermediary nor a browser
            // can keep a copy of it.
            GitCredentialFetchStatus.Success => WithNoStore(httpContext, Results.Json(result.Material)),

            // A bad token is authentication, answered before any run lookup: the 401 reveals only that
            // the credential was not accepted, never whether a run id exists.
            GitCredentialFetchStatus.InvalidToken => Results.Unauthorized(),

            // Deliberately bodyless and identical for every miss (unknown run, foreign tenant, another
            // worker's run, no credential configured): nothing to distinguish, nothing to probe.
            _ => Results.NotFound(),
        };
    }

    private static IResult WithNoStore(HttpContext httpContext, IResult result)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        return result;
    }

    private static IResult Forbidden(GitCredentialMutationResult result) =>
        Results.Text(
            result.Message ?? "You are not allowed to perform this action.",
            "text/plain",
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult Invalid(GitCredentialMutationResult result)
    {
        var error = result.FieldErrors?.FirstOrDefault();
        return Results.Json(
            new GitCredentialErrorResponse(
                error?.Field ?? GitCredentialFields.Value,
                error?.Code ?? GitCredentialErrorCodes.FieldInvalid,
                GitCredentialErrorCodes.MessageFor(error?.Code)),
            statusCode: StatusCodes.Status400BadRequest);
    }

    private static string? FindUserId(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>Create form: name identity, kind, value and the optional HTTPS user name.</summary>
    public sealed class CreateGitCredentialForm
    {
        public string? Name { get; set; }

        public string? Kind { get; set; }

        public string? Value { get; set; }

        public string? UserName { get; set; }

        public GitCredentialInput ToInput() => new(Name, Kind, Value, UserName);
    }

    /// <summary>
    /// Rotate form: a new value and optionally a new kind/user name. There is no <c>Name</c> field - a
    /// rotation cannot rename (that is delete + create).
    /// </summary>
    public sealed class RotateGitCredentialForm
    {
        public string? Kind { get; set; }

        public string? Value { get; set; }

        public string? UserName { get; set; }

        public GitCredentialInput ToInput() => new(Name: null, Kind, Value, UserName);
    }

    /// <summary>Empty on purpose: the parameter makes the delete POST form-bound (antiforgery).</summary>
    public sealed class DeleteGitCredentialForm
    {
    }

    /// <summary>The management answer: the credential's id (and nothing user-supplied echoed back).</summary>
    public sealed record GitCredentialMutationResponse(Guid Id);

    /// <summary>The validation answer: which field, which stable code, and the operator's message.</summary>
    public sealed record GitCredentialErrorResponse(string Field, string Code, string? Message);
}
