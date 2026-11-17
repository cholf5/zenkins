using Microsoft.AspNetCore.Mvc;
using Zenkins.Server.Workers;
using Zenkins.Shared.Pipelines;

namespace Zenkins.Server.Probes;

/// <summary>
/// The worker API of card F-022's definition-only validation: <c>POST /api/workers/definitions/claim</c>
/// and <c>POST /api/workers/definitions/{id}/report</c>. Deliberately the same answer shapes as the Git
/// probe routes (I-005): a 204 means "nothing to do" and never discloses why, a dead token is a bare
/// 401, a settled/lapsed task is a 409 on the report. There is no route for the requesting side - the
/// Pipelines tab calls the service, not HTTP.
/// </summary>
public static class DefinitionValidationEndpoints
{
    /// <summary>The claim route (worker-token authenticated).</summary>
    public const string ClaimPath = "/api/workers/definitions/claim";

    /// <summary>The report route; <c>{validationId}</c> is the claim's id.</summary>
    public const string ReportPath = "/api/workers/definitions/{validationId:guid}/report";

    /// <summary>The report's success status (one word; the worker needs nothing else).</summary>
    public const string ReportRecordedStatus = "recorded";

    public static IEndpointRouteBuilder MapDefinitionValidationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var api = endpoints.MapGroup("/api/workers");
        api.MapPost("/definitions/claim", ClaimAsync);
        api.MapPost("/definitions/{validationId:guid}/report", ReportAsync);
        return endpoints;
    }

    private static async Task<IResult> ClaimAsync(HttpContext httpContext, DefinitionValidationService validations)
    {
        var result = await validations.ClaimAsync(
            WorkerEndpoints.BearerToken(httpContext),
            httpContext.RequestAborted);

        return result.Status switch
        {
            DefinitionValidationClaimStatus.InvalidToken => Results.Unauthorized(),
            DefinitionValidationClaimStatus.NoWork => Results.NoContent(),
            _ => Results.Json(new DefinitionValidationClaimResponse(
                result.Claim!.ValidationId,
                result.Claim.Script,
                result.Claim.LeaseExpiresAt,
                result.Claim.Kind,
                result.Claim.RepositoryUrl,
                result.Claim.Branch,
                result.Claim.Commit,
                result.Claim.PipelinePath)),
        };
    }

    private static async Task<IResult> ReportAsync(
        Guid validationId,
        [FromBody] DefinitionValidationReportRequest request,
        HttpContext httpContext,
        DefinitionValidationService validations)
    {
        var result = await validations.ReportAsync(
            WorkerEndpoints.BearerToken(httpContext),
            validationId,
            request.ToInput(),
            httpContext.RequestAborted);

        return result.Status switch
        {
            DefinitionValidationReportStatus.Success =>
                Results.Json(new DefinitionValidationReportResponse(ReportRecordedStatus)),
            DefinitionValidationReportStatus.InvalidToken => Results.Unauthorized(),
            DefinitionValidationReportStatus.NotFound => Results.NotFound(),
            DefinitionValidationReportStatus.InvalidInput => Results.Json(
                new WorkerEndpoints.WorkerApiError(
                    result.ErrorCode ?? DefinitionValidationErrorCodes.OutcomeInvalid),
                statusCode: StatusCodes.Status400BadRequest),
            _ => Results.Json(
                new WorkerEndpoints.WorkerApiError(
                    result.ErrorCode ?? DefinitionValidationErrorCodes.LeaseLost),
                statusCode: StatusCodes.Status409Conflict),
        };
    }

    /// <summary>
    /// What a claim hands over: the id to report against, the lease, and the subject. An inline task
    /// carries <paramref name="Script"/>; a repository-branch task (F-023) carries the repository
    /// snapshot and a null script. No credential rides here - the Worker fetches it under this lease.
    /// </summary>
    public sealed record DefinitionValidationClaimResponse(
        Guid ValidationId,
        string? Script,
        DateTimeOffset LeaseExpiresAt,
        string Kind,
        string? RepositoryUrl,
        string? Branch,
        string? Commit,
        string? PipelinePath);

    /// <summary>The report's answer: recorded (the worker needs nothing else).</summary>
    public sealed record DefinitionValidationReportResponse(string Status);

    /// <summary>
    /// The report body: the outcome word, the frozen spec on success, the diagnostic on a refusal. Every
    /// field is optional at the binding layer so a malformed body is the shared validator's 400, not a
    /// model-binding 400 the worker cannot explain.
    /// </summary>
    public sealed class DefinitionValidationReportRequest
    {
        /// <summary>A <see cref="DefinitionValidationOutcomes"/> value.</summary>
        public string? Outcome { get; set; }

        /// <summary>The frozen spec JSON, required for <c>valid</c>.</summary>
        public string? SpecJson { get; set; }

        /// <summary>The diagnostic, required for <c>invalid</c> (it carries the Lua position).</summary>
        public string? Diagnostic { get; set; }

        /// <summary>A <see cref="PipelineFailureReasons"/> value, or null.</summary>
        public string? FailureReason { get; set; }

        public DefinitionValidationReportInput ToInput() => new(Outcome, SpecJson, Diagnostic, FailureReason);
    }
}
