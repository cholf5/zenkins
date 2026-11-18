using Zenkins.Shared.Pipelines;
using Zenkins.Worker.Lua;

namespace Zenkins.Worker.Pipelines;

/// <summary>What the definition phase produced: the validated spec and its frozen JSON (card F-014).</summary>
/// <param name="Spec">The spec the executor runs.</param>
/// <param name="SpecJson">The same spec serialised - what the server stores and the page renders.</param>
public sealed record PipelineDefinitionResult(PipelineSpec Spec, string SpecJson);

/// <summary>
/// The secret names one definition referenced, recorded where they become known (card D-006).
/// <para>
/// <b>Why the definition phase.</b> <c>secret("NAME")</c> resolves at execution time on the worker, so
/// the server could never answer "which pipelines reference this name" without reading repository
/// content. The definition phase is the one moment that sees every call, and its result - the frozen
/// spec - is what survives the repository, so the names are collected here and stored in the spec's
/// <see cref="PipelineSpec.SecretReferences"/>. <b>Names only:</b> no value is ever fetched while a
/// pipeline is described (F-015 keeps that rule), so nothing here can hold plaintext.
/// </para>
/// <para>
/// <b>One instance per run.</b> The worker executes many runs in one process, so a capture is drained
/// (<see cref="Drain"/>) when a definition finishes; nothing is carried into the next run.
/// </para>
/// </summary>
public sealed class SecretReferenceCapture
{
    private readonly SortedSet<string> _names = new(StringComparer.Ordinal);

    /// <summary>Records one <c>secret("name")</c> argument (a malformed name is never recorded).</summary>
    public void Record(string? name)
    {
        if (PipelineSpecSchema.IsSecretReferenceName(name))
        {
            _names.Add(name!);
        }
    }

    /// <summary>The names recorded since the last drain, ordered ordinally (the spec's order).</summary>
    public string[] Drain()
    {
        var names = _names.ToArray();
        _names.Clear();
        return names;
    }
}

/// <summary>
/// The definition phase of a run (card F-014, <c>docs/lua-runtime.md</c> §6.1): read the project's
/// pipeline file out of the checked-out tree, run it once under F-011's sandbox with the Lua prelude,
/// validate the table it declared into a <see cref="PipelineSpec"/>, and serialise that spec.
/// <para>
/// <b>A definition cannot do I/O.</b> The prelude offers only pure helpers (<c>pipeline</c>,
/// <c>step</c>, <c>log</c>, <c>is_windows/is_linux/is_macos</c>); the SDK's I/O primitives exist as
/// functions that raise <c>"… is available inside steps only"</c>, so a pipeline that calls one while
/// being described gets a clear, positioned Lua error instead of a null dereference - and nothing is
/// executed. The refusal is a property of this runtime instance, not of the script: the same name
/// means the real primitive in a future imperative form (§6.1).
/// </para>
/// <para>
/// <b>Budget and cancellation are F-009's.</b> The script runs under the run's own deadline
/// (<c>BuildRun.DeadlineAt</c>), so a <c>while true do end</c> is abandoned at that instant and the
/// run fails <c>pipeline-timeout</c> - never a retry. The runtime's coroutine driver observes both the
/// deadline and the cancellation token at every yield point, so no timer or thread is created here.
/// </para>
/// <para>
/// <b>Read once.</b> The file is read here and nowhere else; the executor works from the validated
/// spec, so changing the working tree (or the branch) after this point cannot alter what the run does
/// (TC-004).
/// </para>
/// </summary>
public sealed class PipelineDefinitionRunner
{
    /// <summary>The embedded prelude resource; the logical name is pinned in the worker's csproj.</summary>
    public const string PreludeResourceName = "Zenkins.Worker.Lua.prelude.lua";

    /// <summary>The global the prelude records the pipeline table in (<c>pipeline { … }</c>).</summary>
    public const string PipelineGlobalName = "_zenkins_pipeline";

    /// <summary>The global the prelude reads the host platform from.</summary>
    public const string PlatformGlobalName = "_zenkins_platform";

