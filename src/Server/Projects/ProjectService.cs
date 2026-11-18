using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Projects;
using Zenkins.Shared.RunCancellation;
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
    string? ParametersJson = null,

    /// <summary>F-028: whether every trigger is refused. History, artifacts and settings stay
    /// readable either way - the flag is the trigger brake, not a soft delete.</summary>
    bool Disabled = false,

    /// <summary>F-028: the operator's reason, shown on every page that says the project is disabled.</summary>
    string? DisabledReason = null,

    /// <summary>F-028: when the current disable happened.</summary>
    DateTimeOffset? DisabledAt = null,

    /// <summary>F-028: who disabled it, resolved to an e-mail; <see langword="null"/> for a row written
    /// before the attribution existed or an account that no longer resolves.</summary>
    string? DisabledByEmail = null,

    /// <summary>F-029: the project's concurrency limit; <see langword="null"/> means unlimited.</summary>
    int? MaxConcurrentRuns = null,

    /// <summary>F-029: seconds a queued run waits before it becomes claimable (0 = immediately).</summary>
    int QuietPeriodSeconds = 0,

    /// <summary>F-029: the project's queue cap; <see langword="null"/> means unlimited.</summary>
    int? MaxQueuedRuns = null,

    /// <summary>
    /// Card I-005: the most recent Git remote probe's outcome (a
    /// <c>Zenkins.Shared.Probes.GitProbeOutcomes</c> value), or <see langword="null"/> when the project
    /// has never been probed. The overview states it, so a probe failure is visible to a person and not
    /// only in a log line - and "never probed" stays distinguishable from "probed, and it failed".
    /// </summary>
    string? LastProbeOutcome = null,

    /// <summary>Card I-005: when that probe settled.</summary>
    DateTimeOffset? LastProbeAt = null,

    /// <summary>Card I-005: the probe's diagnostic words, if it had any.</summary>
    string? LastProbeMessage = null,

    /// <summary>F-022: where a Pipeline project's definition lives (<see cref="ProjectSources"/>).</summary>
    string PipelineSource = ProjectSources.Repository,

    /// <summary>F-022: the inline Pipeline's Lua body; null for a repository project and for Freestyle.</summary>
    string? InlineScript = null,

    /// <summary>
    /// F-023: the branch-discovery mode (<see cref="BranchDiscoveryModes"/>).
    /// <see cref="BranchDiscoveryModes.Single"/> is the default and describes every project created
    /// before the card, so a read path that sees this value never has to guess.
    /// </summary>
    string BranchDiscovery = BranchDiscoveryModes.Single,

    /// <summary>Card F-025: whether this project polls its remote for head movement (opt-in, off by default).</summary>
    bool PollingEnabled = false,

    /// <summary>Card F-025: the declared poll interval in minutes (null while polling is off).</summary>
    int? PollIntervalMinutes = null,

    /// <summary>Card F-025: when the last poll tick considered this project.</summary>
    DateTimeOffset? LastPollAt = null,

    /// <summary>Card F-025: what the last tick decided (a <c>Zenkins.Server.Polling.PollOutcomes</c> value),
    /// so a skipped poll is visible on the settings card instead of silent.</summary>
    string? LastPollOutcome = null,

    /// <summary>Card F-025: the poll's diagnostic, when it had one (never a credential value).</summary>
    string? LastPollMessage = null,

    /// <summary>Card F-025: consecutive failed probes - the backoff state the card shows (1x, 2x, 4x ...).</summary>
    int PollFailureStreak = 0,

    /// <summary>
    /// Card F-031: whether a failed post step fails the run (the settings tab's switch). False is the default
    /// and the historical behaviour of every project; the claim hands this value to the worker that executes
    /// the post phase.
    /// </summary>
    bool PostFailureFailsRun = false);

