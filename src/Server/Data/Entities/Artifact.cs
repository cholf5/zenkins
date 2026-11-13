using Zenkins.Shared.Artifacts;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// One published build product of a pipeline run (card F-016, PRD §3.7/§6): a single file or a directory
/// whose tree the run declared with <c>artifact("bin/Release")</c>.
/// <para>
/// <b>The row holds an id, never a path.</b> The bytes live at
/// <c>{artifacts root}/{OrganizationId}/{RunId}/{Id}/{relativePath}</c>, a location this row's own keys
/// derive - so no client-supplied absolute path is ever stored, and nothing a worker sends can point a
/// later download at a file outside the store. <see cref="Name"/> is the declared path, kept as the
/// artifact's identity for the page ("bin/Release"); it is unique per run, because a second artifact with
/// the same name would make downloads ambiguous.
/// </para>
/// <para>
/// <b>Only <see cref="ArtifactStatus.Ready"/> is served.</b> <see cref="Status"/> starts
/// <c>Pending</c> (files may be streaming in), becomes <c>Ready</c> only when <c>finalize</c> has
/// verified every file's SHA-256, count and total, and becomes <c>Incomplete</c> when the sweeper finds a
/// Pending row whose worker went away - or when an upload is refused. The bytes such a row received are
/// <b>kept</b>, deliberately: the row plus its store directory is the diagnosis of why an upload did not
/// finish. Only the state makes it unusable, so a partially uploaded artifact is never downloadable - a
/// truncated artifact would be a lie about the build output.
/// </para>
/// <para>
/// <b>Tenant-owned and cascade-deleted.</b> As with <see cref="BuildLog"/> and <see cref="BuildStep"/>, the
/// organization id is a direct column and a query filter rather than a join per read; the rows die with
/// their run, and the run dies with its project and organization. There is deliberately no retention
/// policy in v1 (a recorded parking-lot item): an artifact lives until its run does.
/// </para>
/// </summary>
public sealed class Artifact : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>The run that published this artifact. Cascades with it.</summary>
    public Guid RunId { get; set; }

    public BuildRun Run { get; set; } = null!;

    /// <summary>
    /// The declared path (<c>bin/Release</c>), normalized by
    /// <see cref="ArtifactPathRules"/> on the way in. Unique per run: <c>artifact("bin")</c> twice is a
    /// pipeline bug, and a duplicate name would make the run page's download links ambiguous.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>A <see cref="Zenkins.Shared.Artifacts.ArtifactKind"/> value (<c>File</c> or <c>Directory</c>).</summary>
    public required string Kind { get; set; }

    /// <summary>An <see cref="Zenkins.Shared.Artifacts.ArtifactStatus"/> value.</summary>
    public string Status { get; set; } = ArtifactStatus.Pending;

    /// <summary>How many files <c>finalize</c> verified (0 while Pending).</summary>
    public int FileCount { get; set; }

    /// <summary>The sum of the verified files' sizes in bytes (0 while Pending).</summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// The manifest (card F-016): while Pending, the per-file entries the server recorded as it hashed
    /// each stream (<c>{"files":[{"path","sha256","bytes"}]}</c>); on finalize, the published record
    /// (<c>{"fileCount","totalBytes","manifestHash","files":[…]}</c>). It is the audit trail that makes
    /// "the artifact is what the worker said it is" checkable after the upload is gone.
    /// </summary>
    public string? ManifestJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Stamped by the context's single timestamp routine on every write (the last upload, the finalize).</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
