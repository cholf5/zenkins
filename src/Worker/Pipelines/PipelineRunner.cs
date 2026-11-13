using System.Diagnostics;
using System.Globalization;
using Zenkins.Shared.Pipelines;
using Zenkins.Worker.Artifacts;
using Zenkins.Worker.Git;
using Zenkins.Worker.Lua;

namespace Zenkins.Worker.Pipelines;

/// <summary>
/// Everything the pipeline layer needs about one run (card F-014, extended by F-015). The Worker's run
/// loop fills this from the claim the server handed it and from the accept report's frozen deadline.
/// </summary>
public sealed class PipelineRunOptions
{
    /// <summary>The run's directory trio: the checkout lives in <c>repository/</c>.</summary>
    public required RunWorkDirectory Work { get; init; }

    /// <summary>The project's repository URL, from the claim payload.</summary>
    public required string RepositoryUrl { get; init; }

    /// <summary>The branch this run clones: the trigger's branch, or the project's default.</summary>
    public required string Branch { get; init; }

    /// <summary>The project's pipeline path, snapshotted on the run at trigger time.</summary>
    public required string PipelinePath { get; init; }

    /// <summary>
    /// The run's deadline, frozen by the server at accept time (F-009). It bounds the definition
    /// phase; the deadline sweeper owns the executor's steps.
    /// </summary>
    public required DateTimeOffset Deadline { get; init; }

    /// <summary>argv[0] of every git invocation; <c>git</c> from <c>PATH</c> in production.</summary>
    public string GitExecutable { get; init; } = "git";

    /// <summary><c>ZENKINS_GIT_DEPTH</c>; <c>0</c> is a full clone.</summary>
    public int GitDepth { get; init; }

    /// <summary><c>ZENKINS_GIT_KNOWN_HOSTS</c>; <see langword="null"/> means accept-new per run.</summary>
    public string? KnownHostsPath { get; init; }

    /// <summary><c>ZENKINS_GIT_TIMEOUT_SECONDS</c>.</summary>
    public TimeSpan GitOperationTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Extra environment for git (tests set <c>PATH</c> and a config override here).</summary>
    public IReadOnlyDictionary<string, string?> GitEnvironment { get; init; } =
        new Dictionary<string, string?>();

    /// <summary>
    /// The server-provided run environment (F-007): <c>ZENKINS_RUN_ID</c>, <c>ZENKINS_PROJECT_ID</c>,
    /// <c>ZENKINS_WORKER_NAME</c>. It is the <b>last</b> layer of the composition - it wins over the
    /// pipeline's and the step's own <c>env</c> (card F-015's precedence).
    /// </summary>
    public IReadOnlyDictionary<string, string?> StepEnvironment { get; init; } =
        new Dictionary<string, string?>();

    /// <summary>
    /// The worker's own process environment, the <b>first</b> layer of the composition. Null means "read the
    /// running process' environment", which is what the child would inherit anyway; tests pass a fixed map
    /// so the precedence chain is a fact rather than an inference.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? InheritedEnvironment { get; init; }

    /// <summary>Where this layer's own lines go (the run log: stdout stream).</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Where a step's captured stdout/stderr lines go (the run's streaming log).</summary>
    public Action<ProcessOutputLine>? OnProcessOutput { get; init; }

    /// <summary>
    /// Called when the run's current step changes (its id while a step is running, null between steps and
    /// during the definition phase). The run loop stamps every line it emits with it, which is how the run
    /// page groups the log per step (card F-015).
    /// </summary>
    public Action<Guid?>? OnStepChanged { get; init; }

    /// <summary>
    /// The run's redaction filter (F-013's, now fed by F-015's secrets too). Every line this layer emits -
    /// including the ones it synthesizes - passes through it, and every value decrypted for the run is
    /// registered with it the instant it is known.
    /// </summary>
    public GitLogFilter Redactor { get; init; } = new();

    /// <summary>Where step transitions are reported; an accepting offline sink when no server is behind the run.</summary>
    public IPipelineStepSink StepSink { get; init; } = OfflinePipelineStepSink.Instance;

