using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// The scopes an organization API token can carry (card F-027). A closed set, like every other
/// vocabulary in this codebase: the token's scope string is parsed into these values and anything else
/// is refused when the token is minted.
/// </summary>
public static class ApiTokenScopes
{
    /// <summary>Queue a run for a project (<c>POST /api/v1/projects/{id}/runs</c>).</summary>
    public const string Trigger = "trigger";

    /// <summary>Read projects, runs and a run's log.</summary>
    public const string Read = "read";

    /// <summary>Cancel a queued or running run.</summary>
    public const string Cancel = "cancel";

    /// <summary>Every scope, in display order (the mint form's checkboxes).</summary>
    public static readonly string[] All = [Trigger, Read, Cancel];

    /// <summary>Whether <paramref name="scope"/> is one of the known scopes.</summary>
    public static bool IsValid(string? scope) => scope is not null && Array.IndexOf(All, scope) >= 0;

    /// <summary>
    /// The stored form: the known scopes in <see cref="All"/> order, deduplicated. The canonical order
    /// keeps two rows with the same scopes byte-identical, so a test can compare them.
    /// </summary>
    public static string Format(IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        var requested = scopes.ToHashSet(StringComparer.Ordinal);
        return string.Join(',', All.Where(requested.Contains));
    }

    /// <summary>The scopes of a stored token row; an unknown or empty string yields no scopes.</summary>
    public static string[] Parse(string? stored) =>
        string.IsNullOrWhiteSpace(stored)
            ? []
            : stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsValid)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    /// <summary>Whether a token carrying <paramref name="stored"/> holds <paramref name="scope"/>.</summary>
    public static bool Grants(string? stored, string scope) => Array.IndexOf(Parse(stored), scope) >= 0;
}

/// <summary>
/// One hashed bearer credential for the organization API (card F-027). The plaintext
/// (<c>zapi_…</c>) is shown exactly once and never stored - only the SHA-256 hash lives here, the same
/// discipline the worker tokens follow (F-004), so a database leak cannot impersonate an organization.
/// <para>
/// <b>What the row selects.</b> The hash lookup runs through the system scope, and the row - never a
/// cookie, header value or URL - selects the tenant: <c>ApiTokenService.AuthenticateAsync</c> sets
/// <c>TenantContext.OrganizationId</c> from <see cref="OrganizationId"/>. A revoked, expired or
/// unknown token is a bare 401 with no tenant detail.
/// </para>
/// <para>
/// <b>Scopes, not roles.</b> A token authorizes exactly the routes its scopes name
/// (<see cref="ApiTokenScopes"/>); a missing scope is a 403 before any resource is read. The minting
/// account's Owner role is checked when the token is minted, never again per request - the token is the
/// standing authority, which is what makes it usable by a script.
/// </para>
/// </summary>
public sealed class ApiToken : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>
    /// The label the Owner gave the token ("deploy pipeline", "nightly mirror"). Unique per
    /// organization, so a listing never shows two indistinguishable rows. A name, never a secret.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>Lowercase hex SHA-256 of the full plaintext token, unique (the authentication lookup key).</summary>
    public required string TokenHash { get; set; }

    /// <summary>The comma-separated <see cref="ApiTokenScopes"/> the token carries (canonical order).</summary>
    public required string Scopes { get; set; }

    /// <summary>
    /// The Identity user id of the Owner who minted the token. Deliberately no foreign key: the
    /// organization's automation must outlive the account that set it up, and deleting an account must
    /// not silently invalidate a running deployment's credential.
    /// </summary>
    public required string CreatedByUserId { get; set; }

    /// <summary>When the token stops being accepted; <see langword="null"/> means it never expires.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Last time the token authenticated a request (updated at most once a minute, so a polling client
    /// does not turn every read into a write). <see langword="null"/> until the first use.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>When an Owner revoked the token; revoked means invalid, permanently.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The bounds of an API token's user-authored fields (card F-027). Bounds, not rules: the rules live in
/// <c>ApiTokenService</c> and the refusal codes live in <c>ApiErrorCodes</c>.
/// </summary>
public static class ApiTokenLimits
{
    /// <summary>An upper bound on the token's label (mirrors the other user-authored names).</summary>
    public const int MaxNameLength = 100;

    /// <summary>An upper bound on the stored scope string (<c>trigger,read,cancel</c> fits easily).</summary>
    public const int MaxScopesLength = 64;

    /// <summary>An upper bound on an <c>Idempotency-Key</c> header value.</summary>
    public const int MaxIdempotencyKeyLength = 200;
}
