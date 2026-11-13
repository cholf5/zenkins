using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Secrets;

/// <summary>Outcome of a secret create/rotate/delete attempt.</summary>
public enum SecretMutationStatus
{
    Success = 0,

    /// <summary>
    /// The secret id does not exist in the active organization. Also the foreign-organization case:
    /// both are the same 404, so the id never becomes an existence oracle.
    /// </summary>
    NotFound = 1,

    /// <summary>The caller is not an Owner of the active organization (or has no organization).</summary>
    Forbidden = 2,

    /// <summary>One or more fields failed validation; nothing was written.</summary>
    InvalidInput = 3,
}

/// <summary>One field-level validation failure. The UI turns the code into the message.</summary>
public sealed record SecretFieldError(string Field, string Code);

/// <summary>
/// A secret as the page displays it: name and timestamps only. The record deliberately has no value
/// or ciphertext member, so a component cannot accidentally render one.
/// </summary>
public sealed record SecretView(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RotatedAt,
    string CreatedByUserId);

/// <summary>The raw form values, before trimming and validation.</summary>
public sealed record SecretInput(string? Name, string? Value);

/// <summary>
/// Result of reading the secret list: the rows for an Owner, or the reason there are none. A record
/// (not a nullable list) because "not allowed to read" and "no secrets yet" must not be confusable.
/// </summary>
public sealed record SecretListResult(
    SecretMutationStatus Status,
    IReadOnlyList<SecretView> Secrets,
    string? Message = null)
{
    public static SecretListResult Success(IReadOnlyList<SecretView> secrets) =>
        new(SecretMutationStatus.Success, secrets);

    public static SecretListResult Forbidden(string message) =>
        new(SecretMutationStatus.Forbidden, [], message);
}

/// <summary>
/// Result of a mutation. <see cref="SecretId"/> is set on success; <see cref="FieldErrors"/> is set for
/// <see cref="SecretMutationStatus.InvalidInput"/>.
/// </summary>
public sealed record SecretMutationResult(
    SecretMutationStatus Status,
    Guid? SecretId = null,
    string? Message = null,
    IReadOnlyList<SecretFieldError>? FieldErrors = null)
{
    public static SecretMutationResult Success(Guid secretId) => new(SecretMutationStatus.Success, secretId);

    public static SecretMutationResult NotFound() => new(SecretMutationStatus.NotFound);

    public static SecretMutationResult Forbidden(string message) =>
        new(SecretMutationStatus.Forbidden, Message: message);

    public static SecretMutationResult Invalid(params SecretFieldError[] errors) =>
        new(SecretMutationStatus.InvalidInput, FieldErrors: errors);
}

