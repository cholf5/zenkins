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
/// <para>
/// <b>Card F-018 adds the assignment routes</b> (<c>/projects/{id}/git-credential/attach</c> and
/// <c>.../detach</c>): Owner-only, form-bound (so <c>UseAntiforgery</c> validates them), and answered with
/// PRG back to the settings tab. A foreign or unknown project id or credential id is a <b>404</b>, the same
/// answer the settings list gives - never a 403 that would confirm an id exists. The read that accompanies
/// them (the list) also carries the project's current assignment and, per credential, the projects that
/// clone with it.
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

    /// <summary>
    /// The two assignment routes card F-018 adds: attach one of the organization's credentials to the
    /// project, and detach it. Both are Owner-only, both resolve the project through the tenant filter,
    /// and both answer PRG back to the settings tab that hosts their forms.
    /// </summary>
    public const string AttachPath = "/projects/{projectId:guid}/git-credential/attach";
    public const string DetachPath = "/projects/{projectId:guid}/git-credential/detach";

    /// <summary>The worker fetch route template.</summary>
    public const string WorkerFetchPath = "/api/workers/runs/{runId:guid}/git-credential";

    /// <summary>
    /// Card I-005's probe-scoped fetch: the same material, addressed by a probe id instead of a run id.
    /// Two routes, two leases - a probe id cannot fetch a run's credential and a run id cannot reach
    /// this one, which is the card's "the two scopes never replay each other". Same <c>no-store</c>
    /// answer, same bodyless 404 for every miss.
    /// </summary>
    public const string ProbeFetchPath = "/api/workers/probes/{probeId:guid}/git-credential";

    /// <summary>
    /// Card F-023's definition-task fetch: the same material, addressed by the observation's id. A third
    /// route behind a third lease, so no id can be replayed on another route.
    /// </summary>
    public const string DefinitionFetchPath = "/api/workers/definitions/{validationId:guid}/git-credential";

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

        // Card F-018: the assignment itself. Form-bound for the same reason every other mutation is -
        // UseAntiforgery validates the token on a form post - and the route (never a form field) is the
        // authoritative project.
        endpoints.MapPost(AttachPath, AttachAsync).RequireAuthorization();
        endpoints.MapPost(DetachPath, DetachAsync).RequireAuthorization();

        endpoints.MapGet(WorkerFetchPath, FetchMaterialAsync);

        // I-005: the probe-scoped fetch. It lives here, beside the run-scoped one, because this is the
        // credential surface - and because the two answer shapes must stay identical by being written
        // once. The authorization behind it is the probe lease (GitProbeService), never the run's.
        endpoints.MapGet(ProbeFetchPath, FetchProbeMaterialAsync);

        // F-023: the definition task's fetch. It lives here for the same reason the probe's does: this
        // is the credential surface, and all three answer with the same shape written once.
        endpoints.MapGet(DefinitionFetchPath, FetchDefinitionMaterialAsync);
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

        var result = await credentials.DeleteAsync(
            userId,
            credentialId,
            ConfirmRequested(form?.ConfirmReferenced)
                && ConfirmsCredential(form?.ConfirmCredentialId, credentialId),
            httpContext.RequestAborted);
        return result.Status switch
        {
            GitCredentialMutationStatus.Success => SuccessOutcome(result, form?.ProjectId, NoticeDeleted),
            GitCredentialMutationStatus.Forbidden => Forbidden(result),

            // Card F-018: a delete that would strand the projects cloning with this credential is
            // refused, and the refusal names them - on the settings tab when the form came from there,
            // as the D-006-shaped answer (409 + the names) otherwise.
            GitCredentialMutationStatus.Referenced =>
                ReferencedOutcome(result, form?.ProjectId, form?.Name),
            _ => Results.NotFound(),
        };
    }

    /// <summary>
    /// Card F-018: attach a credential to the project. The credential id is the only input; the
    /// service resolves both it and the project through the tenant filter, so a foreign or missing id
    /// is one refusal that confirms nothing.
    /// </summary>
    private static async Task<IResult> AttachAsync(
        Guid projectId,
        [FromForm] AttachGitCredentialForm form,
        HttpContext httpContext,
        GitCredentialService credentials)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        if (!Guid.TryParse(form?.GitCredentialId, out var credentialId))
        {
            // A missing/malformed id never becomes a 500, never reaches the database, and is the same
            // 404 a missing credential gets (F-013's contract for this surface).
            return Results.NotFound();
        }

        var result = await credentials.AssignToProjectAsync(
            userId,
            projectId,
            credentialId,
            httpContext.RequestAborted);
        return result.Status switch
        {
            GitCredentialMutationStatus.Success => ChangesOutcome(projectId, NoticeAttached),
            GitCredentialMutationStatus.Forbidden => Forbidden(result),

            // Missing credential, foreign credential, missing project, foreign project: one 404, exactly
            // like the list route above. A redirect would have suggested the id was meaningful.
            _ => Results.NotFound(),
        };
    }

    /// <summary>Card F-018: remove the project's assignment. Detaching nothing is a success.</summary>
    private static async Task<IResult> DetachAsync(
        Guid projectId,
        [FromForm] DetachGitCredentialForm form,
        HttpContext httpContext,
        GitCredentialService credentials)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await credentials.DetachFromProjectAsync(
            userId,
            projectId,
            httpContext.RequestAborted);
        return result.Status switch
        {
            GitCredentialMutationStatus.Success => ChangesOutcome(projectId, NoticeDetached),
            GitCredentialMutationStatus.Forbidden => Forbidden(result),

            // Only a missing or foreign project can answer NotFound here; there is nothing to say about
            // it that would not confirm whether the id exists.
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

    /// <summary>
    /// Card I-005: the probe-scoped material fetch. Exactly the run-scoped answers - a 401 for a dead
    /// token, one bodyless 404 for every other miss, material with <c>no-store</c> on success - because
    /// the two paths deliver the same secret through the same shape; only the lease that authorizes them
    /// differs.
    /// </summary>
    private static async Task<IResult> FetchProbeMaterialAsync(
        Guid probeId,
        HttpContext httpContext,
        Zenkins.Server.Probes.GitProbeService probes)
    {
        var result = await probes.FetchMaterialForProbeAsync(
            WorkerEndpoints.BearerToken(httpContext),
            probeId,
            httpContext.RequestAborted);

        return result.Status switch
        {
            GitCredentialFetchStatus.Success => WithNoStore(httpContext, Results.Json(result.Material)),
            GitCredentialFetchStatus.InvalidToken => Results.Unauthorized(),
            _ => Results.NotFound(),
        };
    }

    /// <summary>
    /// Card F-023: the definition-task material fetch. Exactly the probe path's answers (401, one
    /// bodyless 404, material with <c>no-store</c>); only the lease that authorizes it differs.
    /// </summary>
    private static async Task<IResult> FetchDefinitionMaterialAsync(
        Guid validationId,
        HttpContext httpContext,
        Zenkins.Server.Probes.DefinitionValidationService validations)
    {
        var result = await validations.FetchMaterialForValidationAsync(
            WorkerEndpoints.BearerToken(httpContext),
            validationId,
            httpContext.RequestAborted);

        return result.Status switch
        {
            GitCredentialFetchStatus.Success => WithNoStore(httpContext, Results.Json(result.Material)),
            GitCredentialFetchStatus.InvalidToken => Results.Unauthorized(),
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

    /// <summary>Card F-018's two assignment notices (attach and detach).</summary>
    public const string NoticeAttached = "credential-attached";
    public const string NoticeDetached = "credential-detached";

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
    /// A successful attach/detach redirects back to the settings tab it came from (the form always names
    /// its project, because the route it posted to names it too).
    /// </summary>
    private static IResult ChangesOutcome(Guid projectId, string notice) =>
        Results.Redirect($"/projects/{projectId}/settings?notice={notice}");

    /// <summary>
    /// A delete refused because the credential is still assigned (card F-018): PRG back to the settings
    /// tab with the D-006 code when the form came from there, a 409 naming the projects otherwise.
    /// </summary>
    private static IResult ReferencedOutcome(
        GitCredentialMutationResult result,
        string? projectId,
        string? submittedName)
    {
        if (TryReadProjectId(projectId, out var projectIdentifier))
        {
            var query = QueryString.Create(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [ErrorQueryKey] = GitCredentialErrorCodes.CredentialInUse,
                [NameQueryKey] = submittedName,
            });
            return Results.Redirect($"/projects/{projectIdentifier}/settings{query}");
        }

        return Results.Json(
            new GitCredentialReferencedResponse(
                GitCredentialErrorCodes.CredentialInUse,
                GitCredentialErrorCodes.MessageFor(GitCredentialErrorCodes.CredentialInUse),
                result.ReferencingProjects ?? []),
            statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Whether the delete carries the confirmation a referenced credential needs (card F-018): the
    /// operator ticked the page's visible box <b>and</b> the form named the credential it is deleting.
    /// Both are required, so a stray or replayed post cannot delete a credential by accident: the id must
    /// match the route, which only the rendered page echoes.
    /// </summary>
    private static bool ConfirmRequested(string? value) =>
        value is not null
        && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase)
            || value == "1");

    /// <summary>Whether the confirmation names exactly the credential the route addresses.</summary>
    private static bool ConfirmsCredential(string? submitted, Guid credentialId) =>
        Guid.TryParse(submitted, out var parsed) && parsed == credentialId;

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
    /// makes the POST form-bound, so antiforgery still validates it). Card F-018 adds the confirmation
    /// a referenced credential's delete requires.
    /// </summary>
    public sealed class DeleteGitCredentialForm
    {
        public string? ProjectId { get; set; }

        /// <summary>Set by the page's "delete anyway" checkbox; absent means the plain request.</summary>
        public string? ConfirmReferenced { get; set; }

        /// <summary>
        /// The credential id the page echoes into the confirmation. Required together with
        /// <see cref="ConfirmReferenced"/>, so a confirmation always names what it deletes.
        /// </summary>
        public string? ConfirmCredentialId { get; set; }

        /// <summary>The non-secret echo of the credential's name (the PRG error names it).</summary>
        public string? Name { get; set; }
    }

    /// <summary>Card F-018's attach form: which of the organization's credentials to assign.</summary>
    public sealed class AttachGitCredentialForm
    {
        public string? GitCredentialId { get; set; }

        /// <summary>The settings tab's return address; the route names the project authoritatively.</summary>
        public string? ProjectId { get; set; }
    }

    /// <summary>
    /// Card F-018's detach form. Empty on purpose: it exists so the POST is form-bound (and therefore
    /// covered by <c>UseAntiforgery</c>), and the route names the project.
    /// </summary>
    public sealed class DetachGitCredentialForm
    {
        public string? ProjectId { get; set; }
    }

    /// <summary>The management answer: the credential's id (and nothing user-supplied echoed back).</summary>
    public sealed record GitCredentialMutationResponse(Guid Id);

    /// <summary>The validation answer: which field, which stable code, and the operator's message.</summary>
    public sealed record GitCredentialErrorResponse(string Field, string Code, string? Message);

    /// <summary>
    /// The 409 answer of a delete refused because the credential is still assigned (card F-018): the
    /// stable code, the operator's message and the projects that would lose their credential. Names
    /// only - a project name is not material.
    /// </summary>
    public sealed record GitCredentialReferencedResponse(
        string Code,
        string? Message,
        IReadOnlyList<string> Projects);
}
