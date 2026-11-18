namespace Zenkins.Worker;

/// <summary>
/// Which runs this worker is executing right now (card F-038), so the work-directory sweep never removes a tree
/// out from under a running build.
/// <para>
/// <b>A set, not a count.</b> The sweep asks "is this run id active?", which is a membership question; a counter
/// would answer "some run is active" and could not tell the sweeper which directory to leave alone. The set is
/// also the honest source: a run is in it from the moment its claim is accepted until its report is sent (or
/// given up), which is exactly the window in which its directory is being written.
/// </para>
/// <para>
/// Thread-safe: the build loops run concurrently (<see cref="WorkerConfiguration.MaxConcurrency"/>) and the
/// sweeper runs beside them.
/// </para>
/// </summary>
public sealed class RunActivityRegistry
{
    private readonly HashSet<Guid> _active = [];
    private readonly Lock _gate = new();

    /// <summary>Records that this worker is now executing <paramref name="runId"/>.</summary>
    public void Begin(Guid runId)
    {
        lock (_gate)
        {
            _active.Add(runId);
        }
    }

    /// <summary>Records that this worker is no longer executing <paramref name="runId"/>.</summary>
    public void End(Guid runId)
    {
        lock (_gate)
        {
            _active.Remove(runId);
        }
    }

    /// <summary>Whether <paramref name="runId"/> is executing on this worker right now.</summary>
    public bool IsActive(Guid runId)
    {
        lock (_gate)
        {
            return _active.Contains(runId);
        }
    }

    /// <summary>How many runs are in flight (the sweeper's log line says so).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _active.Count;
            }
        }
    }
}
