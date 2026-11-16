using Zenkins.Shared.Projects;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// A CI project owned by an organization (PRD §3.2, §3.10). The <see cref="Kind"/> decides where a
/// run's definition comes from: a <see cref="ProjectKinds.Pipeline"/> project names a Git repository
/// whose <c>pipeline.lua</c> is read by the Worker's definition phase; a
/// <see cref="ProjectKinds.Freestyle"/> project needs no repository - its steps are configured in the
/// UI, frozen into <see cref="FreestyleSpecJson"/>, and every run travels with that snapshot.
/// </summary>
public sealed class Project : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// The project's kind (card F-020): <see cref="ProjectKinds.Pipeline"/> or
    /// <see cref="ProjectKinds.Freestyle"/>. Data, not a type hierarchy - the frozen spec stays the one
    /// execution contract for both.
    /// </summary>
    public string Kind { get; set; } = ProjectKinds.Pipeline;

    /// <summary>
    /// The repository Pipeline projects clone (nullable since F-020: a Freestyle project has none).
    /// <see langword="null"/> never reaches a clone - the Freestyle path skips it.
    /// </summary>
    public string? RepositoryUrl { get; set; }

    /// <summary>The branch Pipeline projects build by default; <see langword="null"/> for Freestyle.</summary>
    public string? DefaultBranch { get; set; }

    /// <summary>
    /// Path of the pipeline file inside the repository; PRD §3.2 default is <c>pipeline.lua</c>.
    /// <see langword="null"/> for Freestyle (no file, no path).
    /// </summary>
    public string? PipelinePath { get; set; } = "pipeline.lua";

    /// <summary>
    /// The project's build-parameter definitions (card F-021, PRD §3.11): a JSON array of
    /// <see cref="Zenkins.Shared.Projects.ProjectParameter"/>, serialized with the source-generated
    /// <c>ProjectParameterJsonContext</c>. Null means "no parameters declared"; the trigger form
    /// renders them for both kinds, and a run snapshots the resolved values.
    /// </summary>
    public string? ParametersJson { get; set; }

    /// <summary>
    /// The Freestyle project's frozen step spec (card F-020): the same
    /// <see cref="Zenkins.Shared.Pipelines.PipelineSpec"/> JSON the Worker freezes for a Pipeline run,
    /// built and validated by the Server at save time and copied onto every run at trigger time. Null
    /// for Pipeline projects and for a Freestyle project whose steps were never saved yet.
    /// </summary>
    public string? FreestyleSpecJson { get; set; }

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
