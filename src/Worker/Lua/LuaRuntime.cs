using System.Diagnostics;
using MoonSharp.Interpreter;

namespace Zenkins.Worker.Lua;

/// <summary>
/// The runtime's knobs (card F-011) plus the composition seam the conformance fixtures use. The two
/// knobs are deliberately not environment variables: neither is a per-deployment choice, and the run
/// budget production uses is the run's own deadline (<c>BuildRun.DeadlineAt</c>, F-009) -
/// <see cref="RunBudget"/> only covers a caller that passes <see cref="DateTimeOffset.MaxValue"/>. The
/// sibling pins for worker configuration are documented and asserted in
/// <c>tests/Zenkins.Tests/Workers/WorkerConfigurationTests.cs</c>.
/// </summary>
public sealed class LuaRuntimeOptions
{
    /// <summary>
    /// VM instructions between two host-controlled yield points (<c>AutoYieldCounter</c>). This is
    /// what makes a runaway script abandonable: measured at 10 000, <c>while true do end</c> returns
    /// control 6 000-9 000 times inside a 300 ms budget (<c>docs/lua-runtime.md</c> §2/§9 #14).
    /// Lower means tighter cancellation at more host round-trips; higher means fewer.
    /// </summary>
    public const int DefaultInstructionBudget = 10_000;

    /// <summary>
    /// The ceiling used when a caller passes <see cref="DateTimeOffset.MaxValue"/> as the deadline:
    /// no script may run unbounded, even if a caller forgets to pass the run's deadline.
    /// </summary>
    public static readonly TimeSpan DefaultRunBudget = TimeSpan.FromMinutes(5);

    /// <summary>Instructions between yield points; defaults to <see cref="DefaultInstructionBudget"/>.</summary>
    public int InstructionBudget { get; init; } = DefaultInstructionBudget;

    /// <summary>The fallback ceiling; defaults to <see cref="DefaultRunBudget"/>.</summary>
    public TimeSpan RunBudget { get; init; } = DefaultRunBudget;

    /// <summary>
    /// Extra engine modules composed on top of the contract composition
    /// (<c>docs/lua-runtime.md</c> §3). Production always leaves this <see cref="CoreModules.None"/>:
    /// it exists so the registry-hazard fixture can compose <see cref="CoreModules.Dynamic"/> and
    /// prove the post-construction hardening closes <c>package.loaded</c> anyway (§3.1, fixture 12b).
    /// </summary>
    public CoreModules AdditionalModules { get; init; } = CoreModules.None;
}

/// <summary>
/// The MoonSharp implementation of <see cref="ILuaRuntime"/> (card F-011): the contract composition
/// of <c>docs/lua-runtime.md</c> §3, the post-construction hardening of §3.1, and the host-driven
/// coroutine of §5.1.
/// <para>
/// <b>One <c>Script</c> per execution.</b> Every <see cref="Execute"/> builds a fresh sandbox on the
/// calling thread and discards it when the call returns - never pooled, never shared, never resumed
/// after abandonment. A runtime instance may serve many concurrent executions; they share only the
/// immutable primitive table (snapshotted per execution) and the run-log sink.
/// </para>
/// <para>
/// <b>Nothing here registers a CLR type.</b> No <c>UserData.RegisterType</c>/<c>RegisterAssembly</c>,
/// no proxies, no <c>dynamic</c>: the sandbox has no CLR bridge at all, so PRD §3.3.6's four
/// prohibitions hold by construction rather than by filtering (§3.2).
/// </para>
/// </summary>
public sealed class LuaRuntime : ILuaRuntime
{
    // The contract composition (docs/lua-runtime.md §3): explicit, never a preset. Preset_SoftSandbox
    // keeps the CLR bridge; Preset_HardSandbox drops pcall/xpcall/metatables/coroutine and all of os.
    // GlobalConsts is composed in on purpose: without it _G and _VERSION are nil.
    private static readonly CoreModules ContractModules =
        CoreModules.Basic | CoreModules.ErrorHandling | CoreModules.Metatables
        | CoreModules.Coroutine | CoreModules.String | CoreModules.Table
        | CoreModules.TableIterators | CoreModules.Math | CoreModules.Bit32
        | CoreModules.OS_Time | CoreModules.GlobalConsts;

    private readonly object _gate = new();
    private readonly Dictionary<string, Func<LuaHostCall, LuaHostResult>> _primitives =
        new(StringComparer.Ordinal);
    private readonly LuaLogSink _logSink;
    private readonly LuaRuntimeOptions _options;

