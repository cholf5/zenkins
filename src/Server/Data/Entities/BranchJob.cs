using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// The closed vocabulary of a branch job's pipeline state (card F-023).
/// <para>
/// <b>Why a state word and not <c>Nullable&lt;bool&gt;</c>.</b> "Not looked at yet" is a third fact: a
/// branch the product has seen but never read is neither "has a pipeline" nor "has none", and collapsing
/// it onto either would make the branch list lie. The values are stored as text for the same reason the
/// run status is - one vocabulary both the Server and its pages read.
/// </para>
/// </summary>
public static class BranchPipelineStates
{
    /// <summary>The branch exists, but nothing has read its tree yet (no run has defined it).</summary>
    public const string Unknown = "unknown";

    /// <summary>The branch's checkout contains the project's pipeline file.</summary>
    public const string HasPipeline = "has-pipeline";

    /// <summary>
    /// The branch's checkout does not contain the project's pipeline file. Such a branch is listed and
    /// is never queued again - it is not a failure and it produces no run (card F-023's AC-3).
    /// </summary>
    public const string NoPipeline = "no-pipeline";

    /// <summary>Every value the state can carry.</summary>
    public static IReadOnlyList<string> All { get; } = [Unknown, HasPipeline, NoPipeline];

    /// <summary>Whether <paramref name="value"/> is one of <see cref="All"/>.</summary>
    public static bool IsKnown(string? value) =>
        value is not null && All.Contains(value, StringComparer.Ordinal);

    /// <summary>Whether the branch can be queued at all (every state but <see cref="NoPipeline"/>).</summary>
    public static bool IsBuildable(string? value) =>
        !string.Equals(value, NoPipeline, StringComparison.Ordinal);
}

/// <summary>
/// The two pieces of evidence that may mark a branch job orphaned (card F-023). <b>Only a positive
/// observation can</b>: a push-delete event names the branch that disappeared, and a <em>successful</em>
/// refs answer that no longer lists the branch proves it is gone. A failed probe, a timeout or a
/// "no worker" answer is not evidence of absence and must never orphan anything - the same rule
/// <see cref="Zenkins.Shared.Probes.GitProbeOutcomes"/> states for reading a probe at all.
/// </summary>
public static class BranchOrphanEvidence
{
    /// <summary>A signed push whose <c>after</c> is all zeroes: the sender says the branch is gone.</summary>
    public const string PushDelete = "push-delete";

    /// <summary>A successful refs answer that no longer lists the branch.</summary>
    public const string RefsAbsent = "refs-absent";

    /// <summary>Every evidence word the column can carry.</summary>
    public static IReadOnlyList<string> All { get; } = [PushDelete, RefsAbsent];

    /// <summary>Whether <paramref name="value"/> is one of <see cref="All"/>.</summary>
    public static bool IsKnown(string? value) =>
        value is not null && All.Contains(value, StringComparer.Ordinal);
}

