using Zenkins.Server.Localization;
using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Shared.Notifications;

namespace Zenkins.Server.Notifications;

/// <summary>One configured endpoint as the settings card reads it (never the secret).</summary>
public sealed record NotificationEndpointView(
    Guid Id,
    string Url,
    bool Enabled,
    string Events,
    bool AllowInsecureHttp,
    DateTimeOffset? SecretRotatedAt,
    DateTimeOffset? LastDeliveryAt,
    string? LastOutcome,
    string? LastError);

/// <summary>Outcome of a notification endpoint mutation (card F-039).</summary>
public enum NotificationMutationStatus
{
    Success = 0,
    NotFound = 1,
    Forbidden = 2,
    InvalidInput = 3,
}

/// <summary>One mutation's result, with the endpoint it touched and the stable code a refusal redirects with.</summary>
public sealed record NotificationMutationResult(
    NotificationMutationStatus Status,
    Guid? EndpointId = null,
    string? ErrorCode = null,
    string? Message = null)
{
    public static NotificationMutationResult Success(Guid endpointId) =>
        new(NotificationMutationStatus.Success, endpointId);

    public static NotificationMutationResult NotFound() => new(NotificationMutationStatus.NotFound);

    public static NotificationMutationResult Forbidden(string message) =>
        new(NotificationMutationStatus.Forbidden, null, null, message);

    public static NotificationMutationResult Invalid(string errorCode, string message) =>
        new(NotificationMutationStatus.InvalidInput, null, errorCode, message);
}

/// <summary>One "send test" attempt's result: what the operator's notice says.</summary>
public sealed record NotificationTestResult(
    NotificationMutationStatus Status,
    bool Delivered = false,
    int? StatusCode = null,
    string? Message = null)
{
    public static NotificationTestResult Ok(int? statusCode) =>
        new(NotificationMutationStatus.Success, true, statusCode);

    public static NotificationTestResult Failed(string message, int? statusCode = null) =>
        new(NotificationMutationStatus.Success, false, statusCode, message);

    public static NotificationTestResult NotFound() => new(NotificationMutationStatus.NotFound);

    public static NotificationTestResult Forbidden(string message) =>
        new(NotificationMutationStatus.Forbidden, false, null, message);
}

/// <summary>The stable codes the settings card renders for a refused notification form (card F-039).</summary>
public static class NotificationErrorCodes
{
    /// <summary>The URL was blank.</summary>
    public const string UrlRequired = "url-required";

    /// <summary>The URL is not an absolute http(s) URL.</summary>
    public const string UrlInvalid = "url-invalid";

    /// <summary>The URL is plain http and this endpoint did not opt in.</summary>
    public const string InsecureUrl = "url-insecure";

    /// <summary>The secret was blank.</summary>
    public const string SecretRequired = "secret-required";

    /// <summary>The secret is shorter or longer than the bounds allow.</summary>
    public const string SecretLength = "secret-length";

    /// <summary>No event was subscribed.</summary>
    public const string EventsRequired = "events-required";

    /// <summary>An event outside the vocabulary was subscribed.</summary>
    public const string EventsInvalid = "events-invalid";

    /// <summary>The project already has as many endpoints as it may have.</summary>
    public const string EndpointLimit = "endpoint-limit";

    /// <summary>A test send was asked for a project that has no run to describe yet.</summary>
    public const string TestNoRun = "test-no-run";

