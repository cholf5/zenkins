using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Zenkins.Server.Workers;

namespace Zenkins.Server.Git;

/// <summary>
/// The Git credential HTTP surface (card F-013, extended by card D-003): Owner-only management for
/// people, the project settings tab's metadata-only list, and one run-scoped fetch for workers.
/// <para>
/// <b>No value-returning user route.</b> The three mutations accept a value and store ciphertext;
/// nothing on the user side reads one back - not a page, not a JSON endpoint. The single decrypting
/// route is the worker's <c>GET /api/workers/runs/{runId}/git-credential</c>, which is bearer-
/// authenticated, run-scoped and answers only the worker currently assigned to that run. The list
/// route answers name, kind and timestamps - never a value, a ciphertext or a user name.
/// </para>
/// <para>
/// <b>Why the management routes answer JSON instead of a PRG redirect.</b> When F-013 shipped there
/// was no credential page; a redirect to a page that did not exist would have been a lie, so the
/// status codes were made the contract: 200 with the id on success, 403 for a non-Owner, 404 for a
/// missing/foreign id, 400 with a stable code for invalid input. Card D-003's settings tab now posts
/// these same forms with a <c>ProjectId</c> field: when that field parses, the success and
/// validation answers become PRG redirects back to <c>/projects/{id}/settings</c> (below), so the
/// browser flow lands where the form lives. Every other answer - and every answer without the field
/// - is byte-for-byte the F-013 contract, which is what its tests pin. The endpoints are form-bound,
/// so <c>UseAntiforgery</c> still validates every call.
/// </para>
/// <para>
/// <b>The worker route is exempt from cookie auth</b> (like the run endpoints): a bearer token
/// authenticates and selects the tenant. A failure is one bodyless 404 - cross-tenant, foreign-run and
/// unknown-run are indistinguishable, and the server log carries one neutral line for all of them.
/// </para>
/// </summary>
public static class GitCredentialEndpoints
{
    /// <summary>The management group's prefix (the project settings tab targets it).</summary>
    public const string ManagementPath = "/git-credentials";

    /// <summary>
    /// The settings tab's metadata-only list (card D-003): one project's credentials, name/kind/
    /// timestamps, Owner-only. A foreign or unknown project id is the same 404 for every caller.
    /// </summary>
    public const string ProjectListPath = "/projects/{projectId:guid}/git-credentials";

    /// <summary>The settings tab the PRG redirects land on (card D-003).</summary>
    public const string SettingsPath = "/projects/{0}/settings";

    /// <summary>The worker fetch route template.</summary>
    public const string WorkerFetchPath = "/api/workers/runs/{runId:guid}/git-credential";

    /// <summary>Maps the management endpoints, the settings list and the worker fetch.</summary>
    public static IEndpointRouteBuilder MapGitCredentialEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var management = endpoints.MapGroup(ManagementPath).RequireAuthorization();
        management.MapPost("/create", CreateAsync);
        management.MapPost("/{credentialId:guid}/rotate", RotateAsync);
        management.MapPost("/{credentialId:guid}/delete", DeleteAsync);

        // Card D-003's one new surface: what the settings tab renders. Owner-only like the
        // mutations; the service resolves the project through the tenant filter, so a foreign
        // project id is a 404 for everyone.
        endpoints.MapGet(ProjectListPath, ListForProjectAsync).RequireAuthorization();

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

