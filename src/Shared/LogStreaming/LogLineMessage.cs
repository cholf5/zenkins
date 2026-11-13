namespace Zenkins.Shared.LogStreaming;

/// <summary>
/// Which captured stream a build-log line came from (PRD §3.6: stdout and stderr are both
/// shown). Stored as a string so the column stays provider-neutral; the check constraint in
/// <see cref="Zenkins.Server.Data.ZenkinsDbContext"/> keeps out-of-band writes honest.
/// Lives in <c>Zenkins.Shared</c> because the wire contract below needs it on both ends.
/// </summary>
public static class LogStream
{
    /// <summary>The process' standard output stream.</summary>
    public const string StdOut = "StdOut";

    /// <summary>The process' standard error stream.</summary>
    public const string StdErr = "StdErr";
}

/// <summary>
/// One build-log line on the F-008 wire contract (Worker → Server hub and Server → browser hub).
/// A plain record with no dependencies so the Worker (ASP.NET-free, self-contained) and the
/// Server can both carry it; SignalR's default JSON hub protocol serializes it with camelCase
/// property names on both sides.
/// <para>
/// <see cref="Seq"/> is the per-run monotonic sequence assigned by the Worker at emit time,
/// starting at 1. It is the ordering and deduplication authority end to end: reconnects resend
/// overlapping sequence numbers and the database's <c>(RunId, Seq)</c> primary key absorbs the
/// duplicates; browsers merge history chunks and live lines by <see cref="Seq"/>.
/// </para>
/// <para>
/// <b><see cref="StepId"/> is the per-step segmentation (card F-015).</b> The Worker stamps every line
/// it emits while a step runs with that step's id, and leaves it <see langword="null"/> for the
/// lines of the definition phase and the ones between steps. The run page groups lines under step
/// headers with it; the full viewer with per-step filtering stays v0.9.
/// </para>
/// </summary>
public sealed record LogLineMessage(
    long Seq,
    string Stream,
    string Message,
    DateTimeOffset Timestamp,
    Guid? StepId = null);
