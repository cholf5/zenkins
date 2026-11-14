using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Zenkins.Server.Workers;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.RunCancellation;

namespace Zenkins.Server.Runs;

/// <summary>
/// HTTP endpoints for cards F-006 and F-014: the trigger form on the project page (F-003's form shape)
/// and the token-authenticated worker run API under <c>/api/workers/*</c>.
/// <para>
/// <b>Trigger form.</b> Form-bound (so <c>UseAntiforgery</c> validates the token), authenticated
/// (anonymous POSTs are redirected to sign-in), Owner <em>or</em> Member in the service. F-014 replaced
/// the argv textarea with a branch: a run executes the project's pipeline. Success is a PRG redirect to
/// the new run's detail page; validation failures redirect back to the project page with
/// <c>?runError=&lt;code&gt;</c>.
/// </para>
/// <para>
/// <b>Worker run API.</b> Deliberately NOT <c>RequireAuthorization</c> (same as register and
/// heartbeat): the bearer token authenticates and selects the tenant; a cookie never authorizes a
/// worker action. Authentication happens before payload validation, so a tokenless caller cannot
/// probe the contract through the 400-vs-401 difference. Conflicts (illegal transitions, foreign
/// leases, expired leases) are 409 with a stable error code; foreign run ids are 404.
/// </para>
/// </summary>
public static class RunEndpoints
{
    public static IEndpointRouteBuilder MapRunEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Trigger form (F-003 shape): cookie + antiforgery, PRG outcomes.
        var projects = endpoints.MapGroup("/projects").RequireAuthorization();
        projects.MapPost("/{projectId:guid}/runs/start", StartRunAsync);

        // User cancel (F-009): the run page's cancel button. Cookie + antiforgery like the other
        // form endpoints; the service enforces membership, the tenant filter makes a foreign run a
        // 404 (no existence oracle).
        var runForms = endpoints.MapGroup("/runs").RequireAuthorization();
        runForms.MapPost("/{runId:guid}/cancel", CancelRunAsync);

        // Worker run API: token-authenticated, exempt from cookie auth by design.
        var api = endpoints.MapGroup("/api/workers");
        api.MapPost("/claim", ClaimAsync);
        api.MapPost("/runs/{runId:guid}/accepted", AcceptAsync);
        api.MapPost("/runs/{runId:guid}/definition", ReportDefinitionAsync);
        api.MapPost("/runs/{runId:guid}/steps", ReportStepAsync);
        api.MapPost("/runs/{runId:guid}/completed", CompleteAsync);
        api.MapPost("/runs/{runId:guid}/canceled", ReportCanceledAsync);

        // Full-log download (F-008): cookie-authenticated like the pages; the tenant comes from the
        // session (never the URL), so a foreign run id is a plain 404 that leaks no bytes.
        endpoints.MapGet("/runs/{runId:guid}/log/download", DownloadLogAsync).RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> DownloadLogAsync(
        Guid runId,
        RunService runs,
        HttpContext httpContext)
    {
        // Foreign or unknown id: the same 404, no existence oracle, no bytes.
        if (!await runs.RunExistsAsync(runId, httpContext.RequestAborted))
        {
            return Results.NotFound();
        }

        var fileName = $"zenkins-run-{runId.ToString("D")}-log.txt";
        return Results.Stream(
            async output =>
            {
                // The stream is written line by line from a streaming database read (bounded
                // memory regardless of log size); NewLine is pinned so the file is identical
                // on every platform.
                await using var writer = new StreamWriter(output, leaveOpen: true) { NewLine = "\n" };
                await foreach (var line in runs.StreamRunLogAsync(runId, httpContext.RequestAborted))
                {
                    await writer.WriteLineAsync(line.Message);
                }

                await writer.FlushAsync(httpContext.RequestAborted);
            },
            "text/plain; charset=utf-8",
            fileName);
    }

    private static async Task<IResult> StartRunAsync(
        Guid projectId,
        [FromForm] StartRunForm form,
        HttpContext httpContext,
        RunService runs)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // F-019: a body whose fields all fail to bind arrives as null, not as an empty form. Every
        // field of this form is optional, so an empty input would pass validation and really start a
        // run from a body the server could not read. A lost body is refused instead - the same PRG
        // error a malformed field gets - and no run is created.
        //
        // Reading the body happens before the target is resolved, so this answer also reaches a caller
        // who is not allowed to run the project: a non-member with a lost body sees this redirect
        // rather than the 403/404 TriggerAsync would give. That is not an authorization bypass - the
        // Location only echoes the id the caller already supplied, no project row is read, nothing is
        // triggered, and a following GET of that page is still the usual 403/404.
        if (form is null)
        {
            return Results.Redirect($"/projects/{projectId}?runError={RunErrorCodes.FieldInvalid}");
        }

