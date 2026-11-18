using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Shared.Snippets;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Snippets;

/// <summary>One organization's snippet as the page shows it (never a link to anything else).</summary>
public sealed record StepSnippetView(
    Guid Id,
    string Name,
    string Kind,
    string Body,
    string? Description,
    string? UpdatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Outcome of a snippet mutation (Success carries the row's id; the agent renders the rest).</summary>
public sealed record SnippetMutationResult(
    SnippetMutationStatus Status,
    Guid? SnippetId = null,
    IReadOnlyList<SnippetFieldError>? FieldErrors = null)
{
    public static SnippetMutationResult Success(Guid snippetId) => new(SnippetMutationStatus.Success, snippetId);

    public static SnippetMutationResult NotFound() => new(SnippetMutationStatus.NotFound);

    public static SnippetMutationResult Forbidden() => new(SnippetMutationStatus.Forbidden);

    public static SnippetMutationResult Invalid(params SnippetFieldError[] errors) =>
        new(SnippetMutationStatus.InvalidInput, FieldErrors: errors);
}

/// <summary>How a snippet mutation ended.</summary>
public enum SnippetMutationStatus
{
    Success = 0,
    NotFound = 1,
    Forbidden = 2,
    InvalidInput = 3,
}

/// <summary>
/// Card F-035's snippet library: create, edit, delete and list an organization's reusable text. Members and
/// Owners may both manage snippets (a shared convenience, not a secret), and every lookup goes through the
/// tenant-filtered context, so a foreign id is a plain 404 rather than an existence oracle.
/// <para>
/// <b>Nothing here is executed or referenced.</b> The service returns text for editors; there is no runtime
/// consumer, so an edit or a delete cannot reach a saved project, a frozen spec or a run.
/// </para>
/// </summary>
public sealed class StepSnippetService(
    ITenantContext tenantContext,
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    ILogger<StepSnippetService> logger)
{
    /// <summary>Every snippet of the active organization, ordered by name - the pickers' source.</summary>
    public async Task<IReadOnlyList<StepSnippetView>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.StepSnippets
            .AsNoTracking()
            .OrderBy(snippet => snippet.Name)
            .Select(snippet => new StepSnippetView(
                snippet.Id,
                snippet.Name,
                snippet.Kind,
                snippet.Body,
                snippet.Description,
                snippet.UpdatedByUserId,
                snippet.CreatedAt,
                snippet.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Creates a snippet in the active organization. A duplicate name is a field error, not a 500.</summary>
    public async Task<SnippetMutationResult> CreateAsync(
        string actingUserId,
        SnippetInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return SnippetMutationResult.Forbidden();
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return SnippetMutationResult.Forbidden();
        }

        var errors = StepSnippetValidation.Validate(input, out var name, out var kind, out var body, out var description);
        if (errors.Count > 0)
        {
            return SnippetMutationResult.Invalid([.. errors]);
        }

        if (await NameTakenAsync(context, name, exceptId: null, cancellationToken))
        {
            return SnippetMutationResult.Invalid(
                new SnippetFieldError(SnippetFields.Name, SnippetErrorCodes.NameDuplicate));
        }

        var snippet = new StepSnippet
        {
            // The tenant comes from the active request; the data layer rejects anything else.
            OrganizationId = activeOrganizationId.Value,
            Name = name,
            Kind = kind,
            Body = body,
            Description = description,
            CreatedByUserId = actingUserId,
        };
        context.StepSnippets.Add(snippet);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "StepSnippets"))
        {
            return SnippetMutationResult.Invalid(
                new SnippetFieldError(SnippetFields.Name, SnippetErrorCodes.NameDuplicate));
        }

        logger.LogInformation(
            "Step snippet created. OrganizationId={OrganizationId} SnippetId={SnippetId} Kind={Kind} ByUserId={UserId}",
            snippet.OrganizationId,
            snippet.Id,
            snippet.Kind,
            actingUserId);
        return SnippetMutationResult.Success(snippet.Id);
    }

    /// <summary>
    /// Edits a snippet of the active organization. A foreign or missing id is a 404; the kind may change,
    /// because nothing references the row - the next insertion simply offers the new kind.
    /// </summary>
    public async Task<SnippetMutationResult> UpdateAsync(
        string actingUserId,
        Guid snippetId,
        SnippetInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return SnippetMutationResult.Forbidden();
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return SnippetMutationResult.Forbidden();
        }

        var snippet = await context.StepSnippets.SingleOrDefaultAsync(
            candidate => candidate.Id == snippetId,
            cancellationToken);
        if (snippet is null)
        {
            return SnippetMutationResult.NotFound();
        }

        var errors = StepSnippetValidation.Validate(input, out var name, out var kind, out var body, out var description);
        if (errors.Count > 0)
        {
            return SnippetMutationResult.Invalid([.. errors]);
        }

        if (await NameTakenAsync(context, name, exceptId: snippet.Id, cancellationToken))
        {
            return SnippetMutationResult.Invalid(
                new SnippetFieldError(SnippetFields.Name, SnippetErrorCodes.NameDuplicate));
        }

        snippet.Name = name;
        snippet.Kind = kind;
        snippet.Body = body;
        snippet.Description = description;
        snippet.UpdatedByUserId = actingUserId;
        snippet.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "StepSnippets"))
        {
            return SnippetMutationResult.Invalid(
                new SnippetFieldError(SnippetFields.Name, SnippetErrorCodes.NameDuplicate));
        }

        logger.LogInformation(
            "Step snippet updated. OrganizationId={OrganizationId} SnippetId={SnippetId} Kind={Kind} ByUserId={UserId}",
            snippet.OrganizationId,
            snippet.Id,
            snippet.Kind,
            actingUserId);
        return SnippetMutationResult.Success(snippet.Id);
    }

    /// <summary>
    /// Deletes a snippet of the active organization. Nothing else changes: no project, run or spec referenced
    /// the row, so there is nothing to rewrite and no history to break.
    /// </summary>
    public async Task<SnippetMutationResult> DeleteAsync(
        string actingUserId,
        Guid snippetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return SnippetMutationResult.Forbidden();
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return SnippetMutationResult.Forbidden();
        }

        var snippet = await context.StepSnippets.SingleOrDefaultAsync(
            candidate => candidate.Id == snippetId,
            cancellationToken);
        if (snippet is null)
        {
            return SnippetMutationResult.NotFound();
        }

        context.StepSnippets.Remove(snippet);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Step snippet deleted. OrganizationId={OrganizationId} SnippetId={SnippetId} ByUserId={UserId}",
            snippet.OrganizationId,
            snippet.Id,
            actingUserId);
        return SnippetMutationResult.Success(snippet.Id);
    }

    private static Task<bool> NameTakenAsync(
        ZenkinsDbContext context,
        string name,
        Guid? exceptId,
        CancellationToken cancellationToken) =>
        context.StepSnippets.AnyAsync(
            snippet => snippet.Name == name && (exceptId == null || snippet.Id != exceptId),
            cancellationToken);

    private static Task<bool> IsMemberAsync(
        ZenkinsDbContext context,
        string actingUserId,
        CancellationToken cancellationToken) =>
        context.OrganizationMembers.AnyAsync(
            member => member.UserId == actingUserId,
            cancellationToken);
}
