using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// One recorded step of a pipeline run (card F-015, PRD §3.3.5/§6): the state the step reached, when it
/// started and ended, its exit code and - when nothing exited - why it failed or was skipped.
/// <para>
/// <b>The record of truth for step outcomes.</b> The run's own terminal state stays owned by
/// <see cref="BuildRunStatus"/>'s state machine; a step row never decides the run by itself. But the
/// step rows are what the run page and later reports read: a run that finished <c>Failed</c> must name
/// the step that failed, and a step that never started must be distinguishable from one that did.
/// </para>
/// <para>
/// <b>Written as the step changes state, once per transition.</b> <see cref="Index"/> is the step's
/// 1-based position in the run's frozen spec, and <c>(RunId, Index)</c> is unique: the same step cannot
/// be recorded twice, and the worker's re-delivery of a report (a lost response) lands on the row that
/// already exists. A worker that dies mid-step leaves its step <c>Running</c> - the honest record; the
/// run is then resolved by F-010's <c>Lost</c> path, never by inventing a step outcome here.
/// </para>
/// <para>
/// <b>Tenant-owned, cascade with the run.</b> As with <see cref="BuildLog"/>, the organization id is a
/// direct column (and a query filter) rather than a join per read; the rows die with their run, and the
/// run dies with its project and organization.
/// </para>
/// </summary>
public sealed class BuildStep : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>The run this step belongs to. Cascades with it.</summary>
    public Guid RunId { get; set; }

    public BuildRun Run { get; set; } = null!;

    /// <summary>
    /// The step's 1-based position in the run's frozen spec. Half of the unique key with
    /// <see cref="RunId"/>: one row per position, per run.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// The step's name, as the frozen spec declared it. Stored (not joined back to the spec JSON) so a
    /// step row is readable on its own and a spec that no longer parses cannot erase a step's identity;
    /// the ingestion path refuses a name that disagrees with the frozen spec.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>A <see cref="PipelineStepStatus"/> value (never <c>Pending</c>: that state is display-only).</summary>
    public string Status { get; set; } = PipelineStepStatus.Running;

    /// <summary>When the step started (null for a skipped step: nothing ever ran).</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When the step reached its terminal state; null while it runs.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>The process exit code (0 on success), or null when no process produced one.</summary>
    public int? ExitCode { get; set; }

    /// <summary>
    /// How many attempts this step made (card F-032). <b>One</b> unless the step declared <c>retry</c>, in
    /// which case a failed or timed-out attempt was repeated inside this one row - the step list stays 1:1
    /// with the definition, so "step N of the frozen spec" keeps its identity for artifacts, conditions and
    /// the page. The stored exit code is the <b>final</b> attempt's; a cancelled step records the attempt it
    /// died in.
    /// </summary>
    public int Attempts { get; set; } = 1;

    /// <summary>
    /// A <see cref="PipelineStepFailureReasons"/> value, or null. A non-zero exit needs no reason - the
    /// code is the record - so this covers "never exited" (canceled, not started, secret unavailable)
    /// and why a step was skipped.
    /// </summary>
    public string? FailureReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
