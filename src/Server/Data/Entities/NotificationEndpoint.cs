using Zenkins.Shared.Notifications;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// One outbound completion-notification receiver of a project (card F-039): where a run's terminal state is posted,
/// which events it subscribes to, and what happened to the last attempt.
/// <para>
/// <b>The URL is a trusted, Owner-configured value - and still treated as dangerous.</b> Only an Owner may point it
/// somewhere, but a wrong URL must not become an SSRF pivot: redirects are not followed, the response body is read
/// up to a small bound, every request times out, and plain <c>http</c> needs an explicit per-endpoint opt-in that the
/// page shows as a warning.
/// </para>
/// <para>
/// <b>The secret is write-only.</b> Like an organization secret and unlike an inbound webhook's (which is shown once
/// because it must be pasted into a git host), this one is typed by the operator for their own receiver:
/// <see cref="SecretCiphertext"/> is Data Protection output under the <c>Zenkins.Notifications.v1</c> purpose, and no
/// route or page ever renders it again.
/// </para>
/// <para>
/// <b>Rotation does not re-sign what is already owed.</b> A pending delivery copies the ciphertext it was created
/// under onto its outbox row (<see cref="OutboxMessage.NotificationSecretCiphertext"/>), so replacing the secret here
/// never makes an in-flight delivery unverifiable by the receiver that still holds the old one.
/// </para>
/// </summary>
public sealed class NotificationEndpoint : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>The project whose runs this endpoint hears about. Cascade-deleted with it.</summary>
    public Guid ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    /// <summary>
    /// The absolute receiver URL (<c>https</c> unless <see cref="AllowInsecureHttp"/> opts into plain <c>http</c>).
    /// Not unique: the same receiver may legitimately be declared twice with different event subscriptions, and
    /// two rows that differ only in that are not a mistake to refuse.
    /// </summary>
    public required string Url { get; set; }

    /// <summary>
    /// Plain <c>http</c> is allowed only when this endpoint says so, and the page shows the warning next to it. A
    /// plaintext notification carries the run's branch and commit and the HMAC secret in the clear.
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// Data Protection ciphertext of the shared secret (<c>NotificationSecretProtector</c>, purpose
    /// <c>Zenkins.Notifications.v1</c>). No max length: the framework's output grows with the plaintext and the
    /// plaintext is the operator's own value.
    /// </summary>
    public required string SecretCiphertext { get; set; }

    /// <summary>When the secret was last replaced; null until the first rotation.</summary>
    public DateTimeOffset? SecretRotatedAt { get; set; }

    /// <summary>
    /// The subscriptions, canonicalized by <see cref="NotificationEvents.Normalize"/>: a comma-separated subset of
    /// <see cref="NotificationEvents.Subscribable"/> (<c>failed</c>, <c>success</c>, <c>recovered</c>, <c>all</c>).
    /// Stored as text for the same reason the run status is - one vocabulary both the Server and its pages read.
    /// </summary>
    public required string Events { get; set; }

    /// <summary>
    /// Whether new obligations are created for this endpoint. Turning it off stops future notifications; a delivery
    /// already owed is still attempted, because the promise was made while the endpoint was on.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>When the last attempt settled (delivered or finally failed). Null until the first attempt.</summary>
    public DateTimeOffset? LastDeliveryAt { get; set; }

    /// <summary>
    /// The last attempt's outcome - a <see cref="NotificationOutcomes"/> value. The page renders it, so "did my
    /// notification go out?" is answerable without reading logs.
    /// </summary>
    public string? LastOutcome { get; set; }

    /// <summary>
    /// The last failure's words, truncated to <see cref="NotificationLimits.MaxErrorLength"/>: the HTTP status and a
    /// bounded slice of the receiver's answer, a timeout, or the transport error. Never a secret (the body is not
    /// echoed back into it).
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// The Identity user id of the Owner who created the endpoint. No foreign key, like every other attribution
    /// column: deleting an account must not sweep a project's configuration away.
    /// </summary>
    public required string CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>The closed vocabulary of a notification delivery's outcome (card F-039).</summary>
public static class NotificationOutcomes
{
    /// <summary>The receiver answered a 2xx.</summary>
    public const string Delivered = "delivered";

    /// <summary>The receiver refused, timed out or could not be reached, and the attempt bound is spent.</summary>
    public const string Failed = "failed";

    /// <summary>An attempt failed and another will follow after the backoff. Not a settled state.</summary>
    public const string Retrying = "retrying";

    /// <summary>Every value, so a reader can enumerate them.</summary>
    public static readonly string[] All = [Delivered, Failed, Retrying];
}

/// <summary>The bounds of the outbound notification path (card F-039).</summary>
public static class NotificationLimits
{
    /// <summary>The longest receiver URL the product stores.</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>The longest shared secret (bytes of UTF-8 are not checked; this is the character count).</summary>
    public const int MinSecretLength = 8;
    public const int MaxSecretLength = 200;

    /// <summary>How much of the receiver's answer is kept for <see cref="NotificationEndpoint.LastError"/>.</summary>
    public const int MaxErrorLength = 300;

    /// <summary>How many endpoints one project may configure (the same order of magnitude as webhooks).</summary>
    public const int MaxEndpointsPerProject = 20;
}
