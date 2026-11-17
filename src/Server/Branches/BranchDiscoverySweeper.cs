using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Probes;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Projects;

namespace Zenkins.Server.Branches;

/// <summary>What one tick did (the facts a test reads and a log line spells out).</summary>
public sealed record BranchSweepResult(
    int Abandoned = 0,
    int Retried = 0,
    int ProjectsDiscovered = 0);

/// <summary>
/// Card F-023's background half: an interval pass over multibranch projects, and the retry clock of a
/// branch observation that produced no verdict.
/// <para>
/// <b>One probe per repository per interval.</b> The discovery pass asks I-005 for one project's heads -
/// never one probe per branch - and its answer registers branches, asks for tree reads and marks
/// successful-refs absences orphaned. A failed probe changes nothing (the "a failure is not an absence"
/// rule) and never stalls the pass: the next project is still visited.
/// </para>
/// <para>
/// <b>Bounded retries.</b> A pending observation nobody answered is abandoned after
/// <see cref="BranchDiscoveryOptions.ObservationStaleSeconds"/> and re-asked after
/// <see cref="BranchDiscoveryOptions.RetryDelay"/> - doubling per attempt, capped. The sweeper never
/// deletes a branch, a run or a log line: its only writes are retry clocks and the orphan marker a
/// <b>successful</b> refs answer justifies.
/// </para>
/// </summary>
public sealed class BranchDiscoverySweeper(
    IServiceScopeFactory scopeFactory,
    DefinitionValidationRegistry observations,
    BranchDiscoveryOptions options,
    TimeProvider timeProvider,
    ILogger<BranchDiscoverySweeper> logger) : BackgroundService
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
                logger.LogError(exception, "Branch discovery sweep failed; retrying at the next tick.");
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

    /// <summary>
    /// One tick: abandon stale in-flight observations, re-request pending ones that are due, then run the
    /// discovery pass for the projects whose interval elapsed. Every step is bounded and every failure is
    /// logged and skipped - one broken project must not stop the others (the card's "a failure must not
    /// stall discovery" rule).
    /// </summary>
    public async Task<BranchSweepResult> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var abandoned = await AbandonStaleObservationsAsync(now, cancellationToken);
        var retried = await RetryPendingObservationsAsync(now, cancellationToken);
        var discovered = await DiscoverProjectsAsync(now, cancellationToken);
        return new BranchSweepResult(abandoned, retried, discovered);
    }

    /// <summary>
    /// Settles observations no Worker answered (or claimed and never finished) after the stale window, so
    /// the in-memory queue cannot hold a task forever, and moves the branch's retry clock so the next
    /// request waits the backoff. This is not a delete path: the branch row, its runs and its history are
    /// untouched.
    /// </summary>
    private async Task<int> AbandonStaleObservationsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stale = observations.StaleObservations(
            now, TimeSpan.FromSeconds(options.ObservationStaleSeconds));
        var abandoned = 0;
        foreach (var entry in stale)
        {
            var result = observations.TryAbandon(entry.Id, now, force: true);
            if (!result.SettledByThisCall)
            {
                continue;
            }

            abandoned++;
            if (entry.Branch is not { Length: > 0 } branch || entry.Commit is not { Length: > 0 } commit)
            {
                continue;
            }

            try
            {
                await WithTenantAsync(entry.OrganizationId, async services =>
                {
                    await using var context = await services
                        .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                        .CreateDbContextAsync(cancellationToken);
                    await BranchService.NoteObservationAttemptAsync(
                        context, entry.ProjectId, branch, commit, now, cancellationToken);
                });
            }
            catch (Exception failure)
            {
                logger.LogWarning(
                    failure,
                    "Stale branch observation could not move its retry clock. ProjectId={ProjectId} Branch={Branch}",
                    entry.ProjectId,
                    branch);
            }
        }

        return abandoned;
    }

    /// <summary>
    /// Re-requests the pending observations whose backoff elapsed. The branch row is the durable queue -
    /// one query across tenants, one bounded enqueue per due row - and the registry's in-flight dedupe
    /// means a request that is already queued is joined, not duplicated.
    /// </summary>
    private async Task<int> RetryPendingObservationsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var candidates = await InSystemScopeAsync(
            async (context, systemScope) =>
            {
                using var scope = systemScope.BeginScope();
                var rows = await context.SystemQueryAsync<BranchJob>(
                    branch => branch.PendingCommit != null, cancellationToken);
                return rows
                    .Where(branch => branch.PendingSince is null
                        || now - branch.PendingSince.Value >= options.RetryDelay(branch.ObservationAttempts))
                    .OrderBy(branch => branch.PendingSince ?? DateTimeOffset.MinValue)
                    .Take(options.MaxProjectsPerTick * 10)
                    .ToArray();
            },
            cancellationToken);

        var retried = 0;
        foreach (var row in candidates)
        {
            try
            {
                var (enqueued, created) = await WithTenantAsync(
                    row.OrganizationId,
                    services => services.GetRequiredService<DefinitionValidationService>()
                        .EnqueueBranchObservationAsync(
                            row.ProjectId,
                            row.Branch,
                            row.PendingCommit!,
                            row.PendingTags,
                            cancellationToken));

                if (!enqueued)
                {
                    continue;
                }

                retried++;
                if (created)
                {
                    // A new task went out: move the retry clock so a no-worker answer waits the backoff
                    // instead of re-asking on the next tick.
                    await WithTenantAsync(row.OrganizationId, async services =>
                    {
                        await using var context = await services
                            .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                            .CreateDbContextAsync(cancellationToken);
                        await BranchService.NoteObservationAttemptAsync(
                            context, row.ProjectId, row.Branch, row.PendingCommit!, now, cancellationToken);
                    });
                }
            }
            catch (Exception failure)
            {
                logger.LogWarning(
                    failure,
                    "Pending branch observation could not be requested. ProjectId={ProjectId} Branch={Branch}",
                    row.ProjectId,
                    row.Branch);
            }
        }

        return retried;
    }

    /// <summary>
    /// The interval pass: the multibranch projects whose last probe is older than the interval, capped per
    /// tick, each with one probe. Failures are logged per project and never stop the loop.
    /// </summary>
    private async Task<int> DiscoverProjectsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(options.DiscoveryIntervalSeconds);
        var due = await InSystemScopeAsync(
            async (context, systemScope) =>
            {
                using var scope = systemScope.BeginScope();
                var projects = await context.SystemQueryAsync<Project>(
                    project => !project.Disabled
                        && project.BranchDiscovery == BranchDiscoveryModes.Branches,
                    cancellationToken);
                return projects
                    .Where(project => project.LastProbeAt is null || now - project.LastProbeAt.Value >= interval)
                    .OrderBy(project => project.LastProbeAt ?? DateTimeOffset.MinValue)
                    .ThenBy(project => project.Id)
                    .Take(options.MaxProjectsPerTick)
                    .Select(project => new ProjectDue(project.Id, project.OrganizationId))
                    .ToArray();
            },
            cancellationToken);

        var discovered = 0;
        foreach (var project in due)
        {
            try
            {
                await WithTenantAsync(project.OrganizationId, async services =>
                {
                    await services.GetRequiredService<BranchDiscoveryService>()
                        .DiscoverAsync(project.ProjectId, cancellationToken);
                });
                discovered++;
            }
            catch (Exception failure)
            {
                // One project's git/auth/configuration problem is not the pass's problem: the outcome
                // this pass could not record is logged and the next project is still visited.
                logger.LogWarning(
                    failure,
                    "Branch discovery failed for one project. OrganizationId={OrganizationId} ProjectId={ProjectId}",
                    project.OrganizationId,
                    project.ProjectId);
            }
        }

        return discovered;
    }

    private async Task<T> InSystemScopeAsync<T>(
        Func<ZenkinsDbContext, ISystemDataScope, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var systemScope = services.GetRequiredService<ISystemDataScope>();
        await using var context = await services
            .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
            .CreateDbContextAsync(cancellationToken);
        return await action(context, systemScope);
    }

    private async Task WithTenantAsync(Guid organizationId, Func<IServiceProvider, Task> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().OrganizationId = organizationId;
        await action(scope.ServiceProvider);
    }

    private async Task<T> WithTenantAsync<T>(Guid organizationId, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().OrganizationId = organizationId;
        return await action(scope.ServiceProvider);
    }

    private sealed record ProjectDue(Guid ProjectId, Guid OrganizationId);
}
