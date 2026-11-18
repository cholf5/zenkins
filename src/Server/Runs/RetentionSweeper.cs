using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zenkins.Server.Runs;

/// <summary>
/// The background half of card F-038: the daily sweep over every project that has a policy, and the prompt
/// evaluation after a run reaches a terminal state (a project id arrives over <see cref="RunTerminalSignal"/>).
/// <para>
/// <b>Two clocks, one code path.</b> Both halves call the same <see cref="RetentionService"/> evaluation, so "the
/// next sweep will delete 12 builds" on the settings card and the sweep that runs later cannot disagree. The
/// terminal nudge only looks at the project whose run finished; the daily tick looks at everything, which is also
/// what retries a project that failed earlier.
/// </para>
/// <para>
/// <b>Failure policy matches the other sweepers.</b> A tick that throws is logged and retried at the next
/// interval; nothing here ever deletes a run it was not told to, and no retention failure fails a user's request
/// (the run's completion path never waits for this). Tests drive <see cref="SweepOnceAsync"/> and
/// <see cref="SweepProjectOnceAsync"/> instead of waiting on real time.
/// </para>
/// </summary>
public sealed class RetentionSweeper(
    IServiceScopeFactory scopeFactory,
    RetentionOptions options,
    RunTerminalSignal signal,
    ILogger<RetentionSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Whatever arrived while the last pass ran - plus anything the daily tick is due - is handled by
                // one pass: draining first keeps a burst of completions to a single evaluation per project.
                var nudged = signal.Drain();
                foreach (var projectId in nudged)
                {
                    await SweepProjectOnceAsync(projectId, stoppingToken);
                }

                var full = await signal.WaitAsync(options.SweepInterval, stoppingToken);
                if (full)
                {
                    continue;
                }

                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Retention sweep failed; retrying at the next tick.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, options.SweepInterval.TotalSeconds)), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>The daily half: every project with a policy, in its own DI scope. Test seam.</summary>
    public async Task<RetentionSweepResult> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
        try
        {
            return await retention.SweepAllAsync(DateTimeOffset.UtcNow, cancellationToken);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            logger.LogError(failure, "The retention sweep failed; the next tick retries it.");
            return RetentionSweepResult.None;
        }
    }

    /// <summary>The after-terminal half: one project, in its own DI scope. Test seam.</summary>
    public async Task<RetentionSweepResult> SweepProjectOnceAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();
        try
        {
            return await retention.SweepProjectAsync(projectId, DateTimeOffset.UtcNow, cancellationToken);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            logger.LogError(
                failure,
                "The retention evaluation after a terminal run failed; the daily sweep retries it. ProjectId={ProjectId}",
                projectId);
            return RetentionSweepResult.None;
        }
    }
}
