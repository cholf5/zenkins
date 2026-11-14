using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// A Git repository owned by an organization (PRD §3.2).
/// </summary>
public sealed class Project : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    public required string Name { get; set; }

    public required string RepositoryUrl { get; set; }

    public required string DefaultBranch { get; set; }

    /// <summary>Path of the pipeline file inside the repository; PRD §3.2 default is <c>pipeline.lua</c>.</summary>
    public string PipelinePath { get; set; } = "pipeline.lua";

    /// <summary>
    /// Reference to the Git credential this project's runs clone with (card F-018 writes it). The
    /// credential is a <see cref="GitCredential"/> row of the same organization; <see langword="null"/>
    /// means <b>no credential is assigned</b>, and a run then clones without one instead of borrowing
    /// whatever credential the worker host happens to hold.
    /// </summary>
    public Guid? GitCredentialId { get; set; }

    /// <summary>
    /// When the current assignment was made (card F-018's audit trail). Cleared on detach, so a
    /// non-null value always describes <see cref="GitCredentialId"/> - never a previous credential.
    /// </summary>
    public DateTimeOffset? GitCredentialAssignedAt { get; set; }

    /// <summary>
    /// Who made the current assignment: the acting user's id, resolved to an e-mail for display.
    /// Like <c>GitCredential.CreatedByUserId</c> it carries no foreign key, so deleting an account
    /// never rewrites a project's history.
    /// </summary>
    public string? GitCredentialAssignedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
