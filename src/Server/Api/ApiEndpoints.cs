using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Projects;
using Zenkins.Server.Runs;

namespace Zenkins.Server.Api;

/// <summary>
/// The organization API, version 1 (card F-027): <c>/api/v1/*</c>, bearer-token authenticated, plus the
/// Owner-only token-management forms under <c>/api-tokens/*</c>.
/// <para>
/// <b>One trigger path.</b> The trigger route calls <c>RunService.TriggerForApiAsync</c>, which is the
/// form's own routine with a token's tenant and the <c>Api</c> attribution - the same project lookup, the
/// same F-028/F-029 gate, the same branch/tag/timeout/parameter validation and the same error codes. The
/// API never re-implements a rule, so the two surfaces cannot drift.
/// </para>
/// <para>
/// <b>Refusal shapes.</b> Authentication failures are one bare 401 (no body, no tenant detail); a token
/// missing the route's scope is 403 from the policy; a foreign or unknown project/run id is a bare 404 (no
/// existence oracle); a spent per-token budget is 429 with <c>Retry-After</c>, answered by a filter before
/// any project/run lookup; a malformed request is 400 with the form's own field-error codes; and a trigger
/// the project itself refused (F-028 <c>project-disabled</c>, F-029 <c>queue-full</c>) is 409 with the
/// same code the form and the webhook delivery record.
/// </para>
/// <para>
/// <b>No secrets, no parameter values.</b> The run resource carries status, steps, failure reason, numbers
/// and links - never a secret, never a credential, and deliberately no build-parameter values (F-041's
/// password-typed parameters do not exist yet, so the safe answer is to never echo a parameter value here;
/// the value stays on the run page and the worker's environment). The build <c>number</c> arrived with
/// F-036: it is the project-scoped <c>#N</c>, and <c>id</c> remains the reference every route takes.
/// </para>
/// </summary>
public static class ApiEndpoints
{
    /// <summary>The versioned route group's prefix.</summary>
    public const string BasePath = "/api/v1";

    /// <summary>The Owner-only token-management page (cookie + antiforgery).</summary>
    public const string TokensPagePath = "/api-tokens";

    /// <summary>Upper bound on one page of a list response.</summary>
    public const int MaxPageSize = 100;

    /// <summary>The default page size of a list response.</summary>
    public const int DefaultPageSize = 20;

    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var api = endpoints.MapGroup(BasePath);

        // The budget is spent before any project/run lookup: the filter runs after authentication and
        // authorization (middleware), so a 429 can never become an existence oracle for a resource id.
        api.AddEndpointFilter<ApiRateLimitFilter>();

        api.MapPost("/projects/{projectId:guid}/runs", TriggerRunAsync)
            .RequireAuthorization(ApiServiceCollectionExtensions.PolicyFor(ApiTokenScopes.Trigger));
        api.MapGet("/projects", ListProjectsAsync)
            .RequireAuthorization(ApiServiceCollectionExtensions.PolicyFor(ApiTokenScopes.Read));
        api.MapGet("/projects/{projectId:guid}/runs", ListProjectRunsAsync)
            .RequireAuthorization(ApiServiceCollectionExtensions.PolicyFor(ApiTokenScopes.Read));
        api.MapGet("/runs/{runId:guid}", GetRunAsync)
            .RequireAuthorization(ApiServiceCollectionExtensions.PolicyFor(ApiTokenScopes.Read));
        api.MapGet("/runs/{runId:guid}/log", GetRunLogAsync)
            .RequireAuthorization(ApiServiceCollectionExtensions.PolicyFor(ApiTokenScopes.Read));
        api.MapPost("/runs/{runId:guid}/cancel", CancelRunAsync)
            .RequireAuthorization(ApiServiceCollectionExtensions.PolicyFor(ApiTokenScopes.Cancel));

        // Token management: cookie + antiforgery like every other Owner-only form (F-002/F-004 shape).
        var management = endpoints.MapGroup(TokensPagePath).RequireAuthorization();
        management.MapPost("/create", CreateTokenAsync);
        management.MapPost("/{tokenId:guid}/revoke", RevokeTokenAsync);

