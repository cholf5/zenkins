using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// One remembered <c>Idempotency-Key</c> of an API trigger (card F-027, scoped by card B-008): the key,
/// the token that sent it, the project it was used against, a hash of the request payload, and the run it
/// queued. A retried trigger - same token, same key, same project, same payload - answers the remembered
/// run instead of queueing a second one, which is what makes "post the deploy, lose the response, post
/// again" safe.
/// <para>
/// <b>Scoped to the token, bound to one project and one payload.</b> Two scripts of the same
/// organization may reuse the word "nightly" without colliding (the credential that sent the key is the
/// identity, and the unique index is <c>(ApiTokenId, Key)</c>). Within one token the key names <b>one
/// request</b>: reusing it against another project, or with another payload, is refused instead of
/// replaying the wrong run (card B-008).
/// </para>
/// <para>
/// <b>Bounded, not forever.</b> <see cref="ExpiresAt"/> is written with <see cref="Retention"/>; a
/// record past it no longer replays (the lookup forgets it, so the key is usable again). Without that
/// bound a nightly job with a fixed key would replay its first run for the lifetime of the token.
/// </para>
/// <para>
/// <b>The key is not a secret and not stored plaintext-only for convenience.</b> It is the caller's own
/// label, bounded like the other user-authored strings; the row carries no credential.
/// </para>
/// </summary>
public sealed class ApiIdempotencyRecord : ITenantOwned
{
    /// <summary>
    /// How long a key replays its run. Sized for the retry it exists for - a lost response, a retried
    /// deploy - and not for "remember every key forever", which would also need a sweeper per tenant.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>The token that sent the key; cascade-deleted with it (revoking keeps, deleting forgets).</summary>
    public Guid ApiTokenId { get; set; }

    /// <summary>The caller's <c>Idempotency-Key</c> header value, as sent.</summary>
    public required string Key { get; set; }

    /// <summary>
    /// The project the key was used against (card B-008). A key reused against another project is a
    /// refusal, never a replay of the first project's run. Not a foreign key: the run's own cascade
    /// already removes this record when the project's history goes, and a project id that no longer
    /// exists must not make a record unreadable.
    /// </summary>
    public Guid ProjectId { get; set; }

    /// <summary>
    /// SHA-256 (lowercase hex) of the trigger's payload - branch, required tags, timeout and parameter
    /// values, canonicalized (card B-008). Same key plus a different payload is a refusal: a replay of
    /// the old run would silently build the branch the caller abandoned.
    /// </summary>
    public required string RequestHash { get; set; }

    /// <summary>The run this key queued. Cascade-deleted with the run (a deleted project forgets its keys).</summary>
    public Guid RunId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When this binding stops replaying (<see cref="CreatedAt"/> + <see cref="Retention"/>). The lookup
    /// treats an expired record as absent, so the key may be used again for a fresh trigger.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow + Retention;
}
