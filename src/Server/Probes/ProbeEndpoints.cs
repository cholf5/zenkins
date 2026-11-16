using Microsoft.AspNetCore.Mvc;
using Zenkins.Server.Workers;
using Zenkins.Shared.Probes;

namespace Zenkins.Server.Probes;

/// <summary>
/// The worker probe API of card I-005: <c>POST /api/workers/probes/claim</c> and
/// <c>POST /api/workers/probes/{id}/report</c>, plus nothing else - the requesting side is a Server
/// feature calling <see cref="GitProbeService.RequestAsync"/>, never an HTTP route (a future feature's
/// API, if any, would be its own card's surface).
/// <para>
/// <b>Deliberately not <c>RequireAuthorization</c>.</b> The bearer token authenticates and selects the
/// tenant exactly like the run claim; a session cookie never authorizes a worker action. Authentication
/// happens before payload validation, so a tokenless caller cannot probe the contract through a
/// 400-vs-401 difference.
/// </para>
/// <para>
/// <b>Answer shapes copy the run API.</b> A claim with nothing to hand out is a 204 (a worker polls
/// again and learns nothing); a dead token is a bare 401; a foreign or unknown probe id is a bare 404
/// on the report (and the claim simply never leases another tenant's probe). Settled/expired leases are
/// 409 with a stable code, exactly like a lost run lease. The probe's git credential is the one extra
/// route, and it lives with the other credential surface
/// (<see cref="Git.GitCredentialEndpoints.ProbeFetchPath"/>).
/// </para>
/// </summary>
public static class ProbeEndpoints
{
    /// <summary>The claim route (worker-token authenticated).</summary>
    public const string ClaimPath = "/api/workers/probes/claim";

    /// <summary>The report route (worker-token authenticated); <c>{probeId}</c> is the claim's id.</summary>
    public const string ReportPath = "/api/workers/probes/{probeId:guid}/report";

    /// <summary>The report's success status (one word; the worker only needs to know it landed).</summary>
    public const string ReportRecordedStatus = "recorded";

    public static IEndpointRouteBuilder MapProbeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var api = endpoints.MapGroup("/api/workers");
        api.MapPost("/probes/claim", ClaimAsync);
        api.MapPost("/probes/{probeId:guid}/report", ReportAsync);
        return endpoints;
    }

    private static async Task<IResult> ClaimAsync(HttpContext httpContext, GitProbeService probes)
    {
        var result = await probes.ClaimAsync(
            WorkerEndpoints.BearerToken(httpContext),
            httpContext.RequestAborted);

        return result.Status switch
        {
            GitProbeClaimStatus.InvalidToken => Results.Unauthorized(),
            GitProbeClaimStatus.NoProbe => Results.NoContent(),
            _ => Results.Json(new ProbeClaimResponse(
                result.ProbeId!.Value,
                result.ProjectId!.Value,
                result.RepositoryUrl!,
                result.LeaseExpiresAt!.Value)),
        };
    }

    private static async Task<IResult> ReportAsync(
        Guid probeId,
        [FromBody] ProbeReportRequest request,
        HttpContext httpContext,
        GitProbeService probes)
    {
        var result = await probes.ReportAsync(
            WorkerEndpoints.BearerToken(httpContext),
            probeId,
            request.ToInput(),
            httpContext.RequestAborted);

        return result.Status switch
        {
            GitProbeReportStatus.Success => Results.Json(new ProbeReportResponse(ReportRecordedStatus)),
            GitProbeReportStatus.InvalidToken => Results.Unauthorized(),
            GitProbeReportStatus.NotFound => Results.NotFound(),
            GitProbeReportStatus.InvalidInput => Results.Json(
                new WorkerEndpoints.WorkerApiError(result.ErrorCode ?? GitProbeErrorCodes.OutcomeInvalid),
                statusCode: StatusCodes.Status400BadRequest),
            _ => Results.Json(
                new WorkerEndpoints.WorkerApiError(result.ErrorCode ?? GitProbeErrorCodes.LeaseLost),
                statusCode: StatusCodes.Status409Conflict),
        };
    }

    /// <summary>
    /// What a claim hands over: the probe's id, the repository snapshot to run <c>git ls-remote</c>
    /// against, and the lease's expiry. No credential material rides here - it is fetched at probe time
    /// through the probe-scoped credential route, exactly like a run's.
    /// </summary>
    public sealed record ProbeClaimResponse(
        Guid ProbeId,
        Guid ProjectId,
        string RepositoryUrl,
        DateTimeOffset LeaseExpiresAt);

    /// <summary>The report's answer: the probe is recorded (the worker needs nothing else).</summary>
    public sealed record ProbeReportResponse(string Status);

    /// <summary>
    /// The report body: the outcome, the heads and remote HEAD on success, the diagnostic on failure.
    /// Every field is optional at the binding layer so a malformed body is a 400 from the shared
    /// validator rather than a model-binding 400 the worker cannot explain.
    /// </summary>
    public sealed class ProbeReportRequest
    {
        /// <summary>A <see cref="GitProbeOutcomes"/> value a worker may report.</summary>
        public string? Outcome { get; set; }

        /// <summary>The heads (<c>refs/heads/*</c> → sha); empty for a failure outcome.</summary>
        public List<ProbeRefRequest>? Heads { get; set; }

        /// <summary>The commit the remote's <c>HEAD</c> points at, when it has one.</summary>
        public string? HeadSha { get; set; }

        /// <summary>The branch the remote's <c>HEAD</c> is a symbolic ref to.</summary>
        public string? HeadBranch { get; set; }

        /// <summary>The worker's diagnostic for a failure (already redacted of credential material).</summary>
        public string? Message { get; set; }

        public GitProbeReportInput ToInput() => new(
            Outcome,
            Heads?.Select(head => new GitProbeRef(head.Name ?? string.Empty, head.Sha ?? string.Empty)).ToArray(),
            HeadSha,
            HeadBranch,
            Message);
    }

    /// <summary>One head as the report carries it.</summary>
    public sealed class ProbeRefRequest
    {
        public string? Name { get; set; }

        public string? Sha { get; set; }
    }
}
