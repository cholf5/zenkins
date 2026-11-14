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

    /// <summary>
    /// The credential is still assigned to one or more projects and the caller did not confirm the
    /// delete (card F-018). <see cref="GitCredentialMutationResult.ReferencingProjects"/> names them,
    /// so the operator can detach first - or confirm the delete knowingly.
    /// </summary>
    Referenced = 4,
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
    IReadOnlyList<GitCredentialFieldError>? FieldErrors = null,
    IReadOnlyList<string>? ReferencingProjects = null)
{
    public static GitCredentialMutationResult Success(Guid credentialId) =>
        new(GitCredentialMutationStatus.Success, credentialId);

    public static GitCredentialMutationResult NotFound() => new(GitCredentialMutationStatus.NotFound);

    public static GitCredentialMutationResult Forbidden(string message) =>
        new(GitCredentialMutationStatus.Forbidden, Message: message);

    public static GitCredentialMutationResult Invalid(params GitCredentialFieldError[] errors) =>
        new(GitCredentialMutationStatus.InvalidInput, FieldErrors: errors);

    /// <summary>A delete refused because projects still clone with this credential (names, no material).</summary>
    public static GitCredentialMutationResult Referenced(
        Guid credentialId,
        IReadOnlyList<string> projectNames) =>
        new(
            GitCredentialMutationStatus.Referenced,
            credentialId,
            $"The credential is still assigned to {projectNames.Count} project(s): "
            + string.Join(", ", projectNames) + ".",
            ReferencingProjects: projectNames);
}

/// <summary>
/// One Git credential as the project settings page lists it (card D-003): the identity (id, name),
/// the kind and the timestamps - <b>never</b> the value, the ciphertext or the user name. The record
/// has no member a material could travel in, so a component cannot accidentally render one.
/// </summary>
public sealed record GitCredentialListItem(
    Guid Id,
    string Name,
    string Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RotatedAt);

/// <summary>
/// One project that clones with a credential (card F-018): the id to link to and the name a person
/// recognises. Nothing here is credential material - it is the project side of the reference.
/// </summary>
public sealed record GitCredentialReferencingProject(Guid ProjectId, string Name);

/// <summary>
/// A project's current assignment, as the settings tab states it (card F-018): which credential, by
/// whom and when. <see cref="AssignedBy"/> is the acting user's e-mail, resolved for display; it is
/// <see langword="null"/> for an assignment made before the attribution columns existed, which the
/// page renders honestly rather than guessing a person.
/// </summary>
public sealed record GitCredentialAssignmentView(
    Guid CredentialId,
    string Name,
    string Kind,
    DateTimeOffset? AssignedAt,
    string? AssignedBy);

