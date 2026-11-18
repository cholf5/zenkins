using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// One organization environment variable (card F-034, PRD §3.1.1 / §3.11.3): a plain name/value pair
/// every project's steps receive as an environment variable, so a registry host, a tool path or a
/// proxy setting is configured once instead of per project.
/// <para>
/// <b>Name rules are the secret rules.</b> <see cref="Name"/> is validated by
/// <c>SecretValidation.ValidateName</c> - the very same env-var-compatible allow-list a stored secret
/// follows - and is unique per organization (case-sensitive, enforced by a unique index). The
/// name is <em>not</em> immutable (unlike a secret's): an edit may rename the row, because a value
/// here is configuration, not a credential whose identity a run pins.
/// </para>
/// <para>
/// <b>Not a secret.</b> <see cref="Value"/> is stored as plaintext on purpose: it is configuration.
/// It is shown in the organization's environment card, in the run page's frozen environment view and
/// (if a step echoes it) in a run's log. Anything sensitive belongs in Secrets (F-012), whose rows
/// are Owner-only, write-only and encrypted. This row is Owner-writable but Member-readable, like
/// the layer it feeds.
/// </para>
/// <para>
/// <b>Names must not collide with the server's own.</b> <c>ZENKINS_*</c> is the server-provided
/// metadata namespace; a variable there is refused at save time (<c>OrganizationEnvironment.NameReserved</c>)
/// rather than silently losing to the metadata the worker writes.
/// </para>
/// </summary>
public sealed class OrganizationEnvironmentVariable : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>
    /// The environment variable's name, unique inside the organization and case-sensitive. Validated
    /// with the stored-secret rule, so it is always usable as an environment variable.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>Plain configuration (never encrypted, never masked). Bounded by the form's own cap.</summary>
    public required string Value { get; set; }

    /// <summary>
    /// The Identity user id of the Owner who created the row. Deliberately no foreign key, like every
    /// other attribution column: deleting an account must not cascade configuration away.
    /// </summary>
    public required string CreatedByUserId { get; set; }

    /// <summary>The Identity user id of the Owner who last edited it, or <see langword="null"/> when never edited.</summary>
    public string? UpdatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
