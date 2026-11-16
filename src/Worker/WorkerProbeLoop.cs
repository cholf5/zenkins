using System.Net.Sockets;
using Zenkins.Shared.Probes;
using Zenkins.Worker.Git;

namespace Zenkins.Worker;

/// <summary>
/// The worker's Git remote probe loop (card I-005): claim one probe, run <c>git ls-remote</c> with the
/// project's delivered credential, report the heads or the typed failure.
/// <para>
/// <b>Its own loop, its own concurrency.</b> A probe occupies no build slot and is not blocked by one:
/// the worker runs <see cref="WorkerConfiguration.ProbeConcurrency"/> instances of this loop beside the
/// <see cref="WorkerConfiguration.MaxConcurrency"/> build loops, so a worker whose every build slot is
/// busy still answers "what heads does this repository have right now?". Nothing here knows about runs,
/// work directories of runs, log streaming or artifacts - a probe produces no output stream and no
/// history.
/// </para>
/// <para>
/// <b>A definitive answer ends a probe; a transient one is retried inside the lease.</b> A 401 on claim
/// backs off (a restored credential picks work up without a restart); a 404 on claim means the Server
/// predates this card, so this loop stops for the process's lifetime after one line. A refused report
/// (the lease lapsed, the probe was settled) is definitive and the probe is given up - the Server owns
/// its state.
/// </para>
/// </summary>
public sealed class WorkerProbeLoop(
    WorkerConfiguration configuration,
    WorkerApiClient api,
    string workerToken,
    WorkDirectoryService workDirectories,
    BackoffScheduler backoff,
    TextWriter log,
    TextWriter errorLog)
{
    /// <summary>
    /// Claims and answers probes until shutdown. One iteration is one claim → probe → report cycle; a
    /// finished probe is followed immediately by the next claim.
    /// </summary>
    public async Task RunAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                var probe = await ClaimAsync(shutdown);
                if (probe is null)
                {
                    // Nothing to probe (or shutdown): wait the claim interval before asking again.
                    if (!await WaitAsync(configuration.ClaimPollInterval, shutdown))
                    {
                        break;
                    }

                    continue;
                }

                await ExecuteAsync(probe, shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (ProbeLoopCompletedException)
            {
                // The Server has no probe API (a pre-I-005 build): this loop is permanently idle.
                break;
            }
            catch (Exception failure)
            {
                var delay = backoff.NextDelay();
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: unexpected failure in the probe loop ({failure.GetType().Name}: {failure.Message}). "
                    + $"Retrying in {delay.TotalSeconds:0.#}s.");
                if (!await WaitAsync(delay, shutdown))
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// One claim attempt. 204 → <see langword="null"/>; transport failures and a dead token back off and
    /// retry (like the run claim); a 404 is the "this Server has no probes" answer and stops the loop.
    /// </summary>
    private async Task<WorkerProbeClaimResponse?> ClaimAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                var claimed = await api.ClaimProbeAsync(workerToken, shutdown);
                backoff.Reset();
                return claimed;
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return null;
            }
            catch (WorkerApiRejectedException rejection) when (rejection.StatusCode == 404)
            {
                // The route does not exist: an older Server. Probing is an addition, never a requirement,
                // so this worker keeps building runs and stops asking.
                await log.WriteLineAsync(
                    "Zenkins Worker: the server does not offer the Git probe API (404); "
                    + "this worker will not answer probes until it is pointed at a newer server.");
                throw new ProbeLoopCompletedException();
            }
            catch (Exception failure) when (IsTransient(failure) || failure is OperationCanceledException
                || failure is WorkerApiRejectedException)
            {
                var delay = backoff.NextDelay();
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: claiming a Git probe failed ({Describe(failure)}). "
                    + $"Retrying in {delay.TotalSeconds:0.#}s.");
                if (!await WaitAsync(delay, shutdown))
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>One claimed probe: credential, <c>git ls-remote</c>, report, then delete the scratch tree.</summary>
    private async Task ExecuteAsync(WorkerProbeClaimResponse probe, CancellationToken shutdown)
    {
        var work = workDirectories.PrepareProbe(probe.ProbeId);
        try
        {
            await log.WriteLineAsync(
                $"Zenkins Worker: probing {probe.RepositoryUrl} for probe {probe.ProbeId} "
                + $"(project {probe.ProjectId}).");

            GitRefsProbeResult result;
            try
            {
                // The material is fetched per probe, under this probe's lease: nothing is cached on disk
                // and nothing from a previous probe can be replayed.
                var credential = await api.FetchProbeGitCredentialAsync(workerToken, probe.ProbeId, shutdown);
                var runner = new GitRefsProbe(configuration.BuildProbeOptions(work));
                result = await runner.ProbeAsync(probe.RepositoryUrl, credential, shutdown);
            }
            catch (GitException failure)
            {
                // The credential fetch itself failed: the probe could not run, and that is not the
                // remote's answer. Reported as the generic failure with the reason, never as "no heads".
                result = GitRefsProbeResult.Failure(GitProbeOutcomes.Failed, failure.Message);
            }

            if (shutdown.IsCancellationRequested)
            {
                // Shutting down mid-probe: no report. The lease lapses and the Server re-offers the probe
                // to another worker - a lost probe is re-requested, never replayed.
                return;
            }

            await ReportAsync(probe, result, shutdown);
        }
        finally
        {
            try
            {
                workDirectories.CleanupProbe(work);
            }
            catch (Exception failure)
            {
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: probe {probe.ProbeId} scratch cleanup failed ({failure.Message}); "
                    + "leaving it in place.");
            }
        }
    }

    /// <summary>
    /// Reports the answer, retrying transport failures until the lease would lapse. A refused report is
    /// definitive: the Server owns the probe's state and another attempt cannot change its mind.
    /// </summary>
    private async Task ReportAsync(
        WorkerProbeClaimResponse probe,
        GitRefsProbeResult result,
        CancellationToken shutdown)
    {
        var request = new WorkerProbeReportRequest(
            result.Outcome,
            result.Heads.Length == 0 ? null : result.Heads,
            result.HeadSha,
            result.HeadBranch,
            result.Message);

        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                await api.ReportProbeAsync(workerToken, probe.ProbeId, request, shutdown);
                await log.WriteLineAsync(
                    $"Zenkins Worker: probe {probe.ProbeId} reported as '{result.Outcome}' "
                    + $"({result.Heads.Length} head(s)).");
                return;
            }
            catch (WorkerApiRejectedException rejection)
            {
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: reporting probe {probe.ProbeId} was rejected ({rejection.Message}) "
                    + "- giving the probe up; the server owns its state.");
                return;
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception failure) when (IsTransient(failure) || failure is OperationCanceledException)
            {
                var delay = backoff.NextDelay();
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: could not reach the server to report probe {probe.ProbeId} "
                    + $"({Describe(failure)}). Retrying in {delay.TotalSeconds:0.#}s.");
                if (!await WaitAsync(delay, shutdown))
                {
                    return;
                }
            }
        }
    }

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

    private static bool IsTransient(Exception failure) =>
        failure is HttpRequestException or SocketException;

    private static string Describe(Exception failure) => failure switch
    {
        HttpRequestException request => request.StatusCode is { } status
            ? $"the server answered {(int)status} {status}."
            : $"connection problem ({request.InnerException?.Message ?? "transport failure"}).",
        SocketException socket => $"connection problem ({socket.SocketErrorCode}).",
        OperationCanceledException => "the server did not answer in time (request timeout).",
        WorkerApiRejectedException rejection => rejection.Message,
        _ => failure.Message,
    };

    /// <summary>
    /// The one control-flow exception: the probe API does not exist on this Server, so the loop is done for
    /// the process's lifetime. Kept private to the loop - nothing outside it reacts to this state.
    /// </summary>
    private sealed class ProbeLoopCompletedException : Exception;
}
