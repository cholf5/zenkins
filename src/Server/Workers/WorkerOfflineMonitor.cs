using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zenkins.Server.Runs;

namespace Zenkins.Server.Workers;

/// <summary>How many workers went Offline and how many runs were marked Lost by one sweep.</summary>
public sealed record OfflineSweepResult(int WorkersOfflined, int RunsLost);

/// <summary>
/// The heartbeat-staleness monitor (card F-010): a background service that marks Online workers
/// Offline once their last heartbeat is older than <c>Workers:OfflineAfterSeconds</c>, and hands
/// every still-<c>Running</c> run of such a worker to <see cref="RunService.SweepOfflineWorkersAsync"/>,
/// which marks it <c>Lost</c> (result unknown - never Failed, never requeued).
/// <para>
/// The Online → Offline flip is a compare-and-set per worker, so exactly one tick owns each
/// transition (and its runs' Lost marking) even when ticks race; a late tick finds the worker
/// already Offline and does nothing. The tick interval is <c>Workers:MonitorIntervalSeconds</c>
/// (detection latency is bounded by it; the exactly-once discipline does not depend on it), and
/// tests drive <see cref="SweepOnceAsync"/> directly instead of waiting for real time.
/// </para>
/// </summary>
public sealed class WorkerOfflineMonitor(
    IServiceScopeFactory scopeFactory,
    WorkerMonitorOptions options,
    ILogger<WorkerOfflineMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.MonitorIntervalSeconds));
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
                logger.LogError(exception, "Offline sweep failed; retrying at the next tick.");
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
    public async Task<OfflineSweepResult> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<RunService>();
        return await runs.SweepOfflineWorkersAsync(
            TimeSpan.FromSeconds(options.OfflineAfterSeconds),
            cancellationToken);
    }
}
