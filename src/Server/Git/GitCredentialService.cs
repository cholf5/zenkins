using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Workers;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Git;

/// <summary>Outcome of a Git credential create/rotate/delete attempt.</summary>
public enum GitCredentialMutationStatus
{
    Success = 0,

    /// <summary>Missing id in the active organization; also the foreign-organization case (same 404).</summary>
    NotFound = 1,

    /// <summary>The caller is not an Owner of the active organization.</summary>
    Forbidden = 2,

    /// <summary>One or more fields failed validation; nothing was written.</summary>
    InvalidInput = 3,
}

/// <summary>One field-level validation failure; the endpoint maps the code back to its field.</summary>
public sealed record GitCredentialFieldError(string Field, string Code);

/// <summary>The raw form values, before trimming and validation.</summary>
public sealed record GitCredentialInput(string? Name, string? Kind, string? Value, string? UserName);

/// <summary>Result of a mutation; <see cref="GitCredentialId"/> is set on success.</summary>
public sealed record GitCredentialMutationResult(
    GitCredentialMutationStatus Status,
    Guid? GitCredentialId = null,
    string? Message = null,
    IReadOnlyList<GitCredentialFieldError>? FieldErrors = null)
{
    public static GitCredentialMutationResult Success(Guid credentialId) =>
        new(GitCredentialMutationStatus.Success, credentialId);

    public static GitCredentialMutationResult NotFound() => new(GitCredentialMutationStatus.NotFound);

    public static GitCredentialMutationResult Forbidden(string message) =>
        new(GitCredentialMutationStatus.Forbidden, Message: message);

    public static GitCredentialMutationResult Invalid(params GitCredentialFieldError[] errors) =>
        new(GitCredentialMutationStatus.InvalidInput, FieldErrors: errors);
}

/// <summary>Outcome of a worker's credential fetch.</summary>
public enum GitCredentialFetchStatus
{
    /// <summary>Material for a run assigned to the calling worker.</summary>
    Success = 0,

    /// <summary>Missing/unknown/revoked worker token: a bare 401.</summary>
    InvalidToken = 1,

    /// <summary>
    /// No material for this caller. <b>Every</b> reason collapses into this one value: an unknown run,
    /// another tenant's run, a run assigned to a different worker, a project with no credential and a
    /// credential that no longer resolves are the same bodyless 404, so the endpoint is not an
    /// existence oracle.
    /// </summary>
    NotFound = 2,
}

/// <summary>The decrypted material handed to one run. Never rendered by a page or a user endpoint.</summary>
public sealed record GitCredentialMaterialView(string Kind, string Value, string? UserName);

/// <summary>The answer of a worker's fetch: material on success, nothing otherwise.</summary>
public sealed record GitCredentialFetchResult(
    GitCredentialFetchStatus Status,
    GitCredentialMaterialView? Material = null)
{
    public static GitCredentialFetchResult Success(GitCredentialMaterialView material) =>
        new(GitCredentialFetchStatus.Success, material);

    public static GitCredentialFetchResult InvalidToken() => new(GitCredentialFetchStatus.InvalidToken);

    public static GitCredentialFetchResult NotFound() => new(GitCredentialFetchStatus.NotFound);
}

