using Zenkins.Shared.Pipelines;

namespace Zenkins.Worker;

/// <summary>
/// The worker's definition-only validation loop (card F-022): claim one validation, run its
/// <b>definition phase</b> against a scratch <c>pipeline.lua</c>, report the spec or the diagnostic.
/// <para>
/// <b>Not a build.</b> This loop runs beside the <see cref="WorkerConfiguration.MaxConcurrency"/> build
/// loops and takes no build slot: the definition phase is pure Lua (no clone, no step, no artifact), so
/// a worker whose every slot is busy can still answer "does this script describe a pipeline?" without
/// waiting for a run to finish. It also writes no run work directory - <c>validations/{id}/temp</c> is
/// deleted as soon as the answer is reported.
/// </para>
/// <para>
/// <b>A refusal is the answer, not a crash.</b> A Lua syntax or runtime error is reported as
/// <c>invalid</c> with the definition phase's own positioned message
/// (<c>pipeline.lua:(line,col)</c>), which is the same message a repository run would carry. A
/// definitive 4xx on claim/report ends that one attempt; a 404 on claim means this Server predates the
/// card, so the loop stops for the process's lifetime (the probe loop's precedent).
/// </para>
/// </summary>
public sealed class WorkerDefinitionValidationLoop(
    WorkerConfiguration configuration,
    WorkerApiClient api,
    string workerToken,
    WorkDirectoryService workDirectories,
    BackoffScheduler backoff,
    TextWriter log,
    TextWriter errorLog)
{
    /// <summary>
    /// The budget of one validation's definition phase. A validation has no run deadline to inherit
    /// (there is no run), so it gets a bounded one of its own: a script stuck in a loop is abandoned and
    /// reported invalid rather than holding the loop forever.
    /// </summary>
    public static readonly TimeSpan ValidationBudget = TimeSpan.FromMinutes(5);

    /// <summary>Claims and answers validations until shutdown.</summary>
    public async Task RunAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                var validation = await ClaimAsync(shutdown);
                if (validation is null)
                {
                    if (!await WaitAsync(configuration.ClaimPollInterval, shutdown))
                    {
                        break;
                    }

                    continue;
                }

                await ExecuteAsync(validation, shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (DefinitionValidationLoopCompletedException)
            {
                // This Server has no validation API (a pre-F-022 build): the loop stays idle.
                break;
            }
            catch (Exception failure)
            {
                var delay = backoff.NextDelay();
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: unexpected failure in the definition validation loop "
                    + $"({failure.GetType().Name}: {failure.Message}). Retrying in {delay.TotalSeconds:0.#}s.");
                if (!await WaitAsync(delay, shutdown))
                {
                    break;
                }
            }
        }
    }

    private async Task<WorkerDefinitionValidationClaimResponse?> ClaimAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                var claimed = await api.ClaimDefinitionValidationAsync(workerToken, shutdown);
                backoff.Reset();
                return claimed;
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return null;
            }
            catch (WorkerApiRejectedException rejection) when (rejection.StatusCode == 404)
            {
                await log.WriteLineAsync(
                    "Zenkins Worker: the server does not offer the inline definition validation API (404); "
                    + "this worker will not answer validations until it is pointed at a newer server.");
                throw new DefinitionValidationLoopCompletedException();
            }
            catch (Exception failure) when (failure is WorkerApiRejectedException
                || failure is HttpRequestException
                || failure is OperationCanceledException)
            {
                var delay = backoff.NextDelay();
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: claiming a definition validation failed ({failure.Message}). "
                    + $"Retrying in {delay.TotalSeconds:0.#}s.");
                if (!await WaitAsync(delay, shutdown))
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>One claimed validation: describe the script, report, delete the scratch tree.</summary>
    private async Task ExecuteAsync(WorkerDefinitionValidationClaimResponse validation, CancellationToken shutdown)
    {
        var work = workDirectories.PrepareValidation(validation.ValidationId);
        try
        {
            await log.WriteLineAsync(
                $"Zenkins Worker: validating an inline pipeline script for validation "
                + $"{validation.ValidationId} (no run, no build slot).");

            var scriptPath = Path.Combine(work.TempPath, PipelineScriptLimits.InlineFileName);
            await File.WriteAllTextAsync(
                scriptPath,
                validation.Script,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                shutdown);

            var runner = new Pipelines.PipelineDefinitionRunner(
                Pipelines.PipelineDefinitionRunner.CreateRuntime(
                    Pipelines.PipelineRunner.CreateLogSink(line => log.WriteLine("Zenkins Worker: " + line))));

            WorkerDefinitionValidationReportRequest report;
            try
            {
                var result = runner.RunSource(
                    await File.ReadAllTextAsync(scriptPath, shutdown),
                    PipelineScriptLimits.InlineFileName,
                    DateTimeOffset.UtcNow + ValidationBudget,
                    shutdown);
                report = new WorkerDefinitionValidationReportRequest(
                    DefinitionValidationOutcomes.Valid,
                    result.SpecJson);
            }
            catch (Pipelines.PipelineDefinitionException failure)
            {
                report = new WorkerDefinitionValidationReportRequest(
                    DefinitionValidationOutcomes.Invalid,
                    Diagnostic: failure.Message,
                    FailureReason: failure.FailureReason);
            }

            if (shutdown.IsCancellationRequested)
            {
                // Shutting down mid-validation: report nothing. The lease lapses and the Server offers
                // it again - a lost validation is re-requested, never replayed.
                return;
            }

            await api.ReportDefinitionValidationAsync(workerToken, validation.ValidationId, report, shutdown);
        }
        finally
        {
            try
            {
                workDirectories.CleanupValidation(work);
            }
            catch (Exception failure)
            {
                await errorLog.WriteLineAsync(
                    $"Zenkins Worker: validation {validation.ValidationId} scratch cleanup failed "
                    + $"({failure.Message}); leaving it in place.");
            }
        }
    }

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

    private sealed class DefinitionValidationLoopCompletedException : Exception;
}
