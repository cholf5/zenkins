using System.Net;
using System.Net.Sockets;

namespace Zenkins.Worker;

/// <summary>
/// The worker's state machine (card F-005): load-or-register the credential, then heartbeat on the
/// configured interval, retrying transport failures with capped exponential backoff + jitter, and
/// shutting down gracefully on SIGINT/SIGTERM - one best-effort final heartbeat, exit code 0.
/// <para>
/// <b>State transitions are logged</b> (registered, first heartbeat, reconnects, failures with the
/// next retry delay); individual successful heartbeats are silent so a healthy worker produces a
/// bounded log, not a steady drip. Plaintext tokens are never written to the console: only the
/// server-assigned worker id is.
/// </para>
/// </summary>
public sealed class WorkerRunner(
    WorkerConfiguration configuration,
    WorkerApiClient api,
    WorkerTokenStore tokenStore,
    BackoffScheduler backoff,
    TextWriter log,
    TextWriter errorLog)
{
    /// <summary>Exit code for invalid configuration / unusable persisted state.</summary>
    public const int ConfigurationExitCode = 2;

    /// <summary>Exit code when the server permanently rejected the registration token.</summary>
    public const int RegistrationRejectedExitCode = 3;

    /// <summary>Short timeout for the best-effort final heartbeat at shutdown.</summary>
    private static readonly TimeSpan FinalHeartbeatTimeout = TimeSpan.FromSeconds(2);

    public async Task<int> RunAsync(CancellationToken shutdown)
    {
        var workerToken = LoadPersistedToken();
        if (workerToken is null)
        {
            workerToken = await RegisterAsync(shutdown);
            if (workerToken is null)
            {
                // Shutdown arrived while waiting for the server; nothing to unwind.
                return 0;
            }

            tokenStore.Save(workerToken);
            Log($"worker token persisted to {tokenStore.StorePath}.");
        }

        var announceOnFirstSuccess = true;
        var reconnecting = false;

        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                var response = await api.HeartbeatAsync(
                    workerToken,
                    configuration.BuildHeartbeatRequest(),
                    shutdown);

                if (announceOnFirstSuccess)
                {
                    Log($"online as worker '{response.WorkerId}'; heartbeating every "
                        + $"{configuration.HeartbeatInterval.TotalSeconds:0} second(s).");
                    announceOnFirstSuccess = false;
                }
                else if (reconnecting)
                {
                    Log("reconnected; heartbeats are flowing again.");
                    reconnecting = false;
                }

                backoff.Reset();
                await Task.Delay(configuration.HeartbeatInterval, shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
            {
                // F2 (review fix): the per-request ceiling in WorkerApiClient fires as an
                // OperationCanceledException when the server accepts the connection but never
                // answers in time. That is a transport failure, not a crash: back off and retry
                // (F-005 AC-3, TC-002's black-hole variant).
                reconnecting = true;
                if (!await BackoffAfterFailureAsync(
                    "the server did not answer in time while sending a heartbeat (request timeout).",
                    shutdown))
                {
                    break;
                }
            }
            catch (Exception failure) when (failure is HttpRequestException or WorkerApiRejectedException or SocketException)
            {
                reconnecting = true;
                var delay = backoff.NextDelay();
                if (failure is WorkerApiRejectedException rejection)
                {
                    // Definitive server answer (401/403: the persisted token no longer works;
                    // other 4xx: the request as built is not accepted). Nothing this process can
                    // fix - surface it clearly and keep backing off so an operator who restores
                    // the credential server-side reconnects without a restart.
                    var guidance = rejection.StatusCode is 401 or 403
                        ? " Mint a new registration token and re-register if this persists."
                        : string.Empty;
                    await errorLog.WriteLineAsync(
                        $"Zenkins Worker: {rejection.Message} Retrying in {delay.TotalSeconds:0.#}s.{guidance}");
                }
                else
                {
                    await errorLog.WriteLineAsync(
                        $"Zenkins Worker: {Describe(failure)} Retrying in {delay.TotalSeconds:0.#}s.");
                }

                try
                {
                    await Task.Delay(delay, shutdown);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        await SendFinalHeartbeatAsync(workerToken);
        Log("stopped cleanly.");
        return 0;
    }

    private string? LoadPersistedToken()
    {
        string? token;
        try
        {
            token = tokenStore.Load();
        }
        catch (WorkerTokenStoreException exception)
        {
            throw new WorkerStartupException(exception.Message);
        }

        if (token is not null)
        {
            Log($"using the persisted worker token from {tokenStore.StorePath}.");
        }
        else if (configuration.RegistrationToken is null)
        {
            throw new WorkerStartupException(
                $"configuration error: no persisted worker token at '{tokenStore.StorePath}' and "
                + $"{WorkerConfiguration.RegistrationTokenVariable} is not set. Ask an Owner for a "
                + "registration token and set it once.");
        }

        return token;
    }

    /// <summary>Registers with retries; a definitive 4xx stops the process with a clear line.</summary>
    private async Task<string?> RegisterAsync(CancellationToken shutdown)
    {
        var registrationRequest = configuration.BuildRegistrationRequest();
        Log($"registering with {configuration.ServerUrl} as '{registrationRequest.Name}' "
            + $"({registrationRequest.Os}, {registrationRequest.Architecture}, "
            + $"{registrationRequest.CpuCount} CPUs, tags '{registrationRequest.Tags}').");

        while (true)
        {
            try
            {
                var response = await api.RegisterAsync(
                    configuration.RegistrationToken!,
                    registrationRequest,
                    shutdown);
                Log($"registered; the server assigned worker id '{response.WorkerId}'.");
                return response.WorkerToken;
            }
            catch (WorkerApiRejectedException rejection)
            {
                // B1 (review fix): every definitive 4xx is a final answer - the token is dead
                // (401/403) or the request as built will never be accepted (any other non-transient
                // 4xx, e.g. a 400 the server used to answer with for empty tags). Retrying cannot
                // fix either: one clear stderr line and exit non-zero, no retry storm. C (second
                // review fix): the exit code is RegistrationRejectedExitCode (3), distinct from a
                // configuration error (2) - the process parsed and started fine; the server
                // permanently refused this registration.
                var guidance = rejection.StatusCode is 401 or 403
                    ? " Registration tokens are single-use: ask an Owner for a fresh one."
                    : " Fix the worker's configuration (capabilities, tags) and start it again.";
                throw new WorkerStartupException(
                    $"configuration error: {rejection.Message}{guidance}",
                    RegistrationRejectedExitCode);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return null;
            }
            catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
            {
                // F2 (review fix): per-request timeout (server never answered in time) - a
                // transport failure, so back off and retry instead of crashing the process.
                if (!await BackoffAfterFailureAsync(
                    "the server did not answer in time while registering (request timeout).",
                    shutdown))
                {
                    return null;
                }
            }
            catch (Exception failure) when (failure is HttpRequestException or SocketException)
            {
                if (!await BackoffAfterFailureAsync(
                    $"cannot reach {configuration.ServerUrl} ({Describe(failure)}).", shutdown))
                {
                    return null;
                }
            }
        }
    }

    /// <summary>
    /// Logs one failed attempt with the next backoff delay and waits it out. Returns
    /// <c>false</c> when the shutdown signal arrived during the wait (the caller must stop).
    /// </summary>
    private async Task<bool> BackoffAfterFailureAsync(string failureDescription, CancellationToken shutdown)
    {
        var delay = backoff.NextDelay();
        await errorLog.WriteLineAsync(
            $"Zenkins Worker: {failureDescription} Retrying in {delay.TotalSeconds:0.#}s.");
        try
        {
            await Task.Delay(delay, shutdown);
            return true;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Best-effort final heartbeat with a short timeout, independent of the shutdown signal.</summary>
    private async Task SendFinalHeartbeatAsync(string workerToken)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(FinalHeartbeatTimeout);
            await api.HeartbeatAsync(
                workerToken,
                configuration.BuildHeartbeatRequest(),
                timeoutCts.Token);
            Log("final heartbeat sent.");
        }
        catch (Exception)
        {
            // Best effort by definition: the server marks the worker stale later (F-010).
            await log.WriteLineAsync("final heartbeat could not be sent; continuing shutdown.");
        }
    }

    private void Log(string message) => log.WriteLine("Zenkins Worker: " + message);

    private static string Describe(Exception failure) => failure switch
    {
        HttpRequestException request => request.StatusCode is { } status
            ? $"the server answered {(int)status} {status}."
            : $"connection problem ({request.InnerException?.Message ?? "transport failure"}).",
        SocketException socket => $"connection problem ({socket.SocketErrorCode}).",
        _ => failure.Message,
    };
}

/// <summary>
/// One-line fatal startup problem: printed to stderr, process exits with <see cref="ExitCode"/>
/// (2 for configuration errors, 3 when the server permanently rejected the registration).
/// </summary>
public sealed class WorkerStartupException(string message, int exitCode = WorkerRunner.ConfigurationExitCode) : Exception(message)
{
    /// <summary>The exit code this failure exits with (see <see cref="WorkerRunner"/>'s constants).</summary>
    public int ExitCode { get; } = exitCode;
}
