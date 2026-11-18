using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// One pending or settled downstream trigger (card F-026) - the durability guarantee this card is built around.
/// <para>
/// <b>Written in the same transaction as the terminal status.</b> A run's completion and the rows that say "this
/// completion owes a downstream a run" commit together, so a Server that dies immediately afterwards leaves the
/// obligation on disk rather than in a stack frame. The dispatcher then works the rows off after commit, and a row
/// stays <see cref="OutboxStatuses.Pending"/> until it is genuinely settled - a crash between "queued the
/// downstream" and "marked the row" re-dispatches, which is why the dispatch itself is idempotent by
/// (upstream run, link).
/// </para>
/// <para>
/// <b>The row is also the audit trail.</b> A chain that did not produce a run records <em>why</em> here
/// (<see cref="Outcome"/> + <see cref="Reason"/>): a skipped branch, a disabled project, a hop-limit stop, a
/// project that no longer exists. The upstream run page reads these rows, so "nothing happened" is never what a
/// reader is left with.
/// </para>
/// </summary>
public sealed class OutboxMessage : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>The kind of obligation. One value today; a column so F-039 can add its own without a new table.</summary>
    public required string Kind { get; set; }

    /// <summary>The upstream run whose terminal state owes the trigger. Cascade-deleted with it.</summary>
    public Guid UpstreamRunId { get; set; }

    /// <summary>The link that produced the obligation (its policy and target are re-read at dispatch time).</summary>
    public Guid LinkId { get; set; }

    /// <summary>One of <see cref="OutboxStatuses"/>.</summary>
    public string Status { get; set; } = OutboxStatuses.Pending;

    /// <summary>The downstream run this obligation produced, when it produced one.</summary>
    public Guid? DownstreamRunId { get; set; }

    /// <summary>
    /// Why this obligation settled the way it did - a <see cref="DownstreamOutcomes"/> value for a skip or a
    /// failure, null for a queued run. Free text would make the page's reasons unrenderable; a closed set keeps
    /// "not triggered because" answerable.
    /// </summary>
    public string? Outcome { get; set; }

    /// <summary>An operator-facing detail for a skip or a failure (never a secret, always safe to render).</summary>
    public string? Reason { get; set; }

    /// <summary>How many dispatch attempts were made. A permanent refusal settles the row; this counts the tries.</summary>
    public int Attempts { get; set; }

    /// <summary>When the dispatcher may next look at this row (a transient failure backs off).</summary>
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the row was settled (a downstream queued, or a skip/failure recorded).</summary>
    public DateTimeOffset? SettledAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>The two states one outbox row can be in (card F-026).</summary>
public static class OutboxStatuses
{
    /// <summary>Owed and not yet settled. The dispatcher works these off.</summary>
    public const string Pending = "pending";

    /// <summary>Settled: a downstream run exists, or a skip/failure was recorded with its reason.</summary>
    public const string Settled = "settled";
}

/// <summary>
/// The kinds of obligation the outbox carries (card F-026). A closed set: a dispatcher that met an unknown kind
/// would have to guess what to do with it.
/// </summary>
public static class OutboxKinds
{
    /// <summary>An upstream run's terminal state owes one downstream trigger.</summary>
    public const string DownstreamTrigger = "downstream-trigger";
}

/// <summary>
/// Why a chain edge produced no run (card F-026). Closed, so the run page can render every value and a skip is
/// never confused with a failure.
/// </summary>
public static class DownstreamOutcomes
{
    /// <summary>The downstream run was queued - <see cref="OutboxMessage.DownstreamRunId"/> names it.</summary>
    public const string Queued = "queued";

    /// <summary>The upstream did not succeed and the link asked for success only.</summary>
    public const string SkippedUpstreamFailed = "skipped-upstream-failed";

    /// <summary>The branch policy named a branch the downstream cannot build.</summary>
    public const string SkippedBranch = "skipped-branch";

    /// <summary>The downstream project is disabled (card F-028).</summary>
    public const string SkippedDisabled = "skipped-disabled";

    /// <summary>The chain reached the hop limit - how deep a legal chain may go, not a cycle's stop.</summary>
    public const string SkippedHopLimit = "skipped-hop-limit";

    /// <summary>The downstream project no longer exists (deleted between completion and dispatch).</summary>
    public const string SkippedMissing = "skipped-missing";

    /// <summary>The downstream could not be triggered for a reason a retry cannot fix (kind, credential, gate).</summary>
    public const string FailedTrigger = "failed-trigger";

    /// <summary>Every value, so a reader can enumerate them.</summary>
    public static readonly string[] All =
    [
        Queued, SkippedUpstreamFailed, SkippedBranch, SkippedDisabled, SkippedHopLimit, SkippedMissing, FailedTrigger,
    ];
}
