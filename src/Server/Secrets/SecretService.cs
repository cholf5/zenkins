using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Secrets;
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
/// A secret as the page displays it: name, kind, timestamps, attribution and - for the names only - which
/// recorded definitions reference it. The record deliberately has no value or ciphertext member, so a
/// component cannot accidentally render one.
/// </summary>
/// <param name="Kind">The secret's kind (card F-040): what the reference syntax must look like.</param>
/// <param name="CreatedByDisplay">The creator's display name, or the user id when the account is gone.</param>
/// <param name="RotatedByDisplay">The last rotator's display name, null when no rotation is recorded.</param>
public sealed record SecretView(
    Guid Id,
    string Name,
    string Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RotatedAt,
    string CreatedByUserId,
    string CreatedByDisplay,
    string? CreatedByEmail,
    string? RotatedByUserId,
    string? RotatedByDisplay,
    string? RotatedByEmail,
    SecretUsage Usage);

/// <summary>
/// The raw form values, before trimming and validation. <see cref="Kind"/> is optional and absent means
/// <c>text</c>, so every caller written before card F-040 keeps meaning exactly what it used to; a pair's
/// two halves travel in their own fields so the form can label them.
/// </summary>
public sealed record SecretInput(string? Name, string? Value)
{
    /// <summary>The kind (card F-040), or <see langword="null"/> for today's text secret.</summary>
    public string? Kind { get; init; }

    /// <summary>The username half, for <see cref="SecretKinds.UsernamePassword"/>.</summary>
    public string? Username { get; init; }