/// <summary>
/// Owner-only management of the active organization's Git credentials, plus the run-scoped fetch the
/// Worker uses at clone time (card F-013, PRD §3.2/§6).
/// <para>
/// <b>Tenant boundary.</b> The organization always comes from <see cref="ITenantContext"/>; no method
/// takes an organization id from a caller. Every lookup goes through the tenant-filtered
/// <see cref="ZenkinsDbContext"/>, so another organization's id is indistinguishable from a missing one
/// (<see cref="GitCredentialMutationStatus.NotFound"/>, 404) and never a 403 oracle.
/// </para>
/// <para>
/// <b>Ownership.</b> Only an Owner manages credentials, checked before the row lookup and before
/// validation (a Member's write is always 403, whatever it posts). Hiding a control is not the
/// enforcement.
/// </para>
/// <para>
/// <b>The one decrypt path.</b> <see cref="FetchMaterialForRunAsync"/> authenticates a worker token,
/// resolves the run through that tenant, and answers only when the run is currently assigned to the
/// calling worker. The organisation, worker and run are all settled before the ciphertext is touched;
/// a failure anywhere collapses to the same 404 and the same log line, so neither the response nor the
/// server log distinguishes "not yours" from "does not exist".
/// </para>
/// </summary>
public sealed class GitCredentialService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    ITenantContext tenantContext,
    GitCredentialProtector protector,
    WorkerService workers,
    ILogger<GitCredentialService> logger)
{
    /// <summary>Creates a credential in the active organization. Owner-only.</summary>
    public async Task<GitCredentialMutationResult> CreateAsync(
        string actingUserId,
        GitCredentialInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return GitCredentialMutationResult.Forbidden(
                "You are not a member of an organization, so there is no Git credential to create.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return GitCredentialMutationResult.Forbidden("Only an organization Owner can manage Git credentials.");
        }

        var errors = Validate(input, requiresName: true, out var name, out var kind, out var value, out var userName);
        if (errors.Count > 0)
        {
            return GitCredentialMutationResult.Invalid([.. errors]);
        }

        var credential = new GitCredential
        {
            OrganizationId = activeOrganizationId.Value,
            Name = name,
            Kind = kind,
            Ciphertext = protector.Protect(value),
            UserName = userName,
            CreatedByUserId = actingUserId,
        };
        context.GitCredentials.Add(credential);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "GitCredentials"))
        {
            return DuplicateName();
        }

        // The value and the ciphertext are deliberately absent from this log line.
        logger.LogInformation(
            "Git credential created. OrganizationId={OrganizationId} GitCredentialId={GitCredentialId} Name={Name} Kind={Kind} ByUserId={UserId}",
            credential.OrganizationId,
            credential.Id,
            credential.Name,
            credential.Kind,
            actingUserId);
        return GitCredentialMutationResult.Success(credential.Id);
    }

    /// <summary>
    /// Replaces a credential's value and kind in place and stamps <c>RotatedAt</c>. Owner-only; a
    /// missing or foreign id is a 404 for an Owner. The name cannot change here.
    /// </summary>
    public async Task<GitCredentialMutationResult> RotateAsync(
        string actingUserId,
        Guid credentialId,
        GitCredentialInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return GitCredentialMutationResult.Forbidden(
                "You are not a member of an organization, so there is no Git credential to rotate.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return GitCredentialMutationResult.Forbidden("Only an organization Owner can manage Git credentials.");
        }

        var credential = await context.GitCredentials.SingleOrDefaultAsync(
            candidate => candidate.Id == credentialId,
            cancellationToken);
        if (credential is null)
        {
            return GitCredentialMutationResult.NotFound();
        }

        // A rotation may change the kind (an HTTPS token replaced by an SSH key is a legitimate
        // migration of one project's auth), so the submitted kind wins and the value is re-validated
        // against it.
        var errors = Validate(input, requiresName: false, out _, out var kind, out var value, out var userName);
        if (errors.Count > 0)
        {
            return GitCredentialMutationResult.Invalid([.. errors]);
        }

        credential.Kind = kind;
        credential.Ciphertext = protector.Protect(value);
        credential.UserName = userName;
        credential.RotatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Git credential rotated. OrganizationId={OrganizationId} GitCredentialId={GitCredentialId} Name={Name} Kind={Kind} ByUserId={UserId}",
            activeOrganizationId,
            credential.Id,
            credential.Name,
            credential.Kind,
            actingUserId);
        return GitCredentialMutationResult.Success(credential.Id);
    }

    /// <summary>Hard-deletes a credential. Owner-only; a missing or foreign id is a 404 for an Owner.</summary>
    public async Task<GitCredentialMutationResult> DeleteAsync(
        string actingUserId,
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return GitCredentialMutationResult.Forbidden(
                "You are not a member of an organization, so there is no Git credential to delete.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return GitCredentialMutationResult.Forbidden("Only an organization Owner can manage Git credentials.");
        }

        var credential = await context.GitCredentials.SingleOrDefaultAsync(
            candidate => candidate.Id == credentialId,
            cancellationToken);
        if (credential is null)
        {
            return GitCredentialMutationResult.NotFound();
        }

        context.GitCredentials.Remove(credential);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Git credential deleted. OrganizationId={OrganizationId} GitCredentialId={GitCredentialId} Name={Name} ByUserId={UserId}",
            activeOrganizationId,
            credential.Id,
            credential.Name,
            actingUserId);
        return GitCredentialMutationResult.Success(credential.Id);
    }

    /// <summary>
    /// The run-scoped, worker-authenticated fetch (card F-013): decrypts the credential of the run's
    /// project, for a run the calling worker currently holds.
    /// <para>
    /// <b>One 404 for every miss.</b> Unknown run, another tenant's run, a run assigned to a different
    /// worker and a project without a credential all return
    /// <see cref="GitCredentialFetchStatus.NotFound"/> and write the same log line, so neither the
    /// response nor the log can be used to probe which run ids exist.
    /// </para>
    /// </summary>
    public async Task<GitCredentialFetchResult> FetchMaterialForRunAsync(
        string? plaintextWorkerToken,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        var workerToken = await workers.AuthenticateWorkerAsync(plaintextWorkerToken, cancellationToken);
        if (workerToken is null)
        {
            return GitCredentialFetchResult.InvalidToken();
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var worker = await context.Workers.SingleOrDefaultAsync(
            candidate => candidate.Id == workerToken.WorkerId,
            cancellationToken);

        // The run lookup is tenant-filtered: a foreign organization's run id simply is not there, which
        // is exactly the same answer as "that run does not exist".
        var run = worker is null
            ? null
            : await context.BuildRuns.SingleOrDefaultAsync(
                candidate => candidate.Id == runId,
                cancellationToken);

        if (worker is null
            || run is null
            || run.WorkerId != worker.Id
            || IsTerminal(run.Status))
        {
            LogMiss(runId, workerToken.WorkerId);
            return GitCredentialFetchResult.NotFound();
        }

        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == run.ProjectId,
            cancellationToken);
        if (project?.GitCredentialId is not { } credentialId)
        {
            LogMiss(runId, worker.Id);
            return GitCredentialFetchResult.NotFound();
        }

        var credential = await context.GitCredentials.SingleOrDefaultAsync(
            candidate => candidate.Id == credentialId,
            cancellationToken);
        if (credential is null)
        {
            LogMiss(runId, worker.Id);
            return GitCredentialFetchResult.NotFound();
        }

        string value;
        try
        {
            value = protector.Unprotect(credential.Ciphertext);
        }
        catch (CryptographicException failure)
        {
            // A deployment fault, not a "not found": the key ring or the purpose changed. Logged and
            // surfaced as a 500 (no body), because pretending the credential is absent would send an
            // operator looking in the wrong place.
            logger.LogError(
                failure,
                "Git credential could not be decrypted. OrganizationId={OrganizationId} GitCredentialId={GitCredentialId}",
                credential.OrganizationId,
                credential.Id);
            throw;
        }

        logger.LogInformation(
            "Git credential delivered. OrganizationId={OrganizationId} RunId={RunId} WorkerId={WorkerId} GitCredentialId={GitCredentialId} Kind={Kind}",
            credential.OrganizationId,
            runId,
            worker.Id,
            credential.Id,
            credential.Kind);
        return GitCredentialFetchResult.Success(
            new GitCredentialMaterialView(credential.Kind, value, credential.UserName));
    }

    /// <summary>
    /// The single log line for every miss. Deliberately carries no reason: "foreign run", "not
    /// assigned to this worker", "unknown id" and "no credential configured" must be indistinguishable
    /// to anyone reading the server log.
    /// </summary>
    private void LogMiss(Guid runId, Guid? workerId) =>
        logger.LogInformation(
            "Git credential request resolved no material. RunId={RunId} WorkerId={WorkerId}",
            runId,
            workerId);

    private static bool IsTerminal(string status) =>
        status is BuildRunStatus.Success
            or BuildRunStatus.Failed
            or BuildRunStatus.Canceled
            or BuildRunStatus.Lost;

    private static List<GitCredentialFieldError> Validate(
        GitCredentialInput input,
        bool requiresName,
        out string name,
        out string kind,
        out string value,
        out string? userName)
    {
        var errors = new List<GitCredentialFieldError>();

        name = string.Empty;
        if (requiresName)
        {
            var nameError = GitCredentialValidation.ValidateName(input.Name, out name);
            if (nameError is not null)
            {
                errors.Add(new GitCredentialFieldError(GitCredentialFields.Name, nameError));
            }
        }

        var kindError = GitCredentialValidation.ValidateKind(input.Kind, out kind);
        if (kindError is not null)
        {
            errors.Add(new GitCredentialFieldError(GitCredentialFields.Kind, kindError));
        }

        var valueError = GitCredentialValidation.ValidateValue(kind, input.Value);
        if (valueError is not null)
        {
            errors.Add(new GitCredentialFieldError(GitCredentialFields.Value, valueError));
        }

        value = input.Value ?? string.Empty;

        var userNameError = GitCredentialValidation.ValidateUserName(input.UserName, out userName);
        if (userNameError is not null)
        {
            errors.Add(new GitCredentialFieldError(GitCredentialFields.UserName, userNameError));
        }

        return errors;
    }

    private static GitCredentialMutationResult DuplicateName() =>
        GitCredentialMutationResult.Invalid(
            new GitCredentialFieldError(GitCredentialFields.Name, GitCredentialErrorCodes.NameDuplicate));

    /// <summary>Owner check against the active tenant's membership, never another organization's.</summary>
    private static Task<bool> IsOwnerAsync(
        ZenkinsDbContext context,
        string actingUserId,
        CancellationToken cancellationToken) =>
        context.OrganizationMembers.AnyAsync(
            member => member.UserId == actingUserId && member.Role == OrganizationRole.Owner,
            cancellationToken);

}
