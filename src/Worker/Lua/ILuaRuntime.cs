using System.Globalization;

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
/// The outcome of one script execution (card F-011, extended by F-014).
/// <para>
/// <see cref="Value"/> is the script's result rendered for a log line (<c>return 'pipeline-ok'</c> →
/// <c>pipeline-ok</c>); it is <see langword="null"/> for every non-completed outcome and for a script
/// that returns nothing. <see cref="Result"/> is the same result <b>structurally</b>: the scalar it
/// returned, or - for F-014's definition phase - the <see cref="LuaHostTable"/> the script built, so
/// the host can validate and serialise a spec table instead of parsing a string the script rendered
/// (<c>docs/lua-runtime.md</c> §6.1). Both are engine-free values from the seam.
/// </para>
/// </summary>
public sealed record LuaExecutionResult(
    LuaExecutionOutcome Outcome,
    string? Value = null,
    LuaHostValue? Result = null)
{
    /// <summary>Whether the script ran to completion.</summary>
    public bool IsCompleted => Outcome == LuaExecutionOutcome.Completed;
}

/// <summary>
/// Per-execution extras the driver honours (card F-014): a chunk run before the script, and the name
/// of a global to read the result from when the script returns nothing.
/// <para>
/// <b>Why a prelude chunk instead of string concatenation.</b> The pipeline prelude
/// (<c>src/Worker/Lua/prelude.lua</c>) defines <c>pipeline</c>/<c>step</c>/<c>log</c>/<c>is_*</c> as
/// ordinary Lua functions; loading it as its own chunk keeps every position in a script error relative
/// to <em>the user's</em> file (<c>pipeline.lua:(2,10-11)</c>), which concatenation would shift by the
/// prelude's length. It also keeps the prelude out of the user's <c>_G</c> iteration order and out of a
/// syntax error's blame.
/// </para>
/// <para>
/// <b>Why <see cref="ResultGlobal"/>.</b> The declarative form in PRD §3.3.2 is a bare
/// <c>pipeline { … }</c> statement with no <c>return</c>, so the host needs a place to read the table
/// from. The prelude's <c>pipeline()</c> stores it under that global; a script that does
/// <c>return pipeline { … }</c> works too - a returned value wins over the global.
/// </para>
/// </summary>
public sealed record LuaExecutionOptions
{
    /// <summary>The chunk loaded (and run to completion) before the script, or <see langword="null"/>.</summary>
    public string? PreludeSource { get; init; }

    /// <summary>The prelude's chunk name in an error message; defaults to <c>prelude.lua</c>.</summary>
    public string PreludeName { get; init; } = "prelude.lua";

    /// <summary>
    /// The global whose value becomes <see cref="LuaExecutionResult.Result"/> when the script returns
    /// nothing, or <see langword="null"/> to require an explicit return.
    /// </summary>
    public string? ResultGlobal { get; init; }

    /// <summary>
    /// Globals the host defines before the prelude runs (card F-014): the pipeline prelude is ordinary
    /// Lua with no host access, so the platform it reports through <c>is_windows</c>/<c>is_linux</c>/
    /// <c>is_macos</c> is handed to it as one string global. Values are strings only - there is no
    /// general bridge, and nothing here is script-writable before the prelude reads it.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Globals { get; init; }
}

/// <summary>
/// The kind of value a host primitive exchanges with a script (card F-011, <c>docs/lua-runtime.md</c>
/// §5.2). The primitive answer direction is a closed set of scalars plus tables: <c>nil</c>, string,
/// number, boolean and - since F-014's definition phase - a <see cref="LuaHostTable"/> description that
/// the runtime materialises as a real Lua table. No engine object ever crosses the seam, and no live
/// CLR object is ever handed to a script.
/// </summary>
public enum LuaHostValueKind
{
    /// <summary>Lua <c>nil</c>. Also the default of <see cref="LuaHostValue"/>.</summary>
    Nil = 0,

    String = 1,

    Number = 2,

    Boolean = 3,

    /// <summary>
    /// Lua table: a <see cref="LuaHostTable"/> (sequence or string-keyed map, nestable). Built as a
    /// real Lua table when it crosses the seam - never a live CLR object (§5.2).
    /// </summary>
    Table = 4,
}

