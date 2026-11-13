using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Zenkins.Shared.RunCancellation;

namespace Zenkins.Worker;

/// <summary>Registration payload (F-004 contract). Serialized camelCase.</summary>
public sealed record WorkerRegistrationRequest(
    string Name,
    string Os,
    string Architecture,
    string Version,
    string Tags,
    int CpuCount,
    int MaxConcurrency);

/// <summary>Heartbeat payload; capability fields refresh the server row.</summary>
public sealed record WorkerHeartbeatRequest(
    string Os,
    string Architecture,
    string Version,
    string Tags,
    int CpuCount,
    int MaxConcurrency);

public sealed record WorkerRegistrationResponse(string WorkerId, string WorkerToken, string WorkerName);

/// <summary>
/// The heartbeat response. <see cref="CancelRequestedRuns"/> is F-009's fallback cancel channel:
/// the server's still-Running runs with a pending cancel request for this worker. Null when the
/// server predates the field (and on any response that omits it) - the heartbeat loop treats that
/// as "no cancellations pending".
/// </summary>
public sealed record WorkerHeartbeatResponse(
    string WorkerId,
    DateTimeOffset LastHeartbeatAt,
    WorkerCancelRequest[]? CancelRequestedRuns = null);

/// <summary>A queued run handed over by the claim endpoint (F-006 contract; camelCase on the wire).</summary>
public sealed record WorkerClaimResponse(
    Guid RunId,
    Guid ProjectId,
    IReadOnlyList<string> Command,
    DateTimeOffset LeaseExpiresAt);

/// <summary>The completed report payload: exit 0 → Success on the server, anything else → Failed.</summary>
public sealed record WorkerCompleteRequest(int ExitCode);

/// <summary>The canceled report payload (F-009): <c>canceled</c> or <c>timeout</c>; the server owns
/// the mapping to the terminal state.</summary>
public sealed record WorkerCanceledRequest(string Outcome);