/// <summary>
/// Owner-only management of the active organization's secrets (card F-012, PRD §3.8).
/// <para>
/// <b>Tenant boundary.</b> The organization always comes from <see cref="ITenantContext"/>; no method
/// accepts an organization id from a caller. Every lookup goes through the tenant-filtered
/// <see cref="ZenkinsDbContext"/>, so another organization's id is indistinguishable from a missing
/// one and is a <see cref="SecretMutationStatus.NotFound"/> (404), never a 403 oracle.
/// </para>
/// <para>
/// <b>Ownership (PRD §3.1.2).</b> Only an Owner manages secrets. Authorization is checked before the
/// row is looked up and before the input is validated: a Member's write is always a 403 regardless of
/// what it posts (so a malformed body cannot reveal whether an id exists), and a foreign
/// organization's id is a 404 for an Owner. Hiding a control is never the enforcement.
/// </para>
/// <para>
/// <b>Write-only above this layer.</b> Create and rotate store
/// <see cref="SecretProtector"/> ciphertext; nothing here returns a value. The one decrypting member,
/// <see cref="TryGetValueAsync"/>, is internal and reserved for F-013 (Git credentials) and F-015
/// (<c>secret()</c>); it is pinned by the route-shape test, which fails if any endpoint gains a
/// get-value route.
/// </para>
/// <para>
/// <b>Identity is the name.</b> There is no rename: a rotate ignores a submitted name, and a rename is
/// delete + create. The unique index on <c>(OrganizationId, Name)</c> is the duplicate guard, not a
/// pre-flight query.
/// </para>
/// <para>
/// <b>Timestamps.</b> <c>CreatedAt</c>/<c>UpdatedAt</c> are stamped by <see cref="ZenkinsDbContext"/>'s
/// single routine; this service only stamps the domain event <c>RotatedAt</c>. The plaintext is never
/// logged, and neither is the ciphertext.
/// </para>
/// </summary>
public sealed class SecretService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    ITenantContext tenantContext,
    SecretProtector protector,
    ILogger<SecretService> logger)
{
    /// <summary>
    /// The active organization's secrets, ordered by name (case-sensitive, matching the identity and
    /// the unique index), for an Owner only.
    /// <para>
    /// No endpoint exposes a list, but the Owner check lives here as well as in the page: hiding the
    /// page is not the enforcement, and a future caller must not be able to read names by bypassing
    /// the session check.
    /// </para>
    /// </summary>
    public async Task<SecretListResult> GetSecretsAsync(
        string actingUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return SecretListResult.Forbidden(
                "You are not a member of an organization, so there are no secrets to list.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return SecretListResult.Forbidden("Only an organization Owner can manage secrets.");
        }

        var secrets = await context.Secrets.ToListAsync(cancellationToken);
        return SecretListResult.Success(
            secrets
                .OrderBy(secret => secret.Name, StringComparer.Ordinal)
                .Select(ToView)
                .ToArray());
    }

    /// <summary>Creates a secret in the active organization. Owner-only.</summary>
    public async Task<SecretMutationResult> CreateAsync(
        string actingUserId,
        SecretInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return SecretMutationResult.Forbidden(
                "You are not a member of an organization, so there is no secret to create.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return SecretMutationResult.Forbidden("Only an organization Owner can manage secrets.");
        }

        var errors = Validate(input, requiresName: true, out var name, out var value);
        if (errors.Count > 0)
        {
            return SecretMutationResult.Invalid([.. errors]);
        }

        var secret = new Secret
        {
            // The tenant comes from the active request; the data layer rejects anything else.
            OrganizationId = activeOrganizationId.Value,
            Name = name,
            Ciphertext = protector.Protect(value),
            CreatedByUserId = actingUserId,
        };
        context.Secrets.Add(secret);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "Secrets"))
        {
            return DuplicateName();
        }

        // The value and the ciphertext are deliberately absent from this log line.
        logger.LogInformation(
            "Secret created. OrganizationId={OrganizationId} SecretId={SecretId} Name={Name} ByUserId={UserId}",
            secret.OrganizationId,
            secret.Id,
            secret.Name,
            actingUserId);
        return SecretMutationResult.Success(secret.Id);
    }

    /// <summary>
    /// Replaces a secret's value in place and stamps <c>RotatedAt</c>. Owner-only; a missing or foreign
    /// id is a 404 for an Owner. The name cannot change here: the submitted name is ignored.
    /// </summary>
    public async Task<SecretMutationResult> RotateAsync(
        string actingUserId,
        Guid secretId,
        SecretInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return SecretMutationResult.Forbidden(
                "You are not a member of an organization, so there is no secret to rotate.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return SecretMutationResult.Forbidden("Only an organization Owner can manage secrets.");
        }

        var secret = await context.Secrets.SingleOrDefaultAsync(
            candidate => candidate.Id == secretId,
            cancellationToken);
        if (secret is null)
        {
            // Not found in the active tenant: a foreign id never becomes a 403 existence oracle.
            return SecretMutationResult.NotFound();
        }

        var errors = Validate(input, requiresName: false, out _, out var value);
        if (errors.Count > 0)
        {
            return SecretMutationResult.Invalid([.. errors]);
        }

        // Rotation replaces the ciphertext: the previous value's blob is gone, so the old ciphertext
        // can never be decrypted again by any path. RotatedAt is the domain event; CreatedAt/UpdatedAt
        // stay the data layer's.
        secret.Ciphertext = protector.Protect(value);
        secret.RotatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Secret rotated. OrganizationId={OrganizationId} SecretId={SecretId} Name={Name} ByUserId={UserId}",
            activeOrganizationId,
            secret.Id,
            secret.Name,
            actingUserId);
        return SecretMutationResult.Success(secret.Id);
    }

    /// <summary>Hard-deletes a secret. Owner-only; a missing or foreign id is a 404 for an Owner.</summary>
    public async Task<SecretMutationResult> DeleteAsync(
        string actingUserId,
        Guid secretId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return SecretMutationResult.Forbidden(
                "You are not a member of an organization, so there is no secret to delete.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return SecretMutationResult.Forbidden("Only an organization Owner can manage secrets.");
        }

        var secret = await context.Secrets.SingleOrDefaultAsync(
            candidate => candidate.Id == secretId,
            cancellationToken);
        if (secret is null)
        {
            return SecretMutationResult.NotFound();
        }

        context.Secrets.Remove(secret);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Secret deleted. OrganizationId={OrganizationId} SecretId={SecretId} Name={Name} ByUserId={UserId}",
            activeOrganizationId,
            secret.Id,
            secret.Name,
            actingUserId);
        return SecretMutationResult.Success(secret.Id);
    }

    /// <summary>
    /// Decrypts one secret of the <b>active</b> tenant, or returns <see langword="null"/> when the
    /// name does not exist there.
    /// <para>
    /// <b>Not part of the HTTP surface.</b> Reserved for run execution - F-013 (Git credentials) and
    /// F-015 (<c>secret()</c> delivery) - and internal so no component or endpoint can call it. There
    /// is intentionally no organization parameter: the caller's token or request has already selected
    /// the tenant, so a caller cannot ask for another organization's value.
    /// </para>
    /// <para>
    /// Throws <see cref="System.Security.Cryptography.CryptographicException"/> when the ciphertext
    /// cannot be decrypted (wrong purpose, wrong key ring). That is a deployment fault, not "not
    /// found", and is not hidden behind a null.
    /// </para>
    /// </summary>
    internal async Task<string?> TryGetValueAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var ciphertext = await context.Secrets
            .Where(secret => secret.Name == name)
            .Select(secret => secret.Ciphertext)
            .SingleOrDefaultAsync(cancellationToken);

        return ciphertext is null ? null : protector.Unprotect(ciphertext);
    }

    private static List<SecretFieldError> Validate(
        SecretInput input,
        bool requiresName,
        out string name,
        out string value)
    {
        var errors = new List<SecretFieldError>();

        name = string.Empty;
        if (requiresName)
        {
            var nameError = SecretValidation.ValidateName(input.Name, out name);
            if (nameError is not null)
            {
                errors.Add(new SecretFieldError(SecretFields.Name, nameError));
            }
        }

        var valueError = SecretValidation.ValidateValue(input.Value, out value);
        if (valueError is not null)
        {
            errors.Add(new SecretFieldError(SecretFields.Value, valueError));
        }

        return errors;
    }

    private static SecretMutationResult DuplicateName() =>
        SecretMutationResult.Invalid(new SecretFieldError(SecretFields.Name, SecretErrorCodes.NameDuplicate));

    /// <summary>Owner check against the active tenant's membership, never another organization's.</summary>
    private static Task<bool> IsOwnerAsync(
        ZenkinsDbContext context,
        string actingUserId,
        CancellationToken cancellationToken) =>
        context.OrganizationMembers.AnyAsync(
            member => member.UserId == actingUserId && member.Role == OrganizationRole.Owner,
            cancellationToken);

    private static SecretView ToView(Secret secret) => new(
        secret.Id,
        secret.Name,
        secret.CreatedAt,
        secret.UpdatedAt,
        secret.RotatedAt,
        secret.CreatedByUserId);
}
