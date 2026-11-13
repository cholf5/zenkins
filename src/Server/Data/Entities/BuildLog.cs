using Zenkins.Shared.LogStreaming;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// One captured output line of a build run (card F-008, PRD §3.6). The primary key is the
/// composite <c>(RunId, Seq)</c> - the <see cref="Seq"/> is the Worker-assigned per-run monotonic
/// sequence starting at 1, so the key <b>is</b> the deduplication authority: a reconnecting worker
/// that re-delivers overlapping sequence numbers can never create a second row, and the loser of a
/// duplicate insert is absorbed (the ingest writer skips rows the database already holds).
/// <para>
/// <b>Tenant ownership is a direct column, not a join.</b> Every other tenant-owned entity carries
/// <see cref="OrganizationId"/> and is filtered on it; doing the same here keeps the discipline
/// single-shaped and keeps log reads (history chunks, the download stream - potentially tens of
/// thousands of rows) free of a <c>BuildRun</c> join per query. The value is written once, from
/// the run row the worker hub has already validated, and is never client-supplied.
/// </para>
/// </summary>
public sealed class BuildLog : ITenantOwned
{
    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>The run this line belongs to. Half of the composite primary key.</summary>
    public Guid RunId { get; set; }

    /// <summary>Per-run monotonic sequence assigned by the Worker at emit time (starts at 1). The
    /// other half of the composite primary key - the reconnection-deduplication contract.</summary>
    public long Seq { get; set; }

    /// <summary>The <see cref="LogStream"/> discriminator (stdout / stderr).</summary>
    public string Stream { get; set; } = LogStream.StdOut;

    /// <summary>The line's content without its newline. Deliberately no max length: a child
    /// process may emit arbitrarily long lines (the same reasoning as the run's frozen spec text).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>When the Worker captured the line (worker clock, monotonic per line).</summary>
    public DateTimeOffset Timestamp { get; set; }
}