/// <summary>The server answered with a definitive failure (e.g. 401); retries cannot fix it.</summary>
public sealed class WorkerApiRejectedException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// The worker's only dependency on the Server: a plain <see cref="HttpClient"/> and JSON
/// (card F-005). No ASP.NET Core, no SignalR, no inbound port - the worker dials out, authenticates
/// with its token, and everything else is the server's business.
/// <para>
/// Transport failures (DNS, TCP, TLS, timeouts, 5xx) surface as exceptions so the runner can
/// retry with backoff; transient 4xx answers (408/409/425/429) stay retryable too - a 409 on
/// register is what the loser of a same-name first-start race gets, and its retry succeeds once
/// the winner's row is committed. Every other 4xx is a <see cref="WorkerApiRejectedException"/>
/// - a definitive answer that must not be retried blindly.
/// </para>
/// </summary>
public sealed class WorkerApiClient(HttpClient httpClient)
{
    /// <summary>Per-request ceiling: a hung server must not hold the loop forever.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Single-use registration token → per-worker token (shown/stored once).</summary>
    public async Task<WorkerRegistrationResponse> RegisterAsync(
        string registrationToken,
        WorkerRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(
                HttpMethod.Post,
                "api/workers/register",
                registrationToken,
                request,
                WorkerJsonContext.Default.WorkerRegistrationRequest),
            cancellationToken);
        var response = inFlight.Response;
        await EnsureSuccessAsync(response, "registering", inFlight.Timeout);
        var body = await response.Content
            .ReadFromJsonAsync(WorkerJsonContext.Default.WorkerRegistrationResponse, inFlight.Timeout)
            ?? throw new WorkerApiRejectedException("the server returned an empty registration response.", (int)response.StatusCode);
        return body;
    }

    /// <summary>Worker token → refreshed heartbeat.</summary>
    public async Task<WorkerHeartbeatResponse> HeartbeatAsync(
        string workerToken,
        WorkerHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(
                HttpMethod.Post,
                "api/workers/heartbeat",
                workerToken,
                request,
                WorkerJsonContext.Default.WorkerHeartbeatRequest),
            cancellationToken);
        var response = inFlight.Response;
        await EnsureSuccessAsync(response, "sending a heartbeat", inFlight.Timeout);
        var body = await response.Content
            .ReadFromJsonAsync(WorkerJsonContext.Default.WorkerHeartbeatResponse, inFlight.Timeout)
            ?? throw new WorkerApiRejectedException("the server returned an empty heartbeat response.", (int)response.StatusCode);
        return body;
    }

    /// <summary>
    /// Asks for the next queued run (F-006). Returns <see langword="null"/> for the 204 answer
    /// (nothing eligible right now); a claim never learns why it got nothing.
    /// </summary>
    public async Task<WorkerClaimResponse?> ClaimAsync(
        string workerToken,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(HttpMethod.Post, "api/workers/claim", workerToken),
            cancellationToken);
        var response = inFlight.Response;
        await EnsureSuccessAsync(response, "claiming a run", inFlight.Timeout);
        if ((int)response.StatusCode == 204)
        {
            return null;
        }

        return await response.Content
            .ReadFromJsonAsync(WorkerJsonContext.Default.WorkerClaimResponse, inFlight.Timeout)
            ?? throw new WorkerApiRejectedException("the server returned an empty claim response.", (int)response.StatusCode);
    }

    /// <summary>
    /// Reports a claimed run accepted (the single Queued → Running edge). Any 4xx is a definitive
    /// per-run outcome (not-my-lease / expired / foreign id): it is surfaced as a
    /// <see cref="WorkerApiRejectedException"/> and the run is given up, never retried.
    /// </summary>
    public async Task AcceptAsync(
        string workerToken,
        Guid runId,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(
                HttpMethod.Post,
                $"api/workers/runs/{runId}/accepted",
                workerToken),
            cancellationToken);
        await EnsureSuccessAsync(
            inFlight.Response,
            "reporting the run accepted",
            inFlight.Timeout,
            treatAllClientErrorsAsDefinitive: true);
    }

    /// <summary>
    /// Reports a finished run with its exit code (0 → Success, non-zero → Failed on the server).
    /// Any 4xx is definitive per-run: a 409 invalid-transition after a lost response means the
    /// first report already landed, so giving up is correct in every case.
    /// </summary>
    public async Task CompleteAsync(
        string workerToken,
        Guid runId,
        int exitCode,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(
                HttpMethod.Post,
                $"api/workers/runs/{runId}/completed",
                workerToken,
                new WorkerCompleteRequest(exitCode),
                WorkerJsonContext.Default.WorkerCompleteRequest),
            cancellationToken);
        await EnsureSuccessAsync(
            inFlight.Response,
            "reporting the run completed",
            inFlight.Timeout,
            treatAllClientErrorsAsDefinitive: true);
    }

    /// <summary>
    /// Reports that a server-requested cancellation killed the run (F-009). The outcome selects
    /// the server-side mapping: <c>canceled</c> → Canceled with the recorded cancel reason,
    /// <c>timeout</c> → Failed with reason <c>timeout</c> - one kill, one endpoint. Any 4xx is a
    /// definitive per-run outcome (a 409 means the run already reached a terminal state, e.g. a
    /// Lost transition won the race): it is surfaced as a
    /// <see cref="WorkerApiRejectedException"/> and the run is given up, never retried.
    /// </summary>
    public async Task ReportCanceledAsync(
        string workerToken,
        Guid runId,
        string outcome,
        CancellationToken cancellationToken)
    {
        using var inFlight = await SendAsync(
            () => CreateRequest(
                HttpMethod.Post,
                $"api/workers/runs/{runId}/canceled",
                workerToken,
                new WorkerCanceledRequest(outcome),
                WorkerJsonContext.Default.WorkerCanceledRequest),
            cancellationToken);
        await EnsureSuccessAsync(
            inFlight.Response,
            "reporting the run canceled",
            inFlight.Timeout,
            treatAllClientErrorsAsDefinitive: true);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage CreateRequest<T>(
        HttpMethod method,
        string path,
        string token,
        T payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload, typeInfo),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>
    /// Sends one request under the linked shutdown + per-request-ceiling token. The returned
    /// scope owns the response and keeps the ceiling armed through the caller's status handling
    /// and body read (D, second review fix): a server that sends headers but stalls the body also
    /// hits the declared per-request limit, and the shutdown token still cancels at any point.
    /// </summary>
    private async Task<InFlightResponse> SendAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);
        try
        {
            return new InFlightResponse(await httpClient.SendAsync(createRequest(), timeoutCts.Token), timeoutCts);
        }
        catch
        {
            timeoutCts.Dispose();
            throw;
        }
    }

    /// <summary>The response of one request plus the token that bounds its whole lifetime.</summary>
    private sealed class InFlightResponse(HttpResponseMessage response, CancellationTokenSource timeout) : IDisposable
    {
        public HttpResponseMessage Response { get; } = response;

        /// <summary>Shutdown and per-request ceiling, linked; bounds status handling and body reads.</summary>
        public CancellationToken Timeout { get; } = timeout.Token;

        private readonly CancellationTokenSource _timeout = timeout;

        public void Dispose()
        {
            _timeout.Dispose();
            Response.Dispose();
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string what,
        CancellationToken cancellationToken,
        bool treatAllClientErrorsAsDefinitive = false)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var statusCode = (int)response.StatusCode;

        // 401/403 are definitive answers (B1 review fix): the credential is dead, so retrying
        // cannot help. Other non-transient 4xx are definitive too (the request as built will
        // never be accepted); transient 4xx fall through to the retryable branch below. The
        // runner surfaces any WorkerApiRejectedException as a one-line rejection; 401/403
        // additionally keep their "credential is dead" semantics.
        if (statusCode is 401 or 403)
        {
            // Definitive: the token is unknown, burned or revoked. Retrying with the same
            // credential cannot succeed.
            throw new WorkerApiRejectedException(
                $"the server rejected the worker token with {statusCode} while {what} "
                + "(unknown, used or revoked).",
                statusCode);
        }

        // A (second review fix): 408/409/425/429 are transient answers, not definitive ones.
        // A 409 on register is exactly what the loser of a same-name first-start race receives
        // (WorkerEndpoints maps name-duplicate to 409); the winner's row is already committed,
        // so the loser's retry succeeds. These stay on the retryable path below - except on the
        // run reporting calls (accepted/completed pass the flag), where every 4xx is a definitive
        // per-run outcome. The flag must genuinely widen the definitive set: without it a 409
        // after a lost completed response is retried into a 409 forever (and a throttling 429
        // likewise), starving a concurrency-1 worker until shutdown (F-007 review fix; pinned by
        // the stub-server tests in WorkerApiDefinitiveReportingTests).
        if (statusCode is >= 400 and < 500
            && (!IsTransientStatus(statusCode) || treatAllClientErrorsAsDefinitive))
        {
            throw new WorkerApiRejectedException(
                $"the server rejected the request with {statusCode} while {what} "
                + "(payload or contract mismatch).",
                statusCode);
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"the server answered {statusCode} while {what}.",
            null,
            response.StatusCode)
        {
            // The body is server copy (e.g. a validation error code), keep it out of the message
            // chain but available for a future structured log; today the one-line log is enough.
            Data = { ["detail"] = detail.Length > 200 ? detail[..200] : detail },
        };
    }

    /// <summary>The 4xx answers whose retry can succeed (request timeout, race loser, throttling).</summary>
    private static bool IsTransientStatus(int statusCode) => statusCode is 408 or 409 or 425 or 429;
}

/// <summary>Source-generated JSON (trim-safe; the Worker must stay small and AOT-friendly).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkerRegistrationRequest))]
[JsonSerializable(typeof(WorkerHeartbeatRequest))]
[JsonSerializable(typeof(WorkerRegistrationResponse))]
[JsonSerializable(typeof(WorkerHeartbeatResponse))]
[JsonSerializable(typeof(WorkerClaimResponse))]
[JsonSerializable(typeof(WorkerCompleteRequest))]
[JsonSerializable(typeof(WorkerCanceledRequest))]
[JsonSerializable(typeof(WorkerCancelRequest))]
internal sealed partial class WorkerJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