/// <summary>
/// One branch a multibranch project tracks (card F-023): the branch job's identity and its last
/// discovery facts.
/// <para>
/// <b>The branch name is the key.</b> A run already snapshots the branch it built
/// (<see cref="BuildRun.GitBranch"/>), so per-branch history is a grouping problem; this row is what
/// makes a branch that was <em>seen and not built</em> representable. The pair
/// <c>(ProjectId, Branch)</c> is unique, and the comparison is ordinal/case-sensitive exactly like the
/// run's snapshot: <c>Main</c> and <c>main</c> are two git refs, not one. A branch name is never empty
/// - an empty <see cref="BuildRun.GitBranch"/> is the product's Freestyle discriminator, not a branch.
/// </para>
/// <para>
/// <b>The run's snapshot stays the authority.</b> This table is a registry, not a history: the branch
/// list unions these rows with the branches existing runs already name, so a run whose branch has no
/// row (including every row that predates this card) is still listed instead of disappearing.
/// </para>
/// <para>
/// <b>Nothing here deletes history.</b> A branch deleted upstream keeps its row and its runs; the row
/// is marked <see cref="OrphanedAt"/> and stays readable. Removing a branch job is never implicit -
/// only deleting the project cascades its branches away with everything else.
/// </para>
/// <para>
/// <b>Retention is not this row's concern.</b> Card F-038 (still open) will scope its policy to a
/// branch job - <c>(ProjectId, Branch)</c> is the natural key it needs - but this table neither stores
/// nor enforces a retention policy, so nothing here pretends F-038 shipped.
/// </para>
/// </summary>
public sealed class BranchJob : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>The container project whose settings and credentials the branch job inherits.</summary>
    public Guid ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    /// <summary>
    /// The full branch name (git ref short form, e.g. <c>main</c> or <c>feature/x</c>) - the same
    /// snapshot shape <see cref="BuildRun.GitBranch"/> stores, so joining the two is exact.
    /// </summary>
    public required string Branch { get; set; }

    /// <summary>
    /// The last known pipeline state (a <see cref="BranchPipelineStates"/> value). Written by the
    /// discovery paths and by a run's definition: a branch whose tree has no pipeline file is
    /// <see cref="BranchPipelineStates.NoPipeline"/> and is never queued.
    /// </summary>
    public string PipelineState { get; set; } = BranchPipelineStates.Unknown;

    /// <summary>
    /// The last commit discovered on this branch (the pushed <c>after</c>, or a run's reported commit).
    /// Null until something named one; it is a discovery fact, never the authority a run builds (the
    /// run's own <see cref="BuildRun.GitCommit"/> is).
    /// </summary>
    public string? LastCommit { get; set; }

    /// <summary>
    /// The commit the current <see cref="PipelineState"/> is evidence about, or null while the state is
    /// <see cref="BranchPipelineStates.Unknown"/>. A state is only ever written together with the commit
    /// a worker actually read, so "no pipeline" can never be inferred from a missing run, an absent
    /// branch row or a failed probe (card F-023's rule).
    /// </summary>
    public string? ObservedCommit { get; set; }

    /// <summary>When the branch was first seen.</summary>
    public DateTimeOffset DiscoveredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When discovery last saw the branch (a push delivery, or a probe).</summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When a later discovery pass found the branch gone upstream (or null while the branch is live).
    /// The row and every run under it stay readable; this is a marker, never a delete.
    /// </summary>
    public DateTimeOffset? OrphanedAt { get; set; }

    /// <summary>
    /// Which evidence marked the branch orphaned (<see cref="BranchOrphanEvidence"/>), or null while the
    /// branch is live. Stored with the timestamp so "deleted upstream" is never a guess: only a
    /// push-delete or a successful refs answer that omits the branch may set it.
    /// </summary>
    public string? OrphanedEvidence { get; set; }

    /// <summary>
    /// The commit an observation is still owed for (card F-023), or null when nothing is pending. A push
    /// (or an interval pass) sets it when the branch has no verdict about that commit; a Worker's
    /// observation clears it and records <see cref="PipelineState"/> + <see cref="ObservedCommit"/>.
    /// <para>
    /// <b>This is what makes "unknown is not queued" durable.</b> The in-memory task queue is transient
    /// (a Server restart drops it), so the row itself remembers which commit still needs a tree read;
    /// the retry sweeper re-asks for it with a backoff instead of losing the push.
    /// </para>
    /// </summary>
    public string? PendingCommit { get; set; }

    /// <summary>When the pending observation was last requested (the retry backoff's clock).</summary>
    public DateTimeOffset? PendingSince { get; set; }

    /// <summary>
    /// How many times a pending observation had to be re-requested. The sweeper's backoff grows with it
    /// (bounded), so "no Worker" retries ever more slowly instead of becoming a request storm.
    /// </summary>
    public int ObservationAttempts { get; set; }

    /// <summary>
    /// The required tags the run carries when the pending observation finds a pipeline (the webhook's
    /// tags, copied at request time). Empty means unrestricted.
    /// </summary>
    public string PendingTags { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
