using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Dashboard;

/// <summary>
/// One run as the dashboard shows it (card D-002). A projection record, never the tracked entity:
/// the contexts this service creates are disposed before the page renders. Shared fields with the
/// runs list's <see cref="Runs.RunView"/> (status, branch, trigger, timings, failure reason) are
/// rendered through the same shared display bits, so one run never reads differently on two pages.
/// </summary>
public sealed record DashboardRunView(
    Guid RunId,
    Guid ProjectId,
    string ProjectName,
    string Status,
    string TriggerType,
    string GitBranch,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int? ExitCode,
    string? FailureReason,
    DateTimeOffset CreatedAt,

    /// <summary>
    /// Card F-028: whether the run's project is disabled now, so the dashboard's recent list shows the
    /// same fact the project page and the run page do. A run that is still in flight when its project is
    /// disabled keeps running; the badge is how the operator sees that.
    /// </summary>
    bool ProjectDisabled = false)
{
    /// <summary>
    /// The instant this run is ordered and windowed by: when it started, or - for a run that never
    /// started (queued, or canceled while queued) - when it was created. This is exactly the card's
    /// "last 20 runs by <c>StartedAt ?? CreatedAt</c>" definition; nothing else on the dashboard
    /// measures recency.
    /// </summary>
    public DateTimeOffset Recency => StartedAt ?? CreatedAt;

    /// <summary>Whether the run counts as a failure: a terminal <c>Failed</c> or <c>Lost</c> run.
    /// <c>Canceled</c> is a user decision, not a failure - it stays visible in the recent list but
    /// never in the failure count or the failures section.</summary>
    public bool IsFailure => Status is BuildRunStatus.Failed or BuildRunStatus.Lost;
}

/// <summary>
/// The whole dashboard for the active organization in one read model (card D-002): queue and
/// running counts, the worker summary, the 7-day failure count and the capped recent-run list.
/// Counts, not charts; the runs list is the place to browse history.
/// </summary>
/// <param name="ProjectCount">Projects of the active organization (drives the "no projects yet"
/// empty state, which must be distinguishable from "projects exist but no runs").</param>
/// <param name="QueuedCount">Rows with status <c>Queued</c> (claimed-but-queued leases included:
/// the queue is what the operator is waiting on).</param>
/// <param name="RunningCount">Rows with status <c>Running</c>.</param>
/// <param name="WorkersOnline">Workers whose stored <see cref="Entities.Worker.Status"/> is
/// <c>Online</c>. Read, never recomputed: the offline sweeper (F-010) owns liveness, so the
/// dashboard and the run lifecycle can never disagree about a worker.</param>
/// <param name="WorkersOffline">Workers whose stored status is <c>Offline</c> - again the value the
/// sweeper wrote, with no independent heartbeat arithmetic on this page.</param>
/// <param name="WorkersBusy">Workers currently executing at least one <c>Running</c> run. In steady
/// state these are Online workers (the sweeper marks a lost worker's runs <c>Lost</c> when it flips
/// them Offline), so busy is a subset of online; a Running run whose sweep has not fired yet still
/// counts as busy because something is genuinely executing.</param>
/// <param name="FailuresInWindow">Failed/Lost runs whose recency instant falls in the 7-day window,
/// across the organization's whole history - the count is the honest size of the window, the
/// failures section below only lists the recent ones.</param>
/// <param name="WindowStart">Start of the failure window (<see cref="DashboardQuery.FailureWindow"/>
/// before <see cref="GeneratedAt"/>), so the page can say which window the number covers.</param>
/// <param name="GeneratedAt">The clock instant the counts were taken at; the elapsed-time display
/// keeps ticking from here in an interactive circuit without re-reading the database.</param>
/// <param name="RecentRuns">The last 20 runs across the organization's projects by
/// <see cref="DashboardRunView.Recency"/>, newest first (id as the deterministic tie-break).</param>
/// <param name="DisabledProjectCount">Card F-028: projects whose triggers are refused right now. Shown
/// as one line, because "a whole project stopped building" is exactly the fact this page is opened to
/// find; zero disables the line.</param>
public sealed record DashboardSummary(
    int ProjectCount,
    int QueuedCount,
    int RunningCount,
    int WorkersOnline,
    int WorkersOffline,
    int WorkersBusy,
    int FailuresInWindow,
    DateTimeOffset WindowStart,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<DashboardRunView> RecentRuns,
    int DisabledProjectCount = 0)
{
    /// <summary>Total registered workers of the organization (zero drives the "no workers
    /// connected" empty state, which is not the same as "all workers offline").</summary>
    public int WorkerTotal => WorkersOnline + WorkersOffline;

    /// <summary>
    /// The recent failures the failures section lists: the terminal <c>Failed</c>/<c>Lost</c> runs
    /// among the recent 20 that also fall inside the window. With more than 20 runs of history the
    /// two conditions coincide (recent implies in-window only up to the cap, so the window is
    /// checked explicitly); with a short history an old failure can still be among the 20 recent
    /// runs - it stays visible there as a run, but it is neither counted nor listed as a failure.
    /// </summary>
    public IReadOnlyList<DashboardRunView> RecentFailures =>
        [.. RecentRuns.Where(run => run.IsFailure && run.Recency >= WindowStart)];

    /// <summary>Whether the organization owns at least one project.</summary>
    public bool HasProjects => ProjectCount > 0;

    /// <summary>Whether the organization has registered any worker at all (drives the "no workers
    /// connected" empty state, distinct from "all workers offline").</summary>
    public bool HasWorkers => WorkerTotal > 0;
}

