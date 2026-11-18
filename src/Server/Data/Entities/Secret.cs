using Zenkins.Shared.Secrets;
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
    /// What this secret <em>is</em> (card F-040): one of <see cref="SecretKinds"/>. The kind decides which
    /// reference syntax may read it and how <see cref="Ciphertext"/>'s plaintext is shaped - it is never
    /// inferred from the payload, because an empty password or a file whose bytes happen to look like a name
    /// must not change what a secret is.
    /// <para>
    /// <b>It defaults to <c>text</c>, and that is the migration.</b> Every row written before card F-040 held
    /// one string, so the column's database default is <c>text</c> and an old row reads as today's behaviour
    /// without a data update. The form never offers a way to change the kind afterwards: a different kind is
    /// a different secret (delete + create), which keeps a run from silently picking up a differently shaped
    /// value.
    /// </para>
    /// </summary>
    public string Kind { get; set; } = SecretKinds.Text;

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

    /// <summary>
    /// The Identity user id of the Owner who last rotated the secret (card D-006), or <see langword="null"/>.
    /// Null means two different things and the page tells them apart by <see cref="RotatedAt"/>: a secret
    /// that was never rotated, or one rotated before this column existed ("not recorded"). No foreign key
    /// for the same reason as <see cref="CreatedByUserId"/>: attribution must survive account deletion.
    /// </summary>
    public string? RotatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
