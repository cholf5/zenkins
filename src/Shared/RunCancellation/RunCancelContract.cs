namespace Zenkins.Shared.RunCancellation;

/// <summary>
/// The cancellation reasons a run carries (card F-009). Shared with the Worker because a cancel
/// request crosses the wire twice: once on the <c>cancelRun</c> SignalR signal / heartbeat
/// fallback (server → worker) and once in the <c>canceled</c> report's outcome mapping.
/// <para>
/// <see cref="CanceledByUser"/> is the prefix of a reason naming the actor
/// (<c>canceled-by-user &lt;user&gt;</c>, the card's terminal-reason contract); <see cref="Timeout"/>
/// is the deadline sweeper's own reason. The reason recorded at request time becomes the run's
/// <c>FailureReason</c> when the worker reports the kill.
/// </para>
/// </summary>
public static class RunCancelReasons
{
    /// <summary>The deadline sweeper's cancel request: the run exceeded its execution deadline.</summary>
    public const string Timeout = "timeout";

    /// <summary>The prefix of a user-initiated cancel reason; the full reason names the actor.</summary>
    public const string CanceledByUser = "canceled-by-user";

    /// <summary>
    /// Card F-028: the run was still queued when its project was disabled, so the disable canceled it.
    /// The same word is the trigger refusal's code (<c>project-disabled</c>): a run that did not build
    /// and a trigger that did not queue say the same thing about the same cause. Declared here, next to
    /// the other terminal reasons, because it becomes the run's <c>FailureReason</c>.
    /// </summary>
    public const string ProjectDisabled = "project-disabled";

    /// <summary>Builds the full reason for a user-initiated cancel (<c>canceled-by-user &lt;user&gt;</c>).</summary>
    public static string ForUser(string actor) => $"{CanceledByUser} {actor}";

    /// <summary>Whether a reason is the deadline sweeper's timeout request.</summary>
    public static bool IsTimeout(string? reason) =>
        string.Equals(reason, Timeout, StringComparison.Ordinal);
}

/// <summary>
/// The outcome a worker reports to <c>POST /api/workers/runs/{'{'}id{'}'}/canceled</c> (card F-009).
/// One endpoint, two outcomes: <see cref="Canceled"/> maps the run to <c>Canceled</c> with the
/// server's recorded cancel reason; <see cref="Timeout"/> maps it to <c>Failed</c> with reason
/// <c>timeout</c> - the server owns the mapping, so a kill never needs a second endpoint.
/// </summary>
public static class RunCanceledOutcomes
{
    /// <summary>The process was killed because the server requested a cancellation.</summary>
    public const string Canceled = "canceled";

    /// <summary>The process was killed because the server requested it with reason <c>timeout</c>.</summary>
    public const string Timeout = "timeout";
}

/// <summary>
/// One pending cancellation as the heartbeat response carries it (card F-009's fallback channel:
/// a worker whose SignalR connection is down learns the cancel request at most one heartbeat
/// period late). The <see cref="Reason"/> is the server's recorded
/// <c>CancelReason</c> (see <see cref="RunCancelReasons"/>).
/// </summary>
public sealed record WorkerCancelRequest(Guid RunId, string? Reason);
