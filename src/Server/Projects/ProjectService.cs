using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Projects;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Projects;

/// <summary>Outcome of a project create/edit/delete attempt.</summary>
public enum ProjectMutationStatus
{
    Success = 0,

    /// <summary>The project id does not exist in the active organization (also the foreign-tenant case).</summary>
    NotFound = 1,

    /// <summary>The caller is not a member of the active organization, or is not an Owner for an Owner-only action.</summary>
    Forbidden = 2,

    /// <summary>One or more fields failed validation; nothing was written.</summary>
    InvalidInput = 3,
}

/// <summary>One field-level validation failure. The UI turns the code into the message.</summary>
public sealed record ProjectFieldError(string Field, string Code);

/// <summary>
/// A project as the pages display it. A record instead of the tracked entity keeps the UI from
/// holding a row whose <c>ZenkinsDbContext</c> has already been disposed. The repository fields are
/// nullable since F-020 - a Freestyle project has none - and the pages decide what to show per kind.
/// </summary>
public sealed record ProjectView(
    Guid Id,
    string Name,
    string Kind,
    string? RepositoryUrl,
    string? DefaultBranch,
    string? PipelinePath,
    Guid? GitCredentialId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,

    /// <summary>F-020: the Freestyle project's frozen step spec (the steps editor's starting rows).
    /// <see langword="null"/> for Pipeline projects and for a Freestyle project with no saved steps.</summary>
    string? FreestyleSpecJson = null,

    /// <summary>F-021: the project's parameter definitions (the editor's rows and the trigger
    /// form's inputs); <see langword="null"/> when the project declares none.</summary>
    string? ParametersJson = null);

/// <summary>The raw form values, before trimming and validation. <see cref="Kind"/> is honored on
/// create only - a project's kind is immutable after that (F-020), so an edit cannot turn a Pipeline
/// project into a Freestyle one (or back) and strand its runs' semantics.</summary>
public sealed record ProjectInput(
    string? Name,
    string? Kind,
    string? RepositoryUrl,
    string? DefaultBranch,
    string? PipelinePath);

/// <summary>
/// Result of a mutation. <see cref="ProjectId"/> is set on success (and is the id the endpoint
/// redirects to); <see cref="FieldErrors"/> is set for <see cref="ProjectMutationStatus.InvalidInput"/>.
/// </summary>
public sealed record ProjectMutationResult(
    ProjectMutationStatus Status,
    Guid? ProjectId = null,
    string? Message = null,
    IReadOnlyList<ProjectFieldError>? FieldErrors = null)
{
    public static ProjectMutationResult Success(Guid projectId) => new(ProjectMutationStatus.Success, projectId);

    public static ProjectMutationResult NotFound() => new(ProjectMutationStatus.NotFound);

    public static ProjectMutationResult Forbidden(string message) =>
        new(ProjectMutationStatus.Forbidden, Message: message);

    public static ProjectMutationResult Invalid(params ProjectFieldError[] errors) =>
        new(ProjectMutationStatus.InvalidInput, FieldErrors: errors);
}

