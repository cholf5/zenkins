using Zenkins.Shared.Snippets;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// One organization's reusable step snippet (card F-035): a named body of text that an editor can <b>copy
/// into</b> a project's definition.
/// <para>
/// <b>Text, not a reference.</b> Nothing in a frozen spec, a run or a project row points at this entity - the
/// editors insert the body, and the normal save freezes the resulting text. Editing or deleting a snippet
/// therefore cannot change a saved project, a queued run or history: there is no link to dangle.
/// </para>
/// <para>
/// <b>Tenant-owned like every organization resource.</b> The name is unique per organization (a unique index
/// is the guard, not a pre-flight query), and the global query filter keeps one organization's snippets out of
/// another's list, lookup and mutation.
/// </para>
/// </summary>
public sealed class StepSnippet : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>The snippet's display name, unique inside the organization (case-sensitive).</summary>
    public required string Name { get; set; }

    /// <summary>One of <see cref="StepSnippetKinds"/>: which editor offers it and which bound it obeys.</summary>
    public string Kind { get; set; } = StepSnippetKinds.ScriptStep;

    /// <summary>The text an insertion copies. Bounded by <see cref="StepSnippetKinds.MaxBodyChars"/>.</summary>
    public required string Body { get; set; }

    /// <summary>What the snippet is for, in the author's words; optional.</summary>
    public string? Description { get; set; }

    /// <summary>Who created it (audit attribution, no foreign key - it must survive account deletion).</summary>
    public required string CreatedByUserId { get; set; }

    /// <summary>Who last edited it, or <see langword="null"/> if it was never edited after creation.</summary>
    public string? UpdatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
