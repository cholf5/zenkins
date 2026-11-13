namespace Zenkins.Worker.Lua;

/// <summary>
/// The runtime's run-log sink (card F-011, <c>docs/lua-runtime.md</c> §7): every line a script emits
/// - through <c>print</c> or, later, an SDK <c>log()</c> primitive - lands here instead of the
/// worker's console. The engine's default <c>print</c> writes straight to <see cref="Console"/>,
/// where the line would be invisible in the UI and unredactable; re-binding it is part of the
/// hardening (§3.1).
/// <para>
/// The sink <b>forwards</b> every line to the caller's log pipeline (F-008's per-run buffer in
/// production) and <b>retains</b> the most recent
/// <see cref="DefaultRetainedLines"/> lines so a run's tail survives a script failure and tests can
/// assert what the script said. Retention is bounded on purpose: a chatty script must not be able to
/// grow worker memory line by line (§10.1 - the engine has no memory limit, so the host must not
/// introduce one). It is thread-safe: lines arrive from the thread that owns the execution, while a
/// streaming thread may read them.
/// </para>
/// </summary>
public sealed class LuaLogSink
{
    /// <summary>
    /// How many lines are kept for reading back. Mirrors the worker's
    /// <c>ZENKINS_LOG_BUFFER_LINES</c> default (10 000, F-008) so both buffers hold the same order of
    /// magnitude; forwarding is never dropped.
    /// </summary>
    public const int DefaultRetainedLines = 10_000;

    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();
    private readonly Action<string>? _forward;
    private readonly int _retainedLines;

    /// <param name="forward">
    /// Called once per line, after the line has been retained. Production passes the run's log
    /// writer; tests may pass nothing and read <see cref="Lines"/>.
    /// </param>
    /// <param name="retainedLines">How many of the most recent lines to keep; defaults to 10 000.</param>
    public LuaLogSink(Action<string>? forward = null, int retainedLines = DefaultRetainedLines)
    {
        if (retainedLines <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retainedLines), retainedLines, "retainedLines must be positive.");
        }

        _forward = forward;
        _retainedLines = retainedLines;
    }

    /// <summary>Retains one line (one line, no trailing newline) and forwards it.</summary>
    public void Write(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _retainedLines)
            {
                _lines.Dequeue();
            }
        }

        _forward?.Invoke(line);
    }

    /// <summary>A snapshot of the retained lines, oldest first.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    /// <summary>How many lines are currently retained (never more than the configured cap).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _lines.Count;
            }
        }
    }
}
