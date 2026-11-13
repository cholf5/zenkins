using Zenkins.Shared.Pipelines;

namespace Zenkins.Worker.Pipelines;

/// <summary>
/// The production step sink (card F-015): one HTTP report per transition against
/// <c>POST /api/workers/runs/{runId}/steps</c>, with the same transient/definitive split every other
/// worker report uses.
/// <para>
/// A transport failure or a throttling answer is retried with the shared backoff until the shutdown token
/// fires (a lost transition is worth retrying: the step's record is part of the run's history, and the server
/// answers a re-delivery idempotently). A definitive 4xx - the run is not this worker's any more, it already
/// ended, or a position arrived out of order - is <see cref="PipelineStepReceipt.Refused"/>: the executor
/// gives the run up rather than executing steps nobody owns.
/// </para>
/// </summary>
public sealed class ServerStepSink(
    WorkerApiClient api,
    string workerToken,
    Guid runId,
    BackoffScheduler backoff,
    TextWriter errorLog) : IPipelineStepSink
{
    /// <inheritdoc />
    public async Task<PipelineStepReceipt> ReportAsync(
        PipelineStepReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var response = await api.ReportStepAsync(
                    workerToken,
                    runId,
                    new WorkerStepRequest(
                        report.Index,
                        report.Name,
                        report.Status,
                        report.ExitCode,
                        report.FailureReason),
                    cancellationToken);
                return PipelineStepReceipt.Recorded(response.StepId);
            }
            catch (WorkerApiRejectedException rejection)
            {
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: reporting step {report.Index} ({report.Status}) of run {runId} was "
                    + $"rejected ({rejection.Message}) - giving the run up; the server owns its state.");
                return PipelineStepReceipt.Refused();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return PipelineStepReceipt.Refused();
            }
            catch (Exception failure) when (IsTransient(failure))
            {
                // Transport failure or a request timeout: retry with backoff. A report that actually landed
                // is answered idempotently on the retry, so a duplicate is harmless.
                var delay = backoff.NextDelay();
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: could not reach the server to report step {report.Index} of run "
                    + $"{runId} ({failure.Message}). Retrying in {delay.TotalSeconds:0.#}s.");
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return PipelineStepReceipt.Refused();
                }
            }
            catch (Exception failure)
            {
                // An answer this worker cannot read (a protocol mismatch) is not something a retry can fix:
                // give the run up instead of spinning on it forever.
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: the step {report.Index} report of run {runId} could not be understood "
                    + $"({failure.GetType().Name}: {failure.Message}) - giving the run up.");
                return PipelineStepReceipt.Refused();
            }
        }

        return PipelineStepReceipt.Refused();
    }

    /// <summary>The failures whose retry can succeed (transport, timeouts, hung requests).</summary>
    private static bool IsTransient(Exception failure) =>
        failure is HttpRequestException or System.Net.Sockets.SocketException or OperationCanceledException;
}

/// <summary>
/// The production secret source (card F-015): one run-scoped request per name, answered by the server for the
/// worker executing the run. The value is cached by <see cref="RunSecretCache"/> (per run, in memory) - this
/// class never keeps one, and never writes one anywhere.
/// <para>
/// A 404 is <see cref="RunSecretLookup.NotFound"/> - the name does not exist in this organization, or the run
/// is not this worker's; the resolver turns that into a failed step that names the name. Anything else that
/// fails (a dead token, transport) throws, and the resolver reports the same failed step with the reason
/// attached: the run log says what happened, never a value.
/// </para>
/// </summary>
public sealed class ServerSecretSource(
    WorkerApiClient api,
    string workerToken,
    Guid runId) : IRunSecretSource
{
    /// <inheritdoc />
    public async Task<RunSecretResult> FetchAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var response = await api.FetchRunSecretAsync(workerToken, runId, name, cancellationToken);
        return response is null
            ? RunSecretResult.NotFound()
            : RunSecretResult.Found(response.Value);
    }
}
