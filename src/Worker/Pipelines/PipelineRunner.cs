using System.Diagnostics;
using Zenkins.Shared.Pipelines;
using Zenkins.Worker.Git;
using Zenkins.Worker.Lua;

namespace Zenkins.Worker.Pipelines;

/// <summary>
/// Everything the pipeline layer needs about one run (card F-014). The Worker's run loop fills this
/// from the claim the server handed it and from the accept report's frozen deadline.
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

    /// <summary>The environment every step inherits on top of the worker's own (run metadata).</summary>
    public IReadOnlyDictionary<string, string?> StepEnvironment { get; init; } =
        new Dictionary<string, string?>();

    /// <summary>Where this layer's own lines go (the run log: stdout stream).</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Where a step's captured stdout/stderr lines go (the run's streaming log).</summary>
    public Action<ProcessOutputLine>? OnProcessOutput { get; init; }
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
    /// <summary>A successful run: every step exited 0.</summary>
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
/// Executes one pipeline run (card F-014): clone the project's repository at the run's branch through
/// F-013, run the definition phase once, report the resolved commit and the frozen spec, then execute
/// the steps in order with <see cref="ProcessRunner"/> - stopping at the first non-zero exit.
/// <para>
/// <b>Execution is C#, never Lua</b> (<c>docs/lua-runtime.md</c> §6.1): sequencing, timing, exit codes
/// and cancellation live here, so a hostile or buggy script cannot skip them and the run's step list is
/// known before the first process starts. The frozen spec is the only input; the script is never run a
/// second time, so a changed working tree or a moving branch cannot alter what a run does.
/// </para>
/// <para>
/// <b>Failure taxonomy is closed</b> (<see cref="PipelineFailureReasons"/>): a git failure maps from
/// F-013's <c>GitFailureKind</c>, a definition failure carries its own reason, and a non-zero step exit
/// is <c>step-failed</c> with that exact code. Nothing here invents a reason for a step.
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
        //    a previous tree.
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
                Log = _options.Log,
            });

            var credential = await _credentialProvider(cancellationToken);
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

        // 5. Execute the steps in order, stopping at the first failure.
        var steps = definition.Spec.Steps;
        Log($"zenkins: pipeline '{definition.Spec.Name}' with {steps.Length} step(s).");
        for (var i = 0; i < steps.Length; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Log($"zenkins: canceled before step {i + 1}/{steps.Length}.");
                return PipelineRunOutcome.Cancellation(commit);
            }

            var step = steps[i];
            var argv = step.Argv;
            var position = $"{i + 1}/{steps.Length}";
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
                        EnvironmentVariables = BuildStepEnvironment(step),
                    },
                    _options.OnProcessOutput);
                exitCode = await runner.RunAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Log($"zenkins: step {position} \"{step.Name}\" was killed by the run's cancellation.");
                return PipelineRunOutcome.Cancellation(commit);
            }
            catch (Exception failure)
            {
                // The process never started (bad argv[0], unusable working directory...). No exit code
                // exists, so none is recorded - the log line says what happened.
                Log($"zenkins: step {position} \"{step.Name}\" could not be started: {failure.Message}");
                return PipelineRunOutcome.Failed(PipelineFailureReasons.StepFailed, commit);
            }

            stopwatch.Stop();
            var duration = stopwatch.Elapsed.TotalSeconds.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);
            Log($"zenkins: step {position} \"{step.Name}\" exited with code {exitCode} after {duration}s.");

            if (exitCode != 0)
            {
                // The failing step's own code is the record; nothing else is claimed about why.
                return PipelineRunOutcome.StepFailed(exitCode, commit);
            }
        }

        return PipelineRunOutcome.Succeeded(commit);
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

    /// <summary>
    /// The environment of one step: the run's metadata on top of the worker's own environment. The
    /// step's frozen <c>env</c> is deliberately not applied here - F-015 owns it - so this card's
    /// behaviour is the documented default.
    /// </summary>
    private IReadOnlyDictionary<string, string?> BuildStepEnvironment(PipelineStep step)
    {
        var environment = new Dictionary<string, string?>(_options.StepEnvironment)
        {
            ["ZENKINS_STEP_NAME"] = step.Name,
        };
        return environment;
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

    private void Log(string line) => _options.Log?.Invoke(line);
}
