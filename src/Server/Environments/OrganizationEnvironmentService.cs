using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Projects;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Environments;

/// <summary>
/// The frozen organization layer on a run (card F-034): the same name → value JSON shape the run's
/// parameter snapshot uses, so one contract (and one source-generated serializer) serves both.
/// <para>
/// <b>Plain configuration, deliberately readable.</b> Nothing here masks, hashes or encrypts a value:
/// the run page shows them and a step may echo them. A secret belongs in Secrets (F-012), not here.
/// </para>
/// </summary>
public static class RunEnvironmentSnapshot
{
    /// <summary>Serializes one snapshot; an empty set stays <see langword="null"/> (no snapshot).</summary>
    public static string? Serialize(IReadOnlyDictionary<string, string>? values) =>
        values is not { Count: > 0 }
            ? null
            : JsonSerializer.Serialize(
                new Dictionary<string, string>(values, StringComparer.Ordinal),
                ProjectParameterJsonContext.Default.DictionaryStringString);

    /// <summary>Reads a run's snapshot; a row without one stays <see langword="null"/>.</summary>
    public static IReadOnlyDictionary<string, string>? Read(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize(json, ProjectParameterJsonContext.Default.DictionaryStringString);

    /// <summary>
    /// One organization's variables, read for the run that organization is queueing: name → value,
    /// ordered by name so the stored snapshot is deterministic.
    /// <para>
    /// <b>Why an explicit organization and a system scope.</b> A trigger does not always have a request
    /// tenant: a webhook delivery and a schedule tick are headless, so a read that leaned on the request
    /// scope's active organization would silently return an empty layer for exactly the runs nobody is
    /// watching. The id therefore comes from the run being staged (already bound to one organization),
    /// and the read runs inside <see cref="ISystemDataScope"/> - the product's existing escape hatch for a
    /// cross-request read - with that organization named in the predicate, so no other tenant's layer can
    /// be read even by accident.
    /// </para>
    /// </summary>
    internal static async Task<Dictionary<string, string>> ReadForOrganizationAsync(
        ZenkinsDbContext context,
        ISystemDataScope systemDataScope,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(systemDataScope);

        using var scope = systemDataScope.BeginScope();
        var rows = await context.SystemQueryAsync<OrganizationEnvironmentVariable>(
            variable => variable.OrganizationId == organizationId,
            cancellationToken);

        var snapshot = new Dictionary<string, string>(rows.Count, StringComparer.Ordinal);
        foreach (var row in rows.OrderBy(row => row.Name, StringComparer.Ordinal))
        {
            snapshot[row.Name] = row.Value;
        }

        return snapshot;
    }

    /// <summary>
    /// The one name → value map that crosses to the worker: the organization layer first, then the
    /// parameter values on top of it (<paramref name="parameters"/> wins a shared name).
    /// <para>
    /// <b>The order is decided here, once.</b> The worker receives a flat map and injects it - it never
    /// compares two server layers, because a second copy of the order is a second thing that can drift.
    /// The two frozen snapshots stay separate columns on the run row (the page says which value came
    /// from which layer, and a rebuild re-resolves only the parameter one); merging is a transport
    /// decision taken when the claim is composed.
    /// </para>
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? Merge(
        IReadOnlyDictionary<string, string>? organization,
        IReadOnlyDictionary<string, string>? parameters)
    {
        if (organization is not { Count: > 0 })
        {
            return parameters is { Count: > 0 } ? parameters : null;
        }

        var merged = new Dictionary<string, string>(organization, StringComparer.Ordinal);
        if (parameters is { Count: > 0 })
        {
            foreach (var (name, value) in parameters)
            {
                merged[name] = value;
            }
        }

        return merged;
    }
}

/// <summary>One variable as the page renders it: name, plain value and attribution.</summary>
public sealed record OrganizationEnvironmentView(
    Guid Id,
    string Name,
    string Value,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string CreatedByUserId,
    string? UpdatedByUserId);

/// <summary>The raw form values, before trimming and validation.</summary>
public sealed record OrganizationEnvironmentInput(string? Name, string? Value);

/// <summary>Outcome kinds of an organization environment read or mutation.</summary>
public enum OrganizationEnvironmentStatus
{
    Success = 0,

