using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Notifications;

namespace Zenkins.Server.Notifications;

/// <summary>The bounds of one dispatch pass (card F-039), the same shape as F-026's.</summary>
public static class NotificationDispatchLimits
{
    /// <summary>How many obligations one pass works. Bounded so one dead receiver cannot starve the rest.</summary>
    public const int BatchSize = 20;
}

/// <summary>
/// The consumer half of card F-039's delivery guarantee: it works the <c>completion-notification</c> rows of the
/// F-026 outbox - <b>the same table, not a second queue</b> - and settles each one as delivered or finally failed.
/// <para>
/// <b>At-least-once, and nothing more.</b> A row stays <c>pending</c> until the delivery and its settlement commit,
/// so a crash between them re-delivers with the same delivery id (a receiver deduplicates on
/// <c>X-Zenkins-Delivery</c>). A receiver that keeps failing is retried
/// <see cref="NotificationOptions.MaxAttempts"/> times with a linear backoff and then settled as
/// <see cref="NotificationOutcomes.Failed"/> with the last error on the endpoint - visible, not stuck, not infinite.
/// </para>
/// <para>
/// <b>A disabled project still notifies.</b> Card F-028's brake stops new triggers; a run that already ended is
/// history, and its notification is owed. Nothing here reads <c>Project.Disabled</c>.
/// </para>
/// <para>
/// <b>A disabled endpoint still delivers what it owes.</b> The endpoint was on when the terminal state happened, so
/// the obligation is already promised; disabling stops <em>future</em> obligations (staging filters on
/// <c>Enabled</c>), not an in-flight one.
/// </para>
/// </summary>
public sealed class NotificationDispatcher(
    IServiceScopeFactory scopeFactory,
    NotificationOptions options,
    ILogger<NotificationDispatcher> logger) : BackgroundService
{
    /// <summary>
    /// Works one batch of due obligations in its own DI scope (the tenant and the services are scoped; a hosted
    /// service is not). Returns how many rows settled. Test seam.
    /// </summary>
    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<NotificationDispatchPass>()
            .DispatchPendingAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchPendingAsync(stoppingToken);
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Notification dispatch pass failed; retrying at the next tick.");
                try
                {
                    if (!await timer.WaitForNextTickAsync(stoppingToken))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}

/// <summary>
/// One dispatch pass (card F-039), scoped like every other request-shaped service: it holds the tenant the row
/// names and the context factory, and it never outlives the pass that created it.
/// </summary>
public sealed class NotificationDispatchPass(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    ISystemDataScope systemDataScope,
    TenantContext tenantContext,
    NotificationService notifications,
    NotificationSender sender,
    NotificationOptions options,
    ILogger<NotificationDispatchPass> logger)
{
    /// <summary>Works one batch of due obligations. Returns how many rows settled.</summary>
    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        var due = await ReadDueAsync(cancellationToken);
        var settled = 0;
        foreach (var row in due)
        {
            try
            {
                settled += await DispatchOneAsync(row.Id, row.OrganizationId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure)
            {
                // One broken obligation must not stop the batch; the row keeps its attempts and backs off.
                logger.LogError(
                    failure,
                    "Notification dispatch failed. OrganizationId={OrganizationId} OutboxId={OutboxId}",
                    row.OrganizationId,
                    row.Id);
            }
        }

        return settled;
    }

    /// <summary>
    /// The due rows, read across tenants through the system scope, <b>filtered to this dispatcher's kind</b>: F-026's
    /// consumer reads its own rows the same way, which is why the outbox needed a kind column at all.
    /// </summary>
    private async Task<IReadOnlyList<(Guid Id, Guid OrganizationId)>> ReadDueAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        using var scope = systemDataScope.BeginScope();
        var now = DateTimeOffset.UtcNow;

        // The status and kind filters run in SQL; the due check and ordering happen in memory, because SQLite cannot
        // compare or order DateTimeOffset columns in SQL (the same constraint the claim loop documents).
        //
        // Deliberately SystemQueryAsync: a background pass has no request tenant, and the ordinary query filter would
        // answer the empty set (`CurrentOrganizationId` is Guid.Empty here). The system scope is the product's
        // escape hatch for exactly this read - the rows' own OrganizationId is what authorizes each delete below.
        var pending = await context.SystemQueryAsync<OutboxMessage>(
            message => message.Kind == OutboxKinds.CompletionNotification
                && message.Status == OutboxStatuses.Pending,
            cancellationToken);

        return
        [
            .. pending
                .Where(message => message.NextAttemptAt <= now)
                .OrderBy(message => message.CreatedAt)
                .ThenBy(message => message.Id)
                .Take(NotificationDispatchLimits.BatchSize)
                .Select(message => (message.Id, message.OrganizationId)),
        ];
    }

    private async Task<int> DispatchOneAsync(Guid outboxId, Guid organizationId, CancellationToken cancellationToken)
    {
        // The row carries the tenant: a background pass has no request to inherit one from. Setting it here is what
        // the rest of the pass reads through (endpoint, run, project); the row lookup below names the organization
        // itself, so it cannot depend on that write having reached this context.
        tenantContext.OrganizationId = organizationId;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var message = await context.OutboxMessages
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == outboxId && candidate.OrganizationId == organizationId,
                cancellationToken);
        if (message is null
            || !string.Equals(message.Kind, OutboxKinds.CompletionNotification, StringComparison.Ordinal)
            || !string.Equals(message.Status, OutboxStatuses.Pending, StringComparison.Ordinal))
        {
            // Another pass settled it, it is gone, or it was never ours.
            return 0;
        }

        if (message.NotificationEndpointId is not { } endpointId)
        {
            return await SettleAsync(context, message, null, NotificationOutcomes.Failed,
                "the obligation names no endpoint", cancellationToken);
        }

        var endpoint = await context.Set<NotificationEndpoint>().SingleOrDefaultAsync(
            candidate => candidate.Id == endpointId,
            cancellationToken);
        if (endpoint is null)
        {
            // The endpoint was removed after staging; its rows cascade, so this is belt and braces.
            return await SettleAsync(context, message, null, NotificationOutcomes.Failed,
                "the notification endpoint no longer exists", cancellationToken);
        }

        var run = await context.BuildRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == message.UpstreamRunId, cancellationToken);
        if (run is null)
        {
            return await SettleAsync(context, message, endpoint, NotificationOutcomes.Failed,
                "the run no longer exists", cancellationToken);
        }

        var project = await context.Projects
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == run.ProjectId, cancellationToken);
        if (project is null)
        {
            return await SettleAsync(context, message, endpoint, NotificationOutcomes.Failed,
                "the project no longer exists", cancellationToken);
        }

        // The signature secret is the one in force when the obligation was created (a rotation afterwards must not
        // re-sign it), decrypted with the purpose-bound cipher.
        var secret = notifications.TryUnprotect(
            message.NotificationSecretCiphertext ?? endpoint.SecretCiphertext);
        if (secret is null)
        {
            return await SettleAsync(context, message, endpoint, NotificationOutcomes.Failed,
                "the shared secret can no longer be decrypted (the data protection key ring changed)", cancellationToken);
        }

        var attempt = message.Attempts + 1;
        var payload = notifications.BuildPayload(
            message.NotificationEvent ?? NotificationEvents.Success,
            deliveryId: message.Id.ToString("D"),
            attempt,
            run,
            project,
            failureReason: run.FailureReason,
            sentAt: DateTimeOffset.UtcNow);
        var body = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            NotificationJsonContext.Default.NotificationPayload);

        var sent = await sender.SendAsync(
            endpoint.Url,
            secret,
            body,
            payload.DeliveryId,
            payload.Event,
            payload.Attempt,
            cancellationToken);

        if (sent.Delivered)
        {
            return await SettleAsync(context, message, endpoint, NotificationOutcomes.Delivered,
                reason: null, cancellationToken);
        }

        return await RetryOrFailAsync(context, message, endpoint, sent, cancellationToken);
    }

    /// <summary>
    /// One failed attempt: either the row backs off (bounded) or it settles as finally failed with the last error on
    /// both the row and the endpoint - the card's "a dead endpoint ends in a recorded failure, never a stuck run".
    /// </summary>
    private async Task<int> RetryOrFailAsync(
        ZenkinsDbContext context,
        OutboxMessage message,
        NotificationEndpoint endpoint,
        NotificationSendResult sent,
        CancellationToken cancellationToken)
    {
        message.Attempts++;
        var error = NotificationService.Truncate(sent.Error ?? $"HTTP {sent.StatusCode}");

        if (message.Attempts >= options.MaxAttempts)
        {
            return await SettleAsync(
                context,
                message,
                endpoint,
                NotificationOutcomes.Failed,
                $"{error} (after {message.Attempts} attempts)",
                cancellationToken);
        }

        message.NextAttemptAt = DateTimeOffset.UtcNow + (options.Backoff * message.Attempts);
        endpoint.LastDeliveryAt = DateTimeOffset.UtcNow;
        endpoint.LastOutcome = NotificationOutcomes.Retrying;
        endpoint.LastError = error;
        endpoint.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Notification delivery retried. OrganizationId={OrganizationId} OutboxId={OutboxId} EndpointId={EndpointId} Attempts={Attempts} Status={StatusCode} Error={Error}",
            message.OrganizationId,
            message.Id,
            endpoint.Id,
            message.Attempts,
            sent.StatusCode,
            error);
        return 0;
    }

    /// <summary>Settles one row with its outcome and reason, and records the same outcome on the endpoint.</summary>
    private async Task<int> SettleAsync(
        ZenkinsDbContext context,
        OutboxMessage message,
        NotificationEndpoint? endpoint,
        string outcome,
        string? reason,
        CancellationToken cancellationToken)
    {
        message.Status = OutboxStatuses.Settled;
        message.Outcome = outcome;
        message.Reason = reason;
        message.SettledAt = DateTimeOffset.UtcNow;

        if (endpoint is not null)
        {
            endpoint.LastDeliveryAt = DateTimeOffset.UtcNow;
            endpoint.LastOutcome = outcome;
            endpoint.LastError = string.Equals(outcome, NotificationOutcomes.Delivered, StringComparison.Ordinal)
                ? null
                : NotificationService.Truncate(reason);
            endpoint.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Completion notification settled. OrganizationId={OrganizationId} OutboxId={OutboxId} EndpointId={EndpointId} Event={Event} Outcome={Outcome} Reason={Reason}",
            message.OrganizationId,
            message.Id,
            message.NotificationEndpointId,
            message.NotificationEvent,
            outcome,
            reason);
        return 1;
    }
}
