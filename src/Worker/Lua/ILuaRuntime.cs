namespace Zenkins.Worker.Lua;

/// <summary>
/// How one <see cref="ILuaRuntime.Execute"/> call ended (card F-011). Only
/// <see cref="Completed"/> carries a value; the other three are terminal outcomes that the caller
/// reports on the run (never retries, <c>docs/lua-runtime.md</c> §5.3).
/// </summary>
public enum LuaExecutionOutcome
{
    /// <summary>The script ran to completion; <see cref="LuaExecutionResult.Value"/> is its result.</summary>
    Completed,

    /// <summary>
    /// The run's wall clock ran out (<see cref="LuaRuntimeOptions.RunBudget"/> when the caller has
    /// no deadline). The coroutine is abandoned: never resumed, its <c>Script</c> discarded (§5.1).
    /// </summary>
    TimedOut,

    /// <summary>
    /// The <see cref="CancellationToken"/> passed to <see cref="ILuaRuntime.Execute"/> fired. The
    /// token is observed at every yield point (every
    /// <see cref="LuaRuntimeOptions.InstructionBudget"/> VM instructions), so a script honours it
    /// within milliseconds even if it never calls a host primitive (§5.3). Abandoned like a timeout.
    /// </summary>
    Canceled,

    /// <summary>
    /// The driver refused to continue: the script suspended itself (its own <c>coroutine.yield</c>,
    /// which is not a host call, §5.1), or the coroutine reached a state the driver does not know.
    /// The run is abandoned - no empty request queue is ever dequeued.
    /// </summary>
    ScriptViolation,
}

/// <summary>
/// The outcome of one script execution. <see cref="Value"/> is the script's returned value rendered
/// for a log line (<c>return 'pipeline-ok'</c> → <c>pipeline-ok</c>); it is <see langword="null"/>
/// for every non-completed outcome and for a script that returns nothing.
/// </summary>
public sealed record LuaExecutionResult(LuaExecutionOutcome Outcome, string? Value = null)
{
    /// <summary>Whether the script ran to completion.</summary>
    public bool IsCompleted => Outcome == LuaExecutionOutcome.Completed;
}

/// <summary>
/// One call from a script into a host primitive (card F-011, <c>docs/lua-runtime.md</c> §5.2).
/// Values cross the seam as their script-visible text - an argument arrives as Lua renders it
/// (<c>'make build'</c>, <c>42</c>, <c>true</c>) and an answer resumes the coroutine as a string - so
/// F-014 builds any richer value host-side instead of handing a live CLR object to a script.
/// </summary>
public sealed record LuaHostCall(string Name, IReadOnlyList<string> Arguments)
{
    /// <summary>The first argument, or <see langword="null"/> when the script passed none.</summary>
    public string? FirstArgument => Arguments.Count > 0 ? Arguments[0] : null;
}

/// <summary>
/// What a host primitive wants the driver to do next (card F-011, <c>docs/lua-runtime.md</c> §5.1
/// and §5.2). A primitive either answers immediately (<see cref="Value"/>) or hands the driver a
/// suspension: the script pauses, the driver performs the work, and only then resumes the coroutine
/// with the answer - on the owning thread.
/// </summary>
public sealed class LuaHostResult
{
    private readonly string? _value;
    private readonly Func<string?>? _answer;

    private LuaHostResult(string? value, Func<string?>? answer)
    {
        _value = value;
        _answer = answer;
    }

    /// <summary>
    /// The primitive completed without touching anything asynchronous; the script continues with
    /// <paramref name="value"/> (<see langword="null"/> is Lua <c>nil</c>).
    /// </summary>
    public static LuaHostResult Value(string? value) => new(value, null);

    /// <summary>
    /// The primitive needs the host to do real work. The driver suspends the coroutine, invokes
    /// <paramref name="answer"/> on the owning thread (it may block on I/O), and resumes with its
    /// result. Yielding from a primitive is the only way a script can be resumed with an answer
    /// (§5.2); the request queue plus the driver's private sentinel tie the answer to this call.
    /// </summary>
    public static LuaHostResult Suspend(Func<string?> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return new(null, answer);
    }

    /// <summary>Whether this result suspends the script (as opposed to answering immediately).</summary>
    public bool IsSuspended => _answer is not null;

    /// <summary>The immediate answer; only meaningful when <see cref="IsSuspended"/> is false.</summary>
    internal string? ImmediateValue => _value;

    /// <summary>Performs the suspended work; only called by the driver after the script paused.</summary>
    internal string? ResolveAnswer() => _answer!();
}

/// <summary>
/// The Worker's engine seam (card F-011): the only place that knows a Lua engine exists. No MoonSharp
/// type appears in this file, and the pipeline/executor code (F-014 and later) talks to the runtime
/// through it - so swapping the engine (<c>docs/lua-runtime.md</c> §1) is an implementation change,
/// not a rewrite.
/// <para>
/// Thread affinity: one <see cref="Execute"/> call owns its own <c>Script</c> from construction to
/// abandonment, on the calling thread, forever (§5.1). A runtime instance may be shared by many
/// runs - each execution builds a fresh sandbox and shares only the immutable primitive table - but
/// a single execution must not be moved between threads.
/// </para>
/// </summary>
public interface ILuaRuntime
{
    /// <summary>
    /// Registers a host primitive (an SDK function such as <c>log</c> or <c>run</c>) under
    /// <paramref name="name"/>. Registration is expected before the first execution; duplicate
    /// registrations are refused rather than silently overwritten.
    /// </summary>
    void RegisterHostFunction(string name, Func<LuaHostCall, LuaHostResult> primitive);

    /// <summary>
    /// Runs <paramref name="source"/> as chunk <paramref name="name"/> under the hardened sandbox
    /// (§3) with a host-driven coroutine (§5.1).
    /// </summary>
    /// <param name="source">The Lua source, already read from disk by the caller.</param>
    /// <param name="name">
    /// The chunk name the script sees in errors (<c>pipeline.lua</c>); it is what
    /// <see cref="LuaScriptException.SourceName"/> and the message's <c>source:(line,col-col)</c>
    /// prefix report.
    /// </param>
    /// <param name="deadline">
    /// The run's remaining wall clock, i.e. <c>BuildRun.DeadlineAt</c> frozen at accept time by
    /// F-009 (§5.3). <see cref="DateTimeOffset.MaxValue"/> selects
    /// <see cref="LuaRuntimeOptions.RunBudget"/> instead.
    /// </param>
    /// <param name="cancellationToken">Observed at every yield point.</param>
    /// <returns>The outcome; only <see cref="LuaExecutionOutcome.Completed"/> carries a value.</returns>
    /// <exception cref="LuaScriptException">
    /// The script did not compile or raised at runtime. The exception's message is one
    /// <c>source:(line,col-col): message</c> line - no CLR stack trace.
    /// </exception>
    LuaExecutionResult Execute(
        string source,
        string name,
        DateTimeOffset deadline,
        CancellationToken cancellationToken);
}