    /// <summary>The password half, for <see cref="SecretKinds.UsernamePassword"/>.</summary>
    public string? Password { get; init; }
}

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
        var ordered = secrets
            .OrderBy(secret => secret.Name, StringComparer.Ordinal)
            .ToArray();

        // D-006: the creator/rotator display names come from one bounded lookup keyed by the ids just read,
        // and the reference answer from the recorded definitions of this tenant (no repository, no code).
        var userIds = ordered
            .SelectMany(secret => new[] { secret.CreatedByUserId, secret.RotatedByUserId })
            .Where(userId => !string.IsNullOrEmpty(userId))
            .Select(userId => userId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var users = userIds.Length == 0
            ? new Dictionary<string, User>(StringComparer.Ordinal)
            : await context.Users
                .Where(user => userIds.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, cancellationToken);

        var usage = await SecretReferenceIndex.ReadAsync(
            context,
            [.. ordered.Select(secret => secret.Name)],
            cancellationToken);

        return SecretListResult.Success(
            [.. ordered.Select(secret => ToView(secret, users, usage))]);
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

        var errors = Validate(input, requiresName: true, out var name, out var kind, out var payload);
        if (errors.Count > 0)
        {
            return SecretMutationResult.Invalid([.. errors]);
        }

        var secret = new Secret
        {
            // The tenant comes from the active request; the data layer rejects anything else.
            OrganizationId = activeOrganizationId.Value,
            Name = name,

            // F-040: the kind is stored beside the ciphertext, and the ciphertext holds the shape that kind
            // implies (one string, a pair document, or a file's bytes). Only Encode() defines the payload.
            Kind = kind,
            Ciphertext = protector.Protect(payload.Encode()),
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

        // The value, the kind's halves and the ciphertext are deliberately absent from this log line.
        logger.LogInformation(
            "Secret created. OrganizationId={OrganizationId} SecretId={SecretId} Name={Name} Kind={Kind} "
            + "ByUserId={UserId}",
            secret.OrganizationId,
            secret.Id,
            secret.Name,
            secret.Kind,
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

        // F-040: a rotation replaces the value of the secret's <b>existing</b> kind. The kind is never taken
        // from the form - a different kind is a different secret (delete + create), because switching a text
        // secret to a pair in place would change what every existing reference to it means.
        var errors = Validate(input, requiresName: false, out _, out _, out var payload, secret.Kind);
        if (errors.Count > 0)
        {
            return SecretMutationResult.Invalid([.. errors]);
        }

        // Rotation replaces the ciphertext: the previous value's blob is gone, so the old ciphertext
        // can never be decrypted again by any path. RotatedAt is the domain event; CreatedAt/UpdatedAt
        // stay the data layer's.
        secret.Ciphertext = protector.Protect(payload.Encode());
        secret.RotatedAt = DateTimeOffset.UtcNow;

        // D-006: rotation attribution is recorded so the page can say who last rotated it. Still write-only:
        // this is an identity, never the value.
        secret.RotatedByUserId = actingUserId;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Secret rotated. OrganizationId={OrganizationId} SecretId={SecretId} Name={Name} Kind={Kind} "
            + "ByUserId={UserId}",
            activeOrganizationId,
            secret.Id,
            secret.Name,
            secret.Kind,
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

    /// <summary>
    /// Validates one submission and turns it into the payload the ciphertext will hold (card F-040). The
    /// kind decides which fields are required: a text or file secret needs one value, a pair needs both
    /// halves. <paramref name="kindOverride"/> is the stored kind on a rotation - the form cannot change it.
    /// </summary>
    private static List<SecretFieldError> Validate(
        SecretInput input,
        bool requiresName,
        out string name,
        out string kind,
        out SecretPayload payload,
        string? kindOverride = null)
    {
        var errors = new List<SecretFieldError>();

        name = string.Empty;
        payload = SecretPayload.ForText(string.Empty);
        kind = SecretKinds.Default;

        if (requiresName)
        {
            var nameError = SecretValidation.ValidateName(input.Name, out name);
            if (nameError is not null)
            {
                errors.Add(new SecretFieldError(SecretFields.Name, nameError));
            }
        }

        if (kindOverride is null)
        {
            // An absent kind is text (every pre-F-040 caller); an unknown one is refused, never guessed.
            var kindError = SecretValidation.ValidateKind(input.Kind, out kind);
            if (kindError is not null)
            {
                errors.Add(new SecretFieldError(SecretFields.Kind, kindError));
                kind = SecretKinds.Default;
            }
        }
        else
        {
            kind = kindOverride;
        }

        switch (kind)
        {
            case SecretKinds.UsernamePassword:
            {
                var usernameError = SecretValidation.ValidatePairHalf(
                    input.Username, SecretErrorCodes.UsernameRequired, out var username);
                if (usernameError is not null)
                {
                    errors.Add(new SecretFieldError(SecretFields.Username, usernameError));
                }

                var passwordError = SecretValidation.ValidatePairHalf(
                    input.Password, SecretErrorCodes.PasswordRequired, out var password);
                if (passwordError is not null)
                {
                    errors.Add(new SecretFieldError(SecretFields.Password, passwordError));
                }

                payload = SecretPayload.ForPair(username, password);
                break;
            }

            case SecretKinds.File:
            {
                var contentError = SecretValidation.ValidateFileContent(input.Value, out var content);
                if (contentError is not null)
                {
                    errors.Add(new SecretFieldError(SecretFields.Value, contentError));
                }

                payload = SecretPayload.ForFile(content);
                break;
            }

            default:
            {
                var valueError = SecretValidation.ValidateValue(input.Value, out var value);
                if (valueError is not null)
                {
                    errors.Add(new SecretFieldError(SecretFields.Value, valueError));
                }

                payload = SecretPayload.ForText(value);
                break;
            }
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

    private static SecretView ToView(
        Secret secret,
        IReadOnlyDictionary<string, User> users,
        IReadOnlyDictionary<string, SecretUsage> usage)
    {
        users.TryGetValue(secret.CreatedByUserId, out var creator);
        var rotator = secret.RotatedByUserId is { } rotatedBy
            && users.TryGetValue(rotatedBy, out var rotatedByUser)
                ? rotatedByUser
                : null;

        var references = usage.TryGetValue(secret.Name, out var found) ? found : SecretUsage.None;

        return new SecretView(
            secret.Id,
            secret.Name,
            secret.Kind,
            secret.CreatedAt,
            secret.UpdatedAt,
            secret.RotatedAt,
            secret.CreatedByUserId,
            Display(creator, secret.CreatedByUserId),
            creator?.Email,
            secret.RotatedByUserId,
            secret.RotatedByUserId is null ? null : Display(rotator, secret.RotatedByUserId),
            rotator?.Email,
            references);
    }

    /// <summary>
    /// A person's display name for an audit id: the account's user name, its email, or - when the account
    /// is gone - the raw id, so the page never renders an empty attribution it cannot explain.
    /// </summary>
    private static string Display(User? user, string userId) =>
        user?.UserName is { Length: > 0 } userName ? userName : user?.Email ?? userId;
}