/// <summary>
/// The dashboard's single bounded read model (card D-002): everything <c>/</> shows for the active
/// organization, read in a fixed number of database commands regardless of how much build history
/// exists.
/// <para>
/// <b>Tenant boundary.</b> The organization comes from <see cref="ITenantContext"/> only; every
/// query runs through the context's global query filters, so the page can never mix in another
/// organization's rows. With no organization selected the summary is <see langword="null"/> (the
/// page shows its no-organization state instead of numbers).
/// </para>
/// <para>
/// <b>Why the recency ordering happens in memory.</b> The SQLite provider cannot order or compare
/// <see cref="DateTimeOffset"/> columns in SQL (the same constraint the claim endpoint and the lease
/// sweeper already document), and the model must stay provider-neutral. The scan therefore reads one
/// slim projection per run row and everything else is bounded: the detail query re-reads exactly the
/// 20 recent ids (with the project name), and the aggregates are computed in the same pass. The
/// command count is fixed (at most four: worker aggregate, run scan, recent detail, project count);
/// only the scan's row count follows history, which is why its projection is deliberately minimal.
/// </para>
/// </summary>
public sealed class DashboardQuery(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    ITenantContext tenantContext,
    ILogger<DashboardQuery> logger)
{
    /// <summary>How many recent runs the dashboard shows (card decision: no pagination - the runs
    /// list is where browsing happens). Recorded here so tests and the page share one number.</summary>
    public const int RecentRunCap = 20;

    /// <summary>How far back the failure window reaches (card decision).</summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// Reads the whole dashboard for the active organization. <see langword="null"/> when no
    /// organization is selected.
    /// </summary>
    public async Task<DashboardSummary?> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        if (tenantContext.OrganizationId is not { } organizationId)
        {
            return null;
        }

        var generatedAt = DateTimeOffset.UtcNow;
        var windowStart = generatedAt - FailureWindow;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // 1/4 - worker summary, straight from the statuses the offline sweeper wrote. One grouped
        // aggregate; no group means no workers at all (empty sets answer zeros, never exceptions).
        var workerCounts = await context.Workers
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Online = group.Count(worker => worker.Status == WorkerStatus.Online),
                Offline = group.Count(worker => worker.Status == WorkerStatus.Offline),
            })
            .FirstOrDefaultAsync(cancellationToken);

        // 2/4 - the slim scan every number below derives from. One command, minimal columns.
        var scan = await context.BuildRuns
            .Select(run => new
            {
                run.Id,
                run.Status,
                run.WorkerId,
                run.StartedAt,
                run.EndedAt,
                run.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var rows = scan
            .Select(run => new RunScanRow(
                run.Id,
                run.Status,
                run.WorkerId,
                run.StartedAt,
                run.EndedAt,
                run.CreatedAt))
            .ToArray();

        var queuedCount = rows.Count(run => run.Status == BuildRunStatus.Queued);
        var runningCount = rows.Count(run => run.Status == BuildRunStatus.Running);
        var workersBusy = rows
            .Where(run => run.Status == BuildRunStatus.Running && run.WorkerId is not null)
            .Select(run => run.WorkerId!.Value)
            .Distinct()
            .Count();
        var failuresInWindow = rows.Count(run =>
            (run.Status == BuildRunStatus.Failed || run.Status == BuildRunStatus.Lost)
            && (run.StartedAt ?? run.CreatedAt) >= windowStart);

        var recentIds = rows
            .OrderByDescending(run => run.Recency)
            .ThenBy(run => run.Id)
            .Take(RecentRunCap)
            .Select(run => run.Id)
            .ToArray();

        // 3/4 - the recent page, with the project name the rows display. Exactly RecentRunCap rows
        // (fewer on a short history), never one command per row.
        DashboardRunView[] recentRuns = recentIds.Length == 0
            ? []
            : (await context.BuildRuns
                .Where(run => recentIds.Contains(run.Id))
                .Select(run => new
                {
                    run.Id,
                    run.ProjectId,
                    ProjectName = run.Project.Name,
                    run.Project.Disabled,
                    run.Status,
                    run.TriggerType,
                    run.GitBranch,
                    run.StartedAt,
                    run.EndedAt,
                    run.ExitCode,
                    run.FailureReason,
                    run.CreatedAt,
                })
                .ToListAsync(cancellationToken))
                .Select(run => new DashboardRunView(
                    run.Id,
                    run.ProjectId,
                    run.ProjectName,
                    run.Status,
                    run.TriggerType,
                    run.GitBranch,
                    run.StartedAt,
                    run.EndedAt,
                    run.ExitCode,
                    run.FailureReason,
                    run.CreatedAt,
                    run.Disabled))
                .OrderByDescending(run => run.Recency)
                .ThenBy(run => run.RunId)
                .ToArray();

        // 4/4 - the "no projects yet" empty state has to be its own fact: an organization with
        // projects but no runs and one with nothing at all must render differently. F-028 adds the
        // disabled count (a project whose triggers are refused shows no runs, so without it the
        // dashboard could look idle for the wrong reason): one grouped aggregate over the project table
        // answers both numbers in a single round trip - at most two rows, and never the table itself.
        var projectCounts = await context.Projects
            .GroupBy(project => project.Disabled)
            .Select(group => new { Disabled = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var projectCount = projectCounts.Sum(row => row.Count);
        var disabledProjectCount = projectCounts.Where(row => row.Disabled).Sum(row => row.Count);

        var summary = new DashboardSummary(
            ProjectCount: projectCount,
            QueuedCount: queuedCount,
            RunningCount: runningCount,
            WorkersOnline: workerCounts?.Online ?? 0,
            WorkersOffline: workerCounts?.Offline ?? 0,
            WorkersBusy: workersBusy,
            FailuresInWindow: failuresInWindow,
            WindowStart: windowStart,
            GeneratedAt: generatedAt,
            RecentRuns: recentRuns,
            DisabledProjectCount: disabledProjectCount);

        logger.LogInformation(
            "Dashboard summary read. OrganizationId={OrganizationId} Projects={ProjectCount} DisabledProjects={DisabledProjects} Queued={Queued} Running={Running} Recent={Recent} FailuresInWindow={Failures}",
            organizationId,
            projectCount,
            disabledProjectCount,
            queuedCount,
            runningCount,
            recentRuns.Length,
            failuresInWindow);
        return summary;
    }

    /// <summary>The slim per-run row the recency pass works on (see the class remark: SQLite cannot
    /// order DateTimeOffset in SQL, so the cap is applied in memory over this minimal projection).</summary>
    private sealed record RunScanRow(
        Guid Id,
        string Status,
        Guid? WorkerId,
        DateTimeOffset? StartedAt,
        DateTimeOffset? EndedAt,
        DateTimeOffset CreatedAt)
    {
        /// <summary>The card's recency instant: when it ran, or when it was queued.</summary>
        public DateTimeOffset Recency => StartedAt ?? CreatedAt;
    }
}