        return endpoints;
    }

    // ---------------------------------------------------------------------
    // The API (bearer token, scopes)
    // ---------------------------------------------------------------------

    private static async Task<IResult> TriggerRunAsync(
        Guid projectId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ApiTriggerRequest? request,
        HttpContext httpContext,
        RunService runs,
        ApiTokenService tokens,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            // A body that did not bind is a malformed request, not an empty one: every field is optional,
            // so accepting it would queue a run from a document the server could not read.
            return BadRequest(ApiErrorCodes.RequestInvalid);
        }

        var tokenId = TokenId(httpContext);
        if (tokenId is null)
        {
            return Results.Unauthorized();
        }

        var idempotencyKey = ReadIdempotencyKey(httpContext, out var idempotencyError);
        if (idempotencyError is not null)
        {
            return idempotencyError;
        }

        if (idempotencyKey is not null
            && await tokens.FindIdempotentRunAsync(tokenId.Value, idempotencyKey, cancellationToken) is { } remembered)
        {
            // A retried post (the caller lost the first response): answer the run that already exists.
            return await RunResponseAsync(remembered, StatusCodes.Status200OK, replayed: true, runs, cancellationToken);
        }

        var result = await runs.TriggerForApiAsync(projectId, request.ToInput(), cancellationToken);
        if (result.Status != RunOperationStatus.Success)
        {
            return MapRunRefusal(result);
        }

        var runId = result.RunId!.Value;
        if (idempotencyKey is not null)
        {
            var bound = await tokens.RememberIdempotentRunAsync(tokenId.Value, idempotencyKey, runId, cancellationToken);
            if (bound != runId)
            {
                // Two posts raced with the same key: the database remembered the other one. This run was
                // created before the race was detected - report the remembered run (the key's answer) and
                // say so instead of pretending nothing happened.
                return await RunResponseAsync(bound, StatusCodes.Status200OK, replayed: true, runs, cancellationToken);
            }
        }

        return await RunResponseAsync(runId, StatusCodes.Status201Created, replayed: false, runs, cancellationToken);
    }

    private static async Task<IResult> ListProjectsAsync(
        HttpContext httpContext,
        ProjectService projects,
        CancellationToken cancellationToken)
    {
        if (!TryReadPaging(httpContext, out var page, out var pageSize))
        {
            return BadRequest(ApiErrorCodes.PageInvalid);
        }

        var all = await projects.GetProjectsAsync(cancellationToken);
        return Results.Json(Page(all.Select(ApiProjectResource.From).ToArray(), page, pageSize, all.Count));
    }

    private static async Task<IResult> ListProjectRunsAsync(
        Guid projectId,
        HttpContext httpContext,
        ProjectService projects,
        RunService runs,
        CancellationToken cancellationToken)
    {
        if (!TryReadPaging(httpContext, out var page, out var pageSize))
        {
            return BadRequest(ApiErrorCodes.PageInvalid);
        }

        // The project resolves through the tenant filter: a foreign or unknown id is the same bare 404.
        if (await projects.GetProjectAsync(projectId, cancellationToken) is null)
        {
            return Results.NotFound();
        }

        var all = await runs.GetProjectRunsAsync(projectId, cancellationToken);
        return Results.Json(Page(all.Select(ApiRunResource.From).ToArray(), page, pageSize, all.Count));
    }

    private static async Task<IResult> GetRunAsync(
        Guid runId,
        RunService runs,
        CancellationToken cancellationToken)
    {
        var run = await runs.GetRunAsync(runId, cancellationToken);
        if (run is null)
        {
            // Foreign or unknown id: the same 404, no existence oracle.
            return Results.NotFound();
        }

        return Results.Json(ApiRunResource.From(run));
    }

    /// <summary>
    /// The run's log as <c>text/plain</c>, optionally from an offset. <c>?from=&lt;seq&gt;</c> means "I
    /// already hold every line up to this sequence number", so the response carries only the newer lines
    /// (the same rows, in the same order, with the same already-redacted text the UI's download streams).
    /// <c>X-Zenkins-Last-Seq</c> answers the highest sequence held when the request started, which is the
    /// offset to ask for next.
    /// </summary>
    private static async Task<IResult> GetRunLogAsync(
        Guid runId,
        string? from,
        HttpContext httpContext,
        RunService runs,
        CancellationToken cancellationToken)
    {
        long fromSeq = 0;
        if (!string.IsNullOrWhiteSpace(from)
            && (!long.TryParse(from, NumberStyles.Integer, CultureInfo.InvariantCulture, out fromSeq) || fromSeq < 0))
        {
            return BadRequest(ApiErrorCodes.FromInvalid);
        }

        if (!await runs.RunExistsAsync(runId, cancellationToken))
        {
            return Results.NotFound();
        }

        httpContext.Response.Headers["X-Zenkins-Last-Seq"] =
            (await runs.GetRunLogLastSeqAsync(runId, cancellationToken)).ToString(CultureInfo.InvariantCulture);

        var start = fromSeq;
        return Results.Stream(
            async output =>
            {
                // Line by line from a streaming read (bounded memory regardless of log size); NewLine is
                // pinned so the body is byte-identical to the UI download on every platform.
                await using var writer = new StreamWriter(output, leaveOpen: true) { NewLine = "\n" };
                await foreach (var line in runs.StreamRunLogAsync(runId, start, httpContext.RequestAborted))
                {
                    await writer.WriteLineAsync(line.Message);
                }

                await writer.FlushAsync(httpContext.RequestAborted);
            },
            "text/plain; charset=utf-8");
    }

    private static async Task<IResult> CancelRunAsync(
        Guid runId,
        HttpContext httpContext,
        RunService runs,
        CancellationToken cancellationToken)
    {
        var tokenName = httpContext.User.FindFirstValue(ApiTokenAuthenticationDefaults.TokenNameClaimType) ?? "api";
        var result = await runs.CancelForApiAsync(runId, tokenName, cancellationToken);

        return result.Status switch
        {
            RunOperationStatus.Success => Results.Json(new ApiCancelResource(
                runId,
                OutcomeCode(result.Outcome),
                (await runs.GetRunAsync(runId, cancellationToken))?.Status)),

            // Authorization is the policy's job above; kept explicit so a future change cannot silently
            // turn "not allowed" into "not found".
            RunOperationStatus.Forbidden => Results.Text(
                result.Message ?? "This token is not allowed to cancel runs.",
                "text/plain",
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.NotFound(),
        };
    }

    // ---------------------------------------------------------------------
    // Token management (cookie + antiforgery, Owner-only in the service)
    // ---------------------------------------------------------------------

    private static async Task<IResult> CreateTokenAsync(
        [FromForm] CreateApiTokenForm form,
        HttpContext httpContext,
        ApiTokenService tokens,
        CancellationToken cancellationToken)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // A body whose fields all fail to bind arrives as null, not as an empty form (the trigger
        // form's F-019 discovery). Every field here is optional except the name, so the answer is the
        // same refusal a blank name gets - never a server fault, and no token is minted.
        if (form is null)
        {
            return Results.Redirect($"{TokensPagePath}?error={ApiErrorCodes.NameRequired}");
        }

        var result = await tokens.CreateAsync(
            userId,
            form.Name,
            form.RequestedScopes(),
            cancellationToken);

        if (result.Status != ApiTokenOperationStatus.Success)
        {
            return result.Status switch
            {
                // Authorization failures are a hard rejection, never a friendly redirect (F-003 rule).
                ApiTokenOperationStatus.Forbidden => Results.Text(
                    result.Message ?? "Only an organization Owner can create API tokens.",
                    "text/plain",
                    statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Redirect($"{TokensPagePath}?error={result.ErrorCode}"),
            };
        }

        // Shown exactly once, never stored: this document is the only copy (the worker registration
        // token's shape, for the same reason - a redirect would put the secret into the URL).
        return Results.Content(
            $"""
             <!DOCTYPE html>
             <html lang="en">
             <head><meta charset="utf-8"><title>API token created · Zenkins</title></head>
             <body>
               <h1>API token created</h1>
               <p>Copy this token now - it is shown only once and cannot be retrieved again.</p>
               <p>Label: {System.Net.WebUtility.HtmlEncode(result.Name)}</p>
               <p><code id="api-token-plaintext">{System.Net.WebUtility.HtmlEncode(result.Plaintext)}</code></p>
               <p>Send it as <code>Authorization: Bearer &lt;token&gt;</code> to <code>{BasePath}</code>.</p>
               <p><a href="{TokensPagePath}">Back to API tokens</a></p>
             </body>
             </html>
             """,
            "text/html",
            statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> RevokeTokenAsync(
        Guid tokenId,
        [FromForm] RevokeApiTokenForm form,
        HttpContext httpContext,
        ApiTokenService tokens,
        CancellationToken cancellationToken)
    {
        // The empty form parameter is what makes this POST form-bound and antiforgery-validated.
        _ = form;
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await tokens.RevokeAsync(userId, tokenId, cancellationToken);
        return result.Status switch
        {
            ApiTokenOperationStatus.Success => Results.Redirect($"{TokensPagePath}?notice=token-revoked"),
            ApiTokenOperationStatus.NotFound => Results.NotFound(),
            ApiTokenOperationStatus.Forbidden => Results.Text(
                result.Message ?? "Only an organization Owner can revoke API tokens.",
                "text/plain",
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Redirect($"{TokensPagePath}?error={result.ErrorCode}"),
        };
    }

    // ---------------------------------------------------------------------
    // Shared plumbing
    // ---------------------------------------------------------------------

    private static Guid? TokenId(HttpContext httpContext) =>
        Guid.TryParse(
            httpContext.User.FindFirstValue(ApiTokenAuthenticationDefaults.TokenIdClaimType),
            out var tokenId)
            ? tokenId
            : null;

    /// <summary>
    /// The optional <c>Idempotency-Key</c> header: absent is fine, present must be non-blank, bounded and
    /// free of control characters (it is the caller's own label, stored verbatim).
    /// </summary>
    private static string? ReadIdempotencyKey(HttpContext httpContext, out IResult? error)
    {
        error = null;
        if (!httpContext.Request.Headers.TryGetValue("Idempotency-Key", out var values))
        {
            return null;
        }

        var key = values.ToString().Trim();
        if (key.Length == 0
            || key.Length > ApiTokenLimits.MaxIdempotencyKeyLength
            || key.Any(char.IsControl))
        {
            error = BadRequest(ApiErrorCodes.IdempotencyKeyInvalid);
            return null;
        }

        return key;
    }

    /// <summary>Reads <c>?page=</c> and <c>?pageSize=</c> (defaults 1 and 20, pageSize capped at 100).</summary>
    private static bool TryReadPaging(HttpContext httpContext, out int page, out int pageSize)
    {
        page = 1;
        pageSize = DefaultPageSize;

        if (TryReadInt(httpContext, "page", out var requestedPage))
        {
            if (requestedPage < 1)
            {
                return false;
            }

            page = requestedPage;
        }

        if (TryReadInt(httpContext, "pageSize", out var requestedPageSize))
        {
            if (requestedPageSize < 1 || requestedPageSize > MaxPageSize)
            {
                return false;
            }

            pageSize = requestedPageSize;
        }

        return true;
    }

    private static bool TryReadInt(HttpContext httpContext, string name, out int value)
    {
        value = 0;
        var raw = httpContext.Request.Query[name].ToString();
        return !string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static ApiPageResource<T> Page<T>(IReadOnlyList<T> items, int page, int pageSize, int total) =>
        new(items.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), page, pageSize, total);

    /// <summary>
    /// Answers a run resource with the status the route promised. The <c>Location</c> header points at the
    /// run's own API resource, and a replay says so (<c>Idempotency-Replayed: true</c>) rather than
    /// pretending a fresh run was queued.
    /// </summary>
    private static async Task<IResult> RunResponseAsync(
        Guid runId,
        int statusCode,
        bool replayed,
        RunService runs,
        CancellationToken cancellationToken)
    {
        var run = await runs.GetRunAsync(runId, cancellationToken);
        return new RunResponse(
            runId,
            run is null ? null : ApiRunResource.From(run),
            statusCode,
            replayed);
    }

    private static IResult BadRequest(string code) =>
        Results.Json(new ApiErrorBody(code, Message: null, FieldErrors: null),
            statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// Maps a non-success trigger result to the API's refusal shape. The codes are the form's own: the
    /// field-error code for a 400, and the project's refusal code (<c>project-disabled</c>,
    /// <c>queue-full</c>) for a 409 - <see cref="RunOperationStatus.Refused"/> is exactly the "the request
    /// is fine, the project's state says no" case the F-028/F-029 cards left for this endpoint.
    /// </summary>
    private static IResult MapRunRefusal(RunTriggerResult result)
    {
        var fieldErrors = result.FieldErrors?
            .Select(fieldError => new ApiFieldError(fieldError.Field, fieldError.Code))
            .ToArray();
        var code = result.FieldErrors?.FirstOrDefault()?.Code ?? RunErrorCodes.FieldInvalid;

        return result.Status switch
        {
            RunOperationStatus.InvalidInput => Results.Json(
                new ApiErrorBody(code, result.Message, fieldErrors),
                statusCode: StatusCodes.Status400BadRequest),
            RunOperationStatus.Refused => Results.Json(
                new ApiErrorBody(code, result.Message, fieldErrors),
                statusCode: StatusCodes.Status409Conflict),
            RunOperationStatus.NotFound => Results.NotFound(),
            RunOperationStatus.Forbidden => Results.Text(
                result.Message ?? "This token is not allowed to trigger runs.",
                "text/plain",
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Json(
                new ApiErrorBody(code, result.Message, fieldErrors),
                statusCode: StatusCodes.Status409Conflict),
        };
    }

    /// <summary>The wire spelling of a cancel outcome (the page's <c>?notice=</c> vocabulary).</summary>
    private static string OutcomeCode(RunCancelOutcome outcome) => outcome switch
    {
        RunCancelOutcome.Canceled => "canceled",
        RunCancelOutcome.CancelRequested => "cancel-requested",
        _ => "already-finished",
    };

    // ---------------------------------------------------------------------
    // Contracts
    // ---------------------------------------------------------------------

    /// <summary>
    /// The trigger body. Every field is optional and keeps the form's type: the timeout is the same
    /// <b>string</b> the form posts, so a value the form refuses (<c>timeout-invalid</c>) is refused here
    /// with the identical code instead of a JSON binder's 400.
    /// </summary>
    /// <param name="Branch">The branch to build; blank/absent means the project's default branch.</param>
    /// <param name="RequiredTags">Comma-separated tags a claimant worker must cover.</param>
    /// <param name="TimeoutMinutes">The per-run timeout override in minutes, as text (blank = server default).</param>
    /// <param name="Parameters">The build-parameter values, keyed by the definition's name.</param>
    public sealed record ApiTriggerRequest(
        string? Branch = null,
        string? RequiredTags = null,
        string? TimeoutMinutes = null,
        Dictionary<string, string>? Parameters = null)
    {
        public RunTriggerInput ToInput() => new(Branch, RequiredTags, TimeoutMinutes, Parameters);
    }

    /// <summary>One field-level refusal, in the form's own vocabulary.</summary>
    public sealed record ApiFieldError(string Field, string Code);

    /// <summary>The error body of every JSON refusal: a stable <c>error</c> code, the operator's sentence
    /// when there is one, and the field errors a 400 carries.</summary>
    public sealed record ApiErrorBody(
        string Error,
        string? Message = null,
        IReadOnlyList<ApiFieldError>? FieldErrors = null);

    /// <summary>One page of a list response.</summary>
    public sealed record ApiPageResource<T>(
        IReadOnlyList<T> Items,
        int Page,
        int PageSize,
        int Total);

    /// <summary>
    /// One project as the API lists it. The repository URL is deliberately absent: it is configuration,
    /// not status, and a URL is the one project field that could carry an embedded credential.
    /// </summary>
    public sealed record ApiProjectResource(
        Guid Id,
        string Name,
        string Kind,
        string? DefaultBranch,
        string? PipelinePath,
        bool Disabled,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt)
    {
        public static ApiProjectResource From(ProjectView project) => new(
            project.Id,
            project.Name,
            project.Kind,
            project.DefaultBranch,
            project.PipelinePath,
            project.Disabled,
            project.CreatedAt,
            project.UpdatedAt);
    }

    /// <summary>
    /// One run as the API reports it: status, steps, failure reason, the numbers the server owns, and the
    /// resource link. No parameter values (F-041 does not exist yet, so the API never echoes one), no
    /// secret, no credential.
    /// </summary>
    public sealed record ApiRunResource(
        Guid Id,

        /// <summary>
        /// Card F-036: the run's per-project build number - the <c>#N</c> the UI prints and the value the
        /// permalinks carry. Additive: <see cref="Id"/> stays the reference (a link, an idempotency record,
        /// a log request), so a client that never reads the number keeps working. Nullable on purpose: a row
        /// the product did not number (impossible once the migrations have run) answers <c>null</c>, never a
        /// fabricated <c>0</c>.
        /// </summary>
        int? Number,
        Guid ProjectId,
        string Status,
        string TriggerType,
        string Branch,
        string? Commit,
        string PipelinePath,
        string? PipelineName,
        string? WorkerName,
        DateTimeOffset CreatedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? EndedAt,
        int? ExitCode,
        string? FailureReason,
        int? TimeoutSeconds,
        DateTimeOffset? DeadlineAt,
        bool? ProjectDisabled,
        IReadOnlyList<ApiRunStepResource> Steps,
        string Url)
    {
        public static ApiRunResource From(RunView run) => new(
            run.Id,
            run.Number,
            run.ProjectId,
            run.Status,
            run.TriggerType,
            run.GitBranch,
            run.GitCommit,
            run.PipelinePath,
            run.PipelineName,
            run.WorkerName,
            run.CreatedAt,
            run.StartedAt,
            run.EndedAt,
            run.ExitCode,
            run.FailureReason,
            run.TimeoutSeconds,
            run.DeadlineAt,
            run.ProjectDisabled,
            [.. run.StepList.Select(ApiRunStepResource.From)],
            $"{BasePath}/runs/{run.Id}");

    }

    /// <summary>
    /// The reference-only body a trigger answer falls back to when the run row is gone by the time the
    /// resource is rendered (deleted between the insert and the read): just the id and its URL, so a
    /// client never reads an empty-but-well-shaped resource with <c>status: ""</c> and a
    /// <c>Guid.Empty</c> project.
    /// </summary>
    public sealed record ApiRunRefResource(Guid Id, string Url);

    /// <summary>One step of a run: its position, the state it reached and its own numbers.</summary>
    public sealed record ApiRunStepResource(
        Guid? Id,
        int Index,
        string Name,
        string Status,
        DateTimeOffset? StartedAt,
        DateTimeOffset? EndedAt,
        int? ExitCode,
        string? FailureReason)
    {
        public static ApiRunStepResource From(RunStepView step) => new(
            step.Id,
            step.Index,
            step.Name,
            step.Status,
            step.StartedAt,
            step.EndedAt,
            step.ExitCode,
            step.FailureReason);
    }

    /// <summary>The cancel answer: the run, what the cancel did and the state it left behind.</summary>
    public sealed record ApiCancelResource(Guid RunId, string Outcome, string? Status);

    /// <summary>Empty form binding: the parameter makes the mint POST form-bound (antiforgery).</summary>
    public sealed class CreateApiTokenForm
    {
        /// <summary>The Owner's label for the token (required, unique per organization).</summary>
        public string? Name { get; set; }

        public bool Trigger { get; set; }

        public bool Read { get; set; }

        public bool Cancel { get; set; }

        /// <summary>The checked scopes, in the canonical order.</summary>
        public string[] RequestedScopes()
        {
            var scopes = new List<string>();
            if (Trigger)
            {
                scopes.Add(ApiTokenScopes.Trigger);
            }

            if (Read)
            {
                scopes.Add(ApiTokenScopes.Read);
            }

            if (Cancel)
            {
                scopes.Add(ApiTokenScopes.Cancel);
            }

            return [.. scopes];
        }
    }

    /// <summary>Empty form binding: the parameter makes the revoke POST form-bound (antiforgery).</summary>
    public sealed class RevokeApiTokenForm
    {
    }

    /// <summary>
    /// A handler's answer that needs headers (the 201's <c>Location</c>, a replay's marker). A tiny
    /// <see cref="IResult"/> instead of three header writes so the mapping stays in one place.
    /// </summary>
    private sealed class RunResponse(Guid runId, ApiRunResource? run, int statusCode, bool replayed) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.Headers.Location = $"{BasePath}/runs/{runId}";
            if (replayed)
            {
                httpContext.Response.Headers["Idempotency-Replayed"] = "true";
            }

            // A run deleted between the trigger and this render answers honestly: the reference only -
            // never a well-shaped resource fabricated from defaults (see ApiRunRefResource).
            return run is null
                ? httpContext.Response.WriteAsJsonAsync(
                    new ApiRunRefResource(runId, $"{BasePath}/runs/{runId}"))
                : httpContext.Response.WriteAsJsonAsync(run);
        }
    }
}

/// <summary>
/// The per-token budget filter (card F-027): one slot per request, 429 with <c>Retry-After</c> when the
/// token's minute is spent. It runs after authentication and authorization and before the handler, so a
/// rate-limited call never reaches a project or run lookup.
/// </summary>
internal sealed class ApiRateLimitFilter(ApiRateLimiter limiter) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var tokenIdText = httpContext.User.FindFirstValue(ApiTokenAuthenticationDefaults.TokenIdClaimType);
        if (!Guid.TryParse(tokenIdText, out var tokenId))
        {
            // Defensive: the policies on every /api/v1 route authenticate with the API scheme first.
            return Results.Unauthorized();
        }

        if (!limiter.TryAcquire(tokenId, out var retryAfter))
        {
            httpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Results.Json(
                new ApiEndpoints.ApiErrorBody(ApiErrorCodes.RateLimited),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        return await next(context);
    }
}
