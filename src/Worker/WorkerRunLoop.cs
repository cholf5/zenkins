using System.Net.Sockets;
using Zenkins.Shared.LogStreaming;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.RunCancellation;
using Zenkins.Worker.Git;
using Zenkins.Worker.Pipelines;

namespace Zenkins.Worker;

/// <summary>
/// One claim→execute→report loop of the worker (card F-007, consuming F-006's endpoints; card F-014
/// replaces the argv execution with a pipeline run):
/// claim (<c>POST /api/workers/claim</c>, 204 → wait <see cref="WorkerConfiguration.ClaimPollInterval"/>
/// and poll again; transport failures retry with the existing capped-exponential backoff) → report
/// <c>accepted</c> (clears the lease, answers the run's frozen deadline) → prepare the run's work
/// directory → clone the project's repository at the run's branch (F-013) → run the project's
/// <c>pipeline.lua</c> definition once under F-011's sandbox, validate it and report the resolved
/// commit and the frozen spec → execute the frozen steps in order with the existing
/// <see cref="ProcessRunner"/> - the first non-zero exit stops the run.
/// <para>
/// Every line a run produces (the pipeline's own step lines, the definition's <c>log()</c>, a step's
/// stdout/stderr) lands in the run's <c>temp/</c> logs (F-007's local files, kept through the
/// transition as a debugging aid) <b>and</b> upstream over SignalR (F-008): every line is emitted into
/// the run's bounded <see cref="RunLogBuffer"/> with a per-run monotonic Seq and a drain task ships
/// flush-sized batches to <c>/hubs/worker-logs</c> while the run executes → delete <c>temp/</c> when
/// configured (keeping <c>repository/</c> and <c>artifacts/</c>) → report
/// <c>completed {'{'}exitCode, failureReason{'}'}</c> (0 → Success, non-zero → Failed, a closed reason
/// for a run that failed before any process ran). Cleanup precedes the report, and the drain precedes
/// it too: when the server records the terminal state, the run's temp is already gone and its captured
/// lines are on their way to the database.
/// </para>
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
    /// copies; the server's incremental persistence is authoritative since F-008). The names live with the
    /// writers themselves (F-015), because they are that type's contract now.</summary>
    public const string StdOutLogFileName = RunLogWriters.StdOutLogFileName;
    public const string StdErrorLogFileName = RunLogWriters.StdErrorLogFileName;

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
        //    race-window semantic) gives the run up before anything is executed. F-014: the answer
        //    carries the run's frozen deadline, which bounds the definition phase.
        var deadline = await AcceptAsync(runId, shutdown);
        if (deadline is null)
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
            await ReportCompletionAsync(runId, InfrastructureFailureExitCode, failureReason: null, shutdown);
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
            var result = await ExecutePipelineAsync(
                claim,
                work,
                deadline.Value,
                shutdown,
                cancelScope.Token,
                cancelRequested.Task);

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

            if (result.ExitCode is null && result.FailureReason is null)
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

            // 5. Report the outcome: exit 0 → Success, a non-zero step exit → Failed with that code,
            //    and a run that failed before any process ran → Failed with its closed failure reason.
            await ReportCompletionAsync(runId, result.ExitCode, result.FailureReason, shutdown);
        }
        finally
        {
            cancelSubscription?.Dispose();
        }
    }

    /// <summary>How one execution ended: a real exit code, a closed failure reason, a server-requested
    /// cancellation (with the outcome to report), or nothing at all (a shutdown kill - F-010's
    /// business).</summary>
    private sealed record RunExecutionResult(
        int? ExitCode,
        string? FailureReason,
        string? CancelOutcome = null,
        string? CancelReason = null);

    /// <summary>
    /// Executes one claimed run as a pipeline (card F-014): F-013's clone at the run's branch, the
    /// definition phase under F-011's sandbox, the definition report (resolved commit + frozen spec),
    /// then the frozen steps in order with <see cref="ProcessRunner"/> - the first non-zero exit stops
    /// the run. Every line, the pipeline's own and the steps', flows into
    /// <c>temp/stdout.log</c>/<c>temp/stderr.log</c> (F-007's debugging aid) and upstream through
    /// <see cref="RunLogBuffer"/> (F-008).
    /// <para>
    /// The kill token is the linked shutdown + server-cancel pair, so a cancellation during the
    /// definition phase abandons the script at its next yield point and one during a step kills that
    /// step's process tree - both end through F-009's canceled report, exactly like the pre-pipeline
    /// path did.
    /// </para>
    /// </summary>
    private async Task<RunExecutionResult> ExecutePipelineAsync(
        WorkerClaimResponse claim,
        RunWorkDirectory work,
        DateTimeOffset deadline,
        CancellationToken shutdown,
        CancellationToken cancelKill,
        Task<RunCancelRequest> cancelRequested)
    {
        // The run's local log files: fresh per execution (a retried claim of the same run must not append
        // stale output), written through the type's own gate because three threads feed them (card F-015).
        using var writers = new RunLogWriters(work.TempPath);

        // F-008: every line flows upstream with a per-run monotonic Seq while a drain task ships
        // batches to the server; the temp files stay (transition-period debugging aid). The
        // connectivity probe lets the buffer label overflow drops honestly (review fix F4):
        // drops while connected are backpressure, not a reconnect gap.
        var logBuffer = new RunLogBuffer(
            claim.RunId, configuration.LogBufferLines, errorLog, isConnected: () => logStreams.IsConnected);
        var pump = logBuffer.DrainAsync(logStreams, shutdown);

        // The run's redaction filter (F-013's Git credential plus F-015's secrets). Every line the run emits
        // leaves through it - the pipeline layer's own synthesized lines, the definition phase's print/log,
        // and a child's stdout/stderr - and every value decrypted for this run is registered with it the
        // instant it is known. It is log hygiene, not a boundary (docs/lua-runtime.md §7).
        var redactor = new GitLogFilter();

        // The step whose segment the next line belongs to (card F-015). A holder rather than a captured
        // Guid?: the two output readers run on their own threads, so the value is published as one atomic
        // reference that a reader can never observe half-written.
        var currentStep = new StepStampHolder();

        // The kill token: shutdown OR the server's cancel request for this run. ProcessRunner
        // kills the whole tree on cancellation, and the Lua driver abandons the script.
        using var killTokens = CancellationTokenSource.CreateLinkedTokenSource(shutdown, cancelKill);

        void Emit(string line)
        {
            var redacted = redactor.Redact(line);
            writers.Write(LogStream.StdOut, redacted);
            logBuffer.Emit(LogStream.StdOut, redacted, currentStep.Current);
        }

        void OnOutput(ProcessOutputLine line)
        {
            var redacted = redactor.Redact(line.Line);
            var stream = line.Stream == ProcessOutputStream.StdOut ? LogStream.StdOut : LogStream.StdErr;
            writers.Write(stream, redacted);
            logBuffer.Emit(stream, redacted, currentStep.Current);
        }

        // The pipeline layer's own dependencies: the run's log sink (the prelude's print/log), F-013's
        // git layer (credential per run, clone into repository/) and the definition report.
        // D-006: one capture per run's definition phase - the runtime records the secret() calls into it and
        // the runner drains it into the frozen spec, so nothing leaks into the next run.
        var secretReferences = new SecretReferenceCapture();
        var definitions = new PipelineDefinitionRunner(
            PipelineDefinitionRunner.CreateRuntime(PipelineRunner.CreateLogSink(Emit), secretReferences),
            secretReferences);
        var runner = new PipelineRunner(
            new PipelineRunOptions
            {
                Work = work,
                RepositoryUrl = claim.RepositoryUrl,
                Branch = claim.Branch,
                PipelinePath = claim.PipelinePath,

                // F-017 (review M1): a webhook run names the commit to build; a manual run does not.
                ExpectedCommit = claim.Commit,
                Deadline = deadline,
                GitDepth = configuration.GitDepth,
                KnownHostsPath = configuration.GitKnownHostsPath,
                GitOperationTimeout = configuration.GitOperationTimeout
                    ?? WorkerConfiguration.DefaultGitOperationTimeout,
                StepEnvironment = new Dictionary<string, string?>
                {
                    ["ZENKINS_RUN_ID"] = claim.RunId.ToString(),
                    ["ZENKINS_WORKER_NAME"] = configuration.WorkerName,
                    ["ZENKINS_PROJECT_ID"] = claim.ProjectId.ToString(),
                },
                Log = Emit,
                OnProcessOutput = OnOutput,

                // F-015: the run's redaction filter, the per-run secret cache (a fetch per name at most),
                // the server-side step sink and the step announcement that stamps the log per step.
                Redactor = redactor,
                Secrets = new RunSecretCache(new ServerSecretSource(api, workerToken, claim.RunId)),
                StepSink = new ServerStepSink(api, workerToken, claim.RunId, backoff, errorLog),
                OnStepChanged = currentStep.Set,

                // F-015: the killed step records why it died, through the one shared predicate (so a test that
                // simulates the same cancel cannot drift from this decision).
                CancellationFailureReason = () => StepCancellationReason.For(
                    cancelRequested.IsCompletedSuccessfully ? cancelRequested.Result.Reason : null),

                // F-016: the artifact publisher for this run. The source root is the checkout only, the caps
                // come from the same configuration keys the server reads (a console app reads them as
                // Artifacts__MaxFileBytes), and the run's kill token bounds each upload.
                Artifacts = new Zenkins.Worker.Artifacts.ArtifactPublisher(
                    new Zenkins.Worker.Artifacts.ArtifactUploadClient(api, workerToken),
                    claim.RunId,
                    work.RepositoryPath,
                    work.RootPath,
                    Zenkins.Shared.Artifacts.ArtifactLimits.FromConfiguration(
                        key => Environment.GetEnvironmentVariable(key.Replace(":", "__", StringComparison.Ordinal))),
                    Emit),
            },
            definitions,
            cancellationToken => api.FetchGitCredentialAsync(workerToken, claim.RunId, cancellationToken),
            (commit, specJson, cancellationToken) =>
                ReportDefinitionAsync(claim.RunId, commit, specJson, shutdown));

        PipelineRunOutcome outcome;
        try
        {
            outcome = await runner.RunAsync(killTokens.Token);
        }
        catch (Exception failure)
        {
            // A bug in the pipeline layer must not leave the run Running forever: report it as a failed step and
            // keep the message in the run log (the server-side log shows why). Even a bug's message is a line of
            // this run's log, so it goes through the filter and the writer gate like every other line (card
            // F-015) - the type owns that composition, and its test pins it.
            // The reporter owns the whole composition: filter, writer gate, streaming buffer and the worker's own
            // console channel (which receives the same, already-redacted text).
            new RunCrashReporter(
                    claim.RunId,
                    redactor,
                    writers,
                    logBuffer,
                    () => currentStep.Current,
                    errorLog)
                .Report(failure);
            logBuffer.CompleteInput();
            if (logStreams.HasEverConnected)
            {
                await pump;
            }

            return new RunExecutionResult(null, PipelineFailureReasons.StepFailed);
        }

        if (outcome.Abandoned)
        {
            // The server refused a report: the run is already terminal there (canceled, Lost, or
            // another worker's). Nothing ran and nothing is reported - but the run's own log must still
            // be handed upstream, so the drain happens exactly as on every other path.
            await log.WriteLineAsync(
                $"Zenkins Worker: run {claim.RunId} was given up (the server refused a report); "
                + "no further execution and no completion report.");
            logBuffer.CompleteInput();
            if (logStreams.HasEverConnected)
            {
                await pump;
            }

            return new RunExecutionResult(null, null);
        }

        if (outcome.Canceled)
        {
            logBuffer.CompleteInput();
            if (cancelKill.IsCancellationRequested && !shutdown.IsCancellationRequested)
            {
                // A server cancel request (user cancel or deadline): the process tree/script is gone,
                // the partial output drains upstream, and the server maps the outcome (timeout →
                // Failed, otherwise Canceled).
                var request = await cancelRequested;
                await log.WriteLineAsync(
                    $"Zenkins Worker: run {claim.RunId} received the server's cancel request "
                    + $"(reason: {request.Reason ?? "canceled"}); execution stopped.");
                if (logStreams.HasEverConnected)
                {
                    await pump;
                }

                var cancelOutcome = RunCancelReasons.IsTimeout(request.Reason)
                    ? RunCanceledOutcomes.Timeout
                    : RunCanceledOutcomes.Canceled;
                return new RunExecutionResult(null, null, cancelOutcome, request.Reason);
            }

            await log.WriteLineAsync(
                $"Zenkins Worker: run {claim.RunId} was stopped by shutdown; no completed report.");
            await pump;
            return new RunExecutionResult(null, null);
        }

        logBuffer.CompleteInput();
        if (logStreams.HasEverConnected)
        {
            await pump;
        }

        return new RunExecutionResult(outcome.ExitCode, outcome.FailureReason);
    }

    // -------------------------------------------------------------------
    // Reporting (accepted / definition / completed) with transport retries
    // -------------------------------------------------------------------

    /// <summary>
    /// Reports the run accepted and returns its frozen deadline (F-009's <c>DeadlineAt</c>, computed
    /// server-side at the accept edge). <see langword="null"/> means the report was rejected or
    /// shutdown arrived - the caller must give the run up.
    /// </summary>
    private async Task<DateTimeOffset?> AcceptAsync(Guid runId, CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                // F-013: the commit is reported with the definition report below - the clone happens
                // after the accept, so nothing has been resolved yet at this point.
                var response = await api.AcceptAsync(workerToken, runId, gitCommit: null, shutdown);
                return response.DeadlineAt ?? DateTimeOffset.MaxValue;
            }
            catch (WorkerApiRejectedException rejection)
            {
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: reporting run {runId} accepted was rejected ({rejection.Message}) "
                    + "- giving the run up; the server owns its state.");
                return null;
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception failure) when (IsTransient(failure) || failure is OperationCanceledException)
            {
                var delay = backoff.NextDelay();
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: could not reach the server to report run {runId} accepted "
                    + $"({Describe(failure)}). Retrying in {delay.TotalSeconds:0.#}s.");
                if (!await WaitAsync(delay, shutdown))
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Reports the definition (card F-014): the resolved commit and the frozen spec, before the first
    /// step starts. A rejection is logged and the run is given up server-side anyway; a transport
    /// failure retries with backoff so a brief outage does not lose a validated definition.
    /// </summary>
    /// <returns><see langword="true"/> when the server accepted the report, <see langword="false"/>
    /// when it refused definitively (the caller then gives the run up without executing anything).</returns>
    private Task<bool> ReportDefinitionAsync(
        Guid runId,
        string? gitCommit,
        string? pipelineSpecJson,
        CancellationToken shutdown) =>
        ReportAsync(
            runId,
            pipelineSpecJson is null ? "commit" : "definition",
            (token, cancellationToken) => api.ReportDefinitionAsync(
                token, runId, gitCommit, pipelineSpecJson, cancellationToken),
            shutdown);

    private Task ReportCompletionAsync(
        Guid runId,
        int? exitCode,
        string? failureReason,
        CancellationToken shutdown) =>
        ReportAsync(
            runId,
            "completed",
            (token, cancellationToken) => api.CompleteAsync(
                token, runId, exitCode, failureReason, cancellationToken),
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

    /// <summary>
    /// The current step's id, read by the log writers. The value is published as one atomic reference so a
    /// reader thread never observes a torn value: a <see cref="Guid"/> is sixteen bytes, which is not an
    /// atomic write on every platform.
    /// </summary>
    private sealed class StepStampHolder
    {
        private volatile object? _value;

        /// <summary>The current step, or null between steps and during the definition phase.</summary>
        public Guid? Current => _value is Guid id ? id : null;

        /// <summary>Announces the step whose lines follow (null ends the segment).</summary>
        public void Set(Guid? id) => _value = id;
    }

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