    /// <summary>
    /// The run's secret source (a per-run fetch cache in production). Defaults to a source with no secrets,
    /// so a <c>secret()</c> reference fails the step that made it instead of silently becoming empty.
    /// </summary>
    public IRunSecretSource Secrets { get; init; } = EmptyRunSecretSource.Instance;

    /// <summary>
    /// The run's artifact publisher (card F-016). The executor publishes the paths a step declares
    /// (<c>artifacts = { "bin/Release" }</c>) after its process exits 0, with this call's cancellation token,
    /// so a cancel or the deadline kills the upload too. Null means "this run publishes no artifacts"; a step
    /// that declares one then fails rather than pretending it was published.
    /// </summary>
    public IRunArtifactPublisher? Artifacts { get; init; }

    /// <summary>
    /// Why this run's cancellation fired, asked at the instant a step is killed (card F-015): a user cancel
    /// (F-009's button) or the run's deadline. The answer becomes the killed step's
    /// <see cref="PipelineStepFailureReasons"/> value, so the run page can tell "someone stopped this" from
    /// "it ran out of time". Null (or a null answer) means a user cancel.
    /// </summary>
    public Func<string?>? CancellationFailureReason { get; init; }
}

/// <summary>
/// How one run ended (card F-014). <see cref="Canceled"/> is not a local terminal state: the loop
/// reports it through F-009's canceled endpoint, which owns the Canceled/Failed(timeout) mapping.
/// </summary>
/// <param name="ExitCode">The failing step's exit code, 0 when every step succeeded, null when no
/// process ran (a git or definition failure).</param>
/// <param name="FailureReason">A <see cref="PipelineFailureReasons"/> value, or null on success.</param>
/// <param name="Commit">The commit the run cloned, when a clone happened.</param>
/// <param name="Canceled">The run's cancellation token fired; nothing was reported as completed.</param>
/// <param name="Abandoned">The server definitively refused a report, so the run is not ours to finish
/// (it is already terminal server-side): nothing is executed and nothing is reported.</param>
public sealed record PipelineRunOutcome(
    int? ExitCode,
    string? FailureReason,
    string? Commit,
    bool Canceled = false,
    bool Abandoned = false)
{
    /// <summary>A successful run: every non-skipped step succeeded.</summary>
    public static PipelineRunOutcome Succeeded(string commit) => new(0, null, commit);

    /// <summary>The run answered a cancellation; the caller reports it through F-009's endpoint.</summary>
    public static PipelineRunOutcome Cancellation(string? commit = null) =>
        new(null, null, commit, Canceled: true);

    /// <summary>A failure that produced no exit code (git, or the definition phase).</summary>
    public static PipelineRunOutcome Failed(string reason, string? commit = null) =>
        new(null, reason, commit);

    /// <summary>A failing step: its own exit code is recorded, never an invented one.</summary>
    public static PipelineRunOutcome StepFailed(int exitCode, string commit) =>
        new(exitCode, PipelineFailureReasons.StepFailed, commit);

    /// <summary>
    /// The server refused a report for this run (409/410: another worker owns it, it was canceled, or it
    /// is already terminal). Steps must not run and nothing may be reported - the server owns the state.
    /// </summary>
    public static PipelineRunOutcome GiveUp(string? commit = null) =>
        new(null, null, commit, Abandoned: true);
}

