using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http.Connections.Client;

namespace Zenkins.Worker;

/// <summary>
/// The worker's state machine (cards F-005 + F-007): establish the credential (persisted token,
/// first registration, or a replacement when the Server rejects the persisted one), then run two
/// concurrent loops until shutdown - the heartbeat loop (retrying transport failures with capped
/// exponential backoff + jitter, and re-registering when the credential itself is refused) and
/// <see cref="WorkerConfiguration.MaxConcurrency"/> run-execution loops (<see cref="WorkerRunLoop"/>:
/// claim → execute → report). Card I-005 adds <see cref="WorkerConfiguration.ProbeConcurrency"/>
/// probe loops beside them (claim a probe → <c>git ls-remote</c> → report), so probing never
/// competes with a build for a slot. Shutdown is graceful: one best-effort final heartbeat, exit
/// code 0; a process killed mid-run gets no completed report (the run's Lost transition is
/// F-010's business).
/// <para>
/// <b>Identity is a token, not a name.</b> The name picks a Server row and a local directory; only
/// the token authenticates. A token the Server no longer knows (its data was replaced or restored,
/// or an Owner revoked it) is not survivable by waiting, but it is recoverable: with a registration
/// token in the environment the worker registers again and keeps the same name (and therefore the
/// same row and history) instead of dying behind a 401 until an operator finds a file to
/// delete. <see cref="WorkerCredential"/> is what every loop reads, so the replacement reaches them
/// without a restart.
/// </para>
/// <para>
/// <b>State transitions are logged</b> (registered, first heartbeat, reconnects, failures with the
/// next retry delay, claimed runs); individual successful heartbeats are silent so a healthy worker
/// produces a bounded log, not a steady drip. Plaintext tokens are never written to the console:
/// only the server-assigned worker id is.
/// </para>
/// </summary>
public sealed class WorkerRunner(
    WorkerConfiguration configuration,
    WorkerApiClient api,
    WorkerTokenStore tokenStore,
    WorkDirectoryService workDirectories,
    BackoffScheduler backoff,
    TextWriter log,
    TextWriter errorLog,
    Action<HttpConnectionOptions>? configureConnection = null)
{
    /// <summary>Exit code for invalid configuration / unusable persisted state.</summary>
    public const int ConfigurationExitCode = 2;

    /// <summary>Exit code when the server permanently rejected the registration token.</summary>
    public const int RegistrationRejectedExitCode = 3;

    /// <summary>Short timeout for the best-effort final heartbeat at shutdown.</summary>
    private static readonly TimeSpan FinalHeartbeatTimeout = TimeSpan.FromSeconds(2);

    public async Task<int> RunAsync(CancellationToken shutdown)
    {
        var credential = await EstablishIdentityAsync(shutdown);
        if (credential is null)
        {
            // Shutdown arrived while waiting for the server; nothing to unwind.
            return 0;
        }

        Log($"run execution starts on {configuration.MaxConcurrency} loop(s); "
            + $"claiming every {configuration.ClaimPollInterval.TotalSeconds:0.#} second(s).");

        // F-009: the in-process cancel bus. The upstream log-stream connection's cancelRun handler
        // and the heartbeat loop's cancelRequestedRuns fallback both raise onto it; every run loop
        // subscribes and reacts only to its own run id.
        var cancelSignal = new RunCancelSignal();

        // F-008: one shared upstream log-stream connection (worker-token authenticated, its own
        // reconnect loop next to the heartbeat), owned by every run loop's drain tasks. The
        // connection configurator (optional, in-process test seam) reaches the same options the
        // connection builder passes to the transport.
        await using var logStream = new LogStreamClient(
            configuration, credential, BackoffScheduler.Default(), log, errorLog,
            configureConnection,
            cancelSignal: cancelSignal);

        // F-038: the runs this worker is executing, shared with the work-directory sweep below (it may not remove
        // a live tree) and with every run loop (which reports its own run in and out).
        var activity = new RunActivityRegistry();

        var heartbeat = HeartbeatLoopAsync(credential, cancelSignal, shutdown);
        var logStreamLoop = logStream.RunAsync(shutdown);
        var runLoops = Enumerable.Range(0, configuration.MaxConcurrency)
            .Select(_ => new WorkerRunLoop(
                configuration,
                api,
                credential,
                workDirectories,
                logStream,
                BackoffScheduler.Default(),
                log,
                errorLog,
                cancelSignal,
                activity).RunAsync(shutdown))
            .ToArray();

        // I-005: the probe loops. Their own count (ProbeConcurrency), independent of the build slots, and
        // only when the worker announced the capability - a worker that turned probing off never claims.
        var probeLoops = configuration.ProbesEnabled
            ? Enumerable.Range(0, configuration.ProbeConcurrency)
                .Select(_ => new WorkerProbeLoop(
                    configuration,
                    api,
                    credential,
                    workDirectories,
                    BackoffScheduler.Default(),
                    log,
                    errorLog).RunAsync(shutdown))
                .ToArray()
            : [];

        if (probeLoops.Length > 0)
        {
            Log($"answering Git remote probes on {probeLoops.Length} loop(s); "
                + $"each probe's git budget is {(configuration.ProbeTimeout ?? WorkerConfiguration.DefaultProbeTimeout).TotalSeconds:0}s.");
        }

        // F-022: one definition-only validation loop, always on - answering "is this script valid?" is
        // not a build (it takes no slot and writes no run), so it does not depend on a capability flag.
        var validationLoop = new WorkerDefinitionValidationLoop(
            configuration,
            api,
            credential,
            workDirectories,
            BackoffScheduler.Default(),
            log,
            errorLog).RunAsync(shutdown);

        // F-038: the work-directory half of retention. Its own loop, so a slow disk never blocks a claim, and it
        // is started whatever the concurrency is - the directories outlive the runs either way.
        var workSweep = new WorkDirectorySweeper(workDirectories, activity, configuration, log)
            .RunAsync(shutdown);

        await Task.WhenAll([heartbeat, logStreamLoop, .. runLoops, .. probeLoops, validationLoop, workSweep]);

        await SendFinalHeartbeatAsync(credential);
        Log("stopped cleanly.");
        return 0;
    }

    /// <summary>
    /// The F-005 heartbeat loop: F-009's fallback cancel channel (every response's
    /// <c>cancelRequestedRuns</c> rides the shared cancel bus, so a worker whose SignalR connection
    /// is down still observes a cancel within one heartbeat period) plus identity recovery - a
    /// 401/403 with a registration token in the environment re-registers instead of looping on a
    /// credential the Server will never accept again.
    /// </summary>
    private async Task HeartbeatLoopAsync(
        WorkerCredential credential,
        RunCancelSignal cancelSignal,
        CancellationToken shutdown)
    {
        var announceOnFirstSuccess = true;
        var reconnecting = false;

        // A registration token is single-use. Once a renewal attempt is rejected, the value in the
        // environment is dead too; retrying it would only add 401s to every heartbeat. The process
        // stays up (a supervised service must not crash-loop) with clear guidance instead.
        var registrationTokenUsable = configuration.RegistrationToken is not null;

        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                var response = await api.HeartbeatAsync(
                    credential.Current,
                    configuration.BuildHeartbeatRequest(),
                    shutdown);

                foreach (var cancel in response.CancelRequestedRuns ?? [])
                {
                    cancelSignal.Raise(new RunCancelRequest(cancel.RunId, cancel.Reason));
                }

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

                // Identity recovery. 401/403 is a definitive answer about the credential: the Server
                // does not know this token (its data was replaced or restored) or an Owner revoked
                // it. When the operator supplied a registration token, that is the standing
                // instruction to register again - the alternative is a worker that stays dead until
                // somebody deletes a file on this disk.
                if (failure is WorkerApiRejectedException { StatusCode: 401 or 403 } && registrationTokenUsable)
                {
                    var renewed = await RenewCredentialAsync(shutdown);
                    if (renewed is not null)
                    {
                        credential.Rotate(renewed);
                        backoff.Reset();
                        announceOnFirstSuccess = true;
                        Log("re-registered; this worker now presents the new worker token.");
                        continue;
                    }

                    if (shutdown.IsCancellationRequested)
                    {
                        break;
                    }

                    registrationTokenUsable = false;
                }

                var delay = backoff.NextDelay();
                if (failure is WorkerApiRejectedException rejection)
                {
                    // Other 4xx: the request as built is not accepted - nothing this process can
                    // fix. Surface it clearly and keep backing off so an operator who repairs the
                    // server side reconnects without a restart.
                    var guidance = rejection.StatusCode is 401 or 403
                        ? $" Delete '{tokenStore.StorePath}' and start the worker with a freshly minted "
                          + $"{WorkerConfiguration.RegistrationTokenVariable} to register it again."
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
                + $"{WorkerConfiguration.RegistrationTokenVariable} is not set. The credential lives in "
                + "one directory per server URL and worker name - if this machine ran against a "
                + "different URL (host or port) or as a different user, the file is elsewhere. Ask an "
                + "Owner for a registration token and set it once.");
        }

        return token;
    }

    /// <summary>
    /// Establishes the credential this process will present: the persisted one, a freshly
    /// registered one, or - when the Server rejects the persisted token and a registration token is
    /// configured - a replacement.
    /// <para>
    /// <b>Why probe at all.</b> A persisted token can be dead without this process being able to
    /// notice: the Server's data was replaced or restored, an Owner revoked the worker, the token
    /// was rotated by a registration elsewhere. Without a check at the start, a restart ends in an
    /// endless 401 loop even though the environment carries a perfectly good registration token -
    /// that is the trap this method closes. The probe is one heartbeat; a success is the ordinary
    /// case, and its side effect (marking the worker Online) is what the first heartbeat loop
    /// iteration would do anyway.
    /// </para>
    /// </summary>
    private async Task<WorkerCredential?> EstablishIdentityAsync(CancellationToken shutdown)
    {
        var token = LoadPersistedToken();
        if (token is null)
        {
            var registered = await RegisterAsync(shutdown);
            if (registered is null)
            {
                return null;
            }

            Persist(registered);
            return new WorkerCredential(registered);
        }

        if (configuration.RegistrationToken is not null && await PersistedTokenIsRejectedAsync(token, shutdown))
        {
            Log("the server no longer accepts the persisted worker token; registering again with the "
                + $"configured {WorkerConfiguration.RegistrationTokenVariable}.");

            // Not RenewCredentialAsync here: at startup a registration token the Server has already
            // burned is a fatal answer (exit code 3 with one line), not something to retry around.
            var renewed = await RegisterAsync(shutdown);
            if (renewed is null)
            {
                return null;
            }

            Persist(renewed);
            return new WorkerCredential(renewed);
        }

        return new WorkerCredential(token);
    }

    /// <summary>
    /// One heartbeat that answers "is the persisted token still this worker's?". Only 401/403 counts
    /// as a rejection: a transport failure or a per-request timeout says nothing about the credential
    /// and must leave the ordinary retry loops in charge (a worker that starts while the Server is
    /// unreachable has to keep working the moment it comes back).
    /// </summary>
    private async Task<bool> PersistedTokenIsRejectedAsync(string token, CancellationToken shutdown)
    {
        try
        {
            await api.HeartbeatAsync(token, configuration.BuildHeartbeatRequest(), shutdown);
            return false;
        }
        catch (WorkerApiRejectedException rejection) when (rejection.StatusCode is 401 or 403)
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Registers again with the configured registration token and persists what the Server issued.
    /// Returns <see langword="null"/> when there is nothing to heal with - shutdown, or a
    /// registration token the Server has already burned (<see cref="RegisterAsync"/> throws its
    /// one-line <see cref="WorkerStartupException"/>, which is a fatal answer at startup and a log
    /// line during a running process).
    /// </summary>
    private async Task<string?> RenewCredentialAsync(CancellationToken shutdown)
    {
        try
        {
            var renewed = await RegisterAsync(shutdown);
            if (renewed is not null)
            {
                Persist(renewed);
            }

            return renewed;
        }
        catch (WorkerStartupException failure)
        {
            await errorLog.WriteLineAsync(
                $"Zenkins Worker: automatic re-registration failed. {failure.Message} "
                + "This worker keeps retrying with the credential it has.");
            return null;
        }
    }

    private void Persist(string token)
    {
        tokenStore.Save(token);
        Log($"worker token persisted to {tokenStore.StorePath}.");
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
    private async Task SendFinalHeartbeatAsync(WorkerCredential credential)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(FinalHeartbeatTimeout);
            await api.HeartbeatAsync(
                credential.Current,
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
