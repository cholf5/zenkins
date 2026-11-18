using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Zenkins.Server.Workers;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Projects;
using Zenkins.Shared.RunCancellation;

namespace Zenkins.Server.Runs;

/// <summary>
/// HTTP endpoints for cards F-006, F-014, F-036 and F-037: the trigger form on the project page (F-003's
/// form shape), the build-number permalinks, the run page's two re-run forms, and the token-authenticated
/// worker run API under <c>/api/workers/*</c>.
/// <para>
/// <b>Trigger form.</b> Form-bound (so <c>UseAntiforgery</c> validates the token), authenticated
/// (anonymous POSTs are redirected to sign-in), Owner <em>or</em> Member in the service. F-014 replaced
/// the argv textarea with a branch: a run executes the project's pipeline. Success is a PRG redirect to
/// the new run's detail page; validation failures redirect back to the project page with
/// <c>?runError=&lt;code&gt;</c>.
/// </para>
/// <para>
/// <b>F-036's permalinks.</b> <c>/projects/{id}/runs/last</c> and <c>/projects/{id}/lastSuccessful</c> are
/// 302 aliases to a run's own number permalink (<c>/projects/{id}/runs/{number}</c>, rendered by the run
/// page itself), and a bare 404 when there is no such run. The guid route is untouched, so no existing
/// bookmark or log ticket breaks.
/// </para>
/// <para>
/// <b>F-037's re-run forms.</b> <c>/runs/{id}/rebuild</c> and <c>/runs/{id}/replay</c> are form-bound
/// PRGs like every other form on this surface: success redirects to the <em>new</em> run, a refusal back
/// to the original with its stable code (<c>?rebuildError=</c>/<c>?replayError=</c>), and the hard
/// authorization answers stay hard.
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
        // F-041: the trigger form can carry uploaded file parameters, so the endpoint refuses a body bigger
        // than the bound *before* the handler runs. The handler checks each file too - a caller cannot choose
        // which of the two checks it hits, and a huge body never becomes a megabyte of memory in the app.
        projects.MapPost("/{projectId:guid}/runs/start", StartRunAsync)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(
                ProjectParameterLimits.MaxFileBytes + (64 * 1024)));

        // User cancel (F-009): the run page's cancel button. Cookie + antiforgery like the other
        // form endpoints; the service enforces membership, the tenant filter makes a foreign run a
        // 404 (no existence oracle).
        var runForms = endpoints.MapGroup("/runs").RequireAuthorization();
        runForms.MapPost("/{runId:guid}/cancel", CancelRunAsync);

        // Cards F-036's permalinks: the two redirect aliases. `/projects/{id}/runs/last` and
        // `/projects/{id}/lastSuccessful` answer 302 to the run's own permalink (the number route the run
        // page renders), and a bare 404 when there is no such run - an alias that invented a target would
        // be worse than no alias. Both are cookie-authenticated like every other page route, so the
        // tenant filter is what keeps another organization's project out of the answer.
        var permalinks = endpoints.MapGroup("/projects/{projectId:guid}").RequireAuthorization();
        permalinks.MapGet("/runs/last", LastRunAsync);
        permalinks.MapGet("/lastSuccessful", LastSuccessfulRunAsync);

        // Card F-037: the run page's two re-run buttons. Cookie + antiforgery (they are forms), and the
        // service enforces membership and the run's own re-runnability.
        runForms.MapPost("/{runId:guid}/rebuild", RebuildRunAsync);
        runForms.MapPost("/{runId:guid}/replay", ReplayRunAsync);

        // Card F-038: the pin toggle. Cookie + antiforgery like every other run action; the service enforces
        // membership, and the tenant filter makes a foreign run id the same 404 (no existence oracle).
        runForms.MapPost("/{runId:guid}/pin", PinRunAsync);
        runForms.MapPost("/{runId:guid}/unpin", UnpinRunAsync);

        // Worker run API: token-authenticated, exempt from cookie auth by design.
        var api = endpoints.MapGroup("/api/workers");
        api.MapPost("/claim", ClaimAsync);
        api.MapPost("/runs/{runId:guid}/accepted", AcceptAsync);
        api.MapPost("/runs/{runId:guid}/definition", ReportDefinitionAsync);
        api.MapPost("/runs/{runId:guid}/steps", ReportStepAsync);
        api.MapPost("/runs/{runId:guid}/completed", CompleteAsync);
        api.MapPost("/runs/{runId:guid}/canceled", ReportCanceledAsync);

        // Card F-041: a file parameter's bytes, run-scoped and worker-authenticated. POST with the name in the
        // body - never in the URL - for the same reason F-040's secret delivery is: a name in a path lands in
        // every access log. The refusal contract is F-040's, value for value.
        api.MapPost("/runs/{runId:guid}/parameter-files", FetchRunParameterFileAsync);

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

        // Card F-036: the download is named after the build number - the name a person reads when the
        // browser offers to save it - and keeps the run's own id, so the file a log was taken from is
        // always identifiable (and two projects' #7 saves cannot collide). A run with no number (a row
        // older than the column, read before the migration ran) falls back to the id alone rather than
        // inventing one.
        var number = await runs.GetRunNumberAsync(runId, httpContext.RequestAborted);
        var fileName = number is > 0
            ? $"zenkins-run-{number}-{runId.ToString("D")}-log.txt"
            : $"zenkins-run-{runId.ToString("D")}-log.txt";
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

    /// <summary>
    /// Card F-036: <c>/projects/{id}/runs/last</c> - a 302 to the newest run's own permalink, or a bare
    /// 404 when the project has no run at all (never a guess, never another project's run).
    /// </summary>
    private static async Task<IResult> LastRunAsync(
        Guid projectId,
        RunService runs,
        HttpContext httpContext)
    {
        var run = await runs.GetLastRunAsync(projectId, httpContext.RequestAborted);
        return run is null
            ? Results.NotFound()
            : Results.Redirect($"/projects/{run.ProjectId}/runs/{run.Number}");
    }

    /// <summary>
    /// Card F-036: <c>/projects/{id}/lastSuccessful</c> - the same alias for the newest <c>Success</c> run.
    /// A project whose runs all failed answers 404: "the last successful build" does not exist yet, and
    /// pointing at a failed one would make every badge lie.
    /// </summary>
    private static async Task<IResult> LastSuccessfulRunAsync(
        Guid projectId,
        RunService runs,
        HttpContext httpContext)
    {
        var run = await runs.GetLastSuccessfulRunAsync(projectId, httpContext.RequestAborted);
        return run is null
            ? Results.NotFound()
            : Results.Redirect($"/projects/{run.ProjectId}/runs/{run.Number}");
    }

    /// <summary>
    /// Card F-037's Rebuild button: the optional "with parameters…" form rides the same endpoint (an empty
    /// body means "exactly as it ran"). Success is a PRG to the <b>new</b> run's page - where the "rebuilt
    /// from #N" line is - and every refusal is a PRG back to the original with its stable code, except the
    /// hard authorization answers, which stay hard (the F-003 rule every form on this surface follows).
    /// </summary>
    /// <summary>
    /// The parameter name an uploaded input belongs to (card F-041). The form names a file input
    /// <c>Parameters[NAME]</c> exactly like a text input, so one convention covers both and the parse is a
    /// prefix/suffix strip rather than a second mapping to keep in step.
    /// </summary>
    private static string? ParameterNameOf(string? fieldName)
    {
        const string Prefix = "Parameters[";

        if (fieldName is null
            || !fieldName.StartsWith(Prefix, StringComparison.Ordinal)
            || fieldName[^1] != ']')
        {
            return null;
        }

        var name = fieldName[Prefix.Length..^1];
        return PipelineSpecSchema.IsReferenceNameShape(name) ? name : null;
    }

    private static async Task<IResult> RebuildRunAsync(
        Guid runId,
        [FromForm] RebuildRunForm? form,
        HttpContext httpContext,
        RunService runs)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // F-019's discovery: a body whose fields all fail to bind arrives as null, and this form's fields are
        // legitimately optional - the plain Rebuild button posts nothing but the antiforgery token - so null
        // alone cannot mean "unreadable". A null model together with a non-antiforgery field in the raw form
        // does: the body named something this endpoint could not read, and rebuilding "exactly as it ran"
        // would silently drop what the member typed.
        if (form is null && await HasUnboundFieldAsync(httpContext))
        {
            return Results.Redirect($"/runs/{runId}?rebuildError={RunErrorCodes.FieldInvalid}");
        }

        var result = await runs.RebuildAsync(
            userId,
            runId,
            form?.Parameters,
            httpContext.RequestAborted);
        return MapRerun(result, runId, "rebuildError");
    }

    /// <summary>
    /// Whether the request carries a form field the model did not bind (anything but the antiforgery
    /// token). A body the server cannot parse at all answers <see langword="true"/> too - the same
    /// "nothing readable arrived" refusal, rather than a rebuild that quietly ignores the parameters.
    /// </summary>
    private static async Task<bool> HasUnboundFieldAsync(HttpContext httpContext)
    {
        if (!httpContext.Request.HasFormContentType)
        {
            return false;
        }

        try
        {
            var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
            return form.Keys.Any(key => !key.StartsWith("__", StringComparison.Ordinal));
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    /// <summary>Card F-037's Replay button: the same answers as a rebuild, from the run's own definition.</summary>
    private static async Task<IResult> ReplayRunAsync(
        Guid runId,
        [FromForm] ReplayRunForm? form,
        HttpContext httpContext,
        RunService runs)
    {
        // The empty form parameter is what makes this POST form-bound and antiforgery-validated, like the
        // cancel form; there is nothing else to bind.
        _ = form;
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await runs.ReplayAsync(userId, runId, httpContext.RequestAborted);
        return MapRerun(result, runId, "replayError");
    }

    /// <summary>The PRG mapping the two re-run buttons share (one vocabulary, two buttons).</summary>
    private static IResult MapRerun(RunTriggerResult result, Guid originalRunId, string errorParameter) =>
        result.Status switch
        {
            RunOperationStatus.Success => Results.Redirect($"/runs/{result.RunId}"),

            // Authorization failures are a hard rejection, never a friendly redirect (F-003 rule).
            RunOperationStatus.Forbidden => Results.Text(
                result.Message ?? "You are not allowed to re-run builds.",
                "text/plain",
                statusCode: StatusCodes.Status403Forbidden),
            RunOperationStatus.NotFound => Results.NotFound(),

            // The re-run's own refusals and its parameter errors ride the same PRG the trigger form uses:
            // the code is the whole channel a browser has, and it is the vocabulary the run page prints.
            _ => Results.Redirect(
                $"/runs/{originalRunId}?{errorParameter}={result.FieldErrors?[0].Code ?? RunErrorCodes.FieldInvalid}"),
        };

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

        // F-041: the uploaded file parameters. The inputs are named `Parameters[NAME]`, so the mapping is the
        // form's own convention rather than a second one; an input that names no parameter is ignored (the
        // service refuses a name the project does not define, which is the same answer a text value gets).
        // Read through the request rather than binding an IFormFileCollection parameter: a file parameter is
        // optional, and a handler parameter of that type would make the endpoint accept *only* multipart - 415
        // for the urlencoded posts every existing caller (and every test) sends. The form is already parsed for
        // the model binding, so this is the same read, cached.
        var requestForm = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
        var files = requestForm.Files;
        Dictionary<string, byte[]>? uploads = null;
        if (files is { Count: > 0 })
        {
            uploads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (ParameterNameOf(file.Name) is not { } name)
                {
                    continue;
                }

                if (file.Length > ProjectParameterLimits.MaxFileBytes)
                {
                    return Results.Redirect(
                        $"/projects/{projectId}?runError={RunErrorCodes.ParameterFileTooLarge}");
                }

                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, httpContext.RequestAborted);
                uploads[name] = buffer.ToArray();
            }
        }

        var input = form.ToInput() with
        {
            Files = uploads is { Count: > 0 } ? uploads : null,
        };

        var result = await runs.TriggerAsync(userId, projectId, input, httpContext.RequestAborted);
        return result.Status switch
        {
            RunOperationStatus.Success => Results.Redirect($"/runs/{result.RunId}"),

            // Authorization failures are a hard rejection, never a friendly redirect (F-003 rule).
            RunOperationStatus.Forbidden => Results.Text(
                result.Message ?? "You are not allowed to start runs.",
                "text/plain",
                statusCode: StatusCodes.Status403Forbidden),
            RunOperationStatus.NotFound => Results.NotFound(),

            // Everything else is a PRG back to the project page carrying the first field error's code:
            // the trigger's own validation failures (F-014/F-021) and the project's refusals (cards
            // F-028 + F-029 - disabled, or the queue at its cap; the two ride the same PRG because the
            // browser has no other way to read a code). The *service* status keeps them apart -
            // `Refused` is the "well-formed request, the project's state says no" case that a JSON API
            // (F-027) will map to 409 while this form maps it to the same redirect - so nothing here is
            // pretending that 409 is implemented today.
            _ => Results.Redirect(
                $"/projects/{projectId}?runError={result.FieldErrors?[0].Code ?? RunErrorCodes.FieldInvalid}"),
        };
    }

    /// <summary>
    /// The run-scoped parameter-file fetch (card F-041): the bytes of one uploaded <c>file</c> parameter, for the
    /// worker executing the run. A bad token is a bare 401 answered before any run or name lookup; every miss is
    /// one bodyless 404, so neither the response nor the log can tell "not yours" from "does not exist".
    /// </summary>
    private static async Task<IResult> FetchRunParameterFileAsync(
        Guid runId,
        [FromBody] RunParameterFileRequest request,
        HttpContext httpContext,
        RunParameterFileService files)
    {
        var (status, bytes) = await files.FetchAsync(
            WorkerEndpoints.BearerToken(httpContext),
            runId,
            request.Name,
            httpContext.RequestAborted);

        switch (status)
        {
            case RunParameterFileFetchStatus.Success:
                // The body is an upload: explicitly uncacheable, so neither an intermediary nor a client keeps
                // a copy. Raw bytes, not JSON - there is nothing to encode and nothing to read around.
                httpContext.Response.Headers.CacheControl = "no-store";
                return Results.Bytes(bytes!, "application/octet-stream");

            case RunParameterFileFetchStatus.InvalidToken:
                return Results.Unauthorized();

            default:
                return Results.NotFound();
        }
    }

    /// <summary>
    /// The worker's parameter-file request body: the name the step's <c>${…}</c>-free environment read. No value
    /// and no organization id - the token selects the tenant and the path selects the run.
    /// </summary>
    public sealed class RunParameterFileRequest
    {
        public string? Name { get; set; }
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
                result.RepositoryUrl,
                result.Branch!,
                result.Commit,
                result.PipelinePath!,
                result.LeaseExpiresAt!.Value,
                result.PipelineSpecJson,
                result.Parameters,
                result.Number,
                result.PipelineScriptText,
                result.DefinitionSource,
                result.PasswordParameters,
                result.FileParameters,
                result.PostFailureFailsRun)),
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
                request.FailureReason,
                request.Attempts),
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

    /// <summary>
    /// Card F-038's pin toggle: "keep this build forever". Two routes rather than a flag in the body, because the
    /// button a person presses names the state it asks for - and a body that lost its field could otherwise
    /// unpin a build the reader meant to protect.
    /// </summary>
    private static Task<IResult> PinRunAsync(
        Guid runId,
        [FromForm] PinRunForm? form,
        HttpContext httpContext,
        RetentionService retention)
    {
        _ = form;
        return SetPinnedAsync(runId, pinned: true, httpContext, retention);
    }

    /// <summary>Card F-038: the other half of the toggle. Unpinning never deletes - the next sweep decides.</summary>
    private static Task<IResult> UnpinRunAsync(
        Guid runId,
        [FromForm] PinRunForm? form,
        HttpContext httpContext,
        RetentionService retention)
    {
        _ = form;
        return SetPinnedAsync(runId, pinned: false, httpContext, retention);
    }

    private static async Task<IResult> SetPinnedAsync(
        Guid runId,
        bool pinned,
        HttpContext httpContext,
        RetentionService retention)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await retention.SetPinnedAsync(userId, runId, pinned, httpContext.RequestAborted);
        return result.Status switch
        {
            // PRG either way: the page re-renders the toggle in the state it now has, which is also the state the
            // retention sweep reads.
            RunPinStatus.Success => Results.Redirect(
                $"/runs/{runId}?notice={(result.Pinned ? "pinned" : "unpinned")}"),

            // Authorization failures are a hard rejection, never a friendly redirect (F-003 rule).
            RunPinStatus.Forbidden => Results.Text(
                result.Message ?? "You are not allowed to pin this run.",
                "text/plain",
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.NotFound(),
        };
    }

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

    /// <summary>
    /// Empty on purpose: the parameter is what makes the pin/unpin POSTs form-bound and therefore covered by
    /// <c>UseAntiforgery</c>. Which state the button asks for is the route's, not a field's.
    /// </summary>
    public sealed class PinRunForm
    {
    }

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

        /// <summary>
        /// F-021: the trigger's build-parameter values, bound from the dynamic inputs the project's
        /// definitions render (keys <c>Parameters[NAME]</c>). Absent entries resolve to defaults;
        /// unknown names are refused by the service.
        /// </summary>
        public Dictionary<string, string>? Parameters { get; set; }

        public RunTriggerInput ToInput() => new(Branch, RequiredTags, TimeoutMinutes, Parameters);
    }

    /// <summary>Empty form binding: the parameter makes the cancel POST form-bound (antiforgery).</summary>
    public sealed class CancelRunForm
    {
    }

    /// <summary>
    /// Card F-037's "Rebuild with parameters…" form: the values the member edited, keyed the way the trigger
    /// form names them (<c>Parameters[NAME]</c>). An absent/empty body is the plain Rebuild button and means
    /// "exactly as it ran".
    /// </summary>
    public sealed class RebuildRunForm
    {
        public Dictionary<string, string>? Parameters { get; set; }
    }

    /// <summary>Empty form binding: the parameter makes the replay POST form-bound (antiforgery).</summary>
    public sealed class ReplayRunForm
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

        /// <summary>
        /// How many attempts the step made (card F-032): absent means one. The Server bounds it by the
        /// schema's own retry cap, and refuses the field on a <c>Running</c> report (nothing to count yet).
        /// </summary>
        public int? Attempts { get; set; }
    }

    /// <param name="Commit">
    /// F-017 (review M1): the exact commit this run must build, when the trigger already named one (a
    /// webhook push). <see langword="null"/> for a manual run, which builds the branch tip and reports what
    /// it checked out. The worker pins a non-null value instead of trusting the branch.
    /// </param>
    public sealed record RunClaimResponse(
        Guid RunId,
        Guid ProjectId,

        // Nullable since F-020: a Freestyle run has no repository - the Worker skips the clone and
        // executes the frozen spec the claim carries instead.
        string? RepositoryUrl,
        string Branch,
        string? Commit,
        string PipelinePath,
        DateTimeOffset LeaseExpiresAt,

        // F-020: the Freestyle run's frozen spec, snapshotted at trigger time. Null for a Pipeline run,
        // whose spec comes from the Worker's definition phase.
        string? PipelineSpecJson = null,

        // F-021 + F-034: the run's server-resolved environment values (name → value) - the organization
        // layer this run was queued with, with the parameter values on top of it. **One map**: the server
        // resolved the order at trigger time, so the worker injects this and never compares two server
        // layers (a second copy of the order at the other end is a second thing that can drift). Null
        // when the run has neither layer; the two frozen snapshots stay separate on the run row.
        IReadOnlyDictionary<string, string>? Parameters = null,

        // F-036: the run's per-project build number, injected as ZENKINS_BUILD_NUMBER beside the
        // ZENKINS_RUN_ID/ZENKINS_BUILD_ID guid. Null (never 0) when the row carries no positive number,
        // so the worker's "no number, no variable" defence is reachable rather than dead.
        int? Number = null,

        // F-022: an inline Pipeline run's script, snapshotted at trigger time. Null (and absent from an
        // older server's JSON) for a repository or Freestyle run; the Worker writes a non-null value to
        // temp/pipeline.lua and runs the definition phase instead of cloning.
        string? PipelineScript = null,

        // F-022: the run's definition shape word (repository | freestyle | inline). The Worker switches
        // on this, never on "is there a frozen spec?" - an inline run can carry both.
        string? DefinitionSource = null,

        // F-041: the names of the password-typed parameters in Parameters. The Worker registers their values
        // with the run's redaction filter, so a step that echoes one logs the mask. Names only; absent for a
        // run with no password parameter (and from an older server that does not know the field).
        IReadOnlyList<string>? PasswordParameters = null,

        /// <summary>
        /// Card F-041: the names of the <c>file</c>-typed parameters. Their bytes are <b>not</b> in this payload -
        /// the worker fetches each one over the run-scoped parameter-file route, so a megabyte upload never
        /// travels in a document every worker reads for every run.
        /// </summary>
        IReadOnlyList<string>? FileParameters = null,

        /// <summary>
        /// Card F-031: the project's "a failed post step fails the run" switch. Absent from an older server's
        /// claim, which the worker reads as <see langword="false"/> - the product default.
        /// </summary>
        bool PostFailureFailsRun = false);

    /// <param name="DeadlineAt">F-014: the accepted answer carries the run's frozen deadline.</param>
    public sealed record RunReportResponse(string Status, DateTimeOffset? DeadlineAt = null);

    /// <summary>The step report's answer (card F-015): the recorded step's id, its state and the run's.</summary>
    public sealed record RunStepResponse(Guid StepId, string Status, string RunStatus);
}
