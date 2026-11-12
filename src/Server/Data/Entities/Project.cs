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
    /// Reference to the Git credential used to clone this repository. The credential itself is a
    /// later card's entity (<c>Secret</c>/<c>GitCredential</c>), so this stays an opaque nullable id.
    /// </summary>
    public Guid? GitCredentialId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