/// <summary>
/// The answer of listing a project's credentials: the rows for an Owner, or the reason there are
/// none. A record (not a nullable list) because "not allowed to read" and "no credentials yet" must
/// not be confusable - the same shape <see cref="Zenkins.Server.Secrets.SecretListResult"/> uses.
/// <para>
/// Card F-018 adds the two facts the settings tab needs to <em>assign</em> one: the project's current
/// <see cref="Assignment"/> (or <see langword="null"/>, meaning no credential is assigned and the
/// project's runs clone without one) and, per credential, the projects that clone with it - the
/// reference list a delete must be honest about.
/// </para>
/// </summary>
public sealed record GitCredentialListResult(
    GitCredentialMutationStatus Status,
    IReadOnlyList<GitCredentialListItem> Items,
    string? Message = null,
    GitCredentialAssignmentView? Assignment = null,
    IReadOnlyDictionary<Guid, IReadOnlyList<GitCredentialReferencingProject>>? UsedByProjects = null)
{
    public static GitCredentialListResult Success(
        IReadOnlyList<GitCredentialListItem> items,
        GitCredentialAssignmentView? assignment = null,
        IReadOnlyDictionary<Guid, IReadOnlyList<GitCredentialReferencingProject>>? usedByProjects = null) =>
        new(GitCredentialMutationStatus.Success, items, Assignment: assignment, UsedByProjects: usedByProjects);

    public static GitCredentialListResult NotFound() =>
        new(GitCredentialMutationStatus.NotFound, []);

    public static GitCredentialListResult Forbidden(string message) =>
        new(GitCredentialMutationStatus.Forbidden, [], message);

    /// <summary>The projects that clone with <paramref name="credentialId"/>; empty when it is unused.</summary>
    public IReadOnlyList<GitCredentialReferencingProject> UsedBy(Guid credentialId) =>
        UsedByProjects is { } map && map.TryGetValue(credentialId, out var projects) ? projects : [];
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
/// <b>The assignment (card F-018).</b> <see cref="AssignToProjectAsync"/> and
/// <see cref="DetachFromProjectAsync"/> are the only writers of <see cref="Project.GitCredentialId"/>:
/// Owner-only, both ids resolved through the tenant filter, idempotent, and audited (which credential,
/// which project, by whom, when) in the log and on the project row. A delete of a credential that is
/// still assigned is refused until the caller confirms it with the referencing projects in hand, and a
/// confirmed delete lets the database's foreign key (<c>ON DELETE SET NULL</c>) clear those references, so
/// no project - not even one attached concurrently - is left pointing at a row that is gone.
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

    /// <summary>
    /// Hard-deletes a credential. Owner-only; a missing or foreign id is a 404 for an Owner.
    /// <para>
    /// <b>Reference-aware (card F-018, mirroring D-006 for secrets).</b> A credential that projects
    /// still clone with is never deleted by a plain request: the answer is
    /// <see cref="GitCredentialMutationStatus.Referenced"/> with the projects' names, so the operator
    /// sees what would break - a run of those projects would start failing with no credential. They
    /// can detach the credential there, or repeat the delete with
    /// <paramref name="confirmReferenced"/> after reading that sentence.
    /// </para>
    /// </summary>
    public async Task<GitCredentialMutationResult> DeleteAsync(
        string actingUserId,
        Guid credentialId,
        bool confirmReferenced = false,
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

        // The reference read runs inside the same tenant filter, so a foreign project can neither block
        // this delete nor be named by it. It is a projection on purpose: the rows stay untracked, so the
        // database - not EF's client-side fix-up - is the only thing that clears them.
        var referencingProjects = await context.Projects
            .AsNoTracking()
            .Where(project => project.GitCredentialId == credentialId)
            .Select(project => new { project.Id, project.Name })
            .ToListAsync(cancellationToken);
        if (referencingProjects.Count > 0 && !confirmReferenced)
        {
            var names = referencingProjects
                .Select(project => project.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            logger.LogInformation(
                "Git credential delete deferred: still assigned. OrganizationId={OrganizationId} GitCredentialId={GitCredentialId} ProjectCount={ProjectCount} ByUserId={UserId}",
                activeOrganizationId,
                credential.Id,
                names.Length,
                actingUserId);
            return GitCredentialMutationResult.Referenced(credential.Id, names);
        }

        // A confirmed delete leaves the assignments to the database: `Projects.GitCredentialId` is a
        // foreign key with ON DELETE SET NULL, so every project that referenced this credential has its
        // reference cleared *by the constraint* - including one an attach committed after this read. A
        // service-side loop over `referencingProjects` could not promise that (the attach would slip into
        // the gap), and a project pointing at a deleted row is exactly the silent anonymous clone this
        // card exists to remove.
        context.GitCredentials.Remove(credential);
        await context.SaveChangesAsync(cancellationToken);

        foreach (var project in referencingProjects)
        {
            logger.LogInformation(
                "Git credential assignment cleared by the credential delete (database constraint). OrganizationId={OrganizationId} ProjectId={ProjectId} GitCredentialId={GitCredentialId} ByUserId={UserId}",
                activeOrganizationId,
                project.Id,
                credential.Id,
                actingUserId);
        }

        logger.LogInformation(
            "Git credential deleted. OrganizationId={OrganizationId} GitCredentialId={GitCredentialId} Name={Name} ByUserId={UserId} ReferencedProjectCount={ReferencedProjectCount}",
            activeOrganizationId,
            credential.Id,
            credential.Name,
            actingUserId,
            referencingProjects.Count);
        return GitCredentialMutationResult.Success(credential.Id);
    }

    /// <summary>
    /// Assigns one of the organization's credentials to a project (card F-018's write path): the only
    /// place <see cref="Project.GitCredentialId"/> is ever set.
    /// <para>
    /// <b>Owner-only, tenant-scoped, not an oracle.</b> Both ids are resolved through the
    /// tenant-filtered context, so another organization's project or credential is the same
    /// <see cref="GitCredentialMutationStatus.NotFound"/> as a missing one - never a 403 that would
    /// confirm the id exists.
    /// </para>
    /// <para>
    /// <b>Idempotent.</b> Re-attaching the credential that is already assigned writes nothing and
    /// keeps the original attribution: the audit trail answers "who made this assignment", not "who
    /// last pressed the button".
    /// </para>
    /// </summary>
    public async Task<GitCredentialMutationResult> AssignToProjectAsync(
        string actingUserId,
        Guid projectId,
        Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return GitCredentialMutationResult.Forbidden(
                "You are not a member of an organization, so there is no project to change.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return GitCredentialMutationResult.Forbidden(
                "Only an organization Owner can change a project's Git credential.");
        }

        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        var credential = await context.GitCredentials.SingleOrDefaultAsync(
            candidate => candidate.Id == credentialId,
            cancellationToken);
        if (project is null || credential is null)
        {
            // Missing project, missing credential, another tenant's project, another tenant's
            // credential: one answer, so there is nothing here to probe with.
            return GitCredentialMutationResult.NotFound();
        }

        if (project.GitCredentialId == credential.Id)
        {
            return GitCredentialMutationResult.Success(credential.Id);
        }

        var previousCredentialId = project.GitCredentialId;
        project.GitCredentialId = credential.Id;
        project.GitCredentialAssignedAt = DateTimeOffset.UtcNow;
        project.GitCredentialAssignedByUserId = actingUserId;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsForeignKeyViolation(exception))
        {
            // The credential was deleted by a concurrent confirmed delete between the read above and this
            // write. The constraint refused it, which is the correct answer: the same 404 a missing id
            // gets, and the project keeps the assignment it had. Writing NULL here instead would have
            // looked like success while silently un-assigning the project.
            logger.LogInformation(
                "Git credential assignment refused: the credential no longer exists. OrganizationId={OrganizationId} ProjectId={ProjectId} GitCredentialId={GitCredentialId} ByUserId={UserId}",
                activeOrganizationId,
                project.Id,
                credential.Id,
                actingUserId);
            return GitCredentialMutationResult.NotFound();
        }

        // Which credential, for which project, by whom, and what it replaced: card F-018's audit line.
        // The name and the id are metadata; no value and no ciphertext is ever logged.
        logger.LogInformation(
            "Git credential assigned to project. OrganizationId={OrganizationId} ProjectId={ProjectId} GitCredentialId={GitCredentialId} GitCredentialName={GitCredentialName} PreviousGitCredentialId={PreviousGitCredentialId} ByUserId={UserId}",
            activeOrganizationId,
            project.Id,
            credential.Id,
            credential.Name,
            previousCredentialId,
            actingUserId);
        return GitCredentialMutationResult.Success(credential.Id);
    }

    /// <summary>
    /// Removes a project's credential assignment (card F-018), clearing the attribution with it.
    /// Owner-only; a missing or foreign project is a 404. Detaching a project that has no assignment
    /// is a success and writes nothing - the second half of the card's idempotency requirement.
    /// </summary>
    public async Task<GitCredentialMutationResult> DetachFromProjectAsync(
        string actingUserId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return GitCredentialMutationResult.Forbidden(
                "You are not a member of an organization, so there is no project to change.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return GitCredentialMutationResult.Forbidden(
                "Only an organization Owner can change a project's Git credential.");
        }

        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            return GitCredentialMutationResult.NotFound();
        }

        if (project.GitCredentialId is null)
        {
            return GitCredentialMutationResult.Success(project.Id);
        }

        var previousCredentialId = project.GitCredentialId;
        project.GitCredentialId = null;
        project.GitCredentialAssignedAt = null;
        project.GitCredentialAssignedByUserId = null;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Git credential detached from project. OrganizationId={OrganizationId} ProjectId={ProjectId} PreviousGitCredentialId={PreviousGitCredentialId} ByUserId={UserId}",
            activeOrganizationId,
            project.Id,
            previousCredentialId,
            actingUserId);
        return GitCredentialMutationResult.Success(project.Id);
    }

    /// <summary>
    /// The credentials a project's settings page manages (card D-003): the active organization's
    /// credentials - the set the project's <c>GitCredentialId</c> resolves inside - ordered by name
    /// (case-sensitive, matching the identity and the unique index), with metadata only.
    /// <para>
    /// <b>Owner-only, like every other credential read.</b> Git credentials are managed like secrets
    /// (card D-003's rule): a Member must not learn which credential names exist, so the list answer
    /// for a non-Owner is <see cref="GitCredentialListResult.Forbidden"/> - the same stance the
    /// secrets page takes. The project itself is resolved through the tenant-filtered context first,
    /// so a foreign project id is <see cref="GitCredentialListResult.NotFound"/> (404) for everyone -
    /// never a 403 existence oracle.
    /// </para>
    /// </summary>
    public async Task<GitCredentialListResult> ListForProjectAsync(
        string actingUserId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return GitCredentialListResult.Forbidden(
                "You are not a member of an organization, so there are no Git credentials to list.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return GitCredentialListResult.Forbidden("Only an organization Owner can manage Git credentials.");
        }

        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            // Foreign or unknown id: the same 404, no existence oracle.
            return GitCredentialListResult.NotFound();
        }

        var credentials = await context.GitCredentials
            .Where(credential => credential.OrganizationId == activeOrganizationId.Value)
            .ToListAsync(cancellationToken);

        // Card F-018: the assignment (with its attribution) and, per credential, the projects that
        // clone with it. Both reads go through the tenant filter, so a foreign project can neither be
        // named nor counted here.
        var assignment = project.GitCredentialId is { } assignedId
            ? await ReadAssignmentAsync(context, project, assignedId, cancellationToken)
            : null;

        var referencingProjects = await context.Projects
            .Where(candidate => candidate.GitCredentialId != null)
            .Select(candidate => new { candidate.Id, candidate.Name, CredentialId = candidate.GitCredentialId!.Value })
            .ToListAsync(cancellationToken);
        var usedByProjects = referencingProjects
            .GroupBy(candidate => candidate.CredentialId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<GitCredentialReferencingProject>)
                    [.. group
                        .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(candidate => new GitCredentialReferencingProject(candidate.Id, candidate.Name))]);

        return GitCredentialListResult.Success(
            [.. credentials
                .OrderBy(credential => credential.Name, StringComparer.Ordinal)
                .Select(ToListItem)],
            assignment,
            usedByProjects);
    }

    /// <summary>
    /// The current assignment as the page states it, resolving the acting user's e-mail for display.
    /// Returns <see langword="null"/> when the referenced credential no longer exists (a row deleted
    /// outside the product's own reference-aware delete), so the page can say the assignment is broken
    /// instead of rendering an empty name.
    /// </summary>
    private static async Task<GitCredentialAssignmentView?> ReadAssignmentAsync(
        ZenkinsDbContext context,
        Project project,
        Guid credentialId,
        CancellationToken cancellationToken)
    {
        var credential = await context.GitCredentials
            .Where(candidate => candidate.Id == credentialId)
            .Select(candidate => new { candidate.Id, candidate.Name, candidate.Kind })
            .SingleOrDefaultAsync(cancellationToken);
        if (credential is null)
        {
            return null;
        }

        string? assignedBy = null;
        if (project.GitCredentialAssignedByUserId is { Length: > 0 } userId)
        {
            assignedBy = await context.Users
                .Where(user => user.Id == userId)
                .Select(user => user.Email)
                .SingleOrDefaultAsync(cancellationToken);
        }

        return new GitCredentialAssignmentView(
            credential.Id,
            credential.Name,
            credential.Kind,
            project.GitCredentialAssignedAt,
            assignedBy);
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

    /// <summary>The metadata-only projection: no ciphertext, no value, no user name.</summary>
    private static GitCredentialListItem ToListItem(GitCredential credential) => new(
        credential.Id,
        credential.Name,
        credential.Kind,
        credential.CreatedAt,
        credential.UpdatedAt,
        credential.RotatedAt);

    /// <summary>Owner check against the active tenant's membership, never another organization's.</summary>
    private static Task<bool> IsOwnerAsync(
        ZenkinsDbContext context,
        string actingUserId,
        CancellationToken cancellationToken) =>
        context.OrganizationMembers.AnyAsync(
            member => member.UserId == actingUserId && member.Role == OrganizationRole.Owner,
            cancellationToken);

}
