using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

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

public sealed record WorkerHeartbeatResponse(string WorkerId, DateTimeOffset LastHeartbeatAt);

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
        CancellationToken cancellationToken)
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
        // so the loser's retry succeeds. These stay on the retryable path below.
        if (statusCode is >= 400 and < 500 && !IsTransientStatus(statusCode))
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
internal sealed partial class WorkerJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