    /// <summary>
    /// A pipeline file is hand-written source; half a megabyte is far beyond any real definition and
    /// keeps a repository from handing the worker an unbounded string to compile.
    /// </summary>
    public const int MaxSourceBytes = 512 * 1024;

    /// <summary>
    /// The SDK's I/O primitives: they need the host to do real work, which only a step may ask for.
    /// <c>env</c> and <c>secret</c> are deliberately <b>not</b> here: since F-015 they answer with a deferred
    /// reference instead of touching anything (see <see cref="StepReference"/>).
    /// </summary>
    private static readonly string[] IoPrimitives = ["run", "shell", "checkout", "artifact"];


    private readonly ILuaRuntime _runtime;
    private readonly SecretReferenceCapture _secretReferences;

    /// <param name="runtime">The definition runtime (see <see cref="CreateRuntime"/>).</param>
    /// <param name="secretReferences">
    /// Where the runtime records the <c>secret()</c> names it saw (card D-006). The same instance must be
    /// the one <see cref="CreateRuntime"/> was given, so the sandbox's calls are what the frozen record
    /// carries. Optional for a caller that only wants the spec.
    /// </param>
    public PipelineDefinitionRunner(ILuaRuntime runtime, SecretReferenceCapture? secretReferences = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _secretReferences = secretReferences ?? new SecretReferenceCapture();
    }

    /// <summary>
    /// How the definition source is read. Production reads the file; the test seam exists so "the
    /// pipeline file is read exactly once per run" is an assertion, not a claim (TC-004 depends on it).
    /// </summary>
    internal Func<string, string> ReadSource { get; init; } = static path => File.ReadAllText(path);

    /// <summary>
    /// The prelude's Lua source, read from the worker's embedded resources. Loaded once per process;
    /// the golden test asserts the resource is really shipped in the binary, not merely on disk.
    /// </summary>
    public static string PreludeSource { get; } = LoadPrelude();

    /// <summary>
    /// Builds the runtime a definition runs on: F-011's runtime plus the primitives that exist only to
    /// refuse (<c>run</c>, <c>shell</c>, <c>checkout</c>, <c>artifact</c>) and the two that answer with a
    /// deferred reference (<c>env</c>, <c>secret</c>).
    /// </summary>
    /// <param name="logSink">Where the script's <c>print</c>/<c>log</c> lines go (the run's log).</param>
    /// <param name="secretReferences">
    /// Where every <c>secret("NAME")</c> call is recorded (card D-006); pass the same instance to the
    /// <see cref="PipelineDefinitionRunner"/> so the capture reaches the frozen spec.
    /// </param>
    public static ILuaRuntime CreateRuntime(LuaLogSink logSink, SecretReferenceCapture? secretReferences = null)
    {
        ArgumentNullException.ThrowIfNull(logSink);
        var runtime = new LuaRuntime(logSink);
        foreach (var name in IoPrimitives)
        {
            runtime.RegisterHostFunction(name, _ => RefuseIo(name));
        }

        // F-015: the definition phase cannot read the run's environment or decrypt a secret - neither exists
        // yet, and describing a pipeline does no I/O - so both answer with a deferred reference the C#
        // executor substitutes at the step boundary (docs/lua-runtime.md §6/§7).
        runtime.RegisterHostFunction(
            "env",
            call => Reference(call, StepReference.EnvironmentPrefix, "env"));

        // D-006: the one moment every secret() call is visible. The name is recorded, the value is not
        // fetched: the reference stays deferred, exactly as before.
        runtime.RegisterHostFunction(
            "secret",
            call => Reference(call, StepReference.SecretPrefix, "secret", secretReferences));

        return runtime;
    }

