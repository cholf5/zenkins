using Zenkins.Shared.Pipelines;

namespace Zenkins.Worker.Pipelines;

/// <summary>
/// One step transition the executor reports (card F-015): the 1-based position in the frozen spec, the
/// name that spec declares there, the state reached and - when nothing exited - the closed reason.
/// <para>
/// The executor reports <b>before</b> a step starts (<see cref="PipelineStepStatus.Running"/>) and again when
/// it ends, so a run page opened mid-run shows completed, current and pending steps. Timestamps are the
/// server's: the report carries none, exactly like the run's own accepted/completed reports.
/// </para>
/// </summary>
public sealed record PipelineStepReport(
    int Index,
    string Name,
    string Status,
    int? ExitCode = null,
    string? FailureReason = null);

/// <summary>
/// What a step report was answered with: whether the server accepted it, and - for a step that now has a
/// row - that row's id, which the executor stamps on the step's log lines.
/// </summary>
public sealed record PipelineStepReceipt(bool Accepted, Guid? StepId = null)
{
    /// <summary>The server recorded the transition.</summary>
    public static PipelineStepReceipt Recorded(Guid stepId) => new(true, stepId);

    /// <summary>The server refused it definitively (the run is not ours any more, or already terminal).</summary>
    public static PipelineStepReceipt Refused() => new(false);
}

/// <summary>
/// Where a step transition goes (card F-015). In production the Worker reports it to the Server; a test
/// records it in a list. A refusal (a 4xx: the run is not this worker's, or it already ended) means the run
/// must be given up - the server owns its state.
/// </summary>
public interface IPipelineStepSink
{
    /// <summary>Reports one transition.</summary>
    Task<PipelineStepReceipt> ReportAsync(PipelineStepReport report, CancellationToken cancellationToken);
}

/// <summary>
/// A sink for a run with no server behind it: it accepts every report and mints an id per position, so the
/// log lines of such a run still segment by step (the F-014 harness and the executor's own unit tests).
/// </summary>
public sealed class OfflinePipelineStepSink : IPipelineStepSink
{
    /// <summary>The shared instance.</summary>
    public static OfflinePipelineStepSink Instance { get; } = new();

    /// <summary>The ids handed out per position, so repeated reports of one step get the same id.</summary>
    private readonly Dictionary<int, Guid> _ids = [];

    /// <inheritdoc />
    public Task<PipelineStepReceipt> ReportAsync(
        PipelineStepReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        // Single-threaded by construction: one run executes its steps in order on one thread.
        if (!_ids.TryGetValue(report.Index, out var id))
        {
            id = Guid.NewGuid();
            _ids[report.Index] = id;
        }

        return Task.FromResult(PipelineStepReceipt.Recorded(id));
    }
}
