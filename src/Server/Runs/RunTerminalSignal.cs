using System.Threading.Channels;

namespace Zenkins.Server.Runs;

/// <summary>
/// The hand-off between "a run reached a terminal state" (the run paths) and "evaluate that project's retention
/// now" (<see cref="RetentionSweeper"/>), without doing retention work on the request that completed a run
/// (card F-038).
/// <para>
/// <b>Why a signal and not a call.</b> Deleting history is not part of completing a build: a slow or failing
/// retention pass must never slow a worker's <c>completed</c> report down or fail it. The completion paths only
/// drop a project id here (a non-blocking write); the sweeper drains it and does the work in its own scope, where
/// a failure is logged and retried on the next tick.
/// </para>
/// <para>
/// <b>Bounded, and lossy on purpose.</b> A full queue drops the id: the daily sweep still evaluates every project
/// with a policy, so a dropped nudge costs latency, never correctness - and a burst of a thousand completions
/// cannot make the process hold a thousand entries.
/// </para>
/// </summary>
public sealed class RunTerminalSignal
{
    private readonly Channel<Guid> _projects = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>Records that <paramref name="projectId"/> may owe a retention evaluation. Never blocks.</summary>
    public void Pulse(Guid projectId)
    {
        if (projectId != Guid.Empty)
        {
            _projects.Writer.TryWrite(projectId);
        }
    }

    /// <summary>Every project nudged since the last call, deduplicated.</summary>
    public IReadOnlyCollection<Guid> Drain()
    {
        var projects = new HashSet<Guid>();
        while (_projects.Reader.TryRead(out var projectId))
        {
            projects.Add(projectId);
        }

        return projects;
    }

    /// <summary>
    /// Waits until a nudge arrives or <paramref name="timeout"/> passes (whichever is first). The timeout is the
    /// daily sweep's clock; a nudge wakes the sweeper early. Returns <see langword="true"/> when a nudge arrived.
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var delaySource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout, delaySource.Token);
        var wait = _projects.Reader.WaitToReadAsync(cancellationToken).AsTask();
        var completed = await Task.WhenAny(wait, delay);
        delaySource.Cancel();
        if (completed != wait)
        {
            // The timeout won: observe the cancellation so a shutdown never leaves an unobserved task behind.
            try
            {
                await delay;
            }
            catch (OperationCanceledException)
            {
                // Expected: the linked source was just cancelled.
            }

            return false;
        }

        try
        {
            return await wait;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
