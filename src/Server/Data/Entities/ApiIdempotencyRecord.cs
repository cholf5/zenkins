using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// One remembered <c>Idempotency-Key</c> of an API trigger (card F-027): the key, the token that sent
/// it and the run it queued. A retried trigger with the same key answers the remembered run instead of
/// queueing a second one, which is what makes "post the deploy, lose the response, post again" safe.
/// <para>
/// <b>Scoped to the token, not the organization.</b> Two scripts of the same organization may reuse the
/// word "nightly" without colliding; the credential that sent the key is the identity, and the unique
/// index is <c>(ApiTokenId, Key)</c>.
/// </para>
/// <para>
/// <b>The key is not a secret and not stored plaintext-only for convenience.</b> It is the caller's own
/// label, bounded like the other user-authored strings; the row carries no credential.
/// </para>
/// </summary>
public sealed class ApiIdempotencyRecord : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>The token that sent the key; cascade-deleted with it (revoking keeps, deleting forgets).</summary>
    public Guid ApiTokenId { get; set; }

    /// <summary>The caller's <c>Idempotency-Key</c> header value, as sent.</summary>
    public required string Key { get; set; }

    /// <summary>The run this key queued. Cascade-deleted with the run (a deleted project forgets its keys).</summary>
    public Guid RunId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
