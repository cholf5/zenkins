using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Zenkins.Server.Workers;

namespace Zenkins.Server.Runs;

/// <summary>
/// HTTP endpoints for card F-006: the trigger form on the project page (F-003's form shape) and
/// the token-authenticated worker run API under <c>/api/workers/*</c>.
/// <para>
/// <b>Trigger form.</b> Form-bound (so <c>UseAntiforgery</c> validates the token), authenticated
/// (anonymous POSTs are redirected to sign-in), Owner <em>or</em> Member in the service. Success
/// is a PRG redirect to the new run's detail page; validation failures redirect back to the
/// project page with <c>?runError=&lt;code&gt;</c>. The submitted command is deliberately
/// <b>not</b> echoed into the redirect URL - an argument may be 32 KiB, which cannot ride a URL
/// (F-003 echoes its small fields; here the field error names the problem instead).
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

        // Worker run API: token-authenticated, exempt from cookie auth by design.
        var api = endpoints.MapGroup("/api/workers");
        api.MapPost("/claim", ClaimAsync);
        api.MapPost("/runs/{runId:guid}/accepted", AcceptAsync);
        api.MapPost("/runs/{runId:guid}/completed", CompleteAsync);

        return endpoints;
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
                result.Command!,
                result.LeaseExpiresAt!.Value)),
        };
    }

    private static async Task<IResult> AcceptAsync(
        Guid runId,
        HttpContext httpContext,
        RunService runs) =>
        MapReport(await runs.AcceptAsync(
            WorkerEndpoints.BearerToken(httpContext),
            runId,
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
            httpContext.RequestAborted));

    private static IResult MapReport(RunReportResult result) => result.Status switch
    {
        RunOperationStatus.Success => Results.Json(new RunReportResponse(result.RunStatus!)),
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
        /// <summary>The command textarea: one argument per line.</summary>
        public string? Command { get; set; }

        /// <summary>Optional comma-separated tags a claimant worker must cover.</summary>
        public string? RequiredTags { get; set; }

        public RunTriggerInput ToInput() => new(Command, RequiredTags);
    }

    public sealed class CompleteRunRequest
    {
        public int? ExitCode { get; set; }
    }

    public sealed record RunClaimResponse(
        Guid RunId,
        Guid ProjectId,
        IReadOnlyList<string> Command,
        DateTimeOffset LeaseExpiresAt);

    public sealed record RunReportResponse(string Status);
}