/// <summary>
/// Executes one pipeline run (cards F-014 and F-015): clone the project's repository at the run's branch
/// through F-013, run the definition phase once, report the resolved commit and the frozen spec, then
/// execute the steps in order with <see cref="ProcessRunner"/>.
/// <para>
/// <b>Execution is C#, never Lua</b> (<c>docs/lua-runtime.md</c> §6.1): sequencing, timing, exit codes,
/// <c>continue_on_error</c>, <c>when</c> conditions, environment composition, secret resolution and
/// cancellation all live here, so a hostile or buggy script cannot skip them and the run's step list is
/// known before the first process starts. The frozen spec is the only input; the script is never run a
/// second time, so a changed working tree or a moving branch cannot alter what a run does.
/// </para>
/// <para>
/// <b>Each step is a first-class record.</b> Every step is reported <c>Running</c> before its process
/// starts (the server stamps the start instant and answers the row's id) and terminal afterwards:
/// <c>Success</c>/<c>Failed</c> with the real exit code, or <c>Skipped</c> with a reason - never started, so
/// no process was spawned and no start instant recorded. A skipped step does not affect the run's success;
/// a failed step does, even when <c>continue_on_error</c> let the rest run.
/// </para>
/// <para>
/// <b>Failure taxonomy is closed</b> (<see cref="PipelineFailureReasons"/>): a git failure maps from
/// F-013's <c>GitFailureKind</c>, a definition failure carries its own reason, and a non-zero step exit
/// is <c>step-failed</c> with that exact code. Nothing here invents a reason for a step.
/// </para>
/// <para>
/// <b>Redaction is applied here, on the way out.</b> Every line this class emits - its own synthesized
/// lines included, which is what makes a step header carrying a resolved secret safe - is passed through the
/// run's <see cref="PipelineRunOptions.Redactor"/> first, and every secret is registered there at the
/// moment it is decrypted. It is log hygiene, not a boundary (<c>docs/lua-runtime.md</c> §7).
/// </para>
/// <para>
/// <b>No timers, no threads.</b> The definition phase's budget is F-011's deadline-driven coroutine
/// driver, and the executor observes the run's cancellation token between steps; a step's process tree
/// is killed by <see cref="ProcessRunner"/>'s own cancellation path. This class creates neither a
/// <see cref="System.Threading.Timer"/> nor a thread.
/// </para>
/// </summary>
public sealed class PipelineRunner
{
    private readonly PipelineRunOptions _options;
    private readonly PipelineDefinitionRunner _definitions;
    private readonly Func<CancellationToken, Task<GitCredentialMaterial?>> _credentialProvider;
    private readonly Func<string?, string?, CancellationToken, Task<bool>> _definitionReporter;
    private readonly IReadOnlyDictionary<string, string?> _inheritedEnvironment;

