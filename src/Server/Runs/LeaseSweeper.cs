using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zenkins.Server.Runs;

/// <summary>
/// The scheduler's background half (card F-006): it assigns nothing by itself - workers pull via
/// <c>POST /api/workers/claim</c> - its only job is expiring leases. On every tick it returns
/// claimed-but-never-accepted runs whose lease window passed to <c>Queued</c>; a run that was
/// accepted (no lease anymore) is unreachable for the statement, no matter how late a tick fires.
/// <para>
/// Failures are logged and the loop continues: a transient database hiccup delays the requeue by
/// one interval, it never kills the host. Tests drive <see cref="RunService.SweepExpiredLeasesAsync"/>
/// directly instead of waiting for real time.
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
}
