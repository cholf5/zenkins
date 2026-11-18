using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// A grouping folder inside one organization (card F-042, PRD §3.2): projects can be nested in folders up
/// to <see cref="Zenkins.Server.Folders.FolderValidation.MaxDepth"/> levels deep, which is what keeps a
/// team's jobs or a monorepo's components one click away instead of a flat list of fifty.
/// <para>
/// <b>No permissions here.</b> A folder is a grouping, not a second authorization model: organization
/// isolation stays the product's tenant boundary, and per-folder permissions are deliberately absent.
/// </para>
/// <para>
/// <b>Two columns for one parent.</b> <see cref="ParentFolderId"/> is the real (nullable) foreign key a
/// reader and the cascade use, while <see cref="ParentKey"/> is the same value encoded for the unique
/// index: both SQLite and PostgreSQL treat <c>NULL</c> as distinct in a unique index, so a nullable parent
/// column could never make "names are unique among siblings" true at the root. The pair is held together
/// by the model's check constraint, so the two cannot drift.
/// </para>
/// </summary>
public sealed class Folder : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// The case-insensitive identity of <see cref="Name"/>, lowercased invariantly by the service that
    /// writes the row. The sibling-unique index is built on this column instead of on <see cref="Name"/>:
    /// neither SQLite's default <c>BINARY</c> collation nor PostgreSQL's default collation compares text
    /// case-insensitively, and a per-provider collation would make the same migration mean two things.
    /// </summary>
    public string NameKey { get; set; } = string.Empty;

    /// <summary>The parent folder, or <see langword="null"/> for a folder at the organization's root.</summary>
    public Guid? ParentFolderId { get; set; }

    /// <summary>
    /// <see cref="ParentFolderId"/> encoded for the unique index: <see cref="Guid.Empty"/> at the root.
    /// Never assigned without <see cref="ParentFolderId"/>; the model's check constraint is the guard.
    /// </summary>
    public Guid ParentKey { get; set; }

    /// <summary>
    /// How many levels below the organization's root this folder sits: 1 for a root folder,
    /// <see cref="Zenkins.Server.Folders.FolderValidation.MaxDepth"/> for the deepest allowed folder.
    /// Stored (and bounded by a check constraint) because the nesting cap is a data rule, not a
    /// convention: a folder at the cap can have no children, and moving a subtree recomputes it.
    /// </summary>
    public int Depth { get; set; } = 1;

    /// <summary>The operator's own words about what the folder groups; optional.</summary>
    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
