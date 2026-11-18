using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Projects;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Folders;

/// <summary>One folder as a page renders it: its own row plus the counts that roll up to its parent.</summary>
public sealed record FolderView(
    Guid Id,
    string Name,
    string? Description,
    Guid? ParentFolderId,
    int Depth,

    /// <summary>Projects sitting directly in this folder.</summary>
    int ProjectCount,

    /// <summary>Folders sitting directly in this folder.</summary>
    int ChildFolderCount,

    /// <summary>Projects in this folder <em>and</em> in its whole subtree - the number that rolls up.</summary>
    int SubtreeProjectCount,

    /// <summary>Folders in this folder's whole subtree (its children and theirs).</summary>
    int SubtreeFolderCount);

/// <summary>One step of the breadcrumb. <see cref="Id"/> is <see langword="null"/> for the root step.</summary>
public sealed record FolderBreadcrumb(Guid? Id, string Name);

/// <summary>A project row in a listing, with the status the folder page shows.</summary>
public sealed record FolderProjectRow(
    Guid Id,
    string Name,
    string Kind,
    string PipelineSource,
    string? RepositoryUrl,
    string? DefaultBranch,
    string? PipelinePath,
    bool Disabled,
    Guid? FolderId,

    /// <summary>The newest run's status (a <see cref="Zenkins.Shared.Runs.BuildRunStatus"/> word), or
    /// <see langword="null"/> when the project never ran.</summary>
    string? LastRunStatus,

    /// <summary>The newest run's build number, for the permalink.</summary>
    int? LastRunNumber,

    /// <summary>When the newest run was created.</summary>
    DateTimeOffset? LastRunAt);

/// <summary>
/// The projects page's or a folder page's content (card F-042). One shape for both: the root listing is
/// the same listing with <see cref="FolderId"/> = <see langword="null"/>, so the two pages cannot drift.
/// </summary>
public sealed record ProjectListing(
    Guid? FolderId,
    FolderView? CurrentFolder,
    IReadOnlyList<FolderBreadcrumb> Breadcrumbs,
    IReadOnlyList<FolderView> Folders,
    IReadOnlyList<FolderProjectRow> Projects,

    /// <summary>Projects in the current folder's subtree (the whole organization at the root).</summary>
    int SubtreeProjectCount,

    /// <summary>Folders in the current folder's subtree (the whole organization at the root).</summary>
    int SubtreeFolderCount,

    /// <summary>The search text this listing was filtered by, if any.</summary>
    string? Query = null,

    /// <summary>Whether the search spanned the whole organization instead of the current subtree.</summary>
    bool Everywhere = false);

/// <summary>One entry of a parent selector: a folder, its depth and its full display path.</summary>
public sealed record FolderOption(Guid Id, string Name, string Path, int Depth);

/// <summary>The raw folder form values, before trimming and validation.</summary>
public sealed record FolderInput(string? Name, string? Description = null, string? ParentFolderId = null);

/// <summary>Outcome kinds of a folder read or mutation.</summary>
public enum FolderMutationStatus
{
    Success = 0,

    /// <summary>The folder does not exist in the active organization (also the foreign-tenant case).</summary>
    NotFound = 1,

    /// <summary>The caller is not a member of the active organization.</summary>
    Forbidden = 2,

    /// <summary>One or more fields failed validation; nothing was written.</summary>
    InvalidInput = 3,
}

/// <summary>A folder mutation's outcome; <see cref="FieldErrors"/> mirrors the project service's shape.</summary>
public sealed record FolderMutationResult(
    FolderMutationStatus Status,
    Guid? FolderId = null,
    string? Message = null,
    IReadOnlyList<ProjectFieldError>? FieldErrors = null)
{
    public static FolderMutationResult Success(Guid folderId) => new(FolderMutationStatus.Success, folderId);

    public static FolderMutationResult NotFound() => new(FolderMutationStatus.NotFound);

    public static FolderMutationResult Forbidden(string message) =>
        new(FolderMutationStatus.Forbidden, Message: message);

    public static FolderMutationResult Invalid(string field, string code) =>
        new(FolderMutationStatus.InvalidInput, FieldErrors: [new ProjectFieldError(field, code)]);

    public static FolderMutationResult Invalid(IReadOnlyList<ProjectFieldError> errors) =>
        new(FolderMutationStatus.InvalidInput, FieldErrors: errors);

    public static FolderMutationResult Invalid(ProjectFieldError error) =>
        new(FolderMutationStatus.InvalidInput, FieldErrors: [error]);
}

