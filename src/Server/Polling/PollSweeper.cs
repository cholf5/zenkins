using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Server.Polling;

/// <summary>What one poll tick did (the facts a test reads and a log line spells out).</summary>
public sealed record PollSweepResult(int ProjectsPolled = 0, int RunsQueued = 0, int Skipped = 0);

/// <summary>
/// Card F-025's background half: an interval pass over the projects that enabled polling, one project per
/// pass, one deduped probe each.
/// <para>
/// <b>Which projects are due.</b> Polling must be enabled and carry an interval; the wait is the declared
/// interval doubled per consecutive probe failure and capped (see <see cref="PollOptions.EffectiveInterval"/>),
/// so a remote that is down costs one probe per half hour rather than one per interval. A disabled project
/// stays in the selection on purpose: its poll is the thing that records <c>skipped:project-disabled</c>, and
/// that record is what makes the skip visible.
/// </para>
/// <para>
/// <b>Bounded and failure-isolated.</b> At most <see cref="PollOptions.MaxProjectsPerTick"/> projects per tick
/// (oldest poll first), and one project's git/auth/configuration problem is logged and skipped - it never
/// stops the tick. Tests drive <see cref="SweepOnceAsync"/> instead of waiting on real time.
/// </para>
/// </summary>
public sealed class PollSweeper(
    IServiceScopeFactory scopeFactory,
    PollOptions options,
    TimeProvider timeProvider,
    ILogger<PollSweeper> logger) : BackgroundService
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
                logger.LogError(exception, "SCM poll sweep failed; retrying at the next tick.");
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

    /// <summary>One tick: poll every due project, in oldest-poll-first order, and report what it did.</summary>
    public async Task<PollSweepResult> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        var due = await InSystemScopeAsync(
            async (context, systemScope) =>
            {
                using var scope = systemScope.BeginScope();
                var projects = await context.SystemQueryAsync<Project>(
                    project => project.PollingEnabled && project.PollIntervalMinutes != null,
                    cancellationToken);

                return projects
                    .Where(project => project.LastPollAt is null
                        || now - project.LastPollAt.Value
                            >= options.EffectiveInterval(project.PollIntervalMinutes, project.PollFailureStreak))
                    .OrderBy(project => project.LastPollAt ?? DateTimeOffset.MinValue)
                    .ThenBy(project => project.Id)
                    .Take(options.MaxProjectsPerTick)
                    .Select(project => new ProjectDue(project.Id, project.OrganizationId))
                    .ToArray();
            },
            cancellationToken);

        var polled = 0;
        var queued = 0;
        var skipped = 0;

        foreach (var project in due)
        {
            try
            {
                var result = await WithTenantAsync(
                    project.OrganizationId,
                    services => services.GetRequiredService<PollService>()
                        .PollProjectAsync(project.ProjectId, cancellationToken));

                polled++;
                queued += result.RunsQueued;
                if (result.Outcome.StartsWith("skipped:", StringComparison.Ordinal))
                {
                    skipped++;
                }
            }
            catch (Exception failure)
            {
                // One project's problem is not the pass's problem: it is recorded on that project by the
                // poll itself in every case the poll can see, and logged here for the ones it cannot.
                logger.LogWarning(
                    failure,
                    "SCM poll failed for one project. OrganizationId={OrganizationId} ProjectId={ProjectId}",
                    project.OrganizationId,
                    project.ProjectId);
            }
        }

        return new PollSweepResult(polled, queued, skipped);
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

    private async Task<T> WithTenantAsync<T>(Guid organizationId, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().OrganizationId = organizationId;
        return await action(scope.ServiceProvider);
    }

    private sealed record ProjectDue(Guid ProjectId, Guid OrganizationId);
}
