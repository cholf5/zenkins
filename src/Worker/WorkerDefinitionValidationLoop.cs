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
    WorkerCredential credential,
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
                var claimed = await api.ClaimDefinitionValidationAsync(credential.Current, shutdown);
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

    /// <summary>
    /// One claimed definition task: describe the subject, report, delete the scratch tree. The kind decides
    /// the subject - an inline script (F-022) or one repository branch at one commit (F-023's observation,
    /// which clones and answers whether the project's pipeline file is there).
    /// </summary>
    private async Task ExecuteAsync(WorkerDefinitionValidationClaimResponse validation, CancellationToken shutdown)
    {
        var work = workDirectories.PrepareValidation(validation.ValidationId);
        try
        {
            var report = DefinitionValidationKinds.Normalize(validation.Kind)
                == DefinitionValidationKinds.RepositoryBranch
                    ? await ObserveRepositoryAsync(validation, work, shutdown)
                    : await DescribeInlineScriptAsync(validation, work, shutdown);

            if (report is null || shutdown.IsCancellationRequested)
            {
                // Shutting down mid-task: report nothing. The lease lapses and the Server offers it
                // again - a lost task is re-requested, never replayed.
                return;
            }

            await api.ReportDefinitionValidationAsync(credential.Current, validation.ValidationId, report, shutdown);
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

    /// <summary>F-022's inline script: write it to the scratch tree and run the definition phase alone.</summary>
    private async Task<WorkerDefinitionValidationReportRequest> DescribeInlineScriptAsync(
        WorkerDefinitionValidationClaimResponse validation,
        ValidationWorkDirectory work,
        CancellationToken shutdown)
    {
        await log.WriteLineAsync(
            $"Zenkins Worker: validating an inline pipeline script for validation "
            + $"{validation.ValidationId} (no run, no build slot).");

        if (validation.Script is null)
        {
            // A claim that says inline-script but carries no text is a contract mismatch; answer it as an
            // unreadable definition rather than compiling an empty pipeline.
            return new WorkerDefinitionValidationReportRequest(
                DefinitionValidationOutcomes.Invalid,
                Diagnostic: "the claim named an inline script but carried no text.");
        }

        var scriptPath = Path.Combine(work.TempPath, PipelineScriptLimits.InlineFileName);
        await File.WriteAllTextAsync(
            scriptPath,
            validation.Script,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            shutdown);

        var runner = new Pipelines.PipelineDefinitionRunner(
            Pipelines.PipelineDefinitionRunner.CreateRuntime(
                Pipelines.PipelineRunner.CreateLogSink(line => log.WriteLine("Zenkins Worker: " + line))));

        try
        {
            var result = runner.RunSource(
                await File.ReadAllTextAsync(scriptPath, shutdown),
                PipelineScriptLimits.InlineFileName,
                DateTimeOffset.UtcNow + ValidationBudget,
                shutdown);
            return new WorkerDefinitionValidationReportRequest(
                DefinitionValidationOutcomes.Valid,
                result.SpecJson);
        }
        catch (Pipelines.PipelineDefinitionException failure)
        {
            return new WorkerDefinitionValidationReportRequest(
                DefinitionValidationOutcomes.Invalid,
                Diagnostic: failure.Message,
                FailureReason: failure.FailureReason);
        }
    }

    /// <summary>
    /// Card F-023's branch observation: clone the repository, pin the requested commit, and answer whether
    /// the project's pipeline file is in that tree. <b>No run, no build slot, no step</b> - the Worker's
    /// definition loop does this beside (never inside) the build loops, exactly as it does for F-022.
    /// <para>
    /// <b>A failed read is not a verdict.</b> A clone/checkout failure reports <c>unreadable</c> with the
    /// classified git reason; the Server keeps the branch's previous state and retries later. Only a tree
    /// this method really checked out can produce <c>has-pipeline</c>/<c>no-pipeline</c>, and the commit
    /// those words are about is the claim's - the Server stores it as the evidence commit.
    /// </para>
    /// <para>
    /// The pipeline file is checked for <b>existence</b>, not for validity: a present-but-broken
    /// <c>pipeline.lua</c> is still a branch that builds (and then fails honestly in the run), which is
    /// the distinction AC-3 draws between "no pipeline file" and a failed build.
    /// </para>
    /// </summary>
    private async Task<WorkerDefinitionValidationReportRequest?> ObserveRepositoryAsync(
        WorkerDefinitionValidationClaimResponse validation,
        ValidationWorkDirectory work,
        CancellationToken shutdown)
    {
        await log.WriteLineAsync(
            $"Zenkins Worker: observing branch '{validation.Branch}' at {validation.Commit} for validation "
            + $"{validation.ValidationId} (no run, no build slot).");

        if (validation.RepositoryUrl is not { Length: > 0 } repositoryUrl
            || validation.Branch is not { Length: > 0 } branch
            || validation.Commit is not { Length: > 0 } commit)
        {
            return new WorkerDefinitionValidationReportRequest(
                DefinitionValidationOutcomes.Unreadable,
                Diagnostic: "the claim carried no repository, branch or commit to observe.",
                FailureReason: PipelineFailureReasons.GitFailed);
        }

        var pipelinePath = validation.PipelinePath is { Length: > 0 } declared
            ? declared
            : PipelineScriptLimits.InlineFileName;

        // The observation borrows a run-shaped scratch tree for the shared clone primitive (repository/,
        // temp/, hardening/); it is not a run and nothing it writes is reported as one.
        var repositoryPath = Path.Combine(work.RootPath, "repository");
        Directory.CreateDirectory(repositoryPath);
        var scratch = new RunWorkDirectory(
            validation.ValidationId,
            work.RootPath,
            repositoryPath,
            Path.Combine(work.RootPath, "artifacts"),
            work.TempPath);

        var credentialDelivered = false;
        try
        {
            var gitCredential = await api.FetchDefinitionGitCredentialAsync(
                credential.Current, validation.ValidationId, shutdown);
            credentialDelivered = gitCredential is not null;

            var workspace = new Git.GitWorkspace(configuration.BuildGitWorkspaceOptions(
                scratch,
                log: line => log.WriteLine("Zenkins Worker: " + line)));
            await workspace.CloneAsync(repositoryUrl, branch, gitCredential, shutdown);
            await workspace.CheckoutAsync(commit, shutdown);

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath))
                + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(repositoryPath, pipelinePath));
            if (!candidate.StartsWith(root, StringComparison.Ordinal))
            {
                return new WorkerDefinitionValidationReportRequest(
                    DefinitionValidationOutcomes.Unreadable,
                    Diagnostic: $"the pipeline path '{pipelinePath}' leaves the checkout.",
                    FailureReason: PipelineFailureReasons.GitPathEscape);
            }

            var found = File.Exists(candidate);
            await log.WriteLineAsync(
                $"Zenkins Worker: branch '{branch}' at {commit} "
                + (found
                    ? $"contains '{pipelinePath}'."
                    : $"has no '{pipelinePath}' - the branch has no pipeline."));
            return new WorkerDefinitionValidationReportRequest(
                found
                    ? DefinitionValidationOutcomes.HasPipeline
                    : DefinitionValidationOutcomes.NoPipeline);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Git.GitException failure)
        {
            await log.WriteLineAsync($"Zenkins Worker: the branch could not be read: {failure.Message}");
            return new WorkerDefinitionValidationReportRequest(
                DefinitionValidationOutcomes.Unreadable,
                Diagnostic: failure.Message,
                FailureReason: Pipelines.PipelineRunner.Classify(failure.Kind, credentialDelivered));
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
