using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Server.Webhooks;

/// <summary>Outcome of a webhook management attempt (create, rotate, enable/disable, delete).</summary>
public enum WebhookManagementStatus
{
    Success = 0,

    /// <summary>
    /// The project or webhook id does not exist in the active organization. Also the foreign-organization
    /// case: one 404 for both, so an id never becomes an existence oracle.
    /// </summary>
    NotFound = 1,

    /// <summary>The caller is not a member of the active organization.</summary>
    Forbidden = 2,

    /// <summary>One or more fields failed validation; nothing was written.</summary>
    InvalidInput = 3,
}

/// <summary>One field-level validation failure of the webhook form.</summary>
public sealed record WebhookFieldError(string Field, string Code);

/// <summary>
/// A webhook as the settings page displays it. Deliberately no secret, no ciphertext and no plaintext
/// member: a component cannot accidentally render one, and a later read only ever says "configured".
/// </summary>
public sealed record WebhookView(
    Guid Id,
    Guid ProjectId,
    string Name,
    string BranchFilter,
    string RequiredTags,
    bool Enabled,
    string CreatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SecretRotatedAt,
    DateTimeOffset? LastDeliveryAt,
    string? LastDeliveryOutcome)
{
    /// <summary>The delivery path a git host is configured with (opaque id, nothing else).</summary>
    public string EndpointPath => $"{WebhookEndpoints.DeliveryPathPrefix}/{Id:D}";

    /// <summary>The branch patterns the page lists; an empty filter reads as the project default.</summary>
    public IReadOnlyList<string> BranchPatterns => WebhookBranchFilter.Parse(BranchFilter);
}

/// <summary>The raw create-form values, before trimming and validation.</summary>
public sealed record WebhookInput(string? Name, string? BranchFilter, string? RequiredTags);

/// <summary>The list result: the rows for a member, the reason there are none, or a missing project.</summary>
public sealed record WebhookListResult(
    WebhookManagementStatus Status,
    IReadOnlyList<WebhookView> Webhooks,
    string? Message = null)
{
    public static WebhookListResult Success(IReadOnlyList<WebhookView> webhooks) =>
        new(WebhookManagementStatus.Success, webhooks);

    public static WebhookListResult NotFound() => new(WebhookManagementStatus.NotFound, []);

    public static WebhookListResult Forbidden(string message) =>
        new(WebhookManagementStatus.Forbidden, [], message);
}

/// <summary>
/// Result of a management mutation. <see cref="PlaintextSecret"/> is set <b>only</b> by create and
/// rotate, the two operations whose response is the one and only chance to copy the secret.
/// </summary>
public sealed record WebhookManagementResult(
    WebhookManagementStatus Status,
    Guid? WebhookId = null,
    string? PlaintextSecret = null,
    string? Message = null,
    IReadOnlyList<WebhookFieldError>? FieldErrors = null)
{
    public static WebhookManagementResult Success(Guid webhookId, string? plaintextSecret = null) =>
        new(WebhookManagementStatus.Success, webhookId, plaintextSecret);

    public static WebhookManagementResult NotFound() => new(WebhookManagementStatus.NotFound);

    public static WebhookManagementResult Forbidden(string message) =>
        new(WebhookManagementStatus.Forbidden, Message: message);

    public static WebhookManagementResult Invalid(params WebhookFieldError[] errors) =>
        new(WebhookManagementStatus.InvalidInput, FieldErrors: errors);
}