        var result = await runs.TriggerAsync(userId, projectId, form.ToInput(), httpContext.RequestAborted);
        return result.Status switch
        {
            RunOperationStatus.Success => Results.Redirect($"/runs/{result.RunId}"),

            // Authorization failures are a hard rejection, never a friendly redirect (F-003 rule).
            RunOperationStatus.Forbidden => Results.Text(
                result.Message ?? "You are not allowed to start runs.",
                "text/plain",
                statusCode: StatusCodes.Status403Forbidden),
            RunOperationStatus.NotFound => Results.NotFound(),
            _ => Results.Redirect(
                $"/projects/{projectId}?runError={result.FieldErrors?[0].Code ?? RunErrorCodes.FieldInvalid}"),
        };
    }

    private static async Task<IResult> ClaimAsync(
        HttpContext httpContext,
        RunService runs)
    {
        var result = await runs.ClaimAsync(
            WorkerEndpoints.BearerToken(httpContext),
            httpContext.RequestAborted);

        return result.Status switch
        {
            RunOperationStatus.InvalidToken => Results.Unauthorized(),
            RunOperationStatus.Success when result.RunId is null => Results.NoContent(),
            _ => Results.Json(new RunClaimResponse(
                result.RunId!.Value,
                result.ProjectId!.Value,
                result.RepositoryUrl!,
                result.Branch!,
                result.Commit,
                result.PipelinePath!,
                result.LeaseExpiresAt!.Value)),
        };
    }

    private static async Task<IResult> AcceptAsync(
        Guid runId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] AcceptRunRequest? request,
        HttpContext httpContext,
        RunService runs) =>
        MapReport(await runs.AcceptAsync(
            WorkerEndpoints.BearerToken(httpContext),
            runId,

            // F-013: the exact commit the run cloned, when it cloned one. Optional so an older worker
            // (and every F-006/F-007 test) keeps posting an empty body.
            request?.GitCommit,
            httpContext.RequestAborted));

    private static async Task<IResult> CompleteAsync(
        Guid runId,
        [FromBody] CompleteRunRequest request,
        HttpContext httpContext,
        RunService runs) =>
        MapReport(await runs.CompleteAsync(
            WorkerEndpoints.BearerToken(httpContext),
            runId,
            request.ExitCode,
            request.FailureReason,
            httpContext.RequestAborted));

    private static async Task<IResult> ReportDefinitionAsync(
        Guid runId,
        [FromBody] RunDefinitionRequest request,
        HttpContext httpContext,
        RunService runs) =>
        MapReport(await runs.ReportDefinitionAsync(
            WorkerEndpoints.BearerToken(httpContext),
            runId,
            request.GitCommit,
            request.PipelineSpecJson,
            httpContext.RequestAborted));

    /// <summary>
    /// F-015: one step transition (before it starts and after it ends). The answer carries the recorded
    /// step's id, which the worker stamps on the step's log lines. 400 is a payload/position mismatch,
    /// 404 an unknown or foreign run, and 409 a run that is not this worker's, has left Running, or a
    /// step whose report arrived out of order.
    /// </summary>
    private static async Task<IResult> ReportStepAsync(
        Guid runId,
        [FromBody] RunStepRequest request,
        HttpContext httpContext,
        RunService runs) =>
        MapStepReport(await runs.ReportStepAsync(
            WorkerEndpoints.BearerToken(httpContext),
            runId,
            new RunStepReport(
                request.Index,
                request.Name,
                request.Status,
                request.ExitCode,
                request.FailureReason),
            httpContext.RequestAborted));

    private static IResult MapStepReport(RunStepResult result) => result.Status switch
    {
        RunOperationStatus.Success => Results.Json(
            new RunStepResponse(result.StepId!.Value, result.StepStatus!, result.RunStatus!)),
        RunOperationStatus.InvalidToken => Results.Unauthorized(),
        RunOperationStatus.NotFound => Results.NotFound(),
        RunOperationStatus.InvalidInput =>
            Results.Json(new WorkerEndpoints.WorkerApiError(result.ErrorCode ?? RunErrorCodes.FieldInvalid),
                statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Json(new WorkerEndpoints.WorkerApiError(result.ErrorCode ?? RunErrorCodes.InvalidTransition),
            statusCode: StatusCodes.Status409Conflict),
    };

    private static async Task<IResult> ReportCanceledAsync(
        Guid runId,
        [FromBody] RunCanceledRequest request,
        HttpContext httpContext,
        RunService runs) =>
        MapReport(await runs.ReportCanceledAsync(
            WorkerEndpoints.BearerToken(httpContext),
            runId,
            request.Outcome,
            httpContext.RequestAborted));

    private static async Task<IResult> CancelRunAsync(
        Guid runId,
        [FromForm] CancelRunForm form,
        HttpContext httpContext,
        RunService runs)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await runs.CancelAsync(userId, runId, httpContext.RequestAborted);
        return result.Status switch
        {
            // PRG with a notice either way - including the friendly no-op for an already-terminal
            // run (the card's decision: a notice, never an error page). Success is Success.
            RunOperationStatus.Success => Results.Redirect($"/runs/{runId}?notice={result.Outcome switch
            {
                RunCancelOutcome.Canceled => "canceled",
                RunCancelOutcome.CancelRequested => "cancel-requested",
                _ => "already-finished",
            }}"),

            // Authorization failures are a hard rejection, never a friendly redirect (F-003 rule).
            RunOperationStatus.Forbidden => Results.Text(
                result.Message ?? "You are not allowed to cancel runs.",
                "text/plain",
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.NotFound(),
        };
    }

    private static IResult MapReport(RunReportResult result) => result.Status switch
    {
        RunOperationStatus.Success => Results.Json(
            new RunReportResponse(result.RunStatus!, result.DeadlineAt)),
        RunOperationStatus.InvalidToken => Results.Unauthorized(),
        RunOperationStatus.NotFound => Results.NotFound(),
        RunOperationStatus.InvalidInput =>
            Results.Json(new WorkerEndpoints.WorkerApiError(result.ErrorCode ?? RunErrorCodes.FieldInvalid),
                statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Json(new WorkerEndpoints.WorkerApiError(result.ErrorCode ?? RunErrorCodes.InvalidTransition),
            statusCode: StatusCodes.Status409Conflict),
    };

    /// <summary>The trigger form. No project id field: the route is the only target selector.</summary>
    public sealed class StartRunForm
    {
        /// <summary>
        /// The branch to build (card F-014 replaced the argv textarea). Blank means the project's
        /// default branch - the service resolves it, so the form needs no project read of its own.
        /// </summary>
        public string? Branch { get; set; }

        /// <summary>Optional comma-separated tags a claimant worker must cover.</summary>
        public string? RequiredTags { get; set; }

        /// <summary>Optional per-run timeout override in minutes (blank = the server default).</summary>
        public string? TimeoutMinutes { get; set; }

        public RunTriggerInput ToInput() => new(Branch, RequiredTags, TimeoutMinutes);
    }

    /// <summary>Empty form binding: the parameter makes the cancel POST form-bound (antiforgery).</summary>
    public sealed class CancelRunForm
    {
    }

    /// <summary>
    /// The accepted report body (card F-013). The body is optional: the field is additive, and a
    /// worker that has no repository report keeps sending an empty POST.
    /// </summary>
    public sealed class AcceptRunRequest
    {
        /// <summary>The exact commit (<c>git rev-parse HEAD</c>) the worker checked out, or null.</summary>
        public string? GitCommit { get; set; }
    }

    public sealed class CompleteRunRequest
    {
        /// <summary>The failing step's exit code (or 0). Null when nothing produced one.</summary>
        public int? ExitCode { get; set; }

        /// <summary>A <see cref="PipelineFailureReasons"/> value; the server refuses anything else.</summary>
        public string? FailureReason { get; set; }
    }

    /// <summary>
    /// The definition report (card F-014): the commit the worker resolved and the frozen, validated
    /// spec. The spec is the worker's own JSON string, stored verbatim once the envelope checks pass.
    /// </summary>
    public sealed class RunDefinitionRequest
    {
        public string? GitCommit { get; set; }

        public string? PipelineSpecJson { get; set; }
    }

    /// <summary>The canceled report: <c>canceled</c> (user cancel) or <c>timeout</c> (deadline).
    /// One kill, one endpoint - the server maps the outcome to the terminal state (F-009).</summary>
    public sealed class RunCanceledRequest
    {
        public string? Outcome { get; set; }
    }

    /// <summary>
    /// The step report body (card F-015): the 1-based position in the run's frozen spec, the name that
    /// spec declares there, the state the step reached and - when nothing exited - the closed reason.
    /// </summary>
    public sealed class RunStepRequest
    {
        public int Index { get; set; }

        public string? Name { get; set; }

        /// <summary>A <see cref="PipelineStepStatus"/> value; the server refuses anything else.</summary>
        public string? Status { get; set; }

        public int? ExitCode { get; set; }

        /// <summary>A <see cref="PipelineStepFailureReasons"/> value, or null.</summary>
        public string? FailureReason { get; set; }
    }

    /// <param name="Commit">
    /// F-017 (review M1): the exact commit this run must build, when the trigger already named one (a
    /// webhook push). <see langword="null"/> for a manual run, which builds the branch tip and reports what
    /// it checked out. The worker pins a non-null value instead of trusting the branch.
    /// </param>
    public sealed record RunClaimResponse(
        Guid RunId,
        Guid ProjectId,
        string RepositoryUrl,
        string Branch,
        string? Commit,
        string PipelinePath,
        DateTimeOffset LeaseExpiresAt);

    /// <param name="DeadlineAt">F-014: the accepted answer carries the run's frozen deadline.</param>
    public sealed record RunReportResponse(string Status, DateTimeOffset? DeadlineAt = null);

    /// <summary>The step report's answer (card F-015): the recorded step's id, its state and the run's.</summary>
    public sealed record RunStepResponse(Guid StepId, string Status, string RunStatus);
}