    /// <summary>Creates a runtime with the production composition and the default knobs.</summary>
    public LuaRuntime(LuaLogSink logSink)
        : this(logSink, new LuaRuntimeOptions())
    {
    }

    /// <param name="logSink">Where script output goes (never the worker's console, §7).</param>
    /// <param name="options">The two knobs; defaults follow the contract.</param>
    public LuaRuntime(LuaLogSink logSink, LuaRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(logSink);
        ArgumentNullException.ThrowIfNull(options);
        if (options.InstructionBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.InstructionBudget,
                "InstructionBudget must be positive: the driver needs a yield point to observe cancellation and the budget.");
        }

        if (options.RunBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.RunBudget, "RunBudget must be positive.");
        }

        _logSink = logSink;
        _options = options;
    }

    /// <summary>The sink this runtime re-binds <c>print</c> to.</summary>
    public LuaLogSink LogSink => _logSink;

    /// <inheritdoc />
    public void RegisterHostFunction(string name, Func<LuaHostCall, LuaHostResult> primitive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(primitive);

        lock (_gate)
        {
            if (!_primitives.TryAdd(name, primitive))
            {
                throw new InvalidOperationException(
                    $"A host primitive named '{name}' is already registered on this runtime.");
            }
        }
    }

    /// <inheritdoc />
    public LuaExecutionResult Execute(
        string source,
        string name,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // The execution owns this thread from Script construction to abandonment (§5.1). The engine's
        // CheckThreadAccess does not enforce that (measured, fixture 19), so it is our discipline and
        // the driver re-checks it at every yield point.
        var ownerThread = Environment.CurrentManagedThreadId;
        var pending = new Queue<LuaHostResult>();
        var primitives = SnapshotPrimitives();
        var script = CreateSandbox(primitives, pending, out var sentinel);

        if (cancellationToken.IsCancellationRequested)
        {
            return new LuaExecutionResult(LuaExecutionOutcome.Canceled);
        }

        var budget = ResolveBudget(deadline);
        var stopwatch = Stopwatch.StartNew();
        if (budget <= TimeSpan.Zero)
        {
            return new LuaExecutionResult(LuaExecutionOutcome.TimedOut);
        }

        DynValue function;
        try
        {
            function = script.LoadString(source, null, name);
        }
        catch (SyntaxErrorException ex)
        {
            throw LuaScriptException.FromDecoratedMessage(ex.DecoratedMessage, name);
        }

        var coroutine = script.CreateCoroutine(function).Coroutine;
        coroutine.AutoYieldCounter = _options.InstructionBudget;

        DynValue result;
        try
        {
            result = coroutine.Resume();
            while (coroutine.State != CoroutineState.Dead)
            {
                EnsureOwningThread(ownerThread);

                if (cancellationToken.IsCancellationRequested)
                {
                    // Abandonment: stop resuming, discard the Script (done by returning). The
                    // coroutine is technically still resumable; we never resume it again (§5.1).
                    return new LuaExecutionResult(LuaExecutionOutcome.Canceled);
                }

                if (stopwatch.Elapsed >= budget)
                {
                    return new LuaExecutionResult(LuaExecutionOutcome.TimedOut);
                }

                switch (coroutine.State)
                {
                    case CoroutineState.ForceSuspended:
                        // The instruction budget handed control back: args MUST be empty (§5.1).
                        result = coroutine.Resume();
                        break;

                    case CoroutineState.Suspended:
                        // A suspension is not a host call. Branch on the request queue first and the
                        // driver's private sentinel second: the script's own coroutine.yield produces
                        // the same state with an empty queue, and dequeuing it would throw
                        // InvalidOperationException: Queue empty. — which is not a script error the
                        // loop can map, so it must be unreachable by construction (§5.1, fixture 18).
                        if (pending.Count == 0 || !IsHostSuspension(result, sentinel))
                        {
                            return new LuaExecutionResult(LuaExecutionOutcome.ScriptViolation);
                        }

                        var answer = pending.Dequeue().ResolveAnswer();
                        // The I/O already completed; Resume happens on this (the owning) thread and
                        // never inside an await/continuation (§5.1).
                        result = coroutine.Resume(DynValue.NewString(answer ?? string.Empty));
                        break;

                    default:
                        // No other state exists today; one we do not know is unsafe to resume.
                        return new LuaExecutionResult(LuaExecutionOutcome.ScriptViolation);
                }
            }
        }
        catch (ScriptRuntimeException ex)
        {
            throw LuaScriptException.FromDecoratedMessage(ex.DecoratedMessage, name);
        }
        catch (SyntaxErrorException ex)
        {
            throw LuaScriptException.FromDecoratedMessage(ex.DecoratedMessage, name);
        }

        return new LuaExecutionResult(LuaExecutionOutcome.Completed, RenderValue(result));
    }

    private static void EnsureOwningThread(int ownerThread)
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
        {
            throw new InvalidOperationException(
                "A Lua execution must stay on the thread that started it (docs/lua-runtime.md §5.1).");
        }
    }

    private TimeSpan ResolveBudget(DateTimeOffset deadline)
    {
        if (deadline == DateTimeOffset.MaxValue)
        {
            return _options.RunBudget;
        }

        var remaining = deadline - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static string? RenderValue(DynValue value) =>
        value.Type is DataType.Nil or DataType.Void ? null : value.ToPrintString();

    /// <summary>
    /// Whether a suspension came from a primitive rather than from the script's own
    /// <c>coroutine.yield</c>. The sentinel is a table the host creates per execution and never
    /// exposes to Lua, so Lua cannot produce a suspension that carries it - the queue fixes the
    /// count, the sentinel fixes the identity (§5.1).
    /// </summary>
    private static bool IsHostSuspension(DynValue result, Table sentinel) =>
        result.Type == DataType.Table && ReferenceEquals(result.Table, sentinel);

    private KeyValuePair<string, Func<LuaHostCall, LuaHostResult>>[] SnapshotPrimitives()
    {
        lock (_gate)
        {
            return [.. _primitives];
        }
    }

    private Script CreateSandbox(
        KeyValuePair<string, Func<LuaHostCall, LuaHostResult>>[] primitives,
        Queue<LuaHostResult> pending,
        out Table sentinel)
    {
        var script = new Script(ContractModules | _options.AdditionalModules);
        sentinel = new Table(script);
        Harden(script);

        // Binding is the only place a primitive reaches a script; the sentinel value handed back as
        // the yield's argument is how the driver recognises this suspension as a host call.
        var sentinelValue = DynValue.NewTable(sentinel);
        foreach (var (functionName, primitive) in primitives)
        {
            script.Globals[functionName] = new CallbackFunction(
                (_, args) => InvokePrimitive(functionName, primitive, args, pending, sentinelValue),
                functionName);
        }

        return script;
    }

    private static DynValue InvokePrimitive(
        string name,
        Func<LuaHostCall, LuaHostResult> primitive,
        CallbackArguments args,
        Queue<LuaHostResult> pending,
        DynValue sentinel)
    {
        var arguments = new string[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            arguments[i] = args[i].ToPrintString();
        }

        var result = primitive(new LuaHostCall(name, arguments));
        if (!result.IsSuspended)
        {
            return result.ImmediateValue is null ? DynValue.Nil : DynValue.NewString(result.ImmediateValue);
        }

        // Record the request before returning the yield: the driver answers the request it dequeues,
        // and the payload it shows the host is the sentinel, never the request itself (§5.2).
        pending.Enqueue(result);
        return DynValue.NewYieldReq([sentinel]);
    }

    /// <summary>
    /// The mandatory post-construction hardening (§3.1). <c>dynamic</c>/<c>json</c> cannot exist under
    /// the contract composition, but the assignments stay as the second line of defence - and
    /// nil-ing the globals is provably not enough on its own: <c>package.loaded</c> keeps a live table
    /// (<c>package.loaded.dynamic.eval('1+1')</c> → 2), so <c>package</c> itself is removed.
    /// </summary>
    private void Harden(Script script)
    {
        script.Globals["dynamic"] = DynValue.Nil;
        script.Globals["json"] = DynValue.Nil;
        script.Globals["package"] = DynValue.Nil;
        script.Globals["_MOONSHARP"] = DynValue.Nil;
        script.Globals["print"] = new CallbackFunction(
            (_, args) =>
            {
                _logSink.Write(JoinPrintArguments(args));
                return DynValue.Nil;
            },
            "print");
    }

    private static string JoinPrintArguments(CallbackArguments args)
    {
        if (args.Count == 0)
        {
            return string.Empty;
        }

        var parts = new string[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            parts[i] = args[i].ToPrintString();
        }

        // Lua's print separates its arguments with a tab.
        return string.Join('\t', parts);
    }
}