/// <summary>The raw form values, before trimming and validation. <see cref="Kind"/> is honored on
/// create only - a project's kind is immutable after that (F-020), so an edit cannot turn a Pipeline
/// project into a Freestyle one (or back) and strand its runs' semantics.</summary>
public sealed record ProjectInput(
    string? Name,
    string? Kind,
    string? RepositoryUrl,
    string? DefaultBranch,
    string? PipelinePath,

    /// <summary>
    /// F-022: the definition source (only honoured on create, like <see cref="Kind"/>). Blank means
    /// <see cref="ProjectSources.Repository"/>; an inline Pipeline needs <see cref="InlineScript"/>.
    /// </summary>
    string? PipelineSource = null,

    /// <summary>F-022: the inline Pipeline's Lua body, if the source is inline.</summary>
    string? InlineScript = null,

    /// <summary>
    /// F-023: the branch-discovery mode. Blank means <see cref="BranchDiscoveryModes.Single"/> - the
    /// historical default - and an edit honours it (the setting is mutable; switching to discovery never
    /// rewrites history). A Freestyle or inline project can only be <c>single</c>.
    /// </summary>
    string? BranchDiscovery = null);

/// <summary>
/// The queue-controls form's raw values (card F-029), before trimming and validation. Blank means "the
/// default": unlimited for the two caps and zero for the quiet period.
/// </summary>
public sealed record ProjectLimitsInput(
    string? MaxConcurrentRuns,
    string? QuietPeriodSeconds,
    string? MaxQueuedRuns);

/// <summary>The validated queue controls (card F-029) a project row stores.</summary>
public sealed record ProjectLimits(int? MaxConcurrentRuns, int QuietPeriodSeconds, int? MaxQueuedRuns);

/// <summary>The polling card's raw form values (card F-025). The interval travels as text, parsed and
/// bounded server-side exactly like the limits form's numbers.</summary>
public sealed record ProjectPollingInput(string? PollingEnabled, string? PollIntervalMinutes);

