namespace Zenkins.Server.Runs;

using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;

/// <summary>
/// "Is a run of this project already queued or running?" - the question F-024's overlap policy and F-025's
/// poll both ask before they stack another run on a busy project.
/// <para>
/// <b>One query, not two.</b> The schedule reactor and the poll sweeper must agree on what "in flight" means,
/// otherwise "3 skipped" in a schedule log and "queued" on a poll card describe the same afternoon differently.
/// </para>
/// </summary>
public static class InFlightRuns
{
    /// <summary>
    /// The project's oldest in-flight run's number, or null when the project has none.
    /// </summary>
    /// <param name="includeQueued">
    /// <see langword="true"/> for a question about "is this project busy at all" (the schedule reactor: a
    /// queued run and a running run both mean another tick would only stack behind it), <see langword="false"/>
    /// for "is something actually executing" (the poll: a queued run is the queue cap's business, so polling a
    /// project whose runs are all queued must still build the head it observed).
    /// </param>
    public static async Task<int?> FindOldestAsync(
        ZenkinsDbContext context,
        Guid projectId,
        bool includeQueued = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Statuses are translated; the ordering is not (SQLite cannot order DateTimeOffset in SQL), so the
        // handful of active rows is read and ordered here.
        var active = await context.BuildRuns
            .AsNoTracking()
            .Where(run => run.ProjectId == projectId
                && (run.Status == BuildRunStatus.Running
                    || (includeQueued && run.Status == BuildRunStatus.Queued)))
            .Select(run => new { run.Number, run.CreatedAt })
            .ToListAsync(cancellationToken);

        if (active.Count == 0)
        {
            return null;
        }

        return active
            .OrderBy(run => run.CreatedAt)
            .ThenBy(run => run.Number)
            .First()
            .Number;
    }
}
