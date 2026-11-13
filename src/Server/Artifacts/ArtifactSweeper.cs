using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zenkins.Server.Artifacts;

/// <summary>
/// The artifact sweeper's tick result: how many abandoned uploads were marked <c>Incomplete</c> and how
/// many store directories of deleted runs were removed.
/// </summary>
/// <param name="IncompleteArtifacts">Pending rows past the window, now un-downloadable and diagnosed.</param>
/// <param name="RemovedDirectories">Orphaned run directories whose rows the database no longer has.</param>
public sealed record ArtifactSweepResult(int IncompleteArtifacts, int RemovedDirectories);

/// <summary>
/// The background half of the artifact feature (card F-016): it closes the two states an upload can leave
/// behind - a <c>Pending</c> row whose worker died (AC-7: no permanently Pending row, and nothing
/// partially uploaded ever downloadable) and a store directory whose run was deleted (the rows cascaded in
/// the database, the bytes must follow).
/// <para>
/// <b>Failure policy matches the other sweepers.</b> A tick that throws is logged and retried on the next
/// interval; nothing here ever deletes a <c>Ready</c> artifact of a live run, and no cleanup failure fails a
/// user's delete. Tests drive <see cref="SweepOnceAsync"/> instead of waiting on real time.
/// </para>
/// </summary>
public sealed class ArtifactSweeper(
    IServiceScopeFactory scopeFactory,
    ArtifactOptions options,
    ILogger<ArtifactSweeper> logger) : BackgroundService
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
                logger.LogError(exception, "Artifact sweep failed; retrying at the next tick.");
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
    public async Task<ArtifactSweepResult> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var artifacts = scope.ServiceProvider.GetRequiredService<ArtifactService>();
        var incomplete = await artifacts.SweepAbandonedUploadsAsync(cancellationToken);
        var removed = await artifacts.SweepOrphanedDirectoriesAsync(cancellationToken);
        return new ArtifactSweepResult(incomplete, removed);
    }
}