    /// <param name="options">The run's knobs (work directory, repository, branch, deadline, logging).</param>
    /// <param name="definitions">The definition phase (F-011's runtime plus the Lua prelude).</param>
    /// <param name="credentialProvider">Fetches the project's credential material for this run, or null.</param>
    /// <param name="definitionReporter">
    /// Reports to the server, in this order: the resolved commit (immediately after the clone, so even a
    /// run whose definition turns out to be missing/syntax-broken/invalid/timed-out has its commit
    /// recorded), then the frozen spec (before the first step starts, so the run page can show the step
    /// list of a run that is only just beginning). A <c>null</c> spec means "commit only".
    /// <para>
    /// Returns <see langword="false"/> when the server refused the report definitively (a 4xx: the run
    /// is not ours any more, or it is already terminal). A refusal aborts the run: no step runs and
    /// nothing is reported afterwards - the server owns that run's state.
    /// </para>
    /// </param>
    public PipelineRunner(
        PipelineRunOptions options,
        PipelineDefinitionRunner definitions,
        Func<CancellationToken, Task<GitCredentialMaterial?>> credentialProvider,
        Func<string?, string?, CancellationToken, Task<bool>> definitionReporter)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(credentialProvider);
        ArgumentNullException.ThrowIfNull(definitionReporter);
        _options = options;
        _definitions = definitions;
        _credentialProvider = credentialProvider;
        _definitionReporter = definitionReporter;
        _inheritedEnvironment = options.InheritedEnvironment ?? StepEnvironment.InheritedEnvironment();
    }

    /// <summary>The Lua log sink a run's definition phase writes its <c>log()</c> lines to.</summary>
    public static LuaLogSink CreateLogSink(Action<string> logLine) => new(line =>
    {
        if (logLine is not null)
        {
            logLine(line);
        }
    });

    /// <summary>
    /// Runs the whole pipeline: clone → define → report → execute. Never throws for a run-level
    /// failure; a cancellation is returned as <see cref="PipelineRunOutcome.Canceled"/> so the caller
    /// can use F-009's existing kill/report path.
    /// </summary>
    public async Task<PipelineRunOutcome> RunAsync(CancellationToken cancellationToken)
    {
        // 1. Clone at the run's branch (F-013). The credential is fetched per run and never travels in
        //    the claim payload; the clone refuses a non-empty target, so a retried run cannot inherit
        //    a previous tree. The credential is registered with the run's redaction filter the instant it
        //    is known - before the first git command, and long before any step (F-015 extends F-013 here).
        string commit;
        try
        {
            var workspace = new GitWorkspace(new GitWorkspaceOptions
            {
                Work = _options.Work,
                GitExecutable = _options.GitExecutable,
                Depth = _options.GitDepth,
                KnownHostsPath = _options.KnownHostsPath,
                OperationTimeout = _options.GitOperationTimeout,
                RunDeadline = _options.Deadline,
                EnvironmentOverrides = _options.GitEnvironment,
                Log = Log,
            });

            var credential = await _credentialProvider(cancellationToken);
            _options.Redactor.Register(credential);
            var clone = await workspace.CloneAsync(
                _options.RepositoryUrl,
                _options.Branch,
                credential,
                cancellationToken);
            commit = clone.Commit;
        }
        catch (OperationCanceledException)
        {
            return PipelineRunOutcome.Cancellation();
        }
        catch (GitException failure)
        {
            Log($"zenkins: the repository could not be prepared: {failure.Message}");
            if (failure.Kind == GitFailureKind.Canceled)
            {
                return PipelineRunOutcome.Cancellation();
            }

            return PipelineRunOutcome.Failed(Classify(failure.Kind));
        }

        Log($"zenkins: checked out {_options.Branch} at {commit}.");

        // 2. Report the commit NOW - before anything can fail. F-013's GitCommit is written once, by
        //    the server, from this report; if the definition below turns out to be missing, broken or
        //    timed out, the run still records what it checked out (AC-3). A definitive refusal means the
        //    run is not ours any more, so nothing else is executed or reported.
        bool commitReported;
        try
        {
            commitReported = await ReportOrCancelAsync(commit, specJson: null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return PipelineRunOutcome.Cancellation(commit);
        }

        if (!commitReported)
        {
            return PipelineRunOutcome.GiveUp(commit);
        }

        // 3. Definition: read the pipeline file from the checkout and validate the table it declares.
        PipelineDefinitionResult definition;
        try
        {
            definition = _definitions.Run(
                _options.Work,
                _options.PipelinePath,
                _options.Deadline,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return PipelineRunOutcome.Cancellation(commit);
        }
        catch (PipelineDefinitionException failure)
        {
            Log($"zenkins: {failure.Message}");
            return PipelineRunOutcome.Failed(failure.FailureReason, commit);
        }

        // 4. Report the frozen spec BEFORE the first process starts. The server freezes it on the run,
        //    so the page shows the steps of a run that is only just beginning; a refusal aborts here,
        //    which is what keeps a run the server already closed from producing side effects.
        bool definitionReported;
        try
        {
            definitionReported = await ReportOrCancelAsync(commit, definition.SpecJson, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return PipelineRunOutcome.Cancellation(commit);
        }

        if (!definitionReported)
        {
            return PipelineRunOutcome.GiveUp(commit);
        }

        // 5. Execute the steps in order (F-015: conditions, env composition, secrets, continue_on_error).
        return await ExecuteStepsAsync(definition.Spec, commit, cancellationToken);
    }

    /// <summary>
    /// The step loop (card F-015). One pass per declared step: evaluate its condition, compose its
    /// environment, resolve the references in its argv and env, report <c>Running</c>, run the process,
    /// report the terminal state - then decide whether the run continues.
    /// </summary>
    private async Task<PipelineRunOutcome> ExecuteStepsAsync(
        PipelineSpec spec,
        string commit,
        CancellationToken cancellationToken)
    {
        var steps = spec.Steps;
        var facts = new StepConditionFacts(_options.Branch, StepConditionFacts.CurrentPlatform);

        Log($"zenkins: pipeline '{spec.Name}' with {steps.Length} step(s).");

        int? failedIndex = null;
        int? failedExitCode = null;

        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            var position = $"{i + 1}/{steps.Length}";

            if (cancellationToken.IsCancellationRequested)
            {
                Log($"zenkins: canceled before step {position}.");
                await SkipRemainingAsync(steps, i, PipelineStepFailureReasons.RunCanceled, cancellationToken);
                return PipelineRunOutcome.Cancellation(commit);
            }

            // 5a. The step's effective environment: inherited < pipeline env < step env < run env. One
            //     composition serves both readers - the deferred env() references below and the child.
            var runLayer = new Dictionary<string, string?>(_options.StepEnvironment, StringComparer.Ordinal)
            {
                [StepEnvironment.StepNameVariable] = step.Name,
            };
            var environment = StepEnvironment.Compose(
                _inheritedEnvironment,
                spec.Env,
                step.Env,
                runLayer);

            // 5b. The condition is evaluated against the run's frozen facts before anything is resolved or
            //     started. A step that does not match is recorded Skipped - never claimed Running, no start
            //     instant, no process - and it does not affect the run's success.
            if (!StepCondition.IsSatisfied(step.When, facts, environment, out var skipReason))
            {
                Log($"zenkins: step {position} \"{step.Name}\" skipped: {skipReason}.");
                await ReportStepAsync(
                    i + 1,
                    step.Name,
                    PipelineStepStatus.Skipped,
                    failureReason: PipelineStepFailureReasons.WhenNotMatched,
                    cancellationToken: cancellationToken);
                continue;
            }

            // 5c. Resolve the deferred references: env references against the composed environment, secret
            //     references through the run's per-run cache (each decrypted value is registered with the
            //     redaction filter at this instant, before the step exists to log it).
            string[] argv;
            Dictionary<string, string?> childEnvironment;
            try
            {
                var resolver = new StepValueResolver(_options.Redactor, _options.Secrets);
                argv = await ResolveArgvAsync(resolver, step.Argv, environment, cancellationToken);
                childEnvironment = await ResolveEnvironmentAsync(resolver, environment, cancellationToken);
            }
            catch (StepPreparationException failure)
            {
                Log($"zenkins: step {position} \"{step.Name}\" could not be prepared: {failure.Message}");
                await ReportStepAsync(
                    i + 1,
                    step.Name,
                    PipelineStepStatus.Failed,
                    failureReason: failure.FailureReason,
                    cancellationToken: cancellationToken);
                if (!step.ContinueOnError)
                {
                    await SkipRemainingAsync(
                        steps, i + 1, PipelineStepFailureReasons.PreviousStepFailed, cancellationToken);
                    return PipelineRunOutcome.Failed(PipelineFailureReasons.StepFailed, commit);
                }

                Log($"zenkins: step {position} \"{step.Name}\" failed but continue_on_error is set; "
                    + "running the remaining step(s).");
                failedIndex ??= i + 1;
                continue;
            }

            // 5d. Report the step Running BEFORE the process starts, so a page opened mid-run shows it as
            //     the current step. The answer carries the row's id, which stamps this step's log lines.
            var running = await ReportStepAsync(
                i + 1,
                step.Name,
                PipelineStepStatus.Running,
                cancellationToken: cancellationToken);
            if (running is null)
            {
                return PipelineRunOutcome.GiveUp(commit);
            }

            SetCurrentStep(running);
            Log($"zenkins: step {position} \"{step.Name}\" started: {Render(argv)}");

            var stopwatch = Stopwatch.StartNew();
            int exitCode;
            try
            {
                using var runner = new ProcessRunner(
                    new ProcessRunnerOptions
                    {
                        FileName = argv[0],
                        Arguments = [.. argv.Skip(1)],
                        WorkingDirectory = _options.Work.RepositoryPath,

                        // The composed environment, in full: the child sees exactly the map env() reports, so
                        // the two can never disagree about a value or its precedence.
                        EnvironmentVariables = childEnvironment,
                    },
                    OnProcessOutput);
                exitCode = await runner.RunAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // F-015: the killed step records *why* it died - a user cancel or the run's deadline - so the
                // run page does not have to infer it from the run's own reason.
                var cancellationReason = _options.CancellationFailureReason?.Invoke()
                    ?? PipelineStepFailureReasons.Canceled;
                Log($"zenkins: step {position} \"{step.Name}\" was killed by the run's cancellation.");
                await ReportStepAsync(
                    i + 1,
                    step.Name,
                    PipelineStepStatus.Failed,
                    failureReason: cancellationReason,
                    cancellationToken: CancellationToken.None);
                SetCurrentStep(null);
                await SkipRemainingAsync(
                    steps, i + 1, PipelineStepFailureReasons.RunCanceled, CancellationToken.None);
                return PipelineRunOutcome.Cancellation(commit);
            }
            catch (Exception failure)
            {
                // The process never started (bad argv[0], unusable working directory...). No exit code
                // exists, so none is recorded - the log line says what happened.
                Log($"zenkins: step {position} \"{step.Name}\" could not be started: {failure.Message}");
                await ReportStepAsync(
                    i + 1,
                    step.Name,
                    PipelineStepStatus.Failed,
                    failureReason: PipelineStepFailureReasons.NotStarted,
                    cancellationToken: cancellationToken);
                SetCurrentStep(null);
                if (!step.ContinueOnError)
                {
                    await SkipRemainingAsync(
                        steps, i + 1, PipelineStepFailureReasons.PreviousStepFailed, cancellationToken);
                    return PipelineRunOutcome.Failed(PipelineFailureReasons.StepFailed, commit);
                }

                failedIndex ??= i + 1;
                continue;
            }

            stopwatch.Stop();
            var duration = stopwatch.Elapsed.TotalSeconds.ToString("0.0#", CultureInfo.InvariantCulture);
            Log($"zenkins: step {position} \"{step.Name}\" exited with code {exitCode} after {duration}s.");

            // F-016: a step that succeeded publishes what it declared. A failing step publishes nothing, and a
            // refused path or a breached cap fails the step with the closed artifact reason (the log line names
            // the path or the limit) - the artifact is never truncated and never silently skipped.
            if (exitCode == 0 && step.ArtifactPaths.Length > 0)
            {
                string? artifactFailure;
                try
                {
                    artifactFailure = await PublishArtifactsAsync(step, position, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // The run was canceled (or its deadline passed) while the upload was in flight: the server's
                    // upload was aborted with this token, the step records why, and the run reports through
                    // F-009's canceled endpoint. No Ready row is left behind - the reserved row stays Pending and
                    // the sweeper closes it.
                    var cancellationReason = _options.CancellationFailureReason?.Invoke()
                        ?? PipelineStepFailureReasons.Canceled;
                    Log($"zenkins: step {position} \"{step.Name}\" was killed while publishing its artifact.");
                    await ReportStepAsync(
                        i + 1,
                        step.Name,
                        PipelineStepStatus.Failed,
                        failureReason: cancellationReason,
                        cancellationToken: CancellationToken.None);
                    SetCurrentStep(null);
                    await SkipRemainingAsync(
                        steps, i + 1, PipelineStepFailureReasons.RunCanceled, CancellationToken.None);
                    return PipelineRunOutcome.Cancellation(commit);
                }

                if (artifactFailure is not null)
                {
                    await ReportStepAsync(
                        i + 1,
                        step.Name,
                        PipelineStepStatus.Failed,
                        failureReason: artifactFailure,
                        cancellationToken: cancellationToken);
                    SetCurrentStep(null);
                    if (!step.ContinueOnError)
                    {
                        await SkipRemainingAsync(
                            steps, i + 1, PipelineStepFailureReasons.PreviousStepFailed, cancellationToken);
                        return PipelineRunOutcome.Failed(PipelineFailureReasons.StepFailed, commit);
                    }

                    Log($"zenkins: step {position} \"{step.Name}\" failed but continue_on_error is set; "
                        + "running the remaining step(s).");
                    failedIndex ??= i + 1;
                    continue;
                }
            }

            await ReportStepAsync(
                i + 1,
                step.Name,
                exitCode == 0 ? PipelineStepStatus.Success : PipelineStepStatus.Failed,
                exitCode: exitCode,
                cancellationToken: cancellationToken);
            SetCurrentStep(null);

            if (exitCode == 0)
            {
                continue;
            }

            // The failing step's own code is the record; nothing else is claimed about why. The line names
            // the step explicitly, so the log says which step failed whether or not the run continues.
            Log($"zenkins: step {position} \"{step.Name}\" failed with exit code {exitCode}.");
            failedIndex ??= i + 1;
            failedExitCode ??= exitCode;
            if (!step.ContinueOnError)
            {
                await SkipRemainingAsync(
                    steps, i + 1, PipelineStepFailureReasons.PreviousStepFailed, cancellationToken);
                return PipelineRunOutcome.StepFailed(exitCode, commit);
            }

            Log($"zenkins: step {position} \"{step.Name}\" failed but continue_on_error is set; "
                + "running the remaining step(s).");
        }

        if (failedIndex is { } index)
        {
            // continue_on_error ran the rest, but a failure is never laundered into a success: the run
            // ends Failed and names the step that failed (card F-015, TC-002).
            Log($"zenkins: the run failed because step {index}/{steps.Length} \"{steps[index - 1].Name}\" "
                + $"failed{(failedExitCode is { } code ? $" with exit code {code}" : string.Empty)}.");

            // F-016: a step that failed only because its artifact could not be published never exited
            // non-zero, so no exit code exists to record - the reason carries it (the same rule the other
            // "nothing ran" failures follow: never invent a code).
            return failedExitCode is { } failedCode
                ? PipelineRunOutcome.StepFailed(failedCode, commit)
                : PipelineRunOutcome.Failed(PipelineFailureReasons.StepFailed, commit);
        }

        return PipelineRunOutcome.Succeeded(commit);
    }

    /// <summary>
    /// Reports every step from <paramref name="startIndex"/> on as <c>Skipped</c>: the run stops here (a
    /// failed step without <c>continue_on_error</c>, or a cancellation), and a step that never ran must say
    /// so rather than silently vanish from the page.
    /// </summary>
    private async Task SkipRemainingAsync(
        PipelineStep[] steps,
        int startIndex,
        string reason,
        CancellationToken cancellationToken)
    {
        for (var i = startIndex; i < steps.Length; i++)
        {
            await ReportStepAsync(
                i + 1,
                steps[i].Name,
                PipelineStepStatus.Skipped,
                failureReason: reason,
                cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Reports one step transition and returns the recorded row's id, or <see langword="null"/> when the
    /// server refused it definitively (the caller then gives the run up).
    /// </summary>
    private async Task<Guid?> ReportStepAsync(
        int index,
        string name,
        string status,
        int? exitCode = null,
        string? failureReason = null,
        CancellationToken cancellationToken = default)
    {
        var receipt = await _options.StepSink.ReportAsync(
            new PipelineStepReport(index, name, status, exitCode, failureReason),
            cancellationToken);
        if (!receipt.Accepted)
        {
            Log($"zenkins: the server refused the report for step {index}/{status}; the run is not ours to "
                + "finish - no further step will be executed.");
            return null;
        }

        return receipt.StepId;
    }

    /// <summary>Announces the current step to the caller (which stamps its log lines with it).</summary>
    private void SetCurrentStep(Guid? stepId) => _options.OnStepChanged?.Invoke(stepId);

    /// <summary>Resolves the references in every argv entry (secrets are fetched here, once per name).</summary>
    private static async Task<string[]> ResolveArgvAsync(
        StepValueResolver resolver,
        IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var resolved = new string[argv.Count];
        for (var i = 0; i < argv.Count; i++)
        {
            resolved[i] = await resolver.ResolveAsync(argv[i], environment, cancellationToken);
        }

        return resolved;
    }

    /// <summary>
    /// The composed environment with every reference resolved - so a secret an <c>env</c> value names is
    /// decrypted here too (and registered with the filter at the same instant), and the child receives exactly
    /// the values <c>env()</c> reports for the same names.
    /// </summary>
    private static async Task<Dictionary<string, string?>> ResolveEnvironmentAsync(
        StepValueResolver resolver,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, string?>(environment.Count, StringComparer.Ordinal);
        foreach (var (name, value) in environment)
        {
            resolved[name] = value is null || !StepReference.Contains(value)
                ? value
                : await resolver.ResolveAsync(value, environment, cancellationToken);
        }

        return resolved;
    }

    /// <summary>
    /// One report with the "refused means give up" rule in one place: <see langword="true"/> when the
    /// server accepted it, <see langword="false"/> when it refused definitively. A cancellation is one
    /// of the run's outcomes, not a failure, so the caller maps it.
    /// </summary>
    private async Task<bool> ReportOrCancelAsync(
        string? commit,
        string? specJson,
        CancellationToken cancellationToken)
    {
        var accepted = await _definitionReporter(commit, specJson, cancellationToken);
        if (!accepted)
        {
            Log(specJson is null
                ? "zenkins: the server refused this run's commit report; the run is not ours to "
                    + "finish - nothing will be executed."
                : "zenkins: the server refused this run's pipeline definition; the run is not ours "
                    + "to finish - no step will be executed.");
        }

        return accepted;
    }

    /// <summary>Maps F-013's failure classification onto the closed run-failure vocabulary.</summary>
    private static string Classify(GitFailureKind kind) => kind switch
    {
        GitFailureKind.GitUnavailable => PipelineFailureReasons.GitUnavailable,
        GitFailureKind.NonEmptyTarget => PipelineFailureReasons.GitNonEmptyTarget,
        GitFailureKind.PathEscape => PipelineFailureReasons.GitPathEscape,
        GitFailureKind.InvalidUrl => PipelineFailureReasons.GitInvalidUrl,
        GitFailureKind.BranchNotFound => PipelineFailureReasons.GitBranchNotFound,
        GitFailureKind.AuthenticationRejected => PipelineFailureReasons.GitAuthRejected,
        GitFailureKind.Unreachable => PipelineFailureReasons.GitUnreachable,
        GitFailureKind.Timeout => PipelineFailureReasons.GitTimeout,
        GitFailureKind.Credential => PipelineFailureReasons.GitCredential,
        _ => PipelineFailureReasons.GitFailed,
    };

    /// <summary>
    /// Publishes every path the step declared, in order (card F-016). Returns <see langword="null"/> on success
    /// and the closed step failure reason when a path was refused or an upload failed - the message has already
    /// gone to the run log. A cancellation is <b>not</b> caught here: the caller maps it onto F-009's canceled
    /// path, so a killed upload can never be reported as a step failure the run ignores.
    /// </summary>
    private async Task<string?> PublishArtifactsAsync(
        PipelineStep step,
        string position,
        CancellationToken cancellationToken)
    {
        if (_options.Artifacts is not { } publisher)
        {
            Log(
                $"zenkins: step {position} \"{step.Name}\" declares artifacts but this run has no artifact "
                + "publisher configured; nothing was published.");
            return PipelineStepFailureReasons.ArtifactFailed;
        }

        try
        {
            foreach (var declaredPath in step.ArtifactPaths)
            {
                await publisher.PublishAsync(declaredPath, cancellationToken);
            }

            return null;
        }
        catch (ArtifactPublishException failure)
        {
            Log($"zenkins: step {position} \"{step.Name}\" could not publish its artifact: {failure.Message}");
            return failure.FailureReason;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Defence in depth (card F-016): the publisher maps transport failures onto its closed reasons, but a
            // bug that escaped it must not leave the step Running forever - it fails the step like any other
            // publication failure, and the message (already this run's line) says what happened.
            Log($"zenkins: step {position} \"{step.Name}\" could not publish its artifact "
                + $"({failure.GetType().Name}: {failure.Message}).");
            return PipelineStepFailureReasons.ArtifactFailed;
        }
    }

    private static string Render(IReadOnlyList<string> argv)
    {
        // One log line per step: quote only what would otherwise be ambiguous, and never interpret
        // anything (there is no shell anywhere in the pipeline).
        var parts = new string[argv.Count];
        for (var i = 0; i < argv.Count; i++)
        {
            var argument = argv[i];
            parts[i] = argument.Length == 0 || argument.Any(char.IsWhiteSpace)
                ? "\"" + argument + "\""
                : argument;
        }

        return string.Join(' ', parts);
    }

    /// <summary>One captured output line, redacted before it leaves this layer.</summary>
    private void OnProcessOutput(ProcessOutputLine line) =>
        _options.OnProcessOutput?.Invoke(
            new ProcessOutputLine(line.Stream, _options.Redactor.Redact(line.Line)));

    /// <summary>
    /// One line of this layer, redacted before it leaves this layer. The worker synthesizes step headers and
    /// exit-code lines from data that can now contain a resolved secret (a step's argv), so this is not
    /// merely defence in depth - it is the only thing that keeps such a line out of the log.
    /// </summary>
    private void Log(string line) => _options.Log?.Invoke(_options.Redactor.Redact(line));
}