/// <summary>
/// The organization's folder tree (card F-042, PRD §3.2) and everything the two listing pages need.
/// <para>
/// <b>Tenant boundary.</b> The organization always comes from <see cref="ITenantContext"/>; no method
/// accepts one from the caller. Reads and writes go through the tenant-filtered
/// <see cref="ZenkinsDbContext"/>, so another organization's folder id is indistinguishable from a
/// nonexistent one and answers <see cref="FolderMutationStatus.NotFound"/> (404), never 403.
/// </para>
/// <para>
/// <b>Uniqueness is the database's.</b> The sibling-unique index on
/// <c>(OrganizationId, ParentKey, NameKey)</c> is the guard, not a pre-flight query: two concurrent
/// creates cannot both pass a "does it exist" check. A violation is translated into the
/// <see cref="FolderErrorCodes.NameDuplicate"/> field error.
/// </para>
/// <para>
/// <b>Depth and cycles.</b> Both are service rules because both are about the tree, not about a row: the
/// depth cap is also restated by the entity's check constraint, and a move is refused when the new parent
/// is the folder itself or one of its descendants (the walk is bounded by the depth cap, with a visited
/// set so a pre-existing malformed cycle cannot hang it).
/// </para>
/// <para>
/// <b>Counts roll up.</b> A folder's subtree counts are computed from the organization's two loaded
/// tables (folders are small by construction: five levels, name-unique siblings), so the page never runs
/// a query per folder.
/// </para>
/// </summary>
public sealed class FolderService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    ITenantContext tenantContext,
    ILogger<FolderService> logger)
{
    /// <summary>
    /// The listing of one folder - or of the organization's root when <paramref name="folderId"/> is
    /// <see langword="null"/>. Returns <see langword="null"/> for a foreign or unknown folder id, which
    /// the pages render as a bare 404.
    /// <para>
    /// <paramref name="query"/> filters both folders and projects by a case-insensitive substring. The
    /// scope is the current folder's subtree, or the whole organization when
    /// <paramref name="everywhere"/> is set (AC-6).
    /// </para>
    /// </summary>
    public async Task<ProjectListing?> GetListingAsync(
        Guid? folderId = null,
        string? query = null,
        bool everywhere = false,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var folders = await context.Folders.ToListAsync(cancellationToken);
        var folderById = folders.ToDictionary(folder => folder.Id);

        Folder? current = null;
        if (folderId is { } requestedId)
        {
            if (!folderById.TryGetValue(requestedId, out var found))
            {
                return null;
            }

            current = found;
        }

        var subtree = SubtreeIds(current?.Id, folders);

        // A search's folder scope follows the project scope: the current folder's subtree, the root's own
        // children at the root, and everything when "everywhere" is set.
        var folderScope = everywhere
            ? folders.Select(folder => folder.Id).ToHashSet()
            : current is null
                ? folders.Where(folder => folder.ParentFolderId is null)
                    .Select(folder => folder.Id)
                    .ToHashSet()
                : subtree;

        var projects = await context.Projects.ToListAsync(cancellationToken);

        // Scope: everywhere = the whole organization; a folder page = its subtree; the root = root projects.
        var scopedProjects = everywhere
            ? projects
            : current is null
                ? projects.Where(project => project.FolderId is null).ToList()
                : projects.Where(project => project.FolderId is { } id && subtree.Contains(id)).ToList();

        var counts = BuildCounts(folders, projects);
        var lastRuns = await ReadLastRunsAsync(context, scopedProjects, cancellationToken);

        var search = query?.Trim();
        var isSearching = !string.IsNullOrEmpty(search);

        var listedProjects = (isSearching
                ? scopedProjects.Where(project =>
                    project.Name.Contains(search!, StringComparison.OrdinalIgnoreCase))
                : scopedProjects)
            .Select(project => ToRow(project, lastRuns))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Id)
            .ToArray();

        // Direct children of the current folder are the page's "subfolders" list. While searching, the
        // list becomes the matching folders from the same scope, so "find that folder" works too.
        var listedFolders = (isSearching
                ? folders.Where(folder => folderScope.Contains(folder.Id)
                    && folder.Name.Contains(search!, StringComparison.OrdinalIgnoreCase))
                : folders.Where(folder => folder.ParentFolderId == current?.Id))
            .Select(folder => View(folder, counts))
            .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(folder => folder.Id)
            .ToArray();

        return new ProjectListing(
            current?.Id,
            current is null ? null : View(current, counts),
            Breadcrumbs(current, folderById),
            listedFolders,
            listedProjects,
            current is null ? projects.Count : counts.SubtreeProjects[current.Id],
            current is null ? folders.Count : counts.SubtreeFolders[current.Id],
            search,
            everywhere);
    }

    /// <summary>
    /// Every folder of the organization as a parent-selector entry, ordered by its display path so a
    /// nested list reads top-down. The path is built from the tree, not stored: a rename or a move never
    /// leaves a stale path anywhere. <paramref name="excludedFolderId"/> drops a folder and its subtree -
    /// a move form never offers the folder itself or its own descendants as a target.
    /// </summary>
    public async Task<IReadOnlyList<FolderOption>> GetOptionsAsync(
        Guid? excludedFolderId = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var folders = await context.Folders.ToListAsync(cancellationToken);
        return Options(folders, excludedFolderId);
    }

    /// <summary>Creates a folder. Members and Owners may both do it, like creating a project.</summary>
    public async Task<FolderMutationResult> CreateAsync(
        string actingUserId,
        FolderInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return FolderMutationResult.Forbidden(
                "You are not a member of an organization, so there is no folder to create.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return FolderMutationResult.Forbidden("You are not a member of the active organization.");
        }

        var errors = new List<ProjectFieldError>();
        var nameError = FolderValidation.ValidateName(input.Name, out var name);
        if (nameError is not null)
        {
            errors.Add(new ProjectFieldError(FolderFields.Name, nameError));
        }

        var descriptionError = FolderValidation.ValidateDescription(input.Description, out var description);
        if (descriptionError is not null)
        {
            errors.Add(new ProjectFieldError(FolderFields.Description, descriptionError));
        }

        var folders = await context.Folders.ToListAsync(cancellationToken);
        var parentError = ResolveParent(input.ParentFolderId, folders, out var parent);
        if (parentError is not null)
        {
            errors.Add(new ProjectFieldError(FolderFields.Parent, parentError));
        }

        if (errors.Count > 0)
        {
            return FolderMutationResult.Invalid(errors);
        }

        var depth = (parent?.Depth ?? 0) + 1;
        if (depth > FolderValidation.MaxDepth)
        {
            return FolderMutationResult.Invalid(
                new ProjectFieldError(FolderFields.Parent, FolderErrorCodes.DepthExceeded));
        }

        var folder = new Folder
        {
            OrganizationId = activeOrganizationId.Value,
            Name = name,
            Description = description,
            ParentFolderId = parent?.Id,
            Depth = depth,
        };
        context.Folders.Add(folder);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "Folders"))
        {
            return DuplicateName();
        }

        logger.LogInformation(
            "Folder created. OrganizationId={OrganizationId} FolderId={FolderId} Depth={Depth} ByUserId={UserId}",
            folder.OrganizationId,
            folder.Id,
            folder.Depth,
            actingUserId);
        return FolderMutationResult.Success(folder.Id);
    }

    /// <summary>
    /// Renames a folder and/or replaces its description. Members and Owners may both do it. A rename
    /// touches exactly one row: children, projects and their keys stay where they are, because the key
    /// encodes the parent, never the path.
    /// </summary>
    public async Task<FolderMutationResult> UpdateAsync(
        string actingUserId,
        Guid folderId,
        FolderInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return FolderMutationResult.Forbidden(
                "You are not a member of an organization, so there is no folder to edit.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return FolderMutationResult.Forbidden("You are not a member of the active organization.");
        }

        var folder = await context.Folders.SingleOrDefaultAsync(
            candidate => candidate.Id == folderId,
            cancellationToken);
        if (folder is null)
        {
            return FolderMutationResult.NotFound();
        }

        var errors = new List<ProjectFieldError>();
        var nameError = FolderValidation.ValidateName(input.Name, out var name);
        if (nameError is not null)
        {
            errors.Add(new ProjectFieldError(FolderFields.Name, nameError));
        }

        var descriptionError = FolderValidation.ValidateDescription(input.Description, out var description);
        if (descriptionError is not null)
        {
            errors.Add(new ProjectFieldError(FolderFields.Description, descriptionError));
        }

        if (errors.Count > 0)
        {
            return FolderMutationResult.Invalid(errors);
        }

        folder.Name = name;
        folder.Description = description;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "Folders"))
        {
            return DuplicateName();
        }

        logger.LogInformation(
            "Folder updated. OrganizationId={OrganizationId} FolderId={FolderId} ByUserId={UserId}",
            activeOrganizationId,
            folder.Id,
            actingUserId);
        return FolderMutationResult.Success(folder.Id);
    }

    /// <summary>
    /// Moves a folder (and its whole subtree) under another parent, or to the root when
    /// <paramref name="parentFolderId"/> is blank.
    /// <para>
    /// Three refusals, each with its own code: the target must exist in this organization; it cannot be
    /// the folder itself or a descendant (a cycle); and the deepest descendant must still fit under
    /// <see cref="FolderValidation.MaxDepth"/>. The subtree's stored depths are rewritten in the same
    /// save, so the depth column never disagrees with the tree.
    /// </para>
    /// </summary>
    public async Task<FolderMutationResult> MoveAsync(
        string actingUserId,
        Guid folderId,
        string? parentFolderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return FolderMutationResult.Forbidden(
                "You are not a member of an organization, so there is no folder to move.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return FolderMutationResult.Forbidden("You are not a member of the active organization.");
        }

        var folders = await context.Folders.ToListAsync(cancellationToken);
        var folder = folders.FirstOrDefault(candidate => candidate.Id == folderId);
        if (folder is null)
        {
            return FolderMutationResult.NotFound();
        }

        var parentError = ResolveParent(parentFolderId, folders, out var parent);
        if (parentError is not null)
        {
            return FolderMutationResult.Invalid(new ProjectFieldError(FolderFields.Parent, parentError));
        }

        if (parent is not null && IsDescendant(parent.Id, folder.Id, folders))
        {
            // IsDescendant includes the folder itself, so "move into itself" and "move into a child" are
            // one refusal with one code.
            return FolderMutationResult.Invalid(
                new ProjectFieldError(FolderFields.Parent, FolderErrorCodes.Cycle));
        }

        var newDepth = (parent?.Depth ?? 0) + 1;
        var subtreeHeight = SubtreeHeight(folder.Id, folders);
        if (newDepth + subtreeHeight - 1 > FolderValidation.MaxDepth)
        {
            return FolderMutationResult.Invalid(
                new ProjectFieldError(FolderFields.Parent, FolderErrorCodes.DepthExceeded));
        }

        var shift = newDepth - folder.Depth;
        var descendants = folders
            .Where(candidate => candidate.Id != folder.Id && IsDescendant(candidate.Id, folder.Id, folders))
            .ToArray();

        folder.ParentFolderId = parent?.Id;
        folder.Depth = newDepth;

        // The subtree moves with it: every descendant's stored depth shifts by the same amount.
        foreach (var descendant in descendants)
        {
            descendant.Depth += shift;
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "Folders"))
        {
            return DuplicateName();
        }

        logger.LogInformation(
            "Folder moved. OrganizationId={OrganizationId} FolderId={FolderId} ParentFolderId={ParentFolderId} Depth={Depth} ByUserId={UserId}",
            activeOrganizationId,
            folder.Id,
            folder.ParentFolderId,
            folder.Depth,
            actingUserId);
        return FolderMutationResult.Success(folder.Id);
    }

    /// <summary>
    /// Deletes an <b>empty</b> folder. A folder that still holds projects or subfolders is refused
    /// (AC-5: delete or move the contents first) - there is no cascade from a folder to anything that
    /// carries history, and deleting an empty folder deletes no other row by construction.
    /// </summary>
    public async Task<FolderMutationResult> DeleteAsync(
        string actingUserId,
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return FolderMutationResult.Forbidden(
                "You are not a member of an organization, so there is no folder to delete.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return FolderMutationResult.Forbidden("You are not a member of the active organization.");
        }

        var folder = await context.Folders.SingleOrDefaultAsync(
            candidate => candidate.Id == folderId,
            cancellationToken);
        if (folder is null)
        {
            return FolderMutationResult.NotFound();
        }

        var projectCount = await context.Projects.CountAsync(
            project => project.FolderId == folder.Id,
            cancellationToken);
        var childFolderCount = await context.Folders.CountAsync(
            candidate => candidate.ParentFolderId == folder.Id,
            cancellationToken);
        if (projectCount > 0 || childFolderCount > 0)
        {
            return FolderMutationResult.Invalid(
                new ProjectFieldError(FolderFields.Parent, FolderErrorCodes.NotEmpty));
        }

        context.Folders.Remove(folder);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Folder deleted. OrganizationId={OrganizationId} FolderId={FolderId} ByUserId={UserId}",
            activeOrganizationId,
            folder.Id,
            actingUserId);
        return FolderMutationResult.Success(folder.Id);
    }

    // ---- tree helpers ---------------------------------------------------------------------

    /// <summary>
    /// The folder itself plus every folder below it. Empty when <paramref name="rootId"/> is
    /// <see langword="null"/>: nothing sits "under" the root listing, and the caller decides what the
    /// root scope means (its own projects and children).
    /// </summary>
    private static HashSet<Guid> SubtreeIds(Guid? rootId, IReadOnlyList<Folder> folders)
    {
        var result = new HashSet<Guid>();
        if (rootId is null)
        {
            return result;
        }

        var childrenByParent = folders
            .Where(folder => folder.ParentFolderId is not null)
            .ToLookup(folder => folder.ParentFolderId!.Value);

        var pending = new Queue<Guid>();
        pending.Enqueue(rootId.Value);
        while (pending.TryDequeue(out var id))
        {
            if (!result.Add(id))
            {
                continue;
            }

            foreach (var child in childrenByParent[id])
            {
                pending.Enqueue(child.Id);
            }
        }

        return result;
    }

    /// <summary>
    /// Whether <paramref name="candidateId"/> is <paramref name="ancestorId"/> or sits below it. The walk
    /// goes upward with a visited set, so a malformed row written out of band cannot make it loop.
    /// </summary>
    private static bool IsDescendant(Guid candidateId, Guid ancestorId, IReadOnlyList<Folder> folders)
    {
        var byId = folders.ToDictionary(folder => folder.Id);
        var visited = new HashSet<Guid>();
        var current = candidateId;
        while (visited.Add(current) && byId.TryGetValue(current, out var folder))
        {
            if (folder.Id == ancestorId)
            {
                return true;
            }

            if (folder.ParentFolderId is not { } parentId)
            {
                return false;
            }

            current = parentId;
        }

        return false;
    }

    /// <summary>How many folder levels the subtree rooted at <paramref name="folderId"/> has (1 = leaf).</summary>
    private static int SubtreeHeight(Guid folderId, IReadOnlyList<Folder> folders)
    {
        var children = folders.Where(folder => folder.ParentFolderId == folderId).ToArray();
        return children.Length == 0
            ? 1
            : 1 + children.Max(child => SubtreeHeight(child.Id, folders));
    }

    /// <summary>Blank means the root; anything else must parse and resolve inside this organization.</summary>
    private static string? ResolveParent(string? parentFolderId, IReadOnlyList<Folder> folders, out Folder? parent)
    {
        parent = null;
        if (string.IsNullOrWhiteSpace(parentFolderId))
        {
            return null;
        }

        if (!Guid.TryParse(parentFolderId.Trim(), out var id))
        {
            return FolderErrorCodes.ParentInvalid;
        }

        parent = folders.FirstOrDefault(folder => folder.Id == id);
        return parent is null ? FolderErrorCodes.ParentNotFound : null;
    }

    private static FolderMutationResult DuplicateName() => FolderMutationResult.Invalid(
        FolderFields.Name,
        FolderErrorCodes.NameDuplicate);

    private static IReadOnlyList<FolderBreadcrumb> Breadcrumbs(Folder? current, Dictionary<Guid, Folder> byId)
    {
        var steps = new List<FolderBreadcrumb> { new(null, "Projects") };
        if (current is null)
        {
            return steps;
        }

        var chain = new List<Folder>();
        var visited = new HashSet<Guid>();
        Folder? cursor = current;
        while (cursor is not null && visited.Add(cursor.Id))
        {
            chain.Add(cursor);
            cursor = cursor.ParentFolderId is { } parentId && byId.TryGetValue(parentId, out var parent)
                ? parent
                : null;
        }

        chain.Reverse();
        steps.AddRange(chain.Select(folder => new FolderBreadcrumb(folder.Id, folder.Name)));
        return steps;
    }

    /// <summary>Direct and rolled-up counts, computed once per listing from the two loaded tables.</summary>
    private sealed record Counts(
        Dictionary<Guid, int> DirectProjects,
        Dictionary<Guid, int> DirectFolders,
        Dictionary<Guid, int> SubtreeProjects,
        Dictionary<Guid, int> SubtreeFolders);

    private static Counts BuildCounts(IReadOnlyList<Folder> folders, IReadOnlyList<Project> projects)
    {
        var directProjects = projects
            .Where(project => project.FolderId is not null)
            .GroupBy(project => project.FolderId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var directFolders = folders
            .Where(folder => folder.ParentFolderId is not null)
            .GroupBy(folder => folder.ParentFolderId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        var subtreeProjects = new Dictionary<Guid, int>(folders.Count);
        var subtreeFolders = new Dictionary<Guid, int>(folders.Count);
        foreach (var folder in folders)
        {
            var subtree = SubtreeIds(folder.Id, folders);
            subtreeFolders[folder.Id] = subtree.Count - 1;
            subtreeProjects[folder.Id] = projects.Count(project =>
                project.FolderId is { } id && subtree.Contains(id));
        }

        return new Counts(directProjects, directFolders, subtreeProjects, subtreeFolders);
    }

    private static FolderView View(Folder folder, Counts counts) => new(
        folder.Id,
        folder.Name,
        folder.Description,
        folder.ParentFolderId,
        folder.Depth,
        counts.DirectProjects.GetValueOrDefault(folder.Id),
        counts.DirectFolders.GetValueOrDefault(folder.Id),
        counts.SubtreeProjects.GetValueOrDefault(folder.Id),
        counts.SubtreeFolders.GetValueOrDefault(folder.Id));

    private static IReadOnlyList<FolderOption> Options(IReadOnlyList<Folder> folders, Guid? excludedFolderId)
    {
        var byId = folders.ToDictionary(folder => folder.Id);
        var options = new List<FolderOption>(folders.Count);

        foreach (var folder in folders)
        {
            if (excludedFolderId is { } excluded && IsDescendant(folder.Id, excluded, folders))
            {
                // A move form never offers the folder itself or its own subtree as a target: the service
                // refuses that anyway, and an option list that offers a refused target is a trap.
                continue;
            }

            var path = new List<string>();
            var visited = new HashSet<Guid>();
            Folder? cursor = folder;
            while (cursor is not null && visited.Add(cursor.Id))
            {
                path.Add(cursor.Name);
                cursor = cursor.ParentFolderId is { } parentId && byId.TryGetValue(parentId, out var parent)
                    ? parent
                    : null;
            }

            path.Reverse();
            options.Add(new FolderOption(folder.Id, folder.Name, string.Join(" / ", path), folder.Depth));
        }

        return options
            .OrderBy(option => option.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.Id)
            .ToArray();
    }

    /// <summary>The newest run of each listed project, in one query (never one query per project).</summary>
    private static async Task<Dictionary<Guid, BuildRun>> ReadLastRunsAsync(
        ZenkinsDbContext context,
        IReadOnlyList<Project> projects,
        CancellationToken cancellationToken)
    {
        if (projects.Count == 0)
        {
            return [];
        }

        var projectIds = projects.Select(project => project.Id).ToHashSet();
        var runs = await context.BuildRuns
            .Where(run => projectIds.Contains(run.ProjectId))
            .ToListAsync(cancellationToken);

        return runs
            .GroupBy(run => run.ProjectId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(run => run.Number).First());
    }

    private static FolderProjectRow ToRow(Project project, Dictionary<Guid, BuildRun> lastRuns) =>
        lastRuns.TryGetValue(project.Id, out var run)
            ? new FolderProjectRow(
                project.Id,
                project.Name,
                project.Kind,
                project.PipelineSource,
                project.RepositoryUrl,
                project.DefaultBranch,
                project.PipelinePath,
                project.Disabled,
                project.FolderId,
                run.Status,
                run.Number,
                run.CreatedAt)
            : new FolderProjectRow(
                project.Id,
                project.Name,
                project.Kind,
                project.PipelineSource,
                project.RepositoryUrl,
                project.DefaultBranch,
                project.PipelinePath,
                project.Disabled,
                project.FolderId,
                null,
                null,
                null);

    /// <summary>Any membership (Owner or Member) of the active organization may create and edit.</summary>
    private static Task<bool> IsMemberAsync(
        ZenkinsDbContext context,
        string actingUserId,
        CancellationToken cancellationToken) =>
        context.OrganizationMembers.AnyAsync(
            member => member.UserId == actingUserId,
            cancellationToken);
}