    /// <summary>
    /// The deferred reference an <c>env()</c>/<c>secret()</c> call yields while a pipeline is being
    /// described. A malformed argument is the script's mistake and is reported at its call site, positioned
    /// like any other Lua error, rather than becoming an empty string that fails somewhere unrelated.
    /// </summary>
    private static LuaHostResult Reference(
        LuaHostCall call,
        string prefix,
        string name,
        SecretReferenceCapture? secretReferences = null)
    {
        var argument = call.FirstArgument;
        var text = argument is { Kind: LuaHostValueKind.String } value ? value.Text : null;
        if (!StepReference.IsValidName(text))
        {
            return LuaHostResult.Fail(
                $"{name}() takes one environment-variable-shaped name, for example {name}(\"NAME\").");
        }

        // D-006: record what the definition references, at the call site, while the name is known.
        secretReferences?.Record(text);

        return LuaHostResult.Value(LuaHostValue.FromString(prefix + text + StepReference.Terminator));
    }

    /// <summary>
    /// Runs the definition phase for one run.
    /// </summary>
    /// <param name="work">The run's work directory (its <c>repository/</c> holds the checkout).</param>
    /// <param name="pipelinePath">The project's pipeline path, relative to the repository root.</param>
    /// <param name="deadline">The run's deadline (F-009), which bounds the script.</param>
    /// <param name="cancellationToken">The run's cancellation token.</param>
    /// <exception cref="PipelineDefinitionException">The definition is missing or invalid.</exception>
    /// <exception cref="OperationCanceledException">The run was canceled while defining.</exception>
    public PipelineDefinitionResult Run(
        RunWorkDirectory work,
        string pipelinePath,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelinePath);

        var path = ResolveInsideRepository(work.RepositoryPath, pipelinePath);
        if (!File.Exists(path))
        {
            throw new PipelineDefinitionException(
                $"the pipeline file '{pipelinePath}' was not found in the checked-out repository "
                + "(check the project's Pipeline Path and that the file is committed).",
                PipelineFailureReasons.PipelineMissing);
        }

        var info = new FileInfo(path);
        if (info.Length > MaxSourceBytes)
        {
            throw new PipelineDefinitionException(
                $"the pipeline file '{pipelinePath}' is larger than {MaxSourceBytes} bytes.",
                PipelineFailureReasons.PipelineInvalid);
        }

