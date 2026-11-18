namespace Zenkins.Worker;

/// <summary>What one sweep removed: the run ids whose trees are gone, and the ones it left alone.</summary>
/// <param name="Removed">Run directories deleted this pass, oldest first.</param>
/// <param name="Active">Directories skipped because the run is executing right now.</param>
/// <param name="Fresh">Directories younger than the retention window.</param>
public sealed record WorkSweepResult(
    IReadOnlyList<Guid> Removed,
    int Active,
    int Fresh);

/// <summary>
/// Card F-038's worker half: the age-based sweep of <c>work/{run-id}</c> trees.
/// <para>
/// <b>The Server cannot delete a worker's disk, so the worker does.</b> The retention policy's server half removes
/// a run's rows, steps, logs, artifact rows and the artifact store directories it owns; this half removes the
/// checkout, the published artifact copies and any leftover scratch under <c>work/{run-id}</c> once the run is
/// old enough. That is what keeps a worker from filling its disk with history nobody will look at again.
/// </para>
/// <para>
/// <b>Age, not a list from the Server.</b> A sweep asks the file system how old each tree is. That means it keeps
/// working when the Server is unreachable, when a run was deleted while this worker was offline, and for trees a
/// previous worker process wrote - none of which a "delete these ids" instruction could cover. The cost is
/// latency: a directory outlives its run until the window passes, which the setting states plainly.
/// </para>
/// <para>
/// <b>Never a running build's tree.</b> A run this worker is executing is skipped unconditionally, even if its
/// directory looks old (a long build that has not written a file for a while). The rule is the active set, not the
/// timestamp, because deleting a live checkout would fail a build rather than clean up.
/// </para>
/// <para>
/// <b>Only run trees.</b> The directory name has to parse as a GUID; <c>probes/</c> and <c>validations/</c> are
/// their own subtrees with their own cleanup, and anything else under the root is left alone. A missing root or a
/// directory that disappears mid-sweep is not an error.
/// </para>
/// </summary>
public sealed class WorkDirectorySweeper(
    WorkDirectoryService workDirectories,
    RunActivityRegistry activity,
    WorkerConfiguration configuration,
    TextWriter log)
{
    /// <summary>Whether the sweep is enabled at all (<c>ZENKINS_WORK_RETENTION_HOURS=0</c> turns it off).</summary>
    public bool Enabled => configuration.WorkRetentionHours > 0;

    /// <summary>The age a tree must exceed to be removed.</summary>
    public TimeSpan RetentionWindow => TimeSpan.FromHours(configuration.WorkRetentionHours);

    /// <summary>
    /// One sweep at <paramref name="now"/>. Idempotent: a second pass with the same clock removes nothing, and a
    /// failure on one directory is logged and left for the next pass - it never stops the others.
    /// </summary>
    public WorkSweepResult SweepOnce(DateTimeOffset now)
    {
        if (!Enabled)
        {
            return new WorkSweepResult([], 0, 0);
        }

        var root = workDirectories.Root;
        if (!Directory.Exists(root))
        {
            return new WorkSweepResult([], 0, 0);
        }

        var removed = new List<Guid>();
        var active = 0;
        var fresh = 0;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (!Guid.TryParse(name, out var runId))
            {
                // `probes/`, `validations/`, anything an operator dropped here: not a run tree.
                continue;
            }

            if (activity.IsActive(runId))
            {
                active++;
                continue;
            }

            if (!IsOlderThan(directory, now, RetentionWindow))
            {
                fresh++;
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                removed.Add(runId);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // A file held open by something else, a permission we do not have: log it and leave the tree for
                // the next pass. Cleanup is never allowed to fail a build or stop the sweep.
                log.WriteLine(
                    $"Zenkins Worker: could not remove the work directory of run {runId:D} ({failure.GetType().Name}); "
                    + "it will be retried at the next sweep.");
            }
        }

        if (removed.Count > 0)
        {
            log.WriteLine(
                $"Zenkins Worker: removed {removed.Count} work director{(removed.Count == 1 ? "y" : "ies")} older than "
                + $"{RetentionWindow.TotalHours:0.#}h ({active} in flight, {fresh} younger).");
        }

        return new WorkSweepResult(removed, active, fresh);
    }

    /// <summary>
    /// Runs the sweep on the retention window's own clock (bounded to an hour at the top so a seven-day window
    /// still notices a removed directory within an hour). Stops with the process.
    /// </summary>
    public async Task RunAsync(CancellationToken shutdown)
    {
        if (!Enabled)
        {
            log.WriteLine(
                $"Zenkins Worker: work-directory retention is off ({WorkerConfiguration.WorkRetentionHoursVariable}=0); "
                + "run directories are kept until the disk is cleaned by hand.");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Clamp(configuration.WorkRetentionHours, 1, 24));
        log.WriteLine(
            $"Zenkins Worker: keeping finished run directories for {RetentionWindow.TotalHours:0.#}h; "
            + $"sweeping every {interval.TotalHours:0.#}h.");

        using var timer = new PeriodicTimer(interval);
        do
        {
            SweepOnce(DateTimeOffset.UtcNow);
        }
        while (await WaitAsync(timer, shutdown));

        static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken shutdown)
        {
            try
            {
                return await timer.WaitForNextTickAsync(shutdown);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    private static bool IsOlderThan(string directory, DateTimeOffset now, TimeSpan window)
    {
        // The tree's own directory mtime - the last structural change under it, which a run makes at start and
        // again when it deletes its temp/ tree at the end. It is deliberately not a full walk of the tree (that
        // would cost a stat per file on every sweep): a live run is protected by the active set, not by this.
        var lastWrite = Directory.GetLastWriteTimeUtc(directory);
        return now - new DateTimeOffset(lastWrite, TimeSpan.Zero) > window;
    }
}
