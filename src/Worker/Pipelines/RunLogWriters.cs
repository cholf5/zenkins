namespace Zenkins.Worker.Pipelines;

/// <summary>
/// One run's local log files (<c>temp/stdout.log</c> and <c>temp/stderr.log</c>): the debugging copies F-007
/// keeps beside the streamed log (cards F-014, F-015).
/// <para>
/// <b>Why this is a type and not two <see cref="StreamWriter"/> fields.</b> The lines arrive from three
/// threads - the pipeline thread (its own synthetic lines) and one reader thread per child stream - and
/// <see cref="StreamWriter"/> is not thread-safe. Writing through one gate keeps a line whole (no interleaved
/// halves, no lost newline) and keeps the two files per-stream ordered. F-014 wrote them without that gate;
/// this card's <c>secret()</c> work put far more lines through the same path, so the gate became load-bearing
/// and testable (<c>StepLogWriterTests</c>).
/// </para>
/// <para>
/// <b>The files are already redacted when they arrive.</b> Every caller passes a line that has been through
/// the run's <c>GitLogFilter</c>, so a local copy never holds a plaintext secret either - the same text the
/// streamed log carries.
/// </para>
/// </summary>
public sealed class RunLogWriters : IDisposable
{
    /// <summary>The temp file the run's stdout lines land in.</summary>
    public const string StdOutLogFileName = "stdout.log";

    /// <summary>The temp file the run's stderr lines land in.</summary>
    public const string StdErrorLogFileName = "stderr.log";

    private readonly object _gate = new();
    private readonly StreamWriter _stdOut;
    private readonly StreamWriter _stdErr;
    private bool _disposed;

    /// <summary>
    /// Truncates (or creates) both files in <paramref name="tempPath"/>. A retried claim of the same run starts
    /// from empty logs: appending to a previous attempt's output would misattribute lines to the new attempt.
    /// </summary>
    public RunLogWriters(string tempPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);

        Directory.CreateDirectory(tempPath);
        _stdOut = new StreamWriter(
            Path.Combine(tempPath, StdOutLogFileName),
            append: false)
        {
            // AutoFlush so an observer (a person, or the run page's own debugging aid) sees lines as they are
            // produced instead of at the end of the run.
            AutoFlush = true,
        };
        _stdErr = new StreamWriter(Path.Combine(tempPath, StdErrorLogFileName), append: false)
        {
            AutoFlush = true,
        };
    }

    /// <summary>The stdout file's path (the tests read it back from disk).</summary>
    public string StdOutPath => ((FileStream)_stdOut.BaseStream).Name;

    /// <summary>The stderr file's path.</summary>
    public string StdErrPath => ((FileStream)_stdErr.BaseStream).Name;

    /// <summary>
    /// Writes one line to the stream's file. Safe from any thread: the gate serializes the two writers, so a
    /// line is never split by another thread's write.
    /// </summary>
    /// <param name="stream">A <see cref="Zenkins.Shared.LogStreaming.LogStream"/> value.</param>
    /// <param name="line">The line, without its newline, already redacted by the caller.</param>
    public void Write(string stream, string line)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(line);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var writer = string.Equals(stream, Zenkins.Shared.LogStreaming.LogStream.StdErr, StringComparison.Ordinal)
                ? _stdErr
                : _stdOut;
            writer.WriteLine(line);
        }
    }

    /// <summary>
    /// Flushes and closes both files. Called when the run's execution ends, before the terminal report (the
    /// cleanup-before-report discipline), so a finished run never has a log line in flight.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stdOut.Dispose();
            _stdErr.Dispose();
        }
    }
}
