using Zenkins.Shared.LogStreaming;
using Zenkins.Worker.Git;

namespace Zenkins.Worker.Pipelines;

/// <summary>
/// The worker's own diagnostic for a run whose pipeline layer threw (cards F-014, F-015): a bug must still
/// leave a line in the run log, and that line is subject to exactly the same rules as every other line - the
/// run's redaction filter, the writer gate, and the run's current step stamp.
/// <para>
/// <b>Why this is a type.</b> The three lines used to live inline in the worker loop, where the only way to test
/// them was to reproduce them in a test (the classic duplicate-implementation trap: the test keeps passing after
/// the loop drifts back to a raw write). A regression that re-bypasses the filter - the failure message can
/// carry anything, including a value the run has decrypted - now has to change this type, and the type's test
/// fails.
/// </para>
/// </summary>
/// <param name="runId">The run, named on the worker's own console line.</param>
/// <param name="redactor">The run's redaction filter, with every decrypted value registered.</param>
/// <param name="writers">The run's local temp log files (the same gate every other line goes through).</param>
/// <param name="buffer">The run's streaming buffer, so the line reaches the server log too.</param>
/// <param name="currentStep">The step whose segment the line belongs to (null between steps).</param>
/// <param name="console">
/// The worker's own channel (its stdout/stderr, where a crash log goes). It receives the same already-redacted
/// text, because a diagnostic on the console is a place a decrypted value must not appear either (AC-10).
/// </param>
public sealed class RunCrashReporter(
    Guid runId,
    GitLogFilter redactor,
    RunLogWriters writers,
    RunLogBuffer buffer,
    Func<Guid?> currentStep,
    TextWriter console)
{
    /// <summary>The line's fixed prefix; the failure's message follows it, redacted.</summary>
    public const string Prefix = "zenkins-worker: the run could not be executed: ";

    /// <summary>
    /// Writes the diagnostic, redacted, to the run's temp stderr log, to its streaming buffer and to the
    /// worker's own console channel - and returns the text it published.
    /// </summary>
    public string Report(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        var message = redactor.Redact(Prefix + failure.Message);
        writers.Write(LogStream.StdErr, message);
        buffer.Emit(LogStream.StdErr, message, currentStep());
        console.WriteLine($"{message} (run {runId})");
        return message;
    }
}
