namespace Zenkins.Worker;

/// <summary>
/// One cancel request as it reaches the run loops (card F-009). <see cref="Reason"/> is the
/// server's recorded cancel reason (<c>canceled-by-user …</c> or <c>timeout</c>) - it selects the
/// outcome the worker reports through the canceled endpoint (a <c>timeout</c> request maps the
/// run to Failed server-side, everything else to Canceled).
/// </summary>
public sealed record RunCancelRequest(Guid RunId, string? Reason);

/// <summary>
/// The worker's in-process cancel-signal bus (card F-009): both signal channels feed it and every
/// run loop subscribes to it. The primary channel is the worker's existing upstream SignalR
/// connection (<see cref="LogStreamClient"/> registers a <c>cancelRun</c> handler); the fallback
/// channel is the heartbeat response's <c>cancelRequestedRuns</c> list
/// (<see cref="WorkerRunner"/>'s heartbeat loop raises each entry here) - so a worker whose SignalR
/// connection is down still observes a cancel within one heartbeat period.
/// <para>
/// The bus is thread-safe: signals arrive on SignalR/heartbeat-loop threads, handlers run on the
/// raising thread, and a handler that matches its own run id completes that loop's per-run
/// cancellation gate. Unknown run ids (other loops' runs, already-finished runs) are ignored by
/// the handler, not by the bus.
/// </para>
/// </summary>
public sealed class RunCancelSignal
{
    private readonly object _gate = new();
    private readonly List<Action<RunCancelRequest>> _handlers = [];

    /// <summary>Registers a handler; returns a disposable that unregisters it.</summary>
    public IDisposable Subscribe(Action<RunCancelRequest> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handlers.Add(handler);
        }

        return new Subscription(this, handler);
    }

    /// <summary>Removes a previously registered handler.</summary>
    public void Unsubscribe(Action<RunCancelRequest> handler)
    {
        lock (_gate)
        {
            _handlers.Remove(handler);
        }
    }

    /// <summary>Raises one cancel request to every registered handler, synchronously.</summary>
    public void Raise(RunCancelRequest request)
    {
        Action<RunCancelRequest>[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _handlers];
        }

        foreach (var handler in snapshot)
        {
            handler(request);
        }
    }

    private sealed class Subscription(RunCancelSignal signal, Action<RunCancelRequest> handler) : IDisposable
    {
        public void Dispose() => signal.Unsubscribe(handler);
    }
}
