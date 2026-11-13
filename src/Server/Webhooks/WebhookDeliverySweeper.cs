using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zenkins.Server.Webhooks;

/// <summary>The sweeper's tick result: how many past-TTL delivery records were removed.</summary>
public sealed record WebhookSweepResult(int PrunedDeliveries);

/// <summary>
/// The background half of card F-017: it prunes delivery records once their replay window has passed, so
/// the dedupe table stays bounded by "deliveries in the last TTL" rather than by everything ever pushed.
/// <para>
/// <b>Failure policy matches the other sweepers.</b> A tick that throws is logged and retried on the next
/// interval; nothing here touches a webhook, a project or a run, and pruning can never fail a delivery
/// (the delivery path treats a missing record as "not a duplicate" and re-claims it). Tests drive
/// <see cref="SweepOnceAsync"/> instead of waiting on real time.
/// </para>
/// </summary>
public sealed class WebhookDeliverySweeper(
    IServiceScopeFactory scopeFactory,
    WebhookOptions options,
    ILogger<WebhookDeliverySweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.SweepIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
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
                logger.LogError(exception, "Webhook delivery sweep failed; retrying at the next tick.");
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

    /// <summary>One sweep in its own DI scope (never a shared singleton scope). Test seam.</summary>
    public async Task<WebhookSweepResult> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var deliveries = scope.ServiceProvider.GetRequiredService<WebhookDeliveryService>();
        return new WebhookSweepResult(await deliveries.PruneExpiredDeliveriesAsync(cancellationToken));
    }
}
