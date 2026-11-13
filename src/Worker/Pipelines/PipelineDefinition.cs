using Zenkins.Shared.Pipelines;
using Zenkins.Worker.Lua;

namespace Zenkins.Worker.Pipelines;

/// <summary>What the definition phase produced: the validated spec and its frozen JSON (card F-014).</summary>
/// <param name="Spec">The spec the executor runs.</param>
/// <param name="SpecJson">The same spec serialised - what the server stores and the page renders.</param>
public sealed record PipelineDefinitionResult(PipelineSpec Spec, string SpecJson);

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
    /// </summary>
    private static readonly string[] IoPrimitives = ["run", "shell", "checkout", "artifact", "secret"];

    /// <summary>
    /// The SDK helpers that belong to a step's lifetime rather than to a pipeline's description:
    /// <c>env</c> reads the run's environment, which a definition cannot have yet (F-015 wires it). It is
    /// refused for a different reason than the I/O primitives, and says so.
    /// </summary>
    private static readonly string[] StepTimeHelpers = ["env"];

    private readonly ILuaRuntime _runtime;

    public PipelineDefinitionRunner(ILuaRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
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
    /// refuse (<c>run</c>, <c>shell</c>, <c>checkout</c>, <c>artifact</c>, <c>secret</c>, <c>env</c>).
    /// </summary>
    /// <param name="logSink">Where the script's <c>print</c>/<c>log</c> lines go (the run's log).</param>
    public static ILuaRuntime CreateRuntime(LuaLogSink logSink)
    {
        ArgumentNullException.ThrowIfNull(logSink);
        var runtime = new LuaRuntime(logSink);
        foreach (var name in IoPrimitives)
        {
            runtime.RegisterHostFunction(name, _ => RefuseIo(name));
        }

        foreach (var name in StepTimeHelpers)
        {
            runtime.RegisterHostFunction(name, _ => RefuseStepTime(name));
        }

        return runtime;
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

        var source = ReadSource(path);
        var chunkName = Path.GetFileName(pipelinePath);

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
            // produce a valid spec, which is pipeline-invalid.
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

        var spec = PipelineSpecReader.Read(result.Result ?? LuaHostValue.Nil);
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
        $"{name}() is available inside steps only: a pipeline definition describes the steps, "
        + "it does not execute anything.");

    /// <summary>
    /// The refusal for a step-time helper such as <c>env()</c>: not an I/O primitive (§6 calls it a
    /// synchronous helper), but it reads the run's environment, which does not exist while the pipeline
    /// is being described.
    /// </summary>
    private static LuaHostResult RefuseStepTime(string name) => LuaHostResult.Fail(
        $"{name}() is a step-time helper: it reads the run's environment while a step runs, so it "
        + "cannot be used while the pipeline is being described.");

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