        var result = await credentials.CreateAsync(
            userId,
            form?.ToInput() ?? EmptyInput(),
            httpContext.RequestAborted);
        return result.Status switch
        {
            // The form binds null when the request carries no field the model knows (F-013's own
            // tests post a mutation with only the antiforgery token): the empty input then fails
            // validation, so a token-only POST is the ordinary 400 field error, never a 500.
            GitCredentialMutationStatus.Success => SuccessOutcome(result, form?.ProjectId, NoticeCreated),
            GitCredentialMutationStatus.Forbidden => Forbidden(result),
            GitCredentialMutationStatus.NotFound => Results.NotFound(),
            _ => Invalid(result, form?.ProjectId, form?.Name, form?.Kind),
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
            form?.ToInput() ?? EmptyInput(),
            httpContext.RequestAborted);
        return result.Status switch
        {
            GitCredentialMutationStatus.Success => SuccessOutcome(result, form?.ProjectId, NoticeRotated),
            GitCredentialMutationStatus.Forbidden => Forbidden(result),
            GitCredentialMutationStatus.NotFound => Results.NotFound(),
            _ => Invalid(result, form?.ProjectId, submittedName: null, submittedKind: form?.Kind),
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
            GitCredentialMutationStatus.Success => SuccessOutcome(result, form?.ProjectId, NoticeDeleted),
            GitCredentialMutationStatus.Forbidden => Forbidden(result),
            _ => Results.NotFound(),
        };
    }

    private static async Task<IResult> ListForProjectAsync(
        Guid projectId,
        HttpContext httpContext,
        GitCredentialService credentials)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await credentials.ListForProjectAsync(userId, projectId, httpContext.RequestAborted);
        return result.Status switch
        {
            // Owner-only metadata, but still credential-adjacent: explicitly uncacheable, the same
            // hygiene every credential-bearing response gets.
            GitCredentialMutationStatus.Success => WithNoStore(httpContext, Results.Json(result.Items)),
            GitCredentialMutationStatus.NotFound => Results.NotFound(),
            _ => Results.Text(
                result.Message ?? "You are not allowed to perform this action.",
                "text/plain",
                statusCode: StatusCodes.Status403Forbidden),
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

    // ---------------------------------------------------------------------
    // The settings tab's PRG outcomes (card D-003)
    // ---------------------------------------------------------------------

    /// <summary>The notices the settings tab renders for the three successful mutations.</summary>
    public const string NoticeCreated = "credential-created";
    public const string NoticeRotated = "credential-rotated";
    public const string NoticeDeleted = "credential-deleted";

    /// <summary>
    /// The query keys of the PRG redirect: the field error code and the two non-secret echo fields.
    /// The submitted <em>value</em> never travels in a query (query strings are logged), the same
    /// discipline the secrets forms follow.
    /// </summary>
    public const string ErrorQueryKey = "credentialError";
    public const string NameQueryKey = "credentialName";
    public const string KindQueryKey = "credentialKind";

    /// <summary>
    /// Success as PRG when the form names its project (the settings tab always does), the F-013 JSON
    /// answer otherwise. A forged project id parses only as a Guid, so the redirect target is always
    /// a well-formed settings path - and that page itself is a 404 for a foreign id, so the redirect
    /// cannot leak anything.
    /// </summary>
    private static IResult SuccessOutcome(GitCredentialMutationResult result, string? projectId, string notice) =>
        TryReadProjectId(projectId, out var projectIdentifier)
            ? Results.Redirect($"/projects/{projectIdentifier}/settings?notice={notice}")
            : Results.Json(new GitCredentialMutationResponse(result.GitCredentialId!.Value));

    /// <summary>
    /// Validation failure as PRG with the first stable field code plus the non-secret echo values, or
    /// the F-013 400 JSON without the project field. Only the name and the kind travel in the query;
    /// the value does not.
    /// </summary>
    private static IResult Invalid(
        GitCredentialMutationResult result,
        string? projectId,
        string? submittedName,
        string? submittedKind)
    {
        if (!TryReadProjectId(projectId, out var projectIdentifier))
        {
            var error = result.FieldErrors?.FirstOrDefault();
            return Results.Json(
                new GitCredentialErrorResponse(
                    error?.Field ?? GitCredentialFields.Value,
                    error?.Code ?? GitCredentialErrorCodes.FieldInvalid,
                    GitCredentialErrorCodes.MessageFor(error?.Code)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var query = QueryString.Create(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ErrorQueryKey] = result.FieldErrors?.FirstOrDefault()?.Code ?? GitCredentialErrorCodes.FieldInvalid,
            [NameQueryKey] = submittedName,
            [KindQueryKey] = submittedKind,
        });
        return Results.Redirect($"/projects/{projectIdentifier}/settings{query}");
    }

    /// <summary>Whether the form named its project (the settings tab's hidden field).</summary>
    private static bool TryReadProjectId(string? projectIdentifier, out Guid projectId) =>
        Guid.TryParse(projectIdentifier, out projectId);

    /// <summary>
    /// The stand-in for a form that bound null (a POST carrying no field the model knows): empty
    /// values, so the service's validation - not a null reference - answers the request.
    /// </summary>
    private static GitCredentialInput EmptyInput() => new(Name: null, Kind: null, Value: null, UserName: null);

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

    private static string? FindUserId(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>
    /// Create form: name identity, kind, value and the optional HTTPS user name. <see cref="ProjectId"/>
    /// is the settings tab's return address (card D-003); absent, the F-013 JSON contract applies.
    /// </summary>
    public sealed class CreateGitCredentialForm
    {
        public string? Name { get; set; }

        public string? Kind { get; set; }

        public string? Value { get; set; }

        public string? UserName { get; set; }

        public string? ProjectId { get; set; }

        public GitCredentialInput ToInput() => new(Name, Kind, Value, UserName);
    }

    /// <summary>
    /// Rotate form: a new value and optionally a new kind/user name. There is no <c>Name</c> field - a
    /// rotation cannot rename (that is delete + create). <see cref="ProjectId"/> is the settings tab's
    /// return address.
    /// </summary>
    public sealed class RotateGitCredentialForm
    {
        public string? Kind { get; set; }

        public string? Value { get; set; }

        public string? UserName { get; set; }

        public string? ProjectId { get; set; }

        public GitCredentialInput ToInput() => new(Name: null, Kind, Value, UserName);
    }

    /// <summary>
    /// The delete form carries the settings tab's return address (and nothing else: the parameter
    /// makes the POST form-bound, so antiforgery still validates it).
    /// </summary>
    public sealed class DeleteGitCredentialForm
    {
        public string? ProjectId { get; set; }
    }

    /// <summary>The management answer: the credential's id (and nothing user-supplied echoed back).</summary>
    public sealed record GitCredentialMutationResponse(Guid Id);

    /// <summary>The validation answer: which field, which stable code, and the operator's message.</summary>
    public sealed record GitCredentialErrorResponse(string Field, string Code, string? Message);
}