/// <summary>
/// Management of a project's webhooks (card F-017): list, create, rotate, enable/disable, delete.
/// <para>
/// <b>Tenant boundary.</b> The organization always comes from <see cref="ITenantContext"/>; no method
/// accepts one. Every lookup goes through the tenant-filtered context, so another organization's project
/// or webhook id is indistinguishable from a missing one (404, never a 403 oracle).
/// </para>
/// <para>
/// <b>Permission is project edit rights.</b> Any member of the organization may manage a project's
/// webhooks - the same rule F-003 applies to creating and editing projects. Hiding the controls is never
/// the enforcement: a Member of another organization has no tenant, and the tenant filter answers 404.
/// </para>
/// <para>
/// <b>The secret is generated and shown once.</b> The product never accepts a user-chosen secret (a weak
/// shared secret would be a configuration mistake to defend against) and never returns a stored one:
/// create and rotate mint a new value, return it in that response only, store its ciphertext, and log
/// neither. Every later read reports "configured / rotated at".
/// </para>
/// </summary>
public sealed class WebhookService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    TenantContext tenantContext,
    WebhookSecretProtector protector,
    ILogger<WebhookService> logger)
{
    /// <summary>The active organization's webhooks of one project, ordered by name.</summary>
    public async Task<WebhookListResult> GetWebhooksAsync(
        string actingUserId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is null)
        {
            return WebhookListResult.Forbidden(
                "You are not a member of an organization, so there are no webhooks to list.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return WebhookListResult.Forbidden("You are not a member of the active organization.");
        }

        if (!await context.Projects.AnyAsync(project => project.Id == projectId, cancellationToken))
        {
            // Foreign or unknown project: the same 404, no existence oracle.
            return WebhookListResult.NotFound();
        }

        var webhooks = await context.Set<Webhook>()
            .Where(webhook => webhook.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        return WebhookListResult.Success(
            [.. webhooks.OrderBy(webhook => webhook.Name, StringComparer.Ordinal).Select(ToView)]);
    }

    /// <summary>Creates a webhook and returns its id plus the one-time plaintext secret.</summary>
    public async Task<WebhookManagementResult> CreateAsync(
        string actingUserId,
        Guid projectId,
        WebhookInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        if (tenantContext.OrganizationId is null)
        {
            return WebhookManagementResult.Forbidden(
                "You are not a member of an organization, so there is no project to add a webhook to.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return WebhookManagementResult.Forbidden("You are not a member of the active organization.");
        }

        var project = await context.Projects.SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            return WebhookManagementResult.NotFound();
        }

        var errors = Validate(input, out var name, out var branchFilter, out var requiredTags);
        if (errors.Count > 0)
        {
            return WebhookManagementResult.Invalid([.. errors]);
        }

        var plaintextSecret = WebhookSecretProtector.Generate();
        var webhook = new Webhook
        {
            // The tenant comes from the active request; the data layer rejects anything else.
            OrganizationId = tenantContext.OrganizationId.Value,
            ProjectId = project.Id,
            Name = name,
            SecretCiphertext = protector.Protect(plaintextSecret),
            BranchFilter = branchFilter,
            RequiredTags = requiredTags,
            Enabled = true,
            CreatedByUserId = actingUserId,
        };
        context.Set<Webhook>().Add(webhook);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "Webhooks"))
        {
            return DuplicateName();
        }

        // The plaintext and the ciphertext are deliberately absent from this log line.
        logger.LogInformation(
            "Webhook created. OrganizationId={OrganizationId} ProjectId={ProjectId} WebhookId={WebhookId} Name={Name} BranchFilter={BranchFilter} ByUserId={UserId}",
            webhook.OrganizationId,
            webhook.ProjectId,
            webhook.Id,
            webhook.Name,
            webhook.BranchFilter,
            actingUserId);
        return WebhookManagementResult.Success(webhook.Id, plaintextSecret);
    }

    /// <summary>
    /// Replaces a webhook's secret in place and returns the new plaintext once. The previous value's
    /// ciphertext is gone after this call, so the old secret can never be used again.
    /// </summary>
    public async Task<WebhookManagementResult> RotateAsync(
        string actingUserId,
        Guid projectId,
        Guid webhookId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is null)
        {
            return WebhookManagementResult.Forbidden(
                "You are not a member of an organization, so there is no webhook to rotate.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return WebhookManagementResult.Forbidden("You are not a member of the active organization.");
        }

        var webhook = await FindAsync(context, projectId, webhookId, cancellationToken);
        if (webhook is null)
        {
            return WebhookManagementResult.NotFound();
        }

        var plaintextSecret = WebhookSecretProtector.Generate();
        webhook.SecretCiphertext = protector.Protect(plaintextSecret);
        webhook.SecretRotatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Webhook secret rotated. OrganizationId={OrganizationId} ProjectId={ProjectId} WebhookId={WebhookId} ByUserId={UserId}",
            webhook.OrganizationId,
            webhook.ProjectId,
            webhook.Id,
            actingUserId);
        return WebhookManagementResult.Success(webhook.Id, plaintextSecret);
    }

    /// <summary>
    /// Enables or disables deliveries. A disabled webhook answers the same 404 an unknown id does, so its
    /// URL never becomes a way to probe the project's existence.
    /// </summary>
    public async Task<WebhookManagementResult> SetEnabledAsync(
        string actingUserId,
        Guid projectId,
        Guid webhookId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is null)
        {
            return WebhookManagementResult.Forbidden(
                "You are not a member of an organization, so there is no webhook to change.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return WebhookManagementResult.Forbidden("You are not a member of the active organization.");
        }

        var webhook = await FindAsync(context, projectId, webhookId, cancellationToken);
        if (webhook is null)
        {
            return WebhookManagementResult.NotFound();
        }

        webhook.Enabled = enabled;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Webhook {State}. OrganizationId={OrganizationId} ProjectId={ProjectId} WebhookId={WebhookId} ByUserId={UserId}",
            enabled ? "enabled" : "disabled",
            webhook.OrganizationId,
            webhook.ProjectId,
            webhook.Id,
            actingUserId);
        return WebhookManagementResult.Success(webhook.Id);
    }

    /// <summary>Hard-deletes a webhook (its delivery records cascade). A missing or foreign id is a 404.</summary>
    public async Task<WebhookManagementResult> DeleteAsync(
        string actingUserId,
        Guid projectId,
        Guid webhookId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (tenantContext.OrganizationId is null)
        {
            return WebhookManagementResult.Forbidden(
                "You are not a member of an organization, so there is no webhook to delete.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await IsMemberAsync(context, actingUserId, cancellationToken))
        {
            return WebhookManagementResult.Forbidden("You are not a member of the active organization.");
        }

        var webhook = await FindAsync(context, projectId, webhookId, cancellationToken);
        if (webhook is null)
        {
            return WebhookManagementResult.NotFound();
        }

        // The delivery records hang off this row and go with it (cascade), so a re-created webhook with
        // the same name never inherits the old one's replay window.
        context.Set<Webhook>().Remove(webhook);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Webhook deleted. OrganizationId={OrganizationId} ProjectId={ProjectId} WebhookId={WebhookId} ByUserId={UserId}",
            webhook.OrganizationId,
            webhook.ProjectId,
            webhook.Id,
            actingUserId);
        return WebhookManagementResult.Success(webhook.Id);
    }

    /// <summary>
    /// The one webhook of the project, or null when it does not exist in the active organization or
    /// belongs to another project (the same 404).
    /// </summary>
    private static Task<Webhook?> FindAsync(
        ZenkinsDbContext context,
        Guid projectId,
        Guid webhookId,
        CancellationToken cancellationToken) =>
        context.Set<Webhook>().SingleOrDefaultAsync(
            candidate => candidate.Id == webhookId && candidate.ProjectId == projectId,
            cancellationToken);

    private static Task<bool> IsMemberAsync(
        ZenkinsDbContext context,
        string actingUserId,
        CancellationToken cancellationToken) =>
        context.OrganizationMembers.AnyAsync(
            member => member.UserId == actingUserId,
            cancellationToken);

    private static List<WebhookFieldError> Validate(
        WebhookInput input,
        out string name,
        out string branchFilter,
        out string requiredTags)
    {
        var errors = new List<WebhookFieldError>();

        if (WebhookValidation.ValidateName(input.Name, out name) is { } nameError)
        {
            errors.Add(new WebhookFieldError(WebhookFields.Name, nameError));
        }

        if (WebhookBranchFilter.Validate(input.BranchFilter, out branchFilter) is { } filterError)
        {
            errors.Add(new WebhookFieldError(WebhookFields.BranchFilter, filterError));
        }

        if (WebhookValidation.ValidateRequiredTags(input.RequiredTags, out requiredTags) is { } tagsError)
        {
            errors.Add(new WebhookFieldError(WebhookFields.RequiredTags, tagsError));
        }

        return errors;
    }

    private static WebhookManagementResult DuplicateName() =>
        WebhookManagementResult.Invalid(
            new WebhookFieldError(WebhookFields.Name, WebhookErrorCodes.NameDuplicate));

    private static WebhookView ToView(Webhook webhook) => new(
        webhook.Id,
        webhook.ProjectId,
        webhook.Name,
        webhook.BranchFilter,
        webhook.RequiredTags,
        webhook.Enabled,
        webhook.CreatedByUserId,
        webhook.CreatedAt,
        webhook.UpdatedAt,
        webhook.SecretRotatedAt,
        webhook.LastDeliveryAt,
        webhook.LastDeliveryOutcome);
}