    /// <summary>The sentence for a code, or null when the code is not one of ours.</summary>
    /// <summary>The message the settings card renders for a code, in the UI's language.</summary>
    public static string? MessageFor(string? code, Localizer L10n) => code switch
    {
        UrlRequired => L10n["A receiver URL is required."],
        UrlInvalid => L10n["The receiver URL must be an absolute http or https URL."],
        InsecureUrl =>
            L10n["That URL is plain http. Tick \"allow plain http\" to send this project's branch, commit and signature in the clear, or use an https receiver."],
        SecretRequired => L10n["A shared secret is required: the receiver verifies the signature with it."],
        SecretLength =>
            L10n["The shared secret must be between {0} and {1} characters.", NotificationLimits.MinSecretLength, NotificationLimits.MaxSecretLength],
        EventsRequired => L10n["Subscribe to at least one event."],
        EventsInvalid => L10n["Events must be some of {0}.", string.Join(", ", NotificationEvents.Subscribable)],
        EndpointLimit =>
            L10n["A project may configure at most {0} notification endpoints.", NotificationLimits.MaxEndpointsPerProject],
        TestNoRun =>
            L10n["This project has no run yet, so there is nothing for a test delivery to describe. Start one first."],
        _ => null,
    };
}

/// <summary>The create form's values as the service validates them.</summary>
/// <param name="Events">The checked events (any of <see cref="NotificationEvents.Subscribable"/>).</param>
public sealed record NotificationEndpointInput(
    string? Url,
    string? Secret,
    IReadOnlyList<string>? Events,
    bool Enabled,
    bool AllowInsecureHttp);

