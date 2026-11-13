using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zenkins.Server.Runs;

/// <summary>
/// The scheduler's background half (card F-006, deadline half F-009): it assigns nothing by itself
/// - workers pull via <c>POST /api/workers/claim</c> - its jobs are expiring leases (F-006) and
/// enforcing run deadlines (F-009: a run past its deadline gets a timeout cancel request; one still
/// Running after the grace period is honestly marked Lost). On every tick both sweeps run; neither
/// ever requeues or retries a finished run (terminal states are closed).
/// <para>
/// Failures are logged and the loop continues: a transient database hiccup delays a sweep by one
/// interval, it never kills the host. Tests drive <see cref="RunService.SweepExpiredLeasesAsync"/>
/// and <see cref="RunService.SweepDeadlinesAsync"/> directly instead of waiting for real time.
/// </para>
/// </summary>
public sealed class LeaseSweeper(
    IServiceScopeFactory scopeFactory,
    RunOptions options,
    ILogger<LeaseSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.SweepIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
                await SweepDeadlinesOnceAsync(stoppingToken);
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
                logger.LogError(exception, "Lease sweep failed; retrying at the next tick.");
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
    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<RunService>();
        return await runs.SweepExpiredLeasesAsync(cancellationToken);
    }

    /// <summary>One deadline-sweep tick in its own DI scope. Test seam (F-009).</summary>
    public async Task<RunService.DeadlineSweepResult> SweepDeadlinesOnceAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<RunService>();
        return await runs.SweepDeadlinesAsync(cancellationToken);
    }
}
