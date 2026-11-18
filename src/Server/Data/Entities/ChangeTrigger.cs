using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// One change trigger for a <c>(project, branch, commit)</c>: the <b>shared</b> record that makes a webhook
/// and a poll observing the same commit queue exactly one run between them (card F-025).
/// <para>
/// <b>Why a ledger and not a second dedupe key.</b> F-017's delivery dedupe answers "have I seen this
/// delivery before?" (per webhook, hashed from the delivery id) - a poll has no delivery id and a webhook
/// for the same commit arrives under a different one, so that key can never join the two paths. This row
/// is keyed by what both paths actually know: the project, the branch and the commit. Its unique index is
/// the guard, so two concurrent triggers for one commit cannot both queue a run.
/// </para>
/// <para>
/// <b>Written with the run, in the caller's transaction.</b> A refused queue (disabled project, full
/// queue) rolls the row back too, so the next poll or push can still build that commit - the same
/// "nothing is claimed about a run that does not exist" rule F-017's delivery record follows.
/// </para>
/// </summary>
public sealed class ChangeTrigger : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    public Guid ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    /// <summary>The branch the change was observed on.</summary>
    public required string Branch { get; set; }

    /// <summary>The commit the change was observed at (40 hex characters, as git writes it).</summary>
    public required string Commit { get; set; }

    /// <summary>A <see cref="BuildRunTriggerType"/> value: which path observed it first.</summary>
    public required string TriggerType { get; set; }

    /// <summary>The run queued for this change; null while a caller's transaction has not set it yet.</summary>
    public Guid? RunId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
