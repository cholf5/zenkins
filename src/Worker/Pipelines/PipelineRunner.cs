using System.Diagnostics;
using System.Globalization;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.RunCancellation;
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

    /// <summary>
    /// The project's repository URL, from the claim payload. <see langword="null"/> for a Freestyle run
    /// (card F-020) - there is no repository, the clone is skipped and the frozen spec below is what
    /// executes.
    /// </summary>
    public required string? RepositoryUrl { get; init; }

    /// <summary>
    /// The Freestyle run's frozen spec, handed over by the claim (card F-020): the server validated and
    /// froze it at trigger time, and the Worker re-checks the envelope before executing it - the same
    /// check its own definitions go through. <see langword="null"/> for a Pipeline run, whose spec
    /// comes from the definition phase; a non-null value <b>is</b> what makes a run Freestyle.
    /// </summary>
    public string? PipelineSpecJson { get; init; }

    /// <summary>
    /// The inline Pipeline run's script (card F-022), handed over by the claim: no repository, no spec
    /// yet - the Worker writes it to <c>temp/pipeline.lua</c>, runs the definition phase and executes the
    /// spec it produces. <see langword="null"/> for a repository run (whose script is the checkout's
    /// file) and for a Freestyle run (whose spec is <see cref="PipelineSpecJson"/>).
    /// </summary>
    public string? PipelineScriptText { get; init; }

    /// <summary>
    /// The run's definition shape word (card F-022): <see cref="RunDefinitionSources.Repository"/>,
    /// <see cref="RunDefinitionSources.Freestyle"/> or <see cref="RunDefinitionSources.Inline"/>. The
    /// claim carries it because presence alone cannot tell the three apart (an inline run may also have
    /// a frozen spec). Defaults to a repository run for a caller that builds the options directly.
    /// </summary>
    public string DefinitionSource { get; init; } = RunDefinitionSources.Repository;

    /// <summary>The branch this run clones: the trigger's branch, or the project's default.</summary>
    public required string Branch { get; init; }

    /// <summary>The project's pipeline path, snapshotted on the run at trigger time.</summary>
    public required string PipelinePath { get; init; }

    /// <summary>
    /// F-017 (review M1): the exact commit this run must build, when the claim named one (a webhook push).
    /// The clone still starts at the branch (it is what makes a targeted fetch cheap), and then
    /// <c>checkout()</c> pins this commit - because the branch may have moved between the push and now, and
    /// a run must build the tree its push named. <see langword="null"/> (a manual run) keeps the F-013
    /// behaviour: the branch tip is what gets built and reported.
    /// </summary>
    public string? ExpectedCommit { get; init; }

    /// <summary>
    /// The run's deadline, frozen by the server at accept time (F-009). It bounds the definition
    /// phase; the deadline sweeper owns the executor's steps.
    /// </summary>
    public required DateTimeOffset Deadline { get; init; }

    /// <summary>
    /// Card F-031: whether a failed post step fails the run. <b>False is the product default</b> - the main
    /// list decides the run's result, and a broken cleanup is recorded as its own failed step plus the note
    /// on the run page, rather than silently turning a green build red (or, worse, being swallowed). The
    /// project's explicit switch sets this, and the claim carries the answer to the worker.
    /// </summary>
    public bool PostFailureFailsRun { get; init; }

    /// <summary>
    /// Card F-031: how long a <b>canceled</b> run's post steps may still run. The default is
    /// <see cref="PipelineSpecSchema.PostGraceSeconds"/>; a test may shorten it to prove the kill. The
    /// window is always clamped by whatever the run's deadline has left, so this never extends a run.
    /// </summary>
    public TimeSpan PostGrace { get; init; } =
        TimeSpan.FromSeconds(PipelineSpecSchema.PostGraceSeconds);

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
    /// The server-provided run environment (F-007/F-021/F-034): the <c>ZENKINS_*</c> metadata, the
    /// organization layer frozen onto the run and the run's parameter values, as named layers. They are
    /// the lower half of the composition (metadata → organization → parameters → pipeline env → step env,
    /// PRD §3.11.3) and the reserved metadata is pinned, so a declared <c>env</c> cannot shadow it.
    /// </summary>
    public RunEnvironment RunEnvironment { get; init; } = RunEnvironment.Empty;

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
    /// <summary>A successful run: every non-skipped step succeeded. <paramref name="commit"/> is
    /// <see langword="null"/> for a Freestyle run (no repository, card F-020).</summary>
    public static PipelineRunOutcome Succeeded(string? commit) => new(0, null, commit);

    /// <summary>The run answered a cancellation; the caller reports it through F-009's endpoint.</summary>
    public static PipelineRunOutcome Cancellation(string? commit = null) =>
        new(null, null, commit, Canceled: true);

    /// <summary>A failure that produced no exit code (git, or the definition phase).</summary>
    public static PipelineRunOutcome Failed(string reason, string? commit = null) =>
        new(null, reason, commit);

    /// <summary>A failing step: its own exit code is recorded, never an invented one. The commit is
    /// <see langword="null"/> for a Freestyle run (no repository, card F-020).</summary>
    public static PipelineRunOutcome StepFailed(int exitCode, string? commit) =>
        new(exitCode, PipelineFailureReasons.StepFailed, commit);

    /// <summary>
    /// A failing step whose reason is typed rather than the generic <c>step-failed</c> (card F-032): a step
    /// killed by its own timeout (<paramref name="exitCode"/> null - nothing exited) or a step whose
    /// declared attempts were all used up (<paramref name="exitCode"/> is the last attempt's). The code is
    /// always a real one; nothing here invents a number.
    /// </summary>
    public static PipelineRunOutcome StepFailedWith(int? exitCode, string reason, string? commit) =>
        new(exitCode, reason, commit);

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

    /// <summary>
    /// Card Q-023: the clock the deadline comparison reads. The default is the wall clock - production is
    /// unchanged - and a fact passes a manual one so "which limit is earlier" stops depending on how late
    /// this process got scheduled.
    /// </summary>
    private readonly TimeProvider _time;

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
        Func<string?, string?, CancellationToken, Task<bool>> definitionReporter,
        TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
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
        // F-020: a Freestyle run has no repository and no definition phase - the claim carried the
        // frozen spec, so this run executes it directly in the run's workspace. The step loop, the
        // environment composition, the secrets, the artifacts, the cancellation and the deadline are
        // exactly the Pipeline run's: only the clone and the definition phase are skipped.
        if (string.Equals(_options.DefinitionSource, RunDefinitionSources.Freestyle, StringComparison.Ordinal))
        {
            return await RunFreestyleAsync(_options.PipelineSpecJson, cancellationToken);
        }

        // F-022: an inline Pipeline has no repository and no frozen spec yet - the claim carried the
        // script, the Worker writes it to temp/pipeline.lua and runs the same definition phase a
        // repository run does. Everything after that (report, steps, secrets, artifacts, cancel,
        // deadline) is the one pipeline path, unchanged.
        if (string.Equals(_options.DefinitionSource, RunDefinitionSources.Inline, StringComparison.Ordinal))
        {
            if (_options.PipelineScriptText is not { } inlineScript)
            {
                Log("zenkins: the claim says this is an inline run but carries no script; refusing to guess.");
                return PipelineRunOutcome.Failed(PipelineFailureReasons.PipelineInvalid);
            }

            return await RunInlineAsync(inlineScript, cancellationToken);
        }

        // 1. Clone at the run's branch (F-013). The credential is fetched per run and never travels in
        //    the claim payload; the clone refuses a non-empty target, so a retried run cannot inherit
        //    a previous tree. The credential is registered with the run's redaction filter the instant it
        //    is known - before the first git command, and long before any step (F-015 extends F-013 here).
        string commit;

        // Card F-018: whether the server delivered a credential for this run. The answer decides what the
        // log says before the clone and which failure reason a rejection carries afterwards.
        var credentialDelivered = false;
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
            credentialDelivered = credential is not null;

            // Card F-018: say which credential this clone has - or that it has none. The line names no
            // credential (a name is not needed to diagnose this and would leak the store's contents into
            // a log a Member can read) and never carries a value. It is what makes the run's log agree
            // with the run page instead of leaving an anonymous clone unexplained.
            Log(credentialDelivered
                ? $"zenkins: cloning with the project's assigned {credential!.KindText} Git credential, "
                    + "fetched from the server for this run."
                : "zenkins: no Git credential is assigned to this project, so this clone runs without one; "
                    + "a credential stored on this worker host is not used.");

            var clone = await workspace.CloneAsync(
                _options.RepositoryUrl,
                _options.Branch,
                credential,
                cancellationToken);
            commit = clone.Commit;

            // F-017 (review M1): when the claim named a commit (a webhook push), pin it. The clone started
            // at the branch so git could fetch only what it needs, but the branch is not the authority -
            // two pushes to one branch may overlap, and this run must build the tree its own push named.
            // checkout() is idempotent (already at the commit: two read-only commands, no fetch) and it
            // verifies HEAD really is the requested object, so a checkout that cannot be honoured fails
            // the run instead of silently building the branch tip.
            if (_options.ExpectedCommit is { Length: > 0 } expected)
            {
                var pinned = await workspace.CheckoutAsync(expected, cancellationToken);
                if (!pinned.AlreadyAtCommit)
                {
                    Log($"zenkins: the branch moved; checked out the pushed commit {pinned.Commit}.");
                }

                commit = pinned.Commit;
            }
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

            return PipelineRunOutcome.Failed(Classify(failure.Kind, credentialDelivered));
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
    /// A Freestyle run (card F-020): re-check the server-frozen spec with the same envelope the Worker's
    /// own definitions go through, prepare the run's workspace, and execute the steps in it. The commit
    /// is <see langword="null"/> - there is no repository, so there is nothing to report and nothing to
    /// pin; the run's log still says what it is executing and where.
    /// <para>
    /// A refused spec is <see cref="PipelineFailureReasons.PipelineInvalid"/>, not a crash: the server
    /// and the Worker could only disagree here through a version skew or a bug, and the honest terminal
    /// state (with the envelope's reason in the log) beats a run left <c>Running</c> forever.
    /// </para>
    /// </summary>
    private async Task<PipelineRunOutcome> RunFreestyleAsync(string? specJson, CancellationToken cancellationToken)
    {
        if (specJson is null)
        {
            Log("zenkins: the claim says this is a Freestyle run but carries no frozen step list; refusing to guess.");
            return PipelineRunOutcome.Failed(PipelineFailureReasons.PipelineInvalid);
        }

        if (!PipelineSpecJson.TryRead(specJson, out var spec, out var error) || spec is null)
        {
            Log($"zenkins: the claim's frozen step list was refused: {error}");
            return PipelineRunOutcome.Failed(PipelineFailureReasons.PipelineInvalid);
        }

        Log($"zenkins: freestyle run - the steps were configured on the server and travel with this "
            + $"run; no repository is cloned.");

        // The workspace is a Freestyle-only directory (the prepared trio stays PRD §3.4.6's), created
        // here because preparation is idempotent and only a Freestyle run ever needs it.
        Directory.CreateDirectory(_options.Work.WorkspacePath);

        return await ExecuteStepsAsync(spec, commit: null, cancellationToken);
    }

    /// <summary>
    /// An inline Pipeline run (card F-022): no clone, no repository. The script is materialised in the
    /// run's <c>temp/pipeline.lua</c> and described exactly like a repository file, so the diagnostic's
    /// name and position are the same (<c>pipeline.lua:(line,col)</c>) and the frozen spec is the one
    /// contract downstream. A refusal fails the run with the definition phase's own reason; nothing is
    /// executed and no step is recorded.
    /// </summary>
    private async Task<PipelineRunOutcome> RunInlineAsync(string script, CancellationToken cancellationToken)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(script) > PipelineScriptLimits.MaxScriptBytes)
        {
            Log($"zenkins: the inline pipeline script is larger than {PipelineScriptLimits.MaxScriptBytes} bytes; "
                + "refusing to describe it.");
            return PipelineRunOutcome.Failed(PipelineFailureReasons.PipelineInvalid);
        }

        Log("zenkins: inline pipeline run - the script was snapshotted on the server and travels with "
            + "this run; no repository is cloned.");

        // The workspace is the inline run's working directory (the Freestyle trio): repository/ stays
        // empty, which is exactly what TC-001 asserts.
        Directory.CreateDirectory(_options.Work.WorkspacePath);

        PipelineDefinitionResult definition;
        try
        {
            definition = _definitions.RunInline(
                _options.Work,
                script,
                _options.Deadline,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return PipelineRunOutcome.Cancellation();
        }
        catch (PipelineDefinitionException failure)
        {
            Log($"zenkins: {failure.Message}");
            return PipelineRunOutcome.Failed(failure.FailureReason);
        }

        // One report, with no commit: there is nothing to check out, so the definition IS the first
        // report. A definitive refusal aborts before any step has a side effect.
        bool definitionReported;
        try
        {
            definitionReported = await ReportOrCancelAsync(commit: null, definition.SpecJson, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return PipelineRunOutcome.Cancellation();
        }

        if (!definitionReported)
        {
            return PipelineRunOutcome.GiveUp(null);
        }

        return await ExecuteStepsAsync(definition.Spec, commit: null, cancellationToken);
    }

    /// <summary>
    /// The run's execution: the main step loop, then the post phase (cards F-015 and F-031). The main
    /// list decides the run's outcome; the post phase runs one outcome set and then <c>always</c>, and it
    /// never flips a successful run to failed unless the project explicitly asks for that.
    /// </summary>
    /// <param name="commit">The commit a Pipeline run cloned; <see langword="null"/> for a Freestyle
    /// run, which has no repository (card F-020). It is the record, never invented.</param>
    private async Task<PipelineRunOutcome> ExecuteStepsAsync(
        PipelineSpec spec,
        string? commit,
        CancellationToken cancellationToken)
    {
        var facts = new StepConditionFacts(_options.Branch, StepConditionFacts.CurrentPlatform);

        Log($"zenkins: pipeline '{spec.Name}' with {spec.Steps.Length} step(s).");

        var main = await RunMainStepsAsync(spec, facts, cancellationToken);
        if (main.Abandoned)
        {
            return PipelineRunOutcome.GiveUp(commit);
        }

        var outcome = MapMainOutcome(spec, main, commit);
        return await RunPostAsync(spec, commit, main, outcome, facts, cancellationToken);
    }

    /// <summary>How the main list ended, before the post phase exists to react to it.</summary>
    /// <param name="Canceled">The run's cancellation token fired and the list stopped there.</param>
    /// <param name="Abandoned">The server refused a step report; the run is not ours to finish.</param>
    /// <param name="FailedIndex">The first failing step's 1-based position, or null for a clean list.</param>
    /// <param name="FailedExitCode">That step's exit code, when it had one.</param>
    /// <param name="FailureReason">The run-level reason that step produced.</param>
    private sealed record MainStepResult(
        bool Canceled,
        bool Abandoned,
        int? FailedIndex,
        int? FailedExitCode,
        string? FailureReason);

    /// <summary>How one step ended, as the sequence loops need it (the step row itself is already reported).</summary>
    private enum StepEnd
    {
        Succeeded,
        Skipped,
        Failed,
        Canceled,
        Abandoned,
    }

    /// <summary>One step's terminal answer: what ended it, its exit code and the run-level reason it carries.</summary>
    private sealed record StepExecution(
        StepEnd End,
        int? ExitCode = null,
        string? RunFailureReason = null);

    /// <summary>
    /// The main list (card F-015), unchanged in its rules: a step that fails without
    /// <c>continue_on_error</c> stops the list and skips the rest; a cancellation stops it and reports the
    /// rest as skipped. The post phase is <see cref="RunPostAsync"/>'s job, never this loop's.
    /// </summary>
    private async Task<MainStepResult> RunMainStepsAsync(
        PipelineSpec spec,
        StepConditionFacts facts,
        CancellationToken cancellationToken)
    {
        var steps = spec.Steps;
        int? failedIndex = null;
        int? failedExitCode = null;

        // F-032: the run-level reason the first failing step produced (a step's own timeout, exhausted
        // retries, or the pre-F-032 `step-failed` when neither applies). Null means "no typed reason".
        string? failedReason = null;

        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            var position = $"{i + 1}/{steps.Length}";

            if (cancellationToken.IsCancellationRequested)
            {
                Log($"zenkins: canceled before step {position}.");
                await SkipRemainingAsync(steps, i, PipelineStepFailureReasons.RunCanceled, cancellationToken);
                return new MainStepResult(true, false, null, null, null);
            }

            var result = await ExecuteOneStepAsync(
                spec,
                step,
                reportIndex: i + 1,
                position,
                facts,
                processToken: cancellationToken,
                graceFailureReason: null,
                phaseDeadline: _options.Deadline,
                phaseLimitDescription: "the run's deadline");

            switch (result.End)
            {
                case StepEnd.Abandoned:
                    return new MainStepResult(false, true, null, null, null);

                case StepEnd.Canceled:
                    await SkipRemainingAsync(
                        steps, i + 1, PipelineStepFailureReasons.RunCanceled, CancellationToken.None);
                    return new MainStepResult(true, false, null, null, null);

                case StepEnd.Skipped:
                    continue;

                case StepEnd.Succeeded:
                    continue;
            }

            failedIndex ??= i + 1;
            failedExitCode ??= result.ExitCode;
            failedReason ??= result.RunFailureReason;
            if (!step.ContinueOnError)
            {
                await SkipRemainingAsync(
                    steps, i + 1, PipelineStepFailureReasons.PreviousStepFailed, cancellationToken);
                return new MainStepResult(false, false, failedIndex, failedExitCode, failedReason);
            }

            Log($"zenkins: step {position} \"{step.Name}\" failed but continue_on_error is set; "
                + "running the remaining step(s).");
        }

        return failedIndex is null
            ? new MainStepResult(false, false, null, null, null)
            : new MainStepResult(false, false, failedIndex, failedExitCode, failedReason);
    }

    /// <summary>
    /// The main list's outcome, with the one summary line card F-015 has always logged when
    /// <c>continue_on_error</c> ran the rest: a failure is never laundered into a success.
    /// </summary>
    private PipelineRunOutcome MapMainOutcome(PipelineSpec spec, MainStepResult main, string? commit)
    {
        if (main.Canceled)
        {
            return PipelineRunOutcome.Cancellation(commit);
        }

        if (main.FailedIndex is { } index)
        {
            var steps = spec.Steps;
            Log($"zenkins: the run failed because step {index}/{steps.Length} \"{steps[index - 1].Name}\" "
                + $"failed{(main.FailedExitCode is { } code ? $" with exit code {code}" : string.Empty)}.");

            // F-016: a step that failed only because its artifact could not be published never exited
            // non-zero, so no exit code exists to record - the reason carries it (the same rule the other
            // "nothing ran" failures follow: never invent a code). F-032: the reason is the first failing
            // step's own typed one when it has one (`step-timeout`, `step-retry-exhausted`).
            var runFailureReason = main.FailureReason ?? PipelineFailureReasons.StepFailed;
            return main.FailedExitCode is { } failedCode
                ? PipelineRunOutcome.StepFailedWith(failedCode, runFailureReason, commit)
                : PipelineRunOutcome.Failed(runFailureReason, commit);
        }

        return PipelineRunOutcome.Succeeded(commit);
    }

    /// <summary>
    /// The post phase (card F-031): after the main list ends, the <b>matching</b> outcome set runs, then
    /// <c>always</c> runs last. The two outcome sets never both run; a canceled run runs neither (a cancel is
    /// not a pipeline failure - cleanup is what it asked for) and gets <c>always</c> inside a bounded grace
    /// window instead.
    /// <para>
    /// <b>A post failure does not flip the result by default.</b> The step is recorded as its own failure and
    /// the run's outcome stays what the main list decided; the project's
    /// <see cref="PipelineRunOptions.PostFailureFailsRun"/> switch is the explicit opt-in that turns the first
    /// post failure into a failed run. A canceled run keeps <c>Canceled</c> either way - a cleanup failure
    /// cannot rewrite why the run stopped.
    /// </para>
    /// <para>
    /// <b>The grace window is a real token, not a comment.</b> A canceled run's post steps run under a fresh
    /// token bounded by <see cref="PipelineRunOptions.PostGrace"/>, clamped to whatever the run's deadline has
    /// left: a deadline cancel therefore gets only the remainder (often none), while an operator cancel gets
    /// the full window. A step that overruns is killed by that token and recorded with the reason that says
    /// which window it hit (<c>post-grace-exceeded</c> or <c>timed-out</c>).
    /// </para>
    /// </summary>
    private async Task<PipelineRunOutcome> RunPostAsync(
        PipelineSpec spec,
        string? commit,
        MainStepResult main,
        PipelineRunOutcome mainOutcome,
        StepConditionFacts facts,
        CancellationToken cancellationToken)
    {
        if (spec.Post is not { } post)
        {
            return mainOutcome;
        }

        var succeeded = !main.Canceled && main.FailedIndex is null;
        var plan = new List<(string Outcome, PipelineStep Step)>();
        if (!main.Canceled)
        {
            var outcome = succeeded ? PipelinePostOutcomes.Success : PipelinePostOutcomes.Failure;
            var set = succeeded ? post.Success : post.Failure;
            if (set is not null)
            {
                foreach (var step in set)
                {
                    plan.Add((outcome, step));
                }
            }
        }

        if (post.Always is not null)
        {
            foreach (var step in post.Always)
            {
                plan.Add((PipelinePostOutcomes.Always, step));
            }
        }

        if (plan.Count == 0)
        {
            return mainOutcome;
        }

        Log($"zenkins: the main steps ended {(main.Canceled ? "canceled" : succeeded ? "successfully" : "failed")}; "
            + $"running {plan.Count} post step(s) ({string.Join(", ", plan.Select(entry => entry.Outcome).Distinct())}).");

        CancellationToken postToken = cancellationToken;
        string? graceFailureReason = null;
        CancellationTokenSource? grace = null;
        var postDeadline = _options.Deadline;
        if (main.Canceled)
        {
            // A cancel normally fires the token the main list ran under. Post steps get their own bounded
            // window: the run's deadline still has the final word, and a deadline cancel has nothing left
            // to give (window <= 0 below records the steps as skipped instead of running them).
            var deadlineCanceled = RunCancelReasons.IsTimeout(_options.CancellationFailureReason?.Invoke());
            var now = _time.GetUtcNow();
            var remaining = _options.Deadline - now;
            var window = deadlineCanceled
                ? remaining
                : remaining < _options.PostGrace ? remaining : _options.PostGrace;

            if (window <= TimeSpan.Zero)
            {
                Log("zenkins: the run's deadline left no post grace window; post steps are recorded as skipped.");
                await SkipPostStepsAsync(spec, plan, start: 0, CancellationToken.None);
                return mainOutcome;
            }

            grace = new CancellationTokenSource();
            grace.CancelAfter(window);
            postToken = grace.Token;
            postDeadline = now + window;
            graceFailureReason = deadlineCanceled
                ? PipelineStepFailureReasons.TimedOut
                : PipelineStepFailureReasons.PostGraceExceeded;
            Log(deadlineCanceled
                ? $"zenkins: post steps run inside the {Seconds(window)}s the run's deadline has left "
                    + "(a deadline cancel creates no new time); a step that overruns is killed."
                : $"zenkins: post steps run inside a {Seconds(window)}s grace window "
                    + "(a cancel must not skip cleanup; the run's deadline still bounds it).");
        }

        var postLimitDescription = main.Canceled ? "the post grace window" : "the run's deadline";

        try
        {
            StepExecution? firstPostFailure = null;
            for (var i = 0; i < plan.Count; i++)
            {
                if (postToken.IsCancellationRequested)
                {
                    // The grace window (or the run) ended between steps: what has not started never will.
                    await SkipPostStepsAsync(spec, plan, i, CancellationToken.None);
                    break;
                }

                var (outcomeName, step) = plan[i];
                var result = await ExecuteOneStepAsync(
                    spec,
                    step,
                    reportIndex: spec.Steps.Length + i + 1,
                    position: $"post {i + 1}/{plan.Count}",
                    facts,
                    processToken: postToken,
                    graceFailureReason,
                    phaseDeadline: postDeadline,
                    phaseLimitDescription: postLimitDescription);

                switch (result.End)
                {
                    case StepEnd.Abandoned:
                        return PipelineRunOutcome.GiveUp(commit);

                    case StepEnd.Canceled:
                        // The window ended. Remaining post steps are skipped, and the run's own outcome stands:
                        // a canceled run stays canceled, and a run canceled during its cleanup is canceled too.
                        await SkipPostStepsAsync(spec, plan, i + 1, CancellationToken.None);
                        return main.Canceled ? mainOutcome : PipelineRunOutcome.Cancellation(commit);

                    case StepEnd.Failed:
                        // One post step's failure does not stop the others: cleanup is the point, and each
                        // failure is its own visible step record. The outcome question is answered below.
                        firstPostFailure ??= result;
                        Log($"zenkins: post step {i + 1}/{plan.Count} \"{step.Name}\" ({outcomeName}) failed; "
                            + "the remaining post steps still run.");
                        continue;

                    default:
                        continue;
                }
            }

            if (main.Canceled)
            {
                // The terminal answer stays Canceled, whatever the cleanup reported (AC-5).
                return mainOutcome;
            }

            if (firstPostFailure is { } failure && _options.PostFailureFailsRun)
            {
                Log("zenkins: a post step failed and the project is set to fail the run on that; "
                    + "the run is reported failed (post-failed).");
                return PipelineRunOutcome.StepFailedWith(
                    failure.ExitCode,
                    PipelineFailureReasons.PostFailed,
                    commit);
            }

            if (firstPostFailure is not null)
            {
                Log("zenkins: a post step failed; the run's main result is unchanged "
                    + "(a post failure fails the run only when the project enables it).");
            }

            return mainOutcome;
        }
        finally
        {
            grace?.Dispose();
        }
    }

    /// <summary>
    /// Records every remaining post step as <c>Skipped</c> with <c>run-canceled</c>: the window ended (or the
    /// run was canceled), and a step that never ran must say so - the post section's order and count are the
    /// declared plan's, not "whatever reported in time".
    /// </summary>
    private async Task SkipPostStepsAsync(
        PipelineSpec spec,
        IReadOnlyList<(string Outcome, PipelineStep Step)> plan,
        int start,
        CancellationToken cancellationToken)
    {
        for (var i = start; i < plan.Count; i++)
        {
            await ReportStepAsync(
                spec.Steps.Length + i + 1,
                plan[i].Step.Name,
                PipelineStepStatus.Skipped,
                failureReason: PipelineStepFailureReasons.RunCanceled,
                cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// One declared step, wherever it sits (the main list or a post set): compose its environment, evaluate
    /// its condition, resolve its references, run its attempts, publish its artifacts on success and report
    /// the terminal state. The caller decides what a failure means for the sequence.
    /// </summary>
    /// <param name="reportIndex">The 1-based position on the run (post steps continue after the main list).</param>
    /// <param name="position">The phase-local position the run log prints ("2/3", "post 1/2").</param>
    /// <param name="processToken">
    /// The token that bounds this phase's processes. It is the run's own token, except for a canceled run's
    /// post steps, where it is the grace window's fresh token.
    /// </param>
    /// <param name="graceFailureReason">
    /// Non-null exactly when <paramref name="processToken"/> is a post grace window: the reason a step killed
    /// by it records, and what tells the kill apart from a run cancellation.
    /// </param>
    /// <param name="phaseDeadline">The wall clock that bounds this phase, for the own-timeout comparison.</param>
    /// <param name="phaseLimitDescription">What that bound is called in the log ("the run's deadline" / the grace).</param>
    private async Task<StepExecution> ExecuteOneStepAsync(
        PipelineSpec spec,
        PipelineStep step,
        int reportIndex,
        string position,
        StepConditionFacts facts,
        CancellationToken processToken,
        string? graceFailureReason,
        DateTimeOffset phaseDeadline,
        string phaseLimitDescription)
    {
        // 5a. The step's effective environment, in the one documented order (PRD §3.11.3): inherited
        //     < ZENKINS_* metadata < the server-resolved values (organization, then parameters) <
        //     pipeline env < step env. One composition serves both readers - the deferred env()
        //     references below and the child - and a declared ZENKINS_* name is dropped there with a
        //     line in this run's log (a reserved name is the server's, never a script's).
        var environment = StepEnvironment.Compose(
            _inheritedEnvironment,
            spec.Env,
            step.Env,
            _options.RunEnvironment.WithStepName(step.Name),
            dropped => Log($"zenkins: step {position} \"{step.Name}\": {dropped}"));

        // 5b. The condition is evaluated against the run's frozen facts before anything is resolved or
        //     started. A step that does not match is recorded Skipped - never claimed Running, no start
        //     instant, no process - and it does not affect the run's success.
        if (!StepCondition.IsSatisfied(step.When, facts, environment, out var skipReason))
        {
            Log($"zenkins: step {position} \"{step.Name}\" skipped: {skipReason}.");
            await ReportStepAsync(
                reportIndex,
                step.Name,
                PipelineStepStatus.Skipped,
                failureReason: PipelineStepFailureReasons.WhenNotMatched,
                cancellationToken: processToken);
            return new StepExecution(StepEnd.Skipped);
        }

        // 5c. Resolve the deferred references: env references against the composed environment, secret
        //     references through the run's per-run cache (each decrypted value is registered with the
        //     redaction filter at this instant, before the step exists to log it). A script step
        //     (F-020) resolves its text the same way, then lands in a per-step file the platform
        //     shell executes; an argv step resolves its argv entries as F-015 always did.
        string[] argv;
        Dictionary<string, string?> childEnvironment;
        try
        {
            // F-040: file secrets are materialised inside this run's own temp tree, so the run's existing
            // temp cleanup removes them and no other run can read them.
            var resolver = new StepValueResolver(
                _options.Redactor,
                _options.Secrets,
                new RunSecretFiles(_options.Work.SecretsPath));
            childEnvironment = await ResolveEnvironmentAsync(resolver, environment, processToken);
            argv = step.IsScript
                ? ScriptCommand(WriteScriptFile(
                    reportIndex - 1,
                    await resolver.ResolveAsync(step.Script!, environment, processToken)))
                : await ResolveArgvAsync(resolver, step.Argv, environment, processToken);
        }
        catch (StepPreparationException failure)
        {
            Log($"zenkins: step {position} \"{step.Name}\" could not be prepared: {failure.Message}");
            await ReportStepAsync(
                reportIndex,
                step.Name,
                PipelineStepStatus.Failed,
                failureReason: failure.FailureReason,
                cancellationToken: processToken);
            return new StepExecution(
                StepEnd.Failed,
                RunFailureReason: RunFailureReasonForStep(step, failure.FailureReason, 1));
        }

        // 5d. Report the step Running BEFORE the process starts, so a page opened mid-run shows it as
        //     the current step. The answer carries the row's id, which stamps this step's log lines.
        var running = await ReportStepAsync(
            reportIndex,
            step.Name,
            PipelineStepStatus.Running,
            cancellationToken: processToken);
        if (running is null)
        {
            return new StepExecution(StepEnd.Abandoned);
        }

        SetCurrentStep(running);

        // F-032: the step's own limit, clamped by what the phase has left. The earlier of the two
        // governs, and the line below is how the log (and therefore the operator) learns which one
        // it was: a declared timeout longer than the remaining phase can never fire first, so no step
        // timer is installed at all and the phase's own token (the run's deadline, or a canceled run's
        // post grace window) enforces its own - the step then dies as `timed-out`/`post-grace-exceeded`.
        var ownTimeout = step.OwnTimeout;
        var phaseRemaining = phaseDeadline - _time.GetUtcNow();
        var ownTimeoutGoverns = RunDeadlineComparison.OwnGoverns(ownTimeout, phaseRemaining);
        if (ownTimeout is { } declared)
        {
            Log(ownTimeoutGoverns
                ? $"zenkins: step {position} \"{step.Name}\" declares its own {Seconds(declared)}s timeout "
                    + $"and the phase has {Seconds(phaseRemaining)}s left; the earlier limit is the step's own."
                : $"zenkins: step {position} \"{step.Name}\" declares a {Seconds(declared)}s timeout but "
                    + $"the phase has {Seconds(phaseRemaining)}s left; the earlier limit is {phaseLimitDescription}, "
                    + "and it governs this step.");
        }

        // F-032's attempt loop. One step record (AC-5): every attempt is a delimited section of this
        // step's log, and only the last attempt decides what the record says. The phase token is
        // checked before any attempt timer, so a cancellation is never retried (AC-4).
        var attemptsAllowed = step.AttemptCount;
        int? exitCode = null;
        string? stepFailureReason = null;
        var attempt = 0;
        while (true)
        {
            attempt++;

            // Attempt one's lines are exactly what they were before F-032 (AC-7). A later attempt
            // is prefixed by its own delimiter and does not repeat the script echo: the text is in the
            // log once, the delimiter is what separates the sections.
            if (attempt > 1)
            {
                Log($"zenkins: step {position} \"{step.Name}\" --- attempt {attempt}/{attemptsAllowed} ---");
            }

            // F-020: a script step's header echoes the script verbatim (references stay references -
            // names are not values) instead of the useless "shell + temp file" command line. The file
            // that actually runs holds the resolved text; the redactor covers the resolved lines here
            // as it covers everything else.
            if (step.IsScript)
            {
                Log($"zenkins: step {position} \"{step.Name}\" started: script ({step.Script!.Length} "
                    + $"characters, {(OperatingSystem.IsWindows() ? "cmd" : "sh")}).");
                if (attempt == 1)
                {
                    LogScript(step.Script);
                }
            }
            else
            {
                Log($"zenkins: step {position} \"{step.Name}\" started: {Render(argv)}");
            }

            // The attempt's own limit, and only when it is the earlier one: the linked source turns a
            // cancel into the same OperationCanceledException the phase's token produces, and
            // ProcessRunner's cancellation path kills the whole process tree (F-007/F-009's mechanism,
            // reused - no second kill path exists).
            var attemptTimeout = ownTimeoutGoverns ? ownTimeout : null;
            using var attemptCancellation = attemptTimeout is not null
                ? CancellationTokenSource.CreateLinkedTokenSource(processToken)
                : null;
            attemptCancellation?.CancelAfter(attemptTimeout!.Value);

            var stopwatch = Stopwatch.StartNew();
            exitCode = null;
            stepFailureReason = null;
            var killedByItsOwnTimeout = false;
            try
            {
                using var runner = new ProcessRunner(
                    new ProcessRunnerOptions
                    {
                        FileName = argv[0],
                        Arguments = [.. argv.Skip(1)],

                        // F-020/F-022: a Freestyle or inline run's steps execute in the run's workspace -
                        // there is no checkout, so the repository directory stays empty and unused.
                        WorkingDirectory = string.Equals(
                            _options.DefinitionSource,
                            RunDefinitionSources.Repository,
                            StringComparison.Ordinal)
                                ? _options.Work.RepositoryPath
                                : _options.Work.WorkspacePath,

                        // The composed environment, in full: the child sees exactly the map env() reports, so
                        // the two can never disagree about a value or its precedence.
                        EnvironmentVariables = childEnvironment,
                    },
                    OnProcessOutput);
                exitCode = await runner.RunAsync(attemptCancellation?.Token ?? processToken);
            }
            catch (OperationCanceledException) when (processToken.IsCancellationRequested)
            {
                // F-015: the killed step records *why* it died - a user cancel, the run's deadline, or (card
                // F-031) the bounded grace window a canceled run gives its post steps. F-032: a cancellation
                // is never retried - the run is not the step's to continue.
                var cancellationReason = graceFailureReason
                    ?? _options.CancellationFailureReason?.Invoke()
                    ?? PipelineStepFailureReasons.Canceled;
                Log(graceFailureReason is null
                    ? $"zenkins: step {position} \"{step.Name}\" was killed by the run's cancellation."
                    : $"zenkins: step {position} \"{step.Name}\" hit the post grace window and was killed "
                        + $"({cancellationReason}).");
                await ReportStepAsync(
                    reportIndex,
                    step.Name,
                    PipelineStepStatus.Failed,
                    failureReason: cancellationReason,
                    attempts: attempt,
                    cancellationToken: CancellationToken.None);
                SetCurrentStep(null);
                return new StepExecution(StepEnd.Canceled, exitCode, cancellationReason);
            }
            catch (OperationCanceledException)
            {
                // Only this attempt's timer can have fired here (the phase's token is the filter above),
                // so the step hit the limit it declared itself.
                stopwatch.Stop();
                killedByItsOwnTimeout = true;
                stepFailureReason = PipelineStepFailureReasons.StepTimeout;
                Log($"zenkins: step {position} \"{step.Name}\" hit its own {Seconds(attemptTimeout!.Value)}s "
                    + $"timeout and was killed after {Seconds(stopwatch.Elapsed)}s "
                    + $"(attempt {attempt}/{attemptsAllowed}).");
            }
            catch (Exception failure)
            {
                // The process never started (bad argv[0], unusable working directory...). No exit code
                // exists, so none is recorded - the log line says what happened.
                stopwatch.Stop();
                stepFailureReason = PipelineStepFailureReasons.NotStarted;
                Log($"zenkins: step {position} \"{step.Name}\" could not be started: {failure.Message}");
            }

            if (!killedByItsOwnTimeout && stepFailureReason is null)
            {
                stopwatch.Stop();
                var duration = stopwatch.Elapsed.TotalSeconds.ToString("0.0#", CultureInfo.InvariantCulture);
                Log($"zenkins: step {position} \"{step.Name}\" exited with code {exitCode} after {duration}s.");
                if (exitCode == 0)
                {
                    break;
                }
            }

            // A failed or timed-out attempt: retry while the step declared attempts left and the run
            // itself is still wanted. The delimiter line for the next attempt is written at the top of
            // the loop, so each attempt's output is one delimited section (AC-4).
            if (attempt < attemptsAllowed && !processToken.IsCancellationRequested)
            {
                Log(stepFailureReason is { } attemptReason
                    ? $"zenkins: step {position} \"{step.Name}\" attempt {attempt}/{attemptsAllowed} ended "
                        + $"({attemptReason}); retrying."
                    : $"zenkins: step {position} \"{step.Name}\" attempt {attempt}/{attemptsAllowed} exited "
                        + $"with code {exitCode}; retrying.");
                continue;
            }

            break;
        }

        if (exitCode == 0 && stepFailureReason is null)
        {
            // F-016: a step that succeeded publishes what it declared. A failing step publishes nothing, and a
            // refused path or a breached cap fails the step with the closed artifact reason (the log line names
            // the path or the limit) - the artifact is never truncated and never silently skipped.
            if (step.ArtifactPaths.Length > 0)
            {
                string? artifactFailure;
                try
                {
                    artifactFailure = await PublishArtifactsAsync(step, position, processToken);
                }
                catch (OperationCanceledException)
                {
                    // The phase was canceled (or its deadline/grace passed) while the upload was in flight: the
                    // server's upload was aborted with this token, the step records why, and the caller reports
                    // the run's terminal state. No Ready row is left behind - the reserved row stays Pending and
                    // the sweeper closes it.
                    var cancellationReason = graceFailureReason
                        ?? _options.CancellationFailureReason?.Invoke()
                        ?? PipelineStepFailureReasons.Canceled;
                    Log($"zenkins: step {position} \"{step.Name}\" was killed while publishing its artifact.");
                    await ReportStepAsync(
                        reportIndex,
                        step.Name,
                        PipelineStepStatus.Failed,
                        failureReason: cancellationReason,
                        attempts: attempt,
                        cancellationToken: CancellationToken.None);
                    SetCurrentStep(null);
                    return new StepExecution(StepEnd.Canceled, exitCode, cancellationReason);
                }

                if (artifactFailure is not null)
                {
                    await ReportStepAsync(
                        reportIndex,
                        step.Name,
                        PipelineStepStatus.Failed,
                        failureReason: artifactFailure,
                        attempts: attempt,
                        cancellationToken: processToken);
                    SetCurrentStep(null);
                    return new StepExecution(
                        StepEnd.Failed,
                        RunFailureReason: RunFailureReasonForStep(step, artifactFailure, attempt));
                }
            }

            await ReportStepAsync(
                reportIndex,
                step.Name,
                PipelineStepStatus.Success,
                exitCode: 0,
                attempts: attempt,
                cancellationToken: processToken);
            SetCurrentStep(null);
            return new StepExecution(StepEnd.Succeeded, 0);
        }

        // The terminal record of a step that never succeeded: the final attempt's exit code (null when
        // it was killed or never started) and, when nothing exited, why - one row, not one per attempt.
        await ReportStepAsync(
            reportIndex,
            step.Name,
            PipelineStepStatus.Failed,
            exitCode: exitCode,
            failureReason: stepFailureReason,
            attempts: attempt,
            cancellationToken: processToken);
        SetCurrentStep(null);

        if (exitCode is { } failedCode)
        {
            // The failing step's own code is the record; nothing else is claimed about why. The line names
            // the step explicitly, so the log says which step failed whether or not the run continues.
            Log($"zenkins: step {position} \"{step.Name}\" failed with exit code {failedCode}.");
        }

        return new StepExecution(
            StepEnd.Failed,
            exitCode,
            RunFailureReasonForStep(step, stepFailureReason, attempt));
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
        int? attempts = null,
        CancellationToken cancellationToken = default)
    {
        var receipt = await _options.StepSink.ReportAsync(
            new PipelineStepReport(index, name, status, exitCode, failureReason, attempts),
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

    /// <summary>
    /// Maps a git failure onto the closed <see cref="PipelineFailureReasons"/> vocabulary. Card F-018
    /// splits one case out: a rejected authentication with <b>no credential delivered</b> is
    /// <see cref="PipelineFailureReasons.GitCredentialNotAssigned"/>, so the run page can say "nothing is
    /// assigned" instead of implying a credential the project does not have was refused.
    /// </summary>
    internal static string Classify(GitFailureKind kind, bool credentialDelivered) => kind switch
    {
        GitFailureKind.GitUnavailable => PipelineFailureReasons.GitUnavailable,
        GitFailureKind.NonEmptyTarget => PipelineFailureReasons.GitNonEmptyTarget,
        GitFailureKind.PathEscape => PipelineFailureReasons.GitPathEscape,
        GitFailureKind.InvalidUrl => PipelineFailureReasons.GitInvalidUrl,
        GitFailureKind.BranchNotFound => PipelineFailureReasons.GitBranchNotFound,
        GitFailureKind.AuthenticationRejected when !credentialDelivered =>
            PipelineFailureReasons.GitCredentialNotAssigned,
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

    /// <summary>
    /// Card F-032: the run-level reason a failed step produces. The last attempt's cause names the reason
    /// when it is a typed one; otherwise a step that declared a retry and used more than one attempt says
    /// its attempts were exhausted (a different next step for the reader than a plain one-shot failure),
    /// and a step without the knob keeps the pre-F-032 word.
    /// </summary>
    private static string RunFailureReasonForStep(
        PipelineStep step,
        string? stepFailureReason,
        int attempts) =>
        stepFailureReason == PipelineStepFailureReasons.StepTimeout
            ? PipelineFailureReasons.StepTimeout
            : step.Retry is not null && attempts > 1
                ? PipelineFailureReasons.StepRetryExhausted
                : PipelineFailureReasons.StepFailed;

    /// <summary>A duration for a log line, in seconds with one optional decimal (culture-invariant).</summary>
    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);

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

    /// <summary>
    /// The cap on a script's echoed lines (card F-020). The echo is debugging aid, not a copy: a
    /// pathological script still logs what it starts with and says how much was skipped.
    /// </summary>
    private const int MaxEchoedScriptLines = 100;

    /// <summary>
    /// Writes the resolved script text to a per-step file in the run's <c>temp/</c> (card F-020) and
    /// returns its path. The file - not a command line - is the step's payload: it sidesteps every
    /// command-line length limit and every quoting question, the way Jenkins's own batch/shell steps
    /// do. On Windows the bytes go through <see cref="WindowsBatchScript"/> because cmd reads the
    /// file in the console's code page, not UTF-8 (card B-016); on Unix UTF-8 without a BOM, because
    /// a BOM's characters become the first command there.
    /// </summary>
    private string WriteScriptFile(int stepIndex, string script)
    {
        var extension = OperatingSystem.IsWindows() ? "cmd" : "sh";
        var path = Path.Combine(_options.Work.TempPath, $"step-{stepIndex + 1}.{extension}");
        if (OperatingSystem.IsWindows())
        {
            var (bytes, _) = WindowsBatchScript.Encode(script, WindowsCodePages.Oem ?? System.Text.Encoding.UTF8);
            File.WriteAllBytes(path, bytes);
        }
        else
        {
            File.WriteAllText(path, script, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        return path;
    }

    /// <summary>
    /// The command line that executes a written script file (card F-020): <c>cmd /d /s /c</c> on
    /// Windows (the <c>/s</c> quote handling makes a path with spaces safe), <c>sh -e</c> on Unix so a
    /// failing command fails the step the way a pipeline author expects. Still pure argv, handed to
    /// <see cref="ProcessRunner"/> verbatim - the script text itself is the only thing the shell ever
    /// interprets, and it is exactly what the project's Member configured.
    /// </summary>
    private static string[] ScriptCommand(string scriptFile) =>
        OperatingSystem.IsWindows()
            ? ["cmd.exe", "/d", "/s", "/c", scriptFile]
            : ["/bin/sh", "-e", scriptFile];

    /// <summary>
    /// Echoes a script step's declared text into the run log, one line at a time (the log is line-
    /// addressed, so a multi-line blob would break per-step grouping and the resume cursor). Bounded:
    /// past <see cref="MaxEchoedScriptLines"/> the echo summarizes rather than continues.
    /// </summary>
    private void LogScript(string script)
    {
        var lines = script.Replace("\r\n", "\n").Split('\n');
        var echoed = Math.Min(lines.Length, MaxEchoedScriptLines);
        for (var i = 0; i < echoed; i++)
        {
            Log($"zenkins: | {lines[i]}");
        }

        if (lines.Length > echoed)
        {
            Log($"zenkins: | … the script continues for {lines.Length - echoed} more line(s).");
        }
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

/// <summary>
/// Card Q-023: which of the two limits is the earlier one - a pure decision, so the question can be asked
/// without a run in flight (a fact pins it under CPU pressure, where comparing against the wall clock used to
/// flip the answer).
/// </summary>
public static class RunDeadlineComparison
{
    /// <summary>
    /// Whether the step's own limit fires before the run's remaining deadline. The step's limit governs only
    /// when there is one, the run still has time left, and that limit is not later than the time left.
    /// </summary>
    public static bool OwnGoverns(TimeSpan? ownTimeout, TimeSpan runRemaining) =>
        ownTimeout is { } own
        && runRemaining > TimeSpan.Zero
        && own <= runRemaining;
}
