using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// The head a branch was last <b>observed</b> at, and what the last poll decision for it was (card F-025).
/// <para>
/// <b>Polling state is not derived from a run.</b> <see cref="BuildRun.GitCommit"/> is written by the
/// <i>worker</i> when it reports the commit it built, so it is null for as long as a run is queued - a
/// poller reading it would see "the head moved" again on every tick and queue another run. This row is the
/// polling state instead: the sha the probe last answered, when, and what the poll did with it.
/// </para>
/// <para>
/// <b>It exists for single-branch projects too.</b> <see cref="BranchJob"/> is F-023's registry and only
/// multibranch projects have rows in it, so a per-branch head cannot live there; the key here is
/// <c>(ProjectId, Branch)</c> and a single-mode project simply has one row.
/// </para>
/// </summary>
public sealed class BranchHead : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    public Guid ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    /// <summary>The branch this head belongs to, compared ordinally like every other branch name.</summary>
    public required string Branch { get; set; }

    /// <summary>
    /// The sha the last <b>successful</b> poll observed, or null before the first one. A probe failure never
    /// writes here: "I could not see the remote" is not a head.
    /// </summary>
    public string? Sha { get; set; }

    /// <summary>When this branch was first seen by a poll.</summary>
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the last successful poll listed this branch.</summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When a poll last queued a run for this head (null: never).</summary>
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>The commit of the last run polling queued for this branch (null: never).</summary>
    public string? LastRunCommit { get; set; }

    /// <summary>
    /// A <see cref="Zenkins.Server.Polling.PollOutcomes"/> value: what the last poll decided for this
    /// branch (<c>queued</c>, <c>duplicate</c>, <c>unchanged</c>, <c>first-sighting</c>, or the skip reason).
    /// </summary>
    public string? LastOutcome { get; set; }

    /// <summary>When <see cref="LastOutcome"/> was recorded.</summary>
    public DateTimeOffset? LastOutcomeAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