        return RunSource(
            ReadSource(path),
            Path.GetFileName(pipelinePath),
            deadline,
            cancellationToken);
    }

    /// <summary>
    /// Runs the definition phase for an <b>inline</b> Pipeline run (card F-022): the script was
    /// snapshotted on the run, so the Worker materialises it at
    /// <c>work/{run-id}/temp/pipeline.lua</c> and describes that file. The on-disk name is what keeps
    /// the diagnostic (<c>pipeline.lua:(line,col)</c>) and the frozen spec's name identical to a
    /// repository run's - only the bytes' origin changed.
    /// <para>
    /// <b>The same taxonomy, deliberately.</b> A syntax error is <c>pipeline-syntax</c> and a raising
    /// or rejected definition is <c>pipeline-invalid</c>, exactly as for a repository file: AC-2's
    /// "behaves exactly as a repository pipeline" would be false if the source changed the reason a
    /// run carries. The card records that its TC-003 prose said <c>pipeline-invalid</c> for a syntax
    /// error; the implementation follows the shipped taxonomy instead.
    /// </para>
    /// </summary>
    public PipelineDefinitionResult RunInline(
        RunWorkDirectory work,
        string source,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(source);

        var bytes = System.Text.Encoding.UTF8.GetBytes(source);
        if (bytes.Length > PipelineScriptLimits.MaxScriptBytes)
        {
            throw new PipelineDefinitionException(
                $"the inline pipeline script is larger than {PipelineScriptLimits.MaxScriptBytes} bytes.",
                PipelineFailureReasons.PipelineInvalid);
        }

        Directory.CreateDirectory(work.TempPath);
        var path = Path.Combine(work.TempPath, PipelineScriptLimits.InlineFileName);

        // UTF-8 without a BOM, byte for byte: the diagnostic's column must not shift by a preamble, and
        // the file is the artifact a person can inspect after a failure.
        File.WriteAllText(path, source, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return RunSource(
            ReadSource(path),
            PipelineScriptLimits.InlineFileName,
            deadline,
            cancellationToken);
    }

    /// <summary>
    /// The definition phase over one source text - the half <see cref="Run"/> and <see cref="RunInline"/>
    /// share, and what a definition-only validation calls (card F-022).
    /// </summary>
    public PipelineDefinitionResult RunSource(
        string source,
        string chunkName,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkName);

        LuaExecutionResult result;
        try
        {
            result = _runtime.Execute(
                source,
                chunkName,
                deadline,
                cancellationToken,
                new LuaExecutionOptions
                {
                    PreludeSource = PreludeSource,
                    ResultGlobal = PipelineGlobalName,
                    Globals = new Dictionary<string, string> { [PlatformGlobalName] = PlatformName() },
                });
        }
        catch (LuaScriptException failure)
        {
            // A compile failure is pipeline-syntax (the message already carries source:(line,col)); a
            // script that raises while declaring - including a refused I/O primitive - could not
            // produce a valid spec, which is pipeline-invalid. The same taxonomy serves both sources.
            throw new PipelineDefinitionException(
                failure.Message,
                failure.Kind == LuaScriptErrorKind.Syntax
                    ? PipelineFailureReasons.PipelineSyntax
                    : PipelineFailureReasons.PipelineInvalid,
                failure);
        }

        switch (result.Outcome)
        {
            case LuaExecutionOutcome.TimedOut:
                throw new PipelineDefinitionException(
                    $"the pipeline definition did not finish within the run's deadline; it was abandoned "
                    + $"(see docs/lua-runtime.md §5.3).",
                    PipelineFailureReasons.PipelineTimeout);

            case LuaExecutionOutcome.Canceled:
                throw new OperationCanceledException(
                    "the run was canceled while its pipeline definition was being evaluated.",
                    cancellationToken);

            case LuaExecutionOutcome.ScriptViolation:
                throw new PipelineDefinitionException(
                    $"'{chunkName}' suspended itself (coroutine.yield) while being described; "
                    + "a pipeline definition must return.",
                    PipelineFailureReasons.PipelineInvalid);
        }

        var declared = PipelineSpecReader.Read(result.Result ?? LuaHostValue.Nil);

        // D-006: attach what this definition references before freezing, so the reference list travels with
        // the record the server stores (and the Secrets page can answer "which pipelines use this name"
        // without ever reading a repository).
        var spec = declared with { SecretReferences = CollectSecretReferences(declared) };
        var specJson = PipelineSpecJson.Serialize(spec);
        if (specJson.Length > PipelineSpecSchema.MaxJsonBytes)
        {
            // The server refuses an over-long spec (its envelope gate), so a run whose definition is
            // that large must not start: failing here keeps the two sides in agreement.
            throw new PipelineDefinitionException(
                $"the pipeline definition serialises to {specJson.Length} bytes, which is over the "
                + $"{PipelineSpecSchema.MaxJsonBytes}-byte limit.",
                PipelineFailureReasons.PipelineInvalid);
        }

        return new PipelineDefinitionResult(spec, specJson);
    }

    /// <summary>
    /// The secret names this definition references (card D-006), collected from the two places they become
    /// known:
    /// <list type="number">
    /// <item>the sandbox's own record of every <c>secret("NAME")</c> call this run made - drained, so one
    /// run can never inherit another's;</item>
    /// <item>every <c>${secret:NAME}</c> marker in a frozen value (a step's argv, a pipeline or step
    /// <c>env</c> value), because the executor substitutes exactly those - a reference a script pasted as text
    /// is therefore not silently missing from the list.</item>
    /// </list>
    /// The union is ordinally ordered and holds <b>names only</b>, never a value. Entries no stored secret
    /// could have (a marker whose name is not a legal secret name) are dropped rather than raised: this is
    /// advisory metadata about names, so dirty input must not turn a valid definition into a failed run.
    /// </summary>
    private string[] CollectSecretReferences(PipelineSpec spec)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);

        void AddFrom(string? value)
        {
            // F-040: the exact reference, not just the name - `NAME.password` and `file:NAME` must reach the
            // record, or the Secrets page would claim a pipeline that reads a pair's password does not use it.
            foreach (var recorded in StepReference.ReferencesIn(value))
            {
                if (SecretReference.TryParseRecorded(recorded, out var reference))
                {
                    names.Add(reference.Recorded);
                }
            }
        }

        foreach (var captured in _secretReferences.Drain())
        {
            // Already filtered at the call site; re-applying the one predicate keeps a single rule here.
            if (PipelineSpecSchema.IsSecretReferenceName(captured))
            {
                names.Add(captured);
            }
        }

        if (spec.Env is { } pipelineEnv)
        {
            foreach (var (_, value) in pipelineEnv)
            {
                AddFrom(value);
            }
        }

        foreach (var step in spec.Steps)
        {
            foreach (var argument in step.Argv)
            {
                AddFrom(argument);
            }

            if (step.Env is { } stepEnv)
            {
                foreach (var (_, value) in stepEnv)
                {
                    AddFrom(value);
                }
            }
        }

        return [.. names];
    }

    /// <summary>
    /// The platform string the prelude's <c>is_windows</c>/<c>is_linux</c>/<c>is_macos</c> compare
    /// against. The worker knows the OS; the prelude, being pure Lua, does not.
    /// </summary>
    private static string PlatformName() =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsMacOS() ? "macos"
        : "unknown";

    /// <summary>
    /// The refusal an I/O primitive answers with in the definition phase: a Lua error at the call site,
    /// so the script (and whoever reads the run log) learns the rule instead of meeting a null reference.
    /// </summary>
    private static LuaHostResult RefuseIo(string name) => LuaHostResult.Fail(
        name == "artifact"
            // Card F-016: steps are C# argv, so a Lua artifact() call is unreachable by construction - the
            // declarative field on the step is the only way to publish. The message says exactly that instead
            // of leaving a pipeline author guessing (and it keeps the shared "inside steps only" wording).
            ? "artifact() is available inside steps only: declare the paths on the step "
                + "(artifacts = { \"bin/Release\" }) and the executor publishes them after the step succeeds."
            : $"{name}() is available inside steps only: a pipeline definition describes the steps, "
                + "it does not execute anything.");

    /// <summary>
    /// Resolves <paramref name="relativePath"/> inside <paramref name="repositoryPath"/>, refusing a
    /// path that escapes it - including through a symlink, since the checkout is untrusted content.
    /// </summary>
    private static string ResolveInsideRepository(string repositoryPath, string relativePath)
    {
        var root = Path.GetFullPath(repositoryPath);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(EnsureTrailingSeparator(root), StringComparison.Ordinal))
        {
            throw new PipelineDefinitionException(
                $"the pipeline path '{relativePath}' leaves the checked-out repository.",
                PipelineFailureReasons.PipelineInvalid);
        }

        if (!File.Exists(full))
        {
            return full;
        }

        // A symlinked pipeline file is followed by the read below, so the link target is what must
        // stay inside the repository - a repository must not be able to make the worker read
        // /etc/shadow through pipeline.lua.
        var resolved = File.ResolveLinkTarget(full, returnFinalTarget: true);
        if (resolved is not null
            && !Path.GetFullPath(resolved.FullName)
                .StartsWith(EnsureTrailingSeparator(root), StringComparison.Ordinal))
        {
            throw new PipelineDefinitionException(
                $"the pipeline file '{relativePath}' links outside the checked-out repository.",
                PipelineFailureReasons.PipelineInvalid);
        }

        return full;
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static string LoadPrelude()
    {
        var assembly = typeof(PipelineDefinitionRunner).Assembly;
        using var stream = assembly.GetManifestResourceStream(PreludeResourceName)
            ?? throw new InvalidOperationException(
                $"The Lua prelude '{PreludeResourceName}' is not embedded in {assembly.GetName().Name}. "
                + "The worker csproj must declare it as an EmbeddedResource with that logical name.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The embedded resource names, for the golden test that pins what the binary ships.</summary>
    public static IReadOnlyList<string> EmbeddedResourceNames() =>
        typeof(PipelineDefinitionRunner).Assembly.GetManifestResourceNames();
}