/// <summary>
/// Project CRUD scoped to the active organization (card F-003, PRD §3.2).
/// <para>
/// <b>Tenant boundary.</b> The organization always comes from <see cref="ITenantContext"/>; no
/// method accepts an organization id from the caller. Project lookups go through the tenant-filtered
/// <see cref="ZenkinsDbContext"/>, so another organization's id is indistinguishable from a
/// nonexistent one and surfaces as <see cref="ProjectMutationStatus.NotFound"/> (404), not 403.
/// </para>
/// <para>
/// <b>Roles (PRD §3.1.2).</b> Members may view, create and edit projects. Deleting is project
/// management and is reserved to Owners. The guard re-reads the membership on the server; hiding a
/// button is never the enforcement.
/// </para>
/// <para>
/// <b>Unique name.</b> The database's unique index on <c>(OrganizationId, Name)</c> is the guard, not
/// a pre-flight query: that also closes the two-request race. A unique violation is translated into
/// the <see cref="ProjectErrorCodes.NameDuplicate"/> field error.
/// </para>
/// <para>
/// <b>Timestamps.</b> <c>CreatedAt</c>/<c>UpdatedAt</c> are stamped by <see cref="ZenkinsDbContext"/>'s
/// single <c>StampTimestamps</c> routine. This service never assigns them, so there is one authority
/// for the audit fields.
/// </para>
/// </summary>
public sealed class ProjectService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    ITenantContext tenantContext,
    ILogger<ProjectService> logger)
{
    /// <summary>
    /// Projects of the active organization, ordered case-insensitively by name (then id) in memory.
    /// The order is a product choice, not a provider limitation: no SQL ordering is needed for a list
    /// this small.
    /// </summary>
    public async Task<IReadOnlyList<ProjectView>> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var projects = await context.Projects.ToListAsync(cancellationToken);
        return projects
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(project => project.Id)
            .Select(ToView)
            .ToArray();
    }

    /// <summary>
    /// One project of the active organization, or <see langword="null"/> when the id belongs to
    /// another organization or does not exist. Both cases are deliberately indistinguishable.
    /// </summary>
    public async Task<ProjectView?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        return project is null ? null : ToView(project);
    }

    /// <summary>Creates a project in the active organization. Members and Owners may both do this.</summary>
    public async Task<ProjectMutationResult> CreateAsync(
        string actingUserId,
        ProjectInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return ProjectMutationResult.Forbidden(
                "You are not a member of an organization, so there is no project to create.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return ProjectMutationResult.Forbidden("You are not a member of the active organization.");
        }

        var errors = new List<ProjectFieldError>();
        var kindError = ProjectValidation.ValidateKind(input.Kind, out var kind);
        if (kindError is not null)
        {
            errors.Add(new ProjectFieldError(ProjectFields.Kind, kindError));
        }

        errors.AddRange(Validate(input, kind, out var name, out var repositoryUrl, out var branch, out var pipelinePath));
        if (errors.Count > 0)
        {
            return ProjectMutationResult.Invalid([.. errors]);
        }

        var project = new Project
        {
            // The tenant comes from the active request; the data layer rejects anything else.
            OrganizationId = activeOrganizationId.Value,
            Name = name,
            Kind = kind,
            RepositoryUrl = repositoryUrl,
            DefaultBranch = branch,
            PipelinePath = pipelinePath,
        };
        context.Projects.Add(project);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "Projects"))
        {
            return DuplicateName();
        }

        logger.LogInformation(
            "Project created. OrganizationId={OrganizationId} ProjectId={ProjectId} ByUserId={UserId}",
            project.OrganizationId,
            project.Id,
            actingUserId);
        return ProjectMutationResult.Success(project.Id);
    }

    /// <summary>
    /// Edits a project of the active organization. Members and Owners may both do this; the id is
    /// resolved through the tenant filter first, so an id swap cannot edit another organization.
    /// </summary>
    public async Task<ProjectMutationResult> UpdateAsync(
        string actingUserId,
        Guid projectId,
        ProjectInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return ProjectMutationResult.Forbidden(
                "You are not a member of an organization, so there is no project to edit.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return ProjectMutationResult.Forbidden("You are not a member of the active organization.");
        }

        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            return ProjectMutationResult.NotFound();
        }

        // The kind is immutable after create (F-020): the edit validates the repository fields against
        // the project's existing kind and never rewrites it, so a kind swap cannot strand the
        // run semantics a queued run already snapshotted.
        var errors = Validate(input, project.Kind, out var name, out var repositoryUrl, out var branch, out var pipelinePath);
        if (errors.Count > 0)
        {
            return ProjectMutationResult.Invalid([.. errors]);
        }

        project.Name = name;
        if (project.Kind == ProjectKinds.Pipeline)
        {
            project.RepositoryUrl = repositoryUrl;
            project.DefaultBranch = branch;
            project.PipelinePath = pipelinePath;
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "Projects"))
        {
            return DuplicateName();
        }

        logger.LogInformation(
            "Project updated. OrganizationId={OrganizationId} ProjectId={ProjectId} ByUserId={UserId}",
            activeOrganizationId,
            project.Id,
            actingUserId);
        return ProjectMutationResult.Success(project.Id);
    }

    /// <summary>
    /// Hard-deletes a project. Owner-only (PRD §3.1.2 reserves project management to Owners). The
    /// project is resolved first, so a foreign id is a 404 for every role; a Member deleting a
    /// project in their own organization gets 403.
    /// <para>
    /// Cascade semantics are deliberately revisited when BuildRuns exist: today a project has no
    /// dependent rows, so a hard delete cannot orphan build history.
    /// </para>
    /// </summary>
    public async Task<ProjectMutationResult> DeleteAsync(
        string actingUserId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return ProjectMutationResult.Forbidden(
                "You are not a member of an organization, so there is no project to delete.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            // Not found in the active tenant: a foreign id never becomes a 403 existence oracle.
            return ProjectMutationResult.NotFound();
        }

        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return ProjectMutationResult.Forbidden("Only an organization Owner can delete projects.");
        }

        context.Projects.Remove(project);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Project deleted. OrganizationId={OrganizationId} ProjectId={ProjectId} ByUserId={UserId}",
            activeOrganizationId,
            project.Id,
            actingUserId);
        return ProjectMutationResult.Success(project.Id);
    }

    /// <summary>
    /// Saves the steps editor's drafts as the Freestyle project's frozen spec (card F-020). Members may
    /// edit, like every other project field. The kind must already be Freestyle (a Pipeline project's
    /// steps come from its repository, so there is nothing here to save), and the drafts become the
    /// stored JSON only after <see cref="FreestyleSteps.BuildSpecJson"/> has built and envelope-checked
    /// the spec - the stored value is exactly what a run will execute.
    /// </summary>
    public async Task<ProjectMutationResult> SaveFreestyleStepsAsync(
        string actingUserId,
        Guid projectId,
        FreestyleStepsInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return ProjectMutationResult.Forbidden(
                "You are not a member of an organization, so there is no project to edit.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return ProjectMutationResult.Forbidden("You are not a member of the active organization.");
        }

        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            return ProjectMutationResult.NotFound();
        }

        if (project.Kind != ProjectKinds.Freestyle)
        {
            return ProjectMutationResult.Invalid(
                new ProjectFieldError(ProjectFields.Steps, ProjectErrorCodes.StepsInvalid));
        }

        var specJson = FreestyleSteps.BuildSpecJson(project.Name, input, out var error);
        if (specJson is null)
        {
            // The specific reason went to the log; the page's redirect carries the stable code.
            logger.LogInformation(
                "Freestyle steps refused. OrganizationId={OrganizationId} ProjectId={ProjectId} Reason={Reason}",
                activeOrganizationId,
                project.Id,
                error);
            return ProjectMutationResult.Invalid(
                new ProjectFieldError(ProjectFields.Steps, ProjectErrorCodes.StepsInvalid));
        }

        project.FreestyleSpecJson = specJson;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Freestyle steps saved. OrganizationId={OrganizationId} ProjectId={ProjectId} ByUserId={UserId}",
            activeOrganizationId,
            project.Id,
            actingUserId);
        return ProjectMutationResult.Success(project.Id);
    }

    /// <summary>
    /// Saves the parameters editor's drafts as the project's parameter definitions (card F-021).
    /// Members may edit, like every other project field; both kinds can declare parameters. The
    /// drafts become the stored JSON only after <see cref="ProjectParameters.BuildDefinitionsJson"/>
    /// has validated them - an empty list clears the definitions.
    /// </summary>
    public async Task<ProjectMutationResult> SaveParametersAsync(
        string actingUserId,
        Guid projectId,
        ProjectParametersInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return ProjectMutationResult.Forbidden(
                "You are not a member of an organization, so there is no project to edit.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return ProjectMutationResult.Forbidden("You are not a member of the active organization.");
        }

        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            return ProjectMutationResult.NotFound();
        }

        var definitionsJson = ProjectParameters.BuildDefinitionsJson(input, out var error);
        if (definitionsJson is null && error is not null)
        {
            logger.LogInformation(
                "Project parameters refused. OrganizationId={OrganizationId} ProjectId={ProjectId} Reason={Reason}",
                activeOrganizationId,
                project.Id,
                error);
            return ProjectMutationResult.Invalid(
                new ProjectFieldError(ProjectFields.Parameters, ProjectErrorCodes.ParametersInvalid));
        }

        project.ParametersJson = definitionsJson;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Project parameters saved. OrganizationId={OrganizationId} ProjectId={ProjectId} ByUserId={UserId}",
            activeOrganizationId,
            project.Id,
            actingUserId);
        return ProjectMutationResult.Success(project.Id);
    }

    private static List<ProjectFieldError> Validate(
        ProjectInput input,
        string kind,
        out string name,
        out string? repositoryUrl,
        out string? branch,
        out string? pipelinePath)
    {
        var errors = new List<ProjectFieldError>();

        var nameError = ProjectValidation.ValidateName(input.Name, out name);
        if (nameError is not null)
        {
            errors.Add(new ProjectFieldError(ProjectFields.Name, nameError));
        }

        if (kind == ProjectKinds.Freestyle)
        {
            // F-020: a Freestyle project has no repository, no default branch and no pipeline path -
            // the three F-003 fields do not exist for it, so their absence is not an error.
            repositoryUrl = null;
            branch = null;
            pipelinePath = null;
            return errors;
        }

        var repositoryUrlError = ProjectValidation.ValidateRepositoryUrl(input.RepositoryUrl, out var normalizedUrl);
        if (repositoryUrlError is not null)
        {
            errors.Add(new ProjectFieldError(ProjectFields.RepositoryUrl, repositoryUrlError));
        }

        var branchError = ProjectValidation.ValidateDefaultBranch(input.DefaultBranch, out var normalizedBranch);
        if (branchError is not null)
        {
            errors.Add(new ProjectFieldError(ProjectFields.DefaultBranch, branchError));
        }

        var pipelinePathError = ProjectValidation.ValidatePipelinePath(input.PipelinePath, out var normalizedPath);
        if (pipelinePathError is not null)
        {
            errors.Add(new ProjectFieldError(ProjectFields.PipelinePath, pipelinePathError));
        }

        repositoryUrl = normalizedUrl;
        branch = normalizedBranch;
        pipelinePath = normalizedPath;
        return errors;
    }

    private static ProjectMutationResult DuplicateName() =>
        ProjectMutationResult.Invalid(new ProjectFieldError(ProjectFields.Name, ProjectErrorCodes.NameDuplicate));

    /// <summary>Any membership (Owner or Member) of the active organization may create and edit.</summary>
    private static Task<bool> IsMemberAsync(
        ZenkinsDbContext context,
        string actingUserId,
        CancellationToken cancellationToken) =>
        context.OrganizationMembers.AnyAsync(
            member => member.UserId == actingUserId,
            cancellationToken);

    /// <summary>Owner check against the active tenant's membership, never another organization's.</summary>
    private static Task<bool> IsOwnerAsync(
        ZenkinsDbContext context,
        string actingUserId,
        CancellationToken cancellationToken) =>
        context.OrganizationMembers.AnyAsync(
            member => member.UserId == actingUserId && member.Role == OrganizationRole.Owner,
            cancellationToken);

    private static ProjectView ToView(Project project) => new(
        project.Id,
        project.Name,
        project.Kind,
        project.RepositoryUrl,
        project.DefaultBranch,
        project.PipelinePath,
        project.GitCredentialId,
        project.CreatedAt,
        project.UpdatedAt,
        project.FreestyleSpecJson,
        project.ParametersJson);
}