/// <summary>The validated polling settings a project row stores (card F-025).</summary>
public sealed record ProjectPolling(bool Enabled, int? IntervalMinutes);

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
            .Select(project => ToView(project))
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
        if (project is null)
        {
            return null;
        }

        // F-028: the attribution resolves to an e-mail for display, in one extra read that only a
        // disabled project pays for. Like the assignment attribution (F-018), the id is a plain string
        // without a foreign key, so "who disabled this" survives an account deletion as an id the
        // lookup no longer resolves - the page then says "not recorded" instead of guessing.
        string? disabledByEmail = null;
        if (project.DisabledByUserId is { Length: > 0 } disabledByUserId)
        {
            disabledByEmail = await context.Users
                .Where(user => user.Id == disabledByUserId)
                .Select(user => user.Email)
                .SingleOrDefaultAsync(cancellationToken);
        }

        return ToView(project, disabledByEmail);
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

        // F-022: the source is a create-time decision. A Freestyle project has no definition source -
        // its steps are frozen in FreestyleSpecJson - so the column keeps the historical default and
        // the inline body is ignored for it, exactly as the repository fields are.
        var sourceError = ProjectValidation.ValidatePipelineSource(input.PipelineSource, out var source);
        if (sourceError is not null)
        {
            errors.Add(new ProjectFieldError(ProjectFields.PipelineSource, sourceError));
        }

        source = kind == ProjectKinds.Pipeline ? source : ProjectSources.Repository;

        // F-023: the discovery mode. Blank is today's one-branch behaviour; a mode the project's shape
        // cannot honour (Freestyle or inline have no repository) is refused rather than stored.
        var discoveryError = ProjectValidation.ValidateBranchDiscovery(input.BranchDiscovery, out var branchDiscovery);
        if (discoveryError is not null)
        {
            errors.Add(new ProjectFieldError(ProjectFields.BranchDiscovery, discoveryError));
        }
        else if (branchDiscovery != BranchDiscoveryModes.Single
                 && !ProjectValidation.CanDiscoverBranches(kind, source))
        {
            errors.Add(new ProjectFieldError(
                ProjectFields.BranchDiscovery,
                ProjectErrorCodes.BranchDiscoveryUnavailable));
        }

        errors.AddRange(Validate(
            input,
            kind,
            source,
            out var name,
            out var repositoryUrl,
            out var branch,
            out var pipelinePath,
            out var inlineScript));
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
            PipelineSource = source,
            InlineScript = inlineScript,
            RepositoryUrl = repositoryUrl,
            DefaultBranch = branch,
            PipelinePath = pipelinePath,
            BranchDiscovery = branchDiscovery,
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
        // run semantics a queued run already snapshotted. F-022 gives the source the same rule: a posted
        // source is ignored, so a Pipeline project cannot turn inline (or back) and strand its runs.
        //
        // F-022: the kind-locked edit form renders no script field (the Pipelines tab owns the editor),
        // so an absent InlineScript means "keep the stored one" rather than "clear it". An explicitly
        // posted empty value is still refused by the validator.
        var effectiveInput = project.PipelineSource == ProjectSources.Inline && input.InlineScript is null
            ? input with { InlineScript = project.InlineScript }
            : input;

        // F-023: the discovery mode is mutable (switching is the card's point), so an edit validates and
        // stores it. The project's kind and source are immutable, so the shape check uses the stored ones.
        var discoveryError = ProjectValidation.ValidateBranchDiscovery(
            effectiveInput.BranchDiscovery,
            out var branchDiscovery);
        if (discoveryError is not null)
        {
            return ProjectMutationResult.Invalid(
                new ProjectFieldError(ProjectFields.BranchDiscovery, discoveryError));
        }

        if (branchDiscovery != BranchDiscoveryModes.Single
            && !ProjectValidation.CanDiscoverBranches(project.Kind, project.PipelineSource))
        {
            return ProjectMutationResult.Invalid(
                new ProjectFieldError(ProjectFields.BranchDiscovery, ProjectErrorCodes.BranchDiscoveryUnavailable));
        }

        var errors = Validate(
            effectiveInput,
            project.Kind,
            project.PipelineSource,
            out var name,
            out var repositoryUrl,
            out var branch,
            out var pipelinePath,
            out var inlineScript);
        if (errors.Count > 0)
        {
            return ProjectMutationResult.Invalid([.. errors]);
        }

        project.Name = name;
        project.BranchDiscovery = branchDiscovery;
        if (project.Kind == ProjectKinds.Pipeline)
        {
            // A definition source decides which half of the form is the project's own state: an inline
            // project stores the script and keeps every repository field null (the honest empty value
            // AC-1 asks for); a repository project stores the three fields and no script.
            if (project.PipelineSource == ProjectSources.Inline)
            {
                project.InlineScript = inlineScript;
            }
            else
            {
                project.RepositoryUrl = repositoryUrl;
                project.DefaultBranch = branch;
                project.PipelinePath = pipelinePath;
            }
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

    /// <summary>
    /// Disables or re-enables a project (card F-028). Members and Owners may both do it, like every
    /// other project mutation - disabling is a brake, not a destructive action, and nothing is deleted.
    /// <para>
    /// <b>Disabling cancels the queue, not the work in flight.</b> Every still-<c>Queued</c> run of the
    /// project becomes <c>Canceled</c> with reason <c>project-disabled</c> in the same transaction as the
    /// flag: a disabled job must never build something it queued while it was enabled. A <c>Running</c>
    /// run is deliberately untouched - stopping work in flight is a second, separate decision (and
    /// already has its own cancel).
    /// </para>
    /// <para>
    /// <b>The flag and the cancels are one unit of work</b>, and the flag write is the project row's own
    /// lock, which is what serializes this with a concurrent trigger (see
    /// <c>ZenkinsDbContext.LockProjectForTriggerAsync</c>): a trigger either commits its run before the
    /// disable (the cancel below then catches it) or reads the flag after it and refuses.
    /// </para>
    /// <para>
    /// <b>Enabling replays nothing.</b> It clears the flag and the attribution only: canceled runs stay
    /// canceled, no run is un-canceled, and no trigger that was refused is replayed - the next trigger
    /// queues a run like any other.
    /// </para>
    /// </summary>
    public async Task<ProjectMutationResult> SetDisabledAsync(
        string actingUserId,
        Guid projectId,
        bool disabled,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return ProjectMutationResult.Forbidden(
                disabled
                    ? "You are not a member of an organization, so there is no project to disable."
                    : "You are not a member of an organization, so there is no project to enable.");
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
            // Foreign or unknown id: the same 404, no existence oracle.
            return ProjectMutationResult.NotFound();
        }

        var normalizedReason = string.Empty;
        if (disabled)
        {
            var reasonError = ProjectValidation.ValidateDisabledReason(reason, out normalizedReason);
            if (reasonError is not null)
            {
                return ProjectMutationResult.Invalid(
                    new ProjectFieldError(ProjectFields.DisabledReason, reasonError));
            }
        }

        var now = DateTimeOffset.UtcNow;
        var canceledRuns = 0;

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        project.Disabled = disabled;
        project.DisabledReason = disabled ? normalizedReason : null;
        project.DisabledAt = disabled ? now : null;
        project.DisabledByUserId = disabled ? actingUserId : null;
        await context.SaveChangesAsync(cancellationToken);

        if (disabled)
        {
            canceledRuns = await context.TryCancelQueuedProjectRunsAsync(
                project.Id,
                RunCancelReasons.ProjectDisabled,
                now,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Project {State}. OrganizationId={OrganizationId} ProjectId={ProjectId} ByUserId={UserId} CanceledRuns={CanceledRuns}",
            disabled ? "disabled" : "enabled",
            activeOrganizationId,
            project.Id,
            actingUserId,
            canceledRuns);
        return ProjectMutationResult.Success(project.Id);
    }

    /// <summary>
    /// Card F-031: the project's explicit "a failed post step fails the run" switch. Members and Owners may
    /// change it (like every other project setting); it is read at claim time and handed to the worker, which
    /// is the side that executes the post phase and reports the run's outcome. Nothing is replayed: runs
    /// already queued carry the answer they were claimed with.
    /// </summary>
    public async Task<ProjectMutationResult> SetPostFailureFailsRunAsync(
        string actingUserId,
        Guid projectId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

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

        project.PostFailureFailsRun = enabled;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Project post-failure switch changed. OrganizationId={OrganizationId} ProjectId={ProjectId} ByUserId={UserId} PostFailureFailsRun={Enabled}",
            activeOrganizationId,
            project.Id,
            actingUserId,
            enabled);
        return ProjectMutationResult.Success(project.Id);
    }

    /// <summary>
    /// Saves the queue controls (card F-029): the concurrency limit, the quiet period and the queue cap.
    /// Members may edit them, like every other project field - they bound the project's own queue, they
    /// do not decide who may do what. Blank means the default (unlimited / zero), which is exactly how
    /// every project behaved before this card, so a project that never opens this form keeps today's
    /// behaviour.
    /// </summary>
    /// <summary>
    /// Card F-025: saves the polling switch and interval. Turning polling off clears the interval (a project
    /// that is not polled has no interval to mean) and leaves the recorded poll state alone - it is history,
    /// and the card still shows the last poll and its outcome.
    /// </summary>
    public async Task<ProjectMutationResult> SavePollingAsync(
        string actingUserId,
        Guid projectId,
        ProjectPollingInput input,
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

        var error = ProjectValidation.ValidatePolling(input, out var polling);
        if (error is not null)
        {
            return ProjectMutationResult.Invalid(
                new ProjectFieldError(ProjectErrorCodes.FieldFor(error) ?? ProjectFields.MaxConcurrentRuns, error));
        }

        // AC-7: a project with no repository cannot be polled, and saying so here is the point - the form
        // refuses to switch it on rather than turning on a poll whose only possible answer is a failure.
        if (polling.Enabled && !ProjectSources.HasRepository(project.Kind, project.PipelineSource))
        {
            return ProjectMutationResult.Invalid(new ProjectFieldError(
                ProjectFields.MaxConcurrentRuns,
                ProjectErrorCodes.PollingNotPipeline));
        }

        project.PollingEnabled = polling.Enabled;
        project.PollIntervalMinutes = polling.IntervalMinutes;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Project polling saved. OrganizationId={OrganizationId} ProjectId={ProjectId} ByUserId={UserId} PollingEnabled={PollingEnabled} PollIntervalMinutes={IntervalMinutes}",
            activeOrganizationId,
            project.Id,
            actingUserId,
            project.PollingEnabled,
            project.PollIntervalMinutes);

        return ProjectMutationResult.Success(project.Id);
    }

    public async Task<ProjectMutationResult> SaveLimitsAsync(
        string actingUserId,
        Guid projectId,
        ProjectLimitsInput input,
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

        var limitsError = ProjectValidation.ValidateLimits(input, out var limits);
        if (limitsError is not null)
        {
            return ProjectMutationResult.Invalid(
                new ProjectFieldError(ProjectErrorCodes.FieldFor(limitsError) ?? ProjectFields.MaxConcurrentRuns, limitsError));
        }

        project.MaxConcurrentRuns = limits.MaxConcurrentRuns;
        project.QuietPeriodSeconds = limits.QuietPeriodSeconds;
        project.MaxQueuedRuns = limits.MaxQueuedRuns;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Project limits saved. OrganizationId={OrganizationId} ProjectId={ProjectId} ByUserId={UserId} MaxConcurrentRuns={MaxConcurrentRuns} QuietPeriodSeconds={QuietPeriodSeconds} MaxQueuedRuns={MaxQueuedRuns}",
            activeOrganizationId,
            project.Id,
            actingUserId,
            limits.MaxConcurrentRuns,
            limits.QuietPeriodSeconds,
            limits.MaxQueuedRuns);
        return ProjectMutationResult.Success(project.Id);
    }

    /// <summary>
    /// Saves the inline script editor's body (card F-022). Members may edit, like every other project
    /// field. The project must be a Pipeline with the inline source: a repository project's definition
    /// is its repository's file, and a Freestyle project's is its frozen steps, so neither has a script
    /// to save. The text is stored verbatim after the same bound the trigger and the Worker apply.
    /// </summary>
    public async Task<ProjectMutationResult> SaveInlineScriptAsync(
        string actingUserId,
        Guid projectId,
        string? script,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

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

        if (project.Kind != ProjectKinds.Pipeline || project.PipelineSource != ProjectSources.Inline)
        {
            return ProjectMutationResult.Invalid(
                new ProjectFieldError(ProjectFields.InlineScript, ProjectErrorCodes.PipelineSourceInvalid));
        }

        var scriptError = ProjectValidation.ValidateInlineScript(script, out var normalized);
        if (scriptError is not null)
        {
            return ProjectMutationResult.Invalid(
                new ProjectFieldError(ProjectFields.InlineScript, scriptError));
        }

        project.InlineScript = normalized;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Inline pipeline script saved. OrganizationId={OrganizationId} ProjectId={ProjectId} ByUserId={UserId} ScriptBytes={ScriptBytes}",
            activeOrganizationId,
            project.Id,
            actingUserId,
            System.Text.Encoding.UTF8.GetByteCount(normalized));
        return ProjectMutationResult.Success(project.Id);
    }

    private static List<ProjectFieldError> Validate(
        ProjectInput input,
        string kind,
        string source,
        out string name,
        out string? repositoryUrl,
        out string? branch,
        out string? pipelinePath,
        out string? inlineScript)
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
            inlineScript = null;
            return errors;
        }

        if (source == ProjectSources.Inline)
        {
            // F-022: an inline project has no repository fields and one Lua body. The empty values are
            // stored as null on the row (the honest empty value), and the pipeline path stays null -
            // the run's own snapshot names pipeline.lua, because that is the file the script becomes.
            repositoryUrl = null;
            branch = null;
            pipelinePath = null;
            var scriptError = ProjectValidation.ValidateInlineScript(input.InlineScript, out var normalizedScript);
            if (scriptError is not null)
            {
                errors.Add(new ProjectFieldError(ProjectFields.InlineScript, scriptError));
            }

            inlineScript = normalizedScript;
            return errors;
        }

        inlineScript = null;
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

    private static ProjectView ToView(Project project, string? disabledByEmail = null) => new(
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
        project.ParametersJson,
        project.Disabled,
        project.DisabledReason,
        project.DisabledAt,
        disabledByEmail,
        project.MaxConcurrentRuns,
        project.QuietPeriodSeconds,
        project.MaxQueuedRuns,
        project.LastProbeOutcome,
        project.LastProbeAt,
        project.LastProbeMessage,
        project.PipelineSource,
        project.InlineScript,
        BranchDiscoveryModes.Normalize(project.BranchDiscovery),
        project.PollingEnabled,
        project.PollIntervalMinutes,
        project.LastPollAt,
        project.LastPollOutcome,
        project.LastPollMessage,
        project.PollFailureStreak,
        project.PostFailureFailsRun);
}