/// <summary>
/// One value a host primitive exchanges with a script (card F-011, <c>docs/lua-runtime.md</c> §5.2):
/// arguments arrive as the kind the script passed, answers resume the coroutine as the kind the
/// primitive chose.
/// <para>
/// <b>Why not a string.</b> A boolean coerced to text is a silent trap in Lua: <c>false</c> becomes
/// <c>"false"</c>, which is a truthy string, so <c>if is_windows() then</c> would always take the
/// <c>then</c> branch, and an exit code coerced to text never compares equal to <c>0</c>. The type is
/// part of the value, so the seam carries it: <see cref="FromBoolean"/>, <see cref="FromNumber"/>,
/// <see cref="FromString"/> and <see cref="Nil"/>.
/// </para>
/// <para>
/// The payload accessors are kind-checked: <see cref="Text"/> on a number throws instead of returning
/// <see langword="null"/>, so a primitive that guesses the kind fails loudly rather than reading a
/// default. <see cref="ToString"/> renders the value for logs and messages only (it is not Lua's own
/// number formatting).
/// </para>
/// <para>
/// <b>Tables.</b> §5.2 allows a resumed value to be a table built by the host through the seam. That
/// is <see cref="FromTable"/> with a <see cref="LuaHostTable"/>: an immutable description that the
/// runtime materialises as a real Lua table when this resume delivers it. The description never
/// holds an engine object, so the seam stays engine-free; the table it becomes belongs to the
/// execution that is resuming and is never pooled or reused across scripts (§5.1).
/// </para>
/// </summary>
public readonly struct LuaHostValue : IEquatable<LuaHostValue>
{
    private readonly string? _text;
    private readonly double _number;
    private readonly bool _boolean;
    private readonly LuaHostTable? _table;

    private LuaHostValue(
        LuaHostValueKind kind,
        string? text,
        double number,
        bool boolean,
        LuaHostTable? table)
    {
        Kind = kind;
        _text = text;
        _number = number;
        _boolean = boolean;
        _table = table;
    }

    /// <summary>Which scalar this value is.</summary>
    public LuaHostValueKind Kind { get; }

    /// <summary>Whether this value is Lua <c>nil</c>.</summary>
    public bool IsNil => Kind == LuaHostValueKind.Nil;

    /// <summary>The string payload; throws <see cref="InvalidOperationException"/> for another kind.</summary>
    public string Text => Kind == LuaHostValueKind.String
        ? _text!
        : throw WrongKind("a string");

    /// <summary>The number payload; throws <see cref="InvalidOperationException"/> for another kind.</summary>
    public double Number => Kind == LuaHostValueKind.Number ? _number : throw WrongKind("a number");

    /// <summary>The boolean payload; throws <see cref="InvalidOperationException"/> for another kind.</summary>
    public bool Boolean => Kind == LuaHostValueKind.Boolean ? _boolean : throw WrongKind("a boolean");

    /// <summary>The table payload; throws <see cref="InvalidOperationException"/> for another kind.</summary>
    public LuaHostTable Table => Kind == LuaHostValueKind.Table ? _table! : throw WrongKind("a table");

    /// <summary>Lua <c>nil</c>; equal to <c>default(LuaHostValue)</c>.</summary>
    public static LuaHostValue Nil => default;

    /// <summary>A Lua string.</summary>
    public static LuaHostValue FromString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new LuaHostValue(LuaHostValueKind.String, text, 0, false, null);
    }

    /// <summary>A Lua number (Lua 5.2 has one number type; MoonSharp stores it as a double).</summary>
    public static LuaHostValue FromNumber(double number) =>
        new(LuaHostValueKind.Number, null, number, false, null);

    /// <summary>A Lua boolean.</summary>
    public static LuaHostValue FromBoolean(bool value) =>
        new(LuaHostValueKind.Boolean, null, 0, value, null);

    /// <summary>
    /// A Lua table described by <paramref name="table"/>. The runtime builds a real Lua table from the
    /// description for the resume that carries it (sequence → integer keys <c>1..n</c>, map → string
    /// keys, nested tables recurse); the description itself is engine-free and immutable.
    /// </summary>
    public static LuaHostValue FromTable(LuaHostTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return new LuaHostValue(LuaHostValueKind.Table, null, 0, false, table);
    }

    /// <inheritdoc />
    public bool Equals(LuaHostValue other) => Kind switch
    {
        LuaHostValueKind.Nil => other.Kind == LuaHostValueKind.Nil,
        LuaHostValueKind.String => other.Kind == LuaHostValueKind.String
            && string.Equals(_text, other._text, StringComparison.Ordinal),
        LuaHostValueKind.Number => other.Kind == LuaHostValueKind.Number && _number.Equals(other._number),
        LuaHostValueKind.Boolean => other.Kind == LuaHostValueKind.Boolean && _boolean == other._boolean,
        // A table value is a reference to its description: deep equality would be surprising and
        // expensive, and tests assert through Lua semantics instead.
        LuaHostValueKind.Table => other.Kind == LuaHostValueKind.Table
            && ReferenceEquals(_table, other._table),
        _ => false,
    };

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LuaHostValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Kind switch
    {
        LuaHostValueKind.String => HashCode.Combine(Kind, StringComparer.Ordinal.GetHashCode(_text!)),
        LuaHostValueKind.Number => HashCode.Combine(Kind, _number),
        LuaHostValueKind.Boolean => HashCode.Combine(Kind, _boolean),
        LuaHostValueKind.Table => HashCode.Combine(Kind, _table),
        _ => Kind.GetHashCode(),
    };

    public static bool operator ==(LuaHostValue left, LuaHostValue right) => left.Equals(right);

    public static bool operator !=(LuaHostValue left, LuaHostValue right) => !left.Equals(right);

    /// <summary>The value as text for a log line; not Lua's own rendering of numbers.</summary>
    public override string ToString() => Kind switch
    {
        LuaHostValueKind.Nil => "nil",
        LuaHostValueKind.String => _text!,
        LuaHostValueKind.Number => _number.ToString("R", CultureInfo.InvariantCulture),
        LuaHostValueKind.Boolean => _boolean ? "true" : "false",
        LuaHostValueKind.Table => "table",
        _ => Kind.ToString(),
    };

    private InvalidOperationException WrongKind(string expected) => new(
        $"This {nameof(LuaHostValue)} is {Kind}, not {expected}. Check Kind before reading the payload.");
}

