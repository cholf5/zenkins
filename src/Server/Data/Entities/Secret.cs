using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// One organization secret (PRD §3.8 / §6, card F-012): a name/value pair a pipeline consumes through
/// <c>secret("NAME")</c> (F-015) or as a Git credential (F-013).
/// <para>
/// <b>The name is the identity.</b> It is unique per organization (case-sensitive, enforced by a
/// unique index), validated to <c>^[A-Za-z_][A-Za-z0-9_]{0,63}$</c> so it is also a usable environment
/// variable name, and it can never be renamed: a rename is delete + create, which keeps a run from
/// silently picking up a differently shaped value.
/// </para>
/// <para>
/// <b>Write-only above the storage layer.</b> <see cref="Ciphertext"/> is Data Protection output
/// bound to the <c>Zenkins.Secrets.v1</c> purpose; the plaintext is never stored, never hashed and
/// never returned by an endpoint or a page. Only <c>SecretService.TryGetValueAsync</c> (internal, for
/// F-013/F-015) decrypts it, and no route exposes that method.
/// </para>
/// <para>
/// <b>Audit-light by design.</b> <see cref="CreatedAt"/>/<see cref="UpdatedAt"/> are stamped by
/// <see cref="ZenkinsDbContext"/>'s single timestamp routine; <see cref="RotatedAt"/> records the last
/// value replacement; <see cref="CreatedByUserId"/> attributes the row. No plaintext, no value hash:
/// a hash of a low-entropy secret is a guessing oracle.
/// </para>
/// </summary>
public sealed class Secret : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>
    /// Name of the secret, unique inside the organization and case-sensitive. Also the environment
    /// variable name F-015 exposes, which is why the validator is an env-var-compatible allow-list.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Data Protection ciphertext (<c>SecretProtector</c>, purpose <c>Zenkins.Secrets.v1</c>).
    /// Deliberately no max length: the framework's output grows with the plaintext and secrets are
    /// bounded by the form limit, not by a column type.
    /// </summary>
    public required string Ciphertext { get; set; }

    /// <summary>
    /// When the value was last replaced (rotation). <see langword="null"/> until the first rotation;
    /// a rotation replaces <see cref="Ciphertext"/> in place, so the previous ciphertext is gone.
    /// </summary>
    public DateTimeOffset? RotatedAt { get; set; }

    /// <summary>
    /// The Identity user id of the Owner who created the secret. Deliberately no foreign key: an
    /// account deletion must not cascade the organization's secrets away, and the id is audit
    /// attribution, not an ownership relation.
    /// </summary>
    public required string CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