/// <summary>
/// Card F-039's endpoint configuration and the "send test" action; the delivery itself lives in
/// <see cref="NotificationDispatcher"/> and <see cref="NotificationSender"/>.
/// <para>
/// <b>Owner-only writes.</b> A receiver URL is a place the Server will POST to, so pointing it somewhere is an
/// Owner's decision (the same stance as git credentials): a Member reads the list - "are we notifying anyone?" is
/// their business - and cannot add, rotate, disable or delete an endpoint. The HTTP layer is the enforcement; the
/// page hides the forms.
/// </para>
/// <para>
/// <b>Every write resolves the project first</b>, through the tenant filter, so a foreign project id is the same 404
/// as a missing one and no role check can become an existence oracle.
/// </para>
/// </summary>
public sealed class NotificationService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    NotificationSecretProtector protector,
    NotificationSender sender,
    NotificationOptions options,
    Localizer L10n,
    ILogger<NotificationService> logger)
{
    private readonly NotificationSecretProtector _secretProtector = protector;

    /// <summary>This project's endpoints, oldest first; <see langword="null"/> for a foreign or missing project.</summary>
    public async Task<IReadOnlyList<NotificationEndpointView>?> ListForProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await context.Projects.AsNoTracking().AnyAsync(project => project.Id == projectId, cancellationToken))
        {
            return null;
        }

        var endpoints = await context.Set<NotificationEndpoint>()
            .AsNoTracking()
            .Where(endpoint => endpoint.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        // Oldest first: the creation order is the order the card lists them in. Ordered in memory because SQLite
        // cannot ORDER BY a DateTimeOffset column (the same constraint the claim loop documents).
        return
        [
            .. endpoints
                .OrderBy(endpoint => endpoint.CreatedAt)
                .Select(endpoint => new NotificationEndpointView(
            endpoint.Id,
            endpoint.Url,
            endpoint.Enabled,
            endpoint.Events,
            endpoint.AllowInsecureHttp,
            endpoint.SecretRotatedAt,
            endpoint.LastDeliveryAt,
            endpoint.LastOutcome,
            endpoint.LastError)),
        ];
    }

    /// <summary>Configures one endpoint. Owner-only; the URL, the secret and the events are all validated.</summary>
    public async Task<NotificationMutationResult> CreateAsync(
        string actingUserId,
        Guid projectId,
        NotificationEndpointInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);
        ArgumentNullException.ThrowIfNull(input);

        var validation = Validate(input.Url, input.Secret, input.Events, input.AllowInsecureHttp, L10n);
        if (validation is { } refusal)
        {
            return refusal;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var project = await context.Projects.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            return NotificationMutationResult.NotFound();
        }

        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return NotificationMutationResult.Forbidden(
                "Only an organization Owner can configure notification endpoints.");
        }

        var count = await context.Set<NotificationEndpoint>().CountAsync(
            endpoint => endpoint.ProjectId == projectId,
            cancellationToken);
        if (count >= NotificationLimits.MaxEndpointsPerProject)
        {
            return NotificationMutationResult.Invalid(
                NotificationErrorCodes.EndpointLimit,
                NotificationErrorCodes.MessageFor(NotificationErrorCodes.EndpointLimit, L10n)!);
        }

        var created = new NotificationEndpoint
        {
            OrganizationId = project.OrganizationId,
            ProjectId = projectId,
            Url = input.Url!.Trim(),
            AllowInsecureHttp = input.AllowInsecureHttp,
            SecretCiphertext = _secretProtector.Protect(input.Secret!),
            Events = NotificationEvents.Normalize(string.Join(",", input.Events!))!,
            Enabled = input.Enabled,
            CreatedByUserId = actingUserId,
        };
        context.Set<NotificationEndpoint>().Add(created);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Notification endpoint created. OrganizationId={OrganizationId} ProjectId={ProjectId} EndpointId={EndpointId} Events={Events} ByUserId={UserId}",
            created.OrganizationId,
            created.ProjectId,
            created.Id,
            created.Events,
            actingUserId);
        return NotificationMutationResult.Success(created.Id);
    }

    /// <summary>Enables or disables one endpoint. A delivery already owed is still attempted.</summary>
    public Task<NotificationMutationResult> SetEnabledAsync(
        string actingUserId,
        Guid projectId,
        Guid endpointId,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            actingUserId,
            projectId,
            endpointId,
            (endpoint, userId) =>
            {
                endpoint.Enabled = enabled;
                logger.LogInformation(
                    "Notification endpoint {State}. OrganizationId={OrganizationId} EndpointId={EndpointId} ByUserId={UserId}",
                    enabled ? "enabled" : "disabled",
                    endpoint.OrganizationId,
                    endpoint.Id,
                    userId);
                return Task.CompletedTask;
            },
            cancellationToken);

    /// <summary>Removes one endpoint, and with it the obligations it still owed (the foreign key cascades).</summary>
    public async Task<NotificationMutationResult> DeleteAsync(
        string actingUserId,
        Guid projectId,
        Guid endpointId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var project = await context.Projects.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            return NotificationMutationResult.NotFound();
        }

        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return NotificationMutationResult.Forbidden(
                "Only an organization Owner can remove notification endpoints.");
        }

        var endpoint = await context.Set<NotificationEndpoint>().SingleOrDefaultAsync(
            candidate => candidate.Id == endpointId && candidate.ProjectId == projectId,
            cancellationToken);
        if (endpoint is null)
        {
            // Another project's endpoint, or one that is gone: the same 404 a missing project answers, so an endpoint
            // id can never become an existence oracle.
            return NotificationMutationResult.NotFound();
        }

        context.Set<NotificationEndpoint>().Remove(endpoint);
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Notification endpoint removed. OrganizationId={OrganizationId} EndpointId={EndpointId} ByUserId={UserId}",
            endpoint.OrganizationId,
            endpoint.Id,
            actingUserId);
        return NotificationMutationResult.Success(endpoint.Id);
    }

    /// <summary>
    /// Replaces the shared secret. <b>Deliveries already owed keep the secret they were created under</b> (the outbox
    /// row carries its own snapshot), so a rotation cannot make an in-flight notification unverifiable for a receiver
    /// that has not been updated yet.
    /// </summary>
    public async Task<NotificationMutationResult> RotateSecretAsync(
        string actingUserId,
        Guid projectId,
        Guid endpointId,
        string? secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        if (string.IsNullOrWhiteSpace(secret))
        {
            return NotificationMutationResult.Invalid(
                NotificationErrorCodes.SecretRequired,
                NotificationErrorCodes.MessageFor(NotificationErrorCodes.SecretRequired, L10n)!);
        }

        if (secret.Trim().Length is < NotificationLimits.MinSecretLength or > NotificationLimits.MaxSecretLength)
        {
            return NotificationMutationResult.Invalid(
                NotificationErrorCodes.SecretLength,
                NotificationErrorCodes.MessageFor(NotificationErrorCodes.SecretLength, L10n)!);
        }

        var rotated = secret.Trim();
        return await MutateAsync(
            actingUserId,
            projectId,
            endpointId,
            (endpoint, userId) =>
            {
                endpoint.SecretCiphertext = _secretProtector.Protect(rotated);
                endpoint.SecretRotatedAt = DateTimeOffset.UtcNow;
                logger.LogInformation(
                    "Notification endpoint secret rotated. OrganizationId={OrganizationId} EndpointId={EndpointId} ByUserId={UserId}",
                    endpoint.OrganizationId,
                    endpoint.Id,
                    userId);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    /// <summary>
    /// Sends one test delivery now, in this request (bounded by the sender's timeout) and records the outcome on the
    /// endpoint. Deliberately synchronous: an operator who pressed a button is waiting for the answer, and the
    /// no-blocking rule is about <em>builds</em>. It describes the project's newest run, because a payload without a
    /// run would not be the shape a receiver has to parse.
    /// </summary>
    public async Task<NotificationTestResult> SendTestAsync(
        string actingUserId,
        Guid projectId,
        Guid endpointId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var project = await context.Projects.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            return NotificationTestResult.NotFound();
        }

        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return NotificationTestResult.Forbidden(
                "Only an organization Owner can send a test notification.");
        }

        var endpoint = await context.Set<NotificationEndpoint>().SingleOrDefaultAsync(
            candidate => candidate.Id == endpointId && candidate.ProjectId == projectId,
            cancellationToken);
        if (endpoint is null)
        {
            return NotificationTestResult.NotFound();
        }

        var run = await context.BuildRuns
            .AsNoTracking()
            .Where(candidate => candidate.ProjectId == projectId)
            .OrderByDescending(candidate => candidate.Number)
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            return NotificationTestResult.Failed(
                NotificationErrorCodes.MessageFor(NotificationErrorCodes.TestNoRun, L10n)!);
        }

        var payload = BuildPayload(
            NotificationEvents.Test,
            deliveryId: Guid.NewGuid().ToString("D"),
            attempt: 1,
            run,
            project,
            failureReason: null,
            sentAt: DateTimeOffset.UtcNow);
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            payload,
            NotificationJsonContext.Default.NotificationPayload);
        var secret = TryUnprotect(endpoint.SecretCiphertext);
        if (secret is null)
        {
            return NotificationTestResult.Failed(
                "The stored secret cannot be decrypted any more (the data protection key ring changed). Rotate it.");
        }

        var sent = await sender.SendAsync(
            endpoint.Url,
            secret,
            body,
            payload.DeliveryId,
            payload.Event,
            payload.Attempt,
            cancellationToken);

        endpoint.LastDeliveryAt = DateTimeOffset.UtcNow;
        endpoint.LastOutcome = sent.Delivered ? NotificationOutcomes.Delivered : NotificationOutcomes.Failed;
        endpoint.LastError = sent.Delivered ? null : Truncate(sent.Error);
        endpoint.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        return sent.Delivered
            ? NotificationTestResult.Ok(sent.StatusCode)
            : NotificationTestResult.Failed(sent.Error ?? "the receiver refused the delivery", sent.StatusCode);
    }

    /// <summary>
    /// The shared payload builder, used by the dispatcher and by the test send so the two cannot drift.
    /// <b>Nothing secret, parameter-valued or environment-shaped is passed in</b> - the signature of this method is the
    /// no-leak guarantee.
    /// </summary>
    public NotificationPayload BuildPayload(
        string eventName,
        string deliveryId,
        int attempt,
        BuildRun run,
        Project project,
        string? failureReason,
        DateTimeOffset sentAt) =>
        new(
            eventName,
            deliveryId,
            attempt,
            new NotificationRun(
                run.Id,
                run.Number,
                run.GitBranch,
                run.GitCommit,
                run.Status,
                run.TriggerType,
                RunUrl(run.Id),
                run.RebuiltFromRunId,
                RebuiltFromRunNumber: null),
            new NotificationProject(project.Id, project.Name, ProjectUrl(project.Id)),
            failureReason,
            run.EndedAt ?? run.UpdatedAt,
            sentAt);

    /// <summary>The run's link: absolute when the Server knows its public base URL, otherwise the relative path.</summary>
    public string RunUrl(Guid runId) =>
        options.PublicBaseUrl is { } baseUrl ? $"{baseUrl}/runs/{runId:D}" : $"/runs/{runId:D}";

    private string ProjectUrl(Guid projectId) =>
        options.PublicBaseUrl is { } baseUrl ? $"{baseUrl}/projects/{projectId:D}" : $"/projects/{projectId:D}";

    /// <summary>Plaintext of a stored ciphertext, or null when this process cannot decrypt it.</summary>
    public string? TryUnprotect(string ciphertext)
    {
        try
        {
            return _secretProtector.Unprotect(ciphertext);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The recorded error, bounded (the column's own cap is the same number).</summary>
    public static string? Truncate(string? text) =>
        text is null || text.Length <= NotificationLimits.MaxErrorLength
            ? text
            : text[..NotificationLimits.MaxErrorLength];

    /// <summary>One validated form, or the refusal the caller redirects with.</summary>
    private static NotificationMutationResult? Validate(
        string? url,
        string? secret,
        IReadOnlyList<string>? events,
        bool allowInsecureHttp,
        Localizer L10n)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Refusal(NotificationErrorCodes.UrlRequired);
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host)
            || uri.UserInfo.Length > 0)
        {
            return Refusal(NotificationErrorCodes.UrlInvalid);
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !allowInsecureHttp)
        {
            return Refusal(NotificationErrorCodes.InsecureUrl);
        }

        if (url.Trim().Length > NotificationLimits.MaxUrlLength)
        {
            return Refusal(NotificationErrorCodes.UrlInvalid);
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            return Refusal(NotificationErrorCodes.SecretRequired);
        }

        if (secret.Trim().Length is < NotificationLimits.MinSecretLength or > NotificationLimits.MaxSecretLength)
        {
            return Refusal(NotificationErrorCodes.SecretLength);
        }

        if (events is null || events.Count == 0)
        {
            return Refusal(NotificationErrorCodes.EventsRequired);
        }

        return NotificationEvents.Normalize(string.Join(",", events)) is null
            ? Refusal(NotificationErrorCodes.EventsInvalid)
            : null;

        NotificationMutationResult Refusal(string code) =>
            NotificationMutationResult.Invalid(code, NotificationErrorCodes.MessageFor(code, L10n)!);
    }

    /// <summary>Owner check, project first: a foreign project never reaches it (the caller resolved it).</summary>
    private static Task<bool> IsOwnerAsync(
        ZenkinsDbContext context,
        string actingUserId,
        CancellationToken cancellationToken) =>
        context.OrganizationMembers.AnyAsync(
            member => member.UserId == actingUserId && member.Role == OrganizationRole.Owner,
            cancellationToken);

    private async Task<NotificationMutationResult> MutateAsync(
        string actingUserId,
        Guid projectId,
        Guid endpointId,
        Func<NotificationEndpoint, string, Task> mutate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actingUserId);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var project = await context.Projects.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == projectId,
            cancellationToken);
        if (project is null)
        {
            return NotificationMutationResult.NotFound();
        }

        if (!await IsOwnerAsync(context, actingUserId, cancellationToken))
        {
            return NotificationMutationResult.Forbidden(
                "Only an organization Owner can change notification endpoints.");
        }

        var endpoint = await context.Set<NotificationEndpoint>().SingleOrDefaultAsync(
            candidate => candidate.Id == endpointId && candidate.ProjectId == projectId,
            cancellationToken);
        if (endpoint is null)
        {
            // Same 404 as a missing project (see DeleteAsync's note).
            return NotificationMutationResult.NotFound();
        }

        await mutate(endpoint, actingUserId);
        endpoint.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return NotificationMutationResult.Success(endpoint.Id);
    }
}
