using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>What a <see cref="WorkerToken"/> row authenticates.</summary>
public static class WorkerTokenKind
{
    /// <summary>A single-use token an Owner minted for registering one worker.</summary>
    public const string Registration = "Registration";

    /// <summary>The per-worker token returned at registration; proves identity on every later call.</summary>
    public const string Worker = "Worker";
}

/// <summary>
/// One hashed bearer credential for the worker API (F-004). The plaintext (`zreg_…`/`zwrk_…`) is
/// shown exactly once and never stored: only the SHA-256 hash lives here, so a database leak cannot
/// mint workers or impersonate one.
/// <para>
/// Registration tokens have <see cref="WorkerId"/> <c>null</c> and are burned (one-time) on use.
/// Worker tokens belong to exactly one <see cref="Worker"/>, are revoked when that worker
/// re-registers under the same name, and are cascade-deleted with it.
/// </para>
/// </summary>
public sealed class WorkerToken : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>
    /// The worker this token authenticates; <c>null</c> for registration tokens. Cascade-deleted
    /// with the worker, so deleting a worker cannot leave orphaned credentials behind.
    /// </summary>
    public Guid? WorkerId { get; set; }

    /// <summary>The <see cref="Entities.WorkerTokenKind"/> discriminator.</summary>
    public required string Kind { get; set; }

    /// <summary>Lowercase hex SHA-256 of the full plaintext token, unique (the lookup key for auth).</summary>
    public required string TokenHash { get; set; }

    /// <summary>Set when a registration token was consumed; a used token can never be used again.</summary>
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>When the token was revoked (registration tokens by an Owner); revoked means invalid.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Last time this token successfully authenticated (worker tokens only).</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
