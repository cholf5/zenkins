using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// One organization's Git credential (PRD §6, card F-013): the material a Worker uses to clone a
/// project's repository, referenced from <see cref="Project.GitCredentialId"/>.
/// <para>
/// <b>Its own entity, its own purpose.</b> A Git credential is not a <see cref="Secret"/>: it has a
/// kind (an HTTPS token or an SSH key), it is delivered only to the worker that is executing a run of
/// the owning project, and its ciphertext is produced under
/// <c>Zenkins.GitCredentials.v1</c> (<c>GitCredentialProtector</c>). Sharing the secrets purpose would
/// make the two ciphertext classes interchangeable, so a blob copied from one table into the other
/// would decrypt; here it does not.
/// </para>
/// <para>
/// <b>The name is the identity</b>, on the same rules as a secret name (unique per organization,
/// case-sensitive, env-var shaped), so a project setting can reference a credential unambiguously.
/// A rename is delete + create; rotation replaces <see cref="Ciphertext"/> in place.
/// </para>
/// <para>
/// <b>Write-only above this layer.</b> Nothing returns the value: the only decrypt path is
/// <c>GitCredentialService.FetchMaterialForRunAsync</c>, which answers a run-scoped, worker-
/// authenticated request. No page and no user-facing endpoint exposes a value or a ciphertext.
/// </para>
/// </summary>
public sealed class GitCredential : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>The identity: unique per organization, case-sensitive, immutable.</summary>
    public required string Name { get; set; }

    /// <summary>One of <c>GitCredentialKind</c>'s stored texts (<c>HttpsToken</c>, <c>SshKey</c>).</summary>
    public required string Kind { get; set; }

    /// <summary>
    /// Data Protection ciphertext (<c>GitCredentialProtector</c>, purpose
    /// <c>Zenkins.GitCredentials.v1</c>). No max length: the output grows with the plaintext and the
    /// form limit, not the column type, bounds it.
    /// </summary>
    public required string Ciphertext { get; set; }

    /// <summary>
    /// The user name an HTTPS token authenticates as. Not a secret (it is the account label), and
    /// <see langword="null"/> means the worker sends its documented default (<c>x-access-token</c>).
    /// Unused for an SSH key.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>When the value was last replaced. Null until the first rotation.</summary>
    public DateTimeOffset? RotatedAt { get; set; }

    /// <summary>Audit attribution; no foreign key, so deleting an account never deletes credentials.</summary>
    public required string CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