/// <summary>
/// An immutable description of a Lua table a host primitive returns through the seam (card F-011,
/// <c>docs/lua-runtime.md</c> §5.2: "anything richer is a <c>Table</c> built by the host through the
/// seam, never a live CLR object"). Two shapes cover what the SDK surface needs:
/// <list type="bullet">
/// <item><see cref="Sequence"/> - an ordered list, materialised as a Lua table with integer keys
/// <c>1..n</c>, so <c>#v</c> is the length and <c>ipairs</c> walks it.</item>
/// <item><see cref="Map"/> - string keys to values, materialised with those keys, so <c>v.name</c>,
/// <c>v["name"]</c> and <c>pairs(v)</c> all work.</item>
/// </list>
/// Values may be nested tables, so a primitive can describe structured data (a step list, a process
/// result) without a CLR object ever crossing into the sandbox.
/// <para>
/// <b>Lifetime.</b> The description is immutable and engine-free. The runtime builds a fresh Lua table
/// from it for the resume that delivers the value, inside the executing <c>Script</c>; the table is
/// never pooled, cached or reused across resumes or scripts (§5.1: one <c>Script</c> per execution).
/// </para>
/// </summary>
public sealed class LuaHostTable
{
    // ReadOnlyCollection wrappers: the description copies its input and cannot be mutated through the
    // exposed views, so a primitive can hand the same value to several resumes without aliasing.
    private readonly IReadOnlyList<LuaHostValue>? _items;
    private readonly IReadOnlyList<KeyValuePair<string, LuaHostValue>>? _entries;

    private LuaHostTable(
        IReadOnlyList<LuaHostValue>? items,
        IReadOnlyList<KeyValuePair<string, LuaHostValue>>? entries)
    {
        _items = items;
        _entries = entries;
    }

    /// <summary>Whether this describes a sequence (integer keys <c>1..n</c>) rather than a map.</summary>
    public bool IsSequence => _items is not null;

    /// <summary>The sequence's values in order; throws <see cref="InvalidOperationException"/> for a map.</summary>
    public IReadOnlyList<LuaHostValue> Items => _items
        ?? throw new InvalidOperationException(
            $"This {nameof(LuaHostTable)} is a map, not a sequence; read {nameof(Entries)} instead.");

    /// <summary>The map's entries in insertion order; throws <see cref="InvalidOperationException"/> for a sequence.</summary>
    public IReadOnlyList<KeyValuePair<string, LuaHostValue>> Entries => _entries
        ?? throw new InvalidOperationException(
            $"This {nameof(LuaHostTable)} is a sequence, not a map; read {nameof(Items)} instead.");

