using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zenkins.Server.Schedules;

/// <summary>
/// The scheduler's background half (card F-024): one tick per <c>Schedules:TickSeconds</c> evaluates every
/// project's cron schedules and queues the runs that are due. It assigns nothing by itself - a queued run is
/// claimed by a worker like any other - and it never backfills: a due time it slept through is recorded, not
/// replayed (see <see cref="ScheduleService.EvaluateDueSchedulesAsync"/>).
/// <para>
/// <b>Failure policy matches the other sweepers.</b> A tick that throws is logged and retried on the next interval;
/// inside a tick, one schedule's failure is logged and the others still run. Tests drive
/// <see cref="SweepOnceAsync"/> instead of waiting on real time.
/// </para>
/// </summary>
public sealed class ScheduleSweeper(
    IServiceScopeFactory scopeFactory,
    ScheduleOptions options,
    ILogger<ScheduleSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.TickSeconds));
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
                logger.LogError(exception, "Schedule sweep failed; retrying at the next tick.");
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
    public async Task<ScheduleTickResult> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var schedules = scope.ServiceProvider.GetRequiredService<ScheduleService>();
        return await schedules.EvaluateDueSchedulesAsync(cancellationToken);
    }
}
