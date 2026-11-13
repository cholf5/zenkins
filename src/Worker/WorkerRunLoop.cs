using System.Net.Sockets;
using Zenkins.Shared.LogStreaming;
using Zenkins.Shared.RunCancellation;

namespace Zenkins.Worker;

/// <summary>
/// One claim→execute→report loop of the worker (card F-007, consuming F-006's endpoints):
/// claim (<c>POST /api/workers/claim</c>, 204 → wait <see cref="WorkerConfiguration.ClaimPollInterval"/>
/// and poll again; transport failures retry with the existing capped-exponential backoff) → report
/// <c>accepted</c> (clears the lease) → prepare the run's work directory → execute the run's argv as
/// a native process with <c>work/{run-id}/repository</c> as its cwd → stream stdout/stderr line by
/// line into per-stream log files under the run's <c>temp/</c> (F-007's local files, kept through
/// the transition as a debugging aid) <b>and</b> upstream over SignalR (F-008): every line is
/// emitted into the run's bounded <see cref="RunLogBuffer"/> with a per-run monotonic Seq and a
/// drain task ships flush-sized batches to <c>/hubs/worker-logs</c> while the process runs →
/// delete <c>temp/</c> when configured (keeping <c>repository/</c> and <c>artifacts/</c>) →
/// report <c>completed {'{'}exitCode{'}'}</c> (0 → Success, non-zero → Failed on the server).
/// Cleanup precedes the report, and the drain precedes it too: when the server records the
/// terminal state, the run's temp is already gone and its captured lines are on their way to the
/// database (the drain retries across reconnects; the (RunId, Seq) key absorbs any overlap).
/// <para>
/// <b>Concurrency.</b> The worker runs exactly <see cref="WorkerConfiguration.MaxConcurrency"/>
/// instances of this loop, so it never executes more runs than it registered for; each loop's run
/// lives in its own <c>work/{run-id}</c> tree, and no writable path is shared between runs.
/// </para>
/// <para>
/// <b>Definitive vs transient.</b> A 4xx on claim/accepted/completed/canceled is a definitive
/// per-run or credential answer: it is logged once and the run is given up (the server owns the
/// run's state; a 409 on a completed/canceled retry means the first report already landed).
/// Transport failures and request timeouts back off and retry. A shutdown while a process runs
/// kills the whole tree and skips the completed report - the run's <c>Lost</c> transition is
/// F-010's business. A server cancel request (F-009, via the shared <see cref="RunCancelSignal"/>)
/// kills the tree through the same F-007 kill path, drains the partial log upstream, and reports
/// through the <c>canceled</c> endpoint - the server maps the outcome (timeout → Failed, else
/// Canceled).
/// </para>
/// </summary>
public sealed class WorkerRunLoop(
    WorkerConfiguration configuration,
    WorkerApiClient api,
    string workerToken,
    WorkDirectoryService workDirectories,
    LogStreamClient logStreams,
    BackoffScheduler backoff,
    TextWriter log,
    TextWriter errorLog,
    RunCancelSignal? cancelSignal = null)
{
    /// <summary>
    /// Exit code reported for a run that never produced one of its own (the work directory could
    /// not be prepared, the process could not be started). Any non-zero maps to Failed server-side;
    /// the human-readable reason is in the worker log (FailureReason is F-009/F-010 territory).
    /// </summary>
    public const int InfrastructureFailureExitCode = 70;

    /// <summary>The temp log files the run's captured streams land in (transition-period local
    /// copies; the server's incremental persistence is authoritative since F-008).</summary>
    public const string StdOutLogFileName = "stdout.log";
    public const string StdErrorLogFileName = "stderr.log";

    /// <summary>
    /// Claims and executes runs until shutdown. One iteration is one full claim → execute → report
    /// cycle; a finished run is followed immediately by the next claim (no poll delay).
    /// </summary>
    public async Task RunAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                await ExecuteSingleClaimAsync(shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception failure)
            {
                // A bug or unexpected environment condition in one cycle must not kill the loop:
                // log it, back off, keep claiming.
                var delay = backoff.NextDelay();
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: unexpected failure in the run loop ({failure.GetType().Name}: {failure.Message}). "
                    + $"Retrying in {delay.TotalSeconds:0.#}s.");
                if (!await WaitAsync(delay, shutdown))
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// One claim attempt plus, when a run was handed over, its full execution. Returns
    /// <see langword="true"/> when a run was taken (the caller may claim again immediately) and
    /// <see langword="false"/> when there was nothing to do or shutdown arrived.
    /// </summary>
    public async Task<bool> ExecuteSingleClaimAsync(CancellationToken shutdown)
    {
        var claimed = await ClaimAsync(shutdown);
        if (claimed is null)
        {
            return false;
        }

        await ExecuteAsync(claimed, shutdown);
        return true;
    }

    // -------------------------------------------------------------------
    // Claim
    // -------------------------------------------------------------------

    /// <summary>
    /// Polls for a run. 204 → wait the configured poll interval and answer null. Transport
    /// failures and request timeouts back off with the shared scheduler; definitive 4xx answers
    /// back off too (the credential problem is the heartbeat loop's story as well), so a restored
    /// credential picks work up without a restart.
    /// </summary>
    private async Task<WorkerClaimResponse?> ClaimAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            WorkerClaimResponse? claimed;
            try
            {
                claimed = await api.ClaimAsync(workerToken, shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception failure) when (IsTransient(failure) || failure is OperationCanceledException)
            {
                // Transport failure or per-request ceiling (server accepted but never answered):
                // back off and poll again.
                var delay = backoff.NextDelay();
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: claiming a run failed ({Describe(failure)}). Retrying in {delay.TotalSeconds:0.#}s.");
                if (!await WaitAsync(delay, shutdown))
                {
                    return null;
                }

                continue;
            }
            catch (WorkerApiRejectedException rejection)
            {
                var delay = backoff.NextDelay();
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: {rejection.Message} Retrying in {delay.TotalSeconds:0.#}s.");
                if (!await WaitAsync(delay, shutdown))
                {
                    return null;
                }

                continue;
            }

            backoff.Reset();
            if (claimed is null && !await WaitAsync(configuration.ClaimPollInterval, shutdown))
            {
                return null;
            }

            return claimed;
        }

        return null;
    }

    // -------------------------------------------------------------------
    // Execute one claimed run
    // -------------------------------------------------------------------

    private async Task ExecuteAsync(WorkerClaimResponse claim, CancellationToken shutdown)
    {
        var runId = claim.RunId;

        // 1. Accept first: it is the lease-clearing Queued → Running edge. A definitive 4xx (the
        //    lease expired, the run is not ours, or the run was canceled while leased - F-009's
        //    race-window semantic) gives the run up before anything is executed.
        if (!await ReportAsync(
                runId,
                "accepted",
                // F-013: the accepted report can carry the commit the run cloned. The clone itself is
                // wired here by F-014; this card ships the report contract and the git layer.
                (token, cancellationToken) => api.AcceptAsync(token, runId, gitCommit: null, cancellationToken),
                shutdown))
        {
            return;
        }

        // 2. The work directory: the run id is validated as a GUID first, so a malformed id never
        //    touches the file system; creation is idempotent.
        RunWorkDirectory work;
        try
        {
            work = workDirectories.Prepare(runId.ToString());
        }
        catch (Exception failure)
        {
            await errorLog.WriteLineAsync(
                $"Zenkins Worker: run {runId} cannot prepare its work directory ({failure.Message}). "
                + $"Reporting exit code {InfrastructureFailureExitCode}.");
            await ReportCompletionAsync(runId, InfrastructureFailureExitCode, shutdown);
            return;
        }

        await log.WriteLineAsync($"Zenkins Worker: run {runId} executing in {work.RepositoryPath}.");

        // F-009: the per-run cancel gate. The shared bus carries every cancel request; this loop
        // completes its gate only for its own run id. Killing the process tree happens through
        // the cancellation token of the process wait itself (ProcessRunner kills on cancel), so
        // the cancel path reuses exactly the shutdown kill path's machinery.
        var cancelRequested = new TaskCompletionSource<RunCancelRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelScope = new CancellationTokenSource();
        void OnCancelRequested(RunCancelRequest request)
        {
            if (request.RunId != runId)
            {
                return;
            }

            cancelRequested.TrySetResult(request);
            cancelScope.Cancel();
        }

        using var cancelSubscription = cancelSignal?.Subscribe(OnCancelRequested);
        try
        {
            var result = await ExecuteProcessAsync(claim, work, shutdown, cancelScope.Token, cancelRequested.Task);

            if (result.CancelOutcome is { } outcome)
            {
                // 3. Killed by a server cancel request (user cancel or deadline). Same
                //    cleanup-before-report discipline as the completed path: temp first, then the
                //    report - when the server records the terminal state, the run's temp is
                //    already gone and the drained log lines are on their way upstream.
                if (configuration.CleanupTemp)
                {
                    try
                    {
                        workDirectories.CleanupTemp(work);
                    }
                    catch (Exception failure)
                    {
                        await errorLog.WriteLineAsync(
                            $"Zenkins Worker: run {runId} temp cleanup failed ({failure.Message}); leaving it in place.");
                    }
                }

                await log.WriteLineAsync(
                    $"Zenkins Worker: run {runId} was canceled by the server (reason: {result.CancelReason ?? "canceled"}); "
                    + "reporting it canceled.");
                await ReportCanceledAsync(runId, outcome, shutdown);
                return;
            }

            if (result.ExitCode is null)
            {
                // Shutdown arrived mid-run; the process tree was killed. No completed report: F-010's
                // offline detection owns this run's Lost transition.
                return;
            }

            // 4. Delete temp/ per configuration BEFORE the completed report: when the server records
            //    the terminal state, the run's temp is already gone (repository/ and artifacts/ stay).
            if (configuration.CleanupTemp)
            {
                try
                {
                    workDirectories.CleanupTemp(work);
                }
                catch (Exception failure)
                {
                    await errorLog.WriteLineAsync(
                        $"Zenkins Worker: run {runId} temp cleanup failed ({failure.Message}); leaving it in place.");
                }
            }

            // 5. Report the exit code (0 → Success, non-zero → Failed, server-side mapping).
            await ReportCompletionAsync(runId, result.ExitCode.Value, shutdown);
        }
        finally
        {
            cancelSubscription?.Dispose();
        }
    }

    /// <summary>How one execution ended: a real exit code, a server-requested cancellation (with
    /// the outcome to report), or a shutdown kill (both null - no report, F-010's business).</summary>
    private sealed record RunExecutionResult(int? ExitCode, string? CancelOutcome, string? CancelReason = null);

    /// <summary>
    /// Starts the run's process and waits for its exit, streaming stdout/stderr lines into
    /// <c>temp/stdout.log</c> and <c>temp/stderr.log</c> as they are produced. Returns the exit
    /// code, the cancel outcome when the server requested the kill, or both null when shutdown
    /// killed the run.
    /// </summary>
    private async Task<RunExecutionResult> ExecuteProcessAsync(
        WorkerClaimResponse claim,
        RunWorkDirectory work,
        CancellationToken shutdown,
        CancellationToken cancelKill,
        Task<RunCancelRequest> cancelRequested)
    {
        if (claim.Command.Count == 0)
        {
            await errorLog.WriteLineAsync(
                $"Zenkins Worker: run {claim.RunId} was claimed with an empty command. "
                + $"Reporting exit code {InfrastructureFailureExitCode}.");
            return new RunExecutionResult(InfrastructureFailureExitCode, null);
        }

        // Fresh empty logs per execution (a retried claim of the same run must not append stale
        // output); AutoFlush so an observer sees lines as they are produced.
        Directory.CreateDirectory(work.TempPath);
        await using var stdOutWriter = new StreamWriter(
            Path.Combine(work.TempPath, StdOutLogFileName), append: false) { AutoFlush = true };
        await using var stdErrorWriter = new StreamWriter(
            Path.Combine(work.TempPath, StdErrorLogFileName), append: false) { AutoFlush = true };

        // F-008: every line flows upstream with a per-run monotonic Seq while a drain task ships
        // batches to the server; the temp files stay (transition-period debugging aid). The
        // connectivity probe lets the buffer label overflow drops honestly (review fix F4):
        // drops while connected are backpressure, not a reconnect gap.
        var logBuffer = new RunLogBuffer(
            claim.RunId, configuration.LogBufferLines, errorLog, isConnected: () => logStreams.IsConnected);
        var pump = logBuffer.DrainAsync(logStreams, shutdown);

        // The kill token: shutdown OR the server's cancel request for this run. ProcessRunner
        // kills the whole tree on cancellation either way (F-007's kill path, reused verbatim).
        using var killTokens = CancellationTokenSource.CreateLinkedTokenSource(shutdown, cancelKill);

        using var runner = new ProcessRunner(
            new ProcessRunnerOptions
            {
                FileName = claim.Command[0],
                Arguments = [.. claim.Command.Skip(1)],
                WorkingDirectory = work.RepositoryPath,
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["ZENKINS_RUN_ID"] = claim.RunId.ToString(),
                    ["ZENKINS_WORKER_NAME"] = configuration.WorkerName,
                    ["ZENKINS_PROJECT_ID"] = claim.ProjectId.ToString(),
                },
            },
            line =>
            {
                var stream = line.Stream == ProcessOutputStream.StdOut ? LogStream.StdOut : LogStream.StdErr;
                if (line.Stream == ProcessOutputStream.StdOut)
                {
                    stdOutWriter.WriteLine(line.Line);
                }
                else
                {
                    stdErrorWriter.WriteLine(line.Line);
                }

                logBuffer.Emit(stream, line.Line);
            });

        try
        {
            var exitCode = await runner.RunAsync(killTokens.Token);
            return new RunExecutionResult(await FinishDrainAsync(logBuffer, pump, exitCode), null);
        }
        catch (OperationCanceledException) when (cancelKill.IsCancellationRequested && !shutdown.IsCancellationRequested)
        {
            // The server asked for this run's cancellation (user cancel or deadline): the tree is
            // killed and the pipes drained (the F-007 cancellation path). Drain the log buffer so
            // the partial output is on its way upstream, then report through the canceled endpoint
            // - the server maps the outcome (timeout → Failed, otherwise Canceled).
            var request = await cancelRequested;
            await log.WriteLineAsync(
                $"Zenkins Worker: run {claim.RunId} received the server's cancel request "
                + $"(reason: {request.Reason ?? "canceled"}); the process tree was killed.");
            logBuffer.CompleteInput();
            if (logStreams.HasEverConnected)
            {
                await pump;
            }

            var outcome = RunCancelReasons.IsTimeout(request.Reason)
                ? RunCanceledOutcomes.Timeout
                : RunCanceledOutcomes.Canceled;
            return new RunExecutionResult(null, outcome, request.Reason);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            await log.WriteLineAsync(
                $"Zenkins Worker: run {claim.RunId} was killed by shutdown (whole process tree); no completed report.");
            logBuffer.CompleteInput();
            await pump;
            return new RunExecutionResult(null, null);
        }
        catch (Exception failure)
        {
            // The process never produced an exit code (bad argv[0], unusable cwd, ...). Report the
            // run failed so it does not hang in Running forever; the reason is in the logs - and
            // upstream too, so the server-side log shows why the run produced nothing.
            var message = $"zenkins-worker: the process could not be executed: {failure.Message}";
            await stdErrorWriter.WriteLineAsync(message);
            logBuffer.Emit(LogStream.StdErr, message);
            logBuffer.CompleteInput();
            await pump;
            await errorLog.WriteLineAsync(
                $"Zenkins Worker: run {claim.RunId} could not be executed ({failure.Message}). "
                + $"Reporting exit code {InfrastructureFailureExitCode}.");
            return new RunExecutionResult(InfrastructureFailureExitCode, null);
        }
    }

    /// <summary>
    /// Closes the run's input and, when the upstream ever came up in this process, waits for the
    /// drain to hand every buffered line to the server before the completed report goes out (the
    /// same cleanup-before-report discipline F-007 established for temp files). When the upstream
    /// never connected at all, the pump keeps retrying in the background and the report proceeds
    /// - a connection that may not exist must not starve the run's terminal transition.
    /// </summary>
    private async Task<int?> FinishDrainAsync(RunLogBuffer logBuffer, Task pump, int exitCode)
    {
        logBuffer.CompleteInput();
        if (logStreams.HasEverConnected)
        {
            await pump;
        }

        return exitCode;
    }

    // -------------------------------------------------------------------
    // Reporting (accepted / completed) with transport retries
    // -------------------------------------------------------------------

    private Task ReportCompletionAsync(Guid runId, int exitCode, CancellationToken shutdown) =>
        ReportAsync(
            runId,
            "completed",
            (token, cancellationToken) => api.CompleteAsync(token, runId, exitCode, cancellationToken),
            shutdown);

    /// <summary>Reports a server-requested cancellation (F-009): one endpoint, the outcome selects
    /// the terminal state server-side (timeout → Failed, otherwise Canceled).</summary>
    private Task ReportCanceledAsync(Guid runId, string outcome, CancellationToken shutdown) =>
        ReportAsync(
            runId,
            $"canceled (outcome {outcome})",
            (token, cancellationToken) => api.ReportCanceledAsync(token, runId, outcome, cancellationToken),
            shutdown);

    /// <summary>
    /// Delivers one report with transport retries. A definitive 4xx is logged once and the run is
    /// given up (never retried); shutdown abandons the report. Returns <see langword="true"/> when
    /// the server answered 2xx.
    /// </summary>
    private async Task<bool> ReportAsync(
        Guid runId,
        string what,
        Func<string, CancellationToken, Task> report,
        CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                await report(workerToken, shutdown);
                return true;
            }
            catch (WorkerApiRejectedException rejection)
            {
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: reporting run {runId} {what} was rejected ({rejection.Message}) "
                    + "- giving the run up; the server owns its state.");
                return false;
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception failure) when (IsTransient(failure) || failure is OperationCanceledException)
            {
                // Transport failure or per-request ceiling: retry with backoff. A completed report
                // whose first attempt actually landed retries into a definitive 409 above.
                var delay = backoff.NextDelay();
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: could not reach the server to report run {runId} {what} "
                    + $"({Describe(failure)}). Retrying in {delay.TotalSeconds:0.#}s.");
                if (!await WaitAsync(delay, shutdown))
                {
                    return false;
                }
            }
        }

        return false;
    }

    // -------------------------------------------------------------------
    // Plumbing
    // -------------------------------------------------------------------

    /// <summary>Waits out a delay. Returns <see langword="false"/> when shutdown arrived first.</summary>
    private async Task<bool> WaitAsync(TimeSpan delay, CancellationToken shutdown)
    {
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

    /// <summary>The failures whose retry can succeed (transport, timeouts, hung requests).</summary>
    private static bool IsTransient(Exception failure) =>
        failure is HttpRequestException or SocketException;

    private static string Describe(Exception failure) => failure switch
    {
        HttpRequestException request => request.StatusCode is { } status
            ? $"the server answered {(int)status} {status}."
            : $"connection problem ({request.InnerException?.Message ?? "transport failure"}).",
        SocketException socket => $"connection problem ({socket.SocketErrorCode}).",
        OperationCanceledException => "the server did not answer in time (request timeout).",
        _ => failure.Message,
    };
}