    /// <summary>The id does not exist in the active organization. A foreign id is the same answer.</summary>
    NotFound = 1,

    /// <summary>The caller is not an Owner of the active organization (mutations only).</summary>
    Forbidden = 2,

    /// <summary>One or more fields failed validation; nothing was written.</summary>
    InvalidInput = 3,
}

/// <summary>One field-level validation failure. The page turns the code into the message.</summary>
public sealed record OrganizationEnvironmentFieldError(string Field, string Code);

/// <summary>
/// Result of reading the list. A record (not a nullable list) because "no organization", "not allowed"
/// and "no variables yet" must not be confusable.
/// <para>
/// <b>Readable by a Member.</b> Unlike secrets, these values are plain configuration every step of every
/// project of the organization receives - so hiding them from the people who read the logs would only
/// obscure what their builds ran with. Mutations stay Owner-only.
/// </para>
/// </summary>
public sealed record OrganizationEnvironmentListResult(
    OrganizationEnvironmentStatus Status,
    IReadOnlyList<OrganizationEnvironmentView> Variables,
    string? Message = null)
{
    public static OrganizationEnvironmentListResult Success(
        IReadOnlyList<OrganizationEnvironmentView> variables) =>
        new(OrganizationEnvironmentStatus.Success, variables);

    public static OrganizationEnvironmentListResult Forbidden(string message) =>
        new(OrganizationEnvironmentStatus.Forbidden, [], message);
}

/// <summary>Result of a mutation. <see cref="VariableId"/> is set on success.</summary>
public sealed record OrganizationEnvironmentMutationResult(
    OrganizationEnvironmentStatus Status,
    Guid? VariableId = null,
    string? Message = null,
    IReadOnlyList<OrganizationEnvironmentFieldError>? FieldErrors = null)
{
    public static OrganizationEnvironmentMutationResult Success(Guid variableId) =>
        new(OrganizationEnvironmentStatus.Success, variableId);

    public static OrganizationEnvironmentMutationResult NotFound() =>
        new(OrganizationEnvironmentStatus.NotFound);

    public static OrganizationEnvironmentMutationResult Forbidden(string message) =>
        new(OrganizationEnvironmentStatus.Forbidden, Message: message);

    public static OrganizationEnvironmentMutationResult Invalid(
        params OrganizationEnvironmentFieldError[] errors) =>
        new(OrganizationEnvironmentStatus.InvalidInput, FieldErrors: errors);

    public static OrganizationEnvironmentMutationResult Invalid(IReadOnlyList<OrganizationEnvironmentFieldError> errors) =>
        new(OrganizationEnvironmentStatus.InvalidInput, FieldErrors: errors);
}

/// <summary>
/// Management of the active organization's environment variables (card F-034).
/// <para>
/// <b>Tenant boundary.</b> The organization always comes from <see cref="ITenantContext"/>; no method
/// accepts an organization id from a caller, and every lookup goes through the tenant-filtered
/// <see cref="ZenkinsDbContext"/>. Another organization's id is therefore indistinguishable from a
/// missing one - a <see cref="OrganizationEnvironmentStatus.NotFound"/> (404), never an existence oracle.
/// </para>
/// <para>
/// <b>Ownership.</b> Reading is allowed for every member (the values reach every project's steps);
/// create, edit and delete are Owner-only (PRD §3.1.2). Authorization is checked before the row is
/// looked up and before the input is validated, so a Member's write is a 403 whatever it posts and a
/// malformed body cannot reveal whether an id exists.
/// </para>
/// <para>
/// <b>No encryption, no masking, on purpose.</b> The value is stored and returned verbatim; the only
/// rules are the name shape (the stored-secret rule, reused), the reserved prefix, the duplicate guard
/// (the unique index, not a pre-flight query), the per-value length and the organization-wide count.
/// </para>
/// </summary>
public sealed class OrganizationEnvironmentService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    ITenantContext tenantContext,
    ILogger<OrganizationEnvironmentService> logger)
{
    /// <summary>The active organization's variables, ordered by name. Every member may read them.</summary>
    public async Task<OrganizationEnvironmentListResult> GetVariablesAsync(
        string actingUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is null)
        {
            return OrganizationEnvironmentListResult.Forbidden(
                "You are not a member of an organization, so there are no variables to list.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Membership rather than ownership: a Member reads the list (the values are plain configuration).
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return OrganizationEnvironmentListResult.Forbidden(
                "You are not a member of this organization.");
        }

        var variables = await context.OrganizationEnvironmentVariables
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return OrganizationEnvironmentListResult.Success(
            [.. variables
                .OrderBy(variable => variable.Name, StringComparer.Ordinal)
                .Select(variable => new OrganizationEnvironmentView(
                    variable.Id,
                    variable.Name,
                    variable.Value,
                    variable.CreatedAt,
                    variable.UpdatedAt,
                    variable.CreatedByUserId,
                    variable.UpdatedByUserId))]);
    }

    /// <summary>Creates a variable in the active organization. Owner-only.</summary>
    public async Task<OrganizationEnvironmentMutationResult> CreateAsync(
        string actingUserId,
        OrganizationEnvironmentInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return OrganizationEnvironmentMutationResult.Forbidden(
                "You are not a member of an organization, so there is no variable to create.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return OrganizationEnvironmentMutationResult.Forbidden(
                "Only an organization Owner can change the organization's environment variables.");
        }

        var errors = Validate(input, requireName: true, out var name, out var value);
        if (errors.Count > 0)
        {
            return OrganizationEnvironmentMutationResult.Invalid(errors);
        }

        // The count cap is checked for creates only (an edit never grows the list), and it is a
        // form-level refusal: the message names the cap and the way out.
        var count = await context.OrganizationEnvironmentVariables.CountAsync(cancellationToken);
        if (count >= OrganizationEnvironmentValidation.MaxVariables)
        {
            return OrganizationEnvironmentMutationResult.Invalid(
                new OrganizationEnvironmentFieldError(string.Empty, OrganizationEnvironmentErrorCodes.LimitReached));
        }

        var variable = new OrganizationEnvironmentVariable
        {
            OrganizationId = activeOrganizationId.Value,
            Name = name,
            Value = value,
            CreatedByUserId = actingUserId,
        };
        context.OrganizationEnvironmentVariables.Add(variable);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "OrganizationEnvironmentVariables"))
        {
            return DuplicateName();
        }

        logger.LogInformation(
            "Organization environment variable created. OrganizationId={OrganizationId} VariableId={VariableId} Name={Name} ByUserId={UserId}",
            variable.OrganizationId,
            variable.Id,
            variable.Name,
            actingUserId);
        return OrganizationEnvironmentMutationResult.Success(variable.Id);
    }

    /// <summary>
    /// Edits one variable (both name and value - the name is not an identity here). Owner-only; a
    /// missing or foreign id is a 404 for an Owner.
    /// </summary>
    public async Task<OrganizationEnvironmentMutationResult> UpdateAsync(
        string actingUserId,
        Guid variableId,
        OrganizationEnvironmentInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return OrganizationEnvironmentMutationResult.Forbidden(
                "You are not a member of an organization, so there is no variable to edit.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return OrganizationEnvironmentMutationResult.Forbidden(
                "Only an organization Owner can change the organization's environment variables.");
        }

        var variable = await context.OrganizationEnvironmentVariables.SingleOrDefaultAsync(
            candidate => candidate.Id == variableId,
            cancellationToken);
        if (variable is null)
        {
            return OrganizationEnvironmentMutationResult.NotFound();
        }

        var errors = Validate(input, requireName: true, out var name, out var value);
        if (errors.Count > 0)
        {
            return OrganizationEnvironmentMutationResult.Invalid(errors);
        }

        variable.Name = name;
        variable.Value = value;
        variable.UpdatedByUserId = actingUserId;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "OrganizationEnvironmentVariables"))
        {
            return DuplicateName();
        }

        // Names and ids only: the values are configuration but the log is not the place to copy them.
        logger.LogInformation(
            "Organization environment variable updated. OrganizationId={OrganizationId} VariableId={VariableId} Name={Name} ByUserId={UserId}",
            activeOrganizationId,
            variable.Id,
            variable.Name,
            actingUserId);
        return OrganizationEnvironmentMutationResult.Success(variable.Id);
    }

    /// <summary>
    /// Deletes one variable. Owner-only; a missing or foreign id is a 404 for an Owner.
    /// <para>
    /// <b>History is untouched.</b> A run's snapshot is its own column (frozen at trigger time), so
    /// deleting the variable never changes a queued, running or finished run - the run page keeps
    /// showing the value that run actually used.
    /// </para>
    /// </summary>
    public async Task<OrganizationEnvironmentMutationResult> DeleteAsync(
        string actingUserId,
        Guid variableId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        var activeOrganizationId = tenantContext.OrganizationId;
        if (activeOrganizationId is null)
        {
            return OrganizationEnvironmentMutationResult.Forbidden(
                "You are not a member of an organization, so there is no variable to delete.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return OrganizationEnvironmentMutationResult.Forbidden(
                "Only an organization Owner can change the organization's environment variables.");
        }

        var variable = await context.OrganizationEnvironmentVariables.SingleOrDefaultAsync(
            candidate => candidate.Id == variableId,
            cancellationToken);
        if (variable is null)
        {
            return OrganizationEnvironmentMutationResult.NotFound();
        }

        context.OrganizationEnvironmentVariables.Remove(variable);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Organization environment variable deleted. OrganizationId={OrganizationId} VariableId={VariableId} Name={Name} ByUserId={UserId}",
            activeOrganizationId,
            variable.Id,
            variable.Name,
            actingUserId);
        return OrganizationEnvironmentMutationResult.Success(variable.Id);
    }

    private static List<OrganizationEnvironmentFieldError> Validate(
        OrganizationEnvironmentInput input,
        bool requireName,
        out string name,
        out string value)
    {
        var errors = new List<OrganizationEnvironmentFieldError>();

        name = string.Empty;
        if (requireName && OrganizationEnvironmentValidation.ValidateName(input.Name, out name) is { } nameError)
        {
            errors.Add(new OrganizationEnvironmentFieldError(
                OrganizationEnvironmentErrorCodes.FieldFor(nameError) ?? OrganizationEnvironmentFields.Name,
                nameError));
        }

        if (OrganizationEnvironmentValidation.ValidateValue(input.Value, out value) is { } valueError)
        {
            errors.Add(new OrganizationEnvironmentFieldError(
                OrganizationEnvironmentFields.Value,
                valueError));
        }

        return errors;
    }

    private static OrganizationEnvironmentMutationResult DuplicateName() =>
        OrganizationEnvironmentMutationResult.Invalid(
            new OrganizationEnvironmentFieldError(
                OrganizationEnvironmentFields.Name,
                OrganizationEnvironmentErrorCodes.NameDuplicate));

    private static Task<bool> IsOwnerAsync(
        ZenkinsDbContext context,
        string actingUserId,
        CancellationToken cancellationToken) =>
        context.OrganizationMembers.AnyAsync(
            member => member.UserId == actingUserId && member.Role == OrganizationRole.Owner,
            cancellationToken);

    private static Task<bool> IsMemberAsync(
        ZenkinsDbContext context,
        string actingUserId,
        CancellationToken cancellationToken) =>
        context.OrganizationMembers.AnyAsync(
            member => member.UserId == actingUserId,
            cancellationToken);
}
