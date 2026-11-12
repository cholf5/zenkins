using System.Net.Sockets;

namespace Zenkins.Worker;

/// <summary>
/// One claim→execute→report loop of the worker (card F-007, consuming F-006's endpoints):
/// claim (<c>POST /api/workers/claim</c>, 204 → wait <see cref="WorkerConfiguration.ClaimPollInterval"/>
/// and poll again; transport failures retry with the existing capped-exponential backoff) → report
/// <c>accepted</c> (clears the lease) → prepare the run's work directory → execute the run's argv as
/// a native process with <c>work/{run-id}/repository</c> as its cwd → stream stdout/stderr line by
/// line into per-stream log files under the run's <c>temp/</c> (SignalR upstream is F-008; this
/// card's only output handling is local) → delete <c>temp/</c> when configured (keeping
/// <c>repository/</c> and <c>artifacts/</c>) → report <c>completed {'{'}exitCode{'}'}</c> (0 →
/// Success, non-zero → Failed on the server). Cleanup precedes the report, so a run recorded as
/// terminal has its temp already gone.
/// <para>
/// <b>Concurrency.</b> The worker runs exactly <see cref="WorkerConfiguration.MaxConcurrency"/>
/// instances of this loop, so it never executes more runs than it registered for; each loop's run
/// lives in its own <c>work/{run-id}</c> tree, and no writable path is shared between runs.
/// </para>
/// <para>
/// <b>Definitive vs transient.</b> A 4xx on claim/accepted/completed is a definitive per-run or
/// credential answer: it is logged once and the run is given up (the server owns the run's state;
/// a 409 on a completed retry means the first report already landed). Transport failures and
/// request timeouts back off and retry. A shutdown while a process runs kills the whole tree and
/// skips the completed report - the run's <c>Lost</c> transition is F-010's business.
/// </para>
/// </summary>
public sealed class WorkerRunLoop(
    WorkerConfiguration configuration,
    WorkerApiClient api,
    string workerToken,
    WorkDirectoryService workDirectories,
    BackoffScheduler backoff,
    TextWriter log,
    TextWriter errorLog)
{
    /// <summary>
    /// Exit code reported for a run that never produced one of its own (the work directory could
    /// not be prepared, the process could not be started). Any non-zero maps to Failed server-side;
    /// the human-readable reason is in the worker log (FailureReason is F-009/F-010 territory).
    /// </summary>
    public const int InfrastructureFailureExitCode = 70;

    /// <summary>The temp log files the run's captured streams land in (F-008 will stream these upstream).</summary>
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
        //    lease expired, the run is not ours) gives the run up before anything is executed.
        if (!await ReportAsync(
                runId,
                "accepted",
                (token, cancellationToken) => api.AcceptAsync(token, runId, cancellationToken),
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

        // 3. Execute the argv as a native process, streaming each output stream into its temp log.
        var exitCode = await ExecuteProcessAsync(claim, work, shutdown);
        if (exitCode is null)
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
        await ReportCompletionAsync(runId, exitCode.Value, shutdown);
    }

    /// <summary>
    /// Starts the run's process and waits for its exit, streaming stdout/stderr lines into
    /// <c>temp/stdout.log</c> and <c>temp/stderr.log</c> as they are produced. Returns the exit
    /// code, or <see langword="null"/> when shutdown killed the run.
    /// </summary>
    private async Task<int?> ExecuteProcessAsync(
        WorkerClaimResponse claim,
        RunWorkDirectory work,
        CancellationToken shutdown)
    {
        if (claim.Command.Count == 0)
        {
            await errorLog.WriteLineAsync(
                $"Zenkins Worker: run {claim.RunId} was claimed with an empty command. "
                + $"Reporting exit code {InfrastructureFailureExitCode}.");
            return InfrastructureFailureExitCode;
        }

        // Fresh empty logs per execution (a retried claim of the same run must not append stale
        // output); AutoFlush so an observer sees lines as they are produced.
        Directory.CreateDirectory(work.TempPath);
        await using var stdOutWriter = new StreamWriter(
            Path.Combine(work.TempPath, StdOutLogFileName), append: false) { AutoFlush = true };
        await using var stdErrorWriter = new StreamWriter(
            Path.Combine(work.TempPath, StdErrorLogFileName), append: false) { AutoFlush = true };

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
                if (line.Stream == ProcessOutputStream.StdOut)
                {
                    stdOutWriter.WriteLine(line.Line);
                }
                else
                {
                    stdErrorWriter.WriteLine(line.Line);
                }
            });

        try
        {
            return await runner.RunAsync(shutdown);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            await log.WriteLineAsync(
                $"Zenkins Worker: run {claim.RunId} was killed by shutdown (whole process tree); no completed report.");
            return null;
        }
        catch (Exception failure)
        {
            // The process never produced an exit code (bad argv[0], unusable cwd, ...). Report the
            // run failed so it does not hang in Running forever; the reason is in the logs.
            await stdErrorWriter.WriteLineAsync(
                $"zenkins-worker: the process could not be executed: {failure.Message}");
            await errorLog.WriteLineAsync(
                $"Zenkins Worker: run {claim.RunId} could not be executed ({failure.Message}). "
                + $"Reporting exit code {InfrastructureFailureExitCode}.");
            return InfrastructureFailureExitCode;
        }
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