    /// <summary>
    /// A sequence of values. It becomes a Lua table with integer keys <c>1..n</c> and no holes, so
    /// <c>#v</c> and <c>ipairs(v)</c> agree with the description.
    /// </summary>
    public static LuaHostTable Sequence(params LuaHostValue[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new LuaHostTable(Array.AsReadOnly([.. items]), null);
    }

    /// <summary>
    /// A map from string keys to values. Keys must be non-empty and unique: a duplicate would silently
    /// overwrite its predecessor in Lua, so it is rejected here instead.
    /// </summary>
    public static LuaHostTable Map(params (string Key, LuaHostValue Value)[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var copy = new KeyValuePair<string, LuaHostValue>[entries.Length];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Length; i++)
        {
            var (key, value) = entries[i];
            ArgumentException.ThrowIfNullOrEmpty(key);
            if (!seen.Add(key))
            {
                throw new ArgumentException($"Duplicate table key '{key}'.", nameof(entries));
            }

            copy[i] = new KeyValuePair<string, LuaHostValue>(key, value);
        }

        return new LuaHostTable(null, Array.AsReadOnly(copy));
    }
}

/// <summary>
/// One call from a script into a host primitive (card F-011, <c>docs/lua-runtime.md</c> §5.2). Each
/// argument arrives with the kind the script passed (<c>LuaHostValueKind.String</c>, <c>Number</c>,
/// <c>Boolean</c> or <c>Nil</c>). The argument direction carries scalars only - the SDK primitives take
/// names, text and numbers - so a table/function/user-data argument is delivered as
/// <see cref="LuaHostValue.Nil"/> rather than coerced into a string a primitive could mistake for a
/// scalar. Tables travel in the two host-controlled directions: host → script as a primitive's answer
/// (§5.2), and script → host as a definition's result (F-014's structured return channel, which is how
/// <c>pipeline { … }</c> reaches the host as data instead of as a JSON string the script rendered).
/// </summary>
public sealed record LuaHostCall(string Name, IReadOnlyList<LuaHostValue> Arguments)
{
    /// <summary>The first argument, or <see langword="null"/> when the script passed none.</summary>
    public LuaHostValue? FirstArgument => Arguments.Count > 0 ? Arguments[0] : null;
}

/// <summary>
/// What a host primitive wants the driver to do next (card F-011, <c>docs/lua-runtime.md</c> §5.1
/// and §5.2). A primitive either answers immediately (<see cref="Value"/>) or hands the driver a
/// suspension: the script pauses, the driver performs the work, and only then resumes the coroutine
/// with the answer - on the owning thread, with the Lua type the primitive chose (a returned boolean
/// stays a boolean, a number stays a number).
/// </summary>
public sealed class LuaHostResult
{
    private readonly LuaHostValue _value;
    private readonly Func<LuaHostValue>? _answer;
    private readonly string? _failure;

    private LuaHostResult(LuaHostValue value, Func<LuaHostValue>? answer, string? failure = null)
    {
        _value = value;
        _answer = answer;
        _failure = failure;
    }

    /// <summary>
    /// The primitive completed without touching anything asynchronous; the script continues with
    /// <paramref name="value"/> (<see cref="LuaHostValue.Nil"/> is Lua <c>nil</c>).
    /// </summary>
    public static LuaHostResult Value(LuaHostValue value) => new(value, null);

    /// <summary>
    /// The primitive needs the host to do real work. The driver suspends the coroutine, invokes
    /// <paramref name="answer"/> on the owning thread (it may block on I/O), and resumes with the
    /// typed value it returns. Yielding from a primitive is the only way a script can be resumed with
    /// an answer (§5.2); the request queue plus the driver's private sentinel tie the answer to this
    /// call.
    /// </summary>
    public static LuaHostResult Suspend(Func<LuaHostValue> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return new(default, answer);
    }

    /// <summary>Whether this result suspends the script (as opposed to answering immediately).</summary>
    public bool IsSuspended => _answer is not null;

    /// <summary>
    /// The primitive refused the call (card F-014): the engine raises a Lua error at the call site with
    /// <paramref name="message"/>, so the script's own <c>pcall</c> can catch it and the run log reports
    /// it with a position - never a silent <c>nil</c> that later fails somewhere unrelated. This is the
    /// engine-free way to say "this call is the script's mistake": no exception type from the engine
    /// crosses the seam.
    /// </summary>
    public static LuaHostResult Fail(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new LuaHostResult(default, null, message);
    }

    /// <summary>The refusal's message; <see langword="null"/> unless the primitive refused.</summary>
    internal string? Failure => _failure;

    /// <summary>The immediate answer; only meaningful when <see cref="IsSuspended"/> is false.</summary>
    internal LuaHostValue ImmediateValue => _value;

    /// <summary>Performs the suspended work; only called by the driver after the script paused.</summary>
    internal LuaHostValue ResolveAnswer() => _answer!();
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
    /// <param name="options">
    /// The per-execution extras (a prelude chunk and the global a definition is read from).
    /// <see langword="null"/> keeps F-011's behaviour exactly: the source runs alone and only its
    /// return value is reported.
    /// </param>
    LuaExecutionResult Execute(
        string source,
        string name,
        DateTimeOffset deadline,
        CancellationToken cancellationToken,
        LuaExecutionOptions? options = null);
}
