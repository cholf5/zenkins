using Microsoft.AspNetCore.SignalR;
using Zenkins.Shared.LogStreaming;

namespace Zenkins.Worker;

/// <summary>
/// One run's bounded unsent-line buffer (card F-008). The process' line callbacks feed
/// <see cref="Emit"/>; a single drain task (<see cref="DrainAsync"/>) sends flush-sized batches to
/// the server over the <see cref="LogStreamClient"/> until the run's input is closed and the
/// buffer is empty.
/// <para>
/// <b>Sequences.</b> Every line gets its per-run monotonic <c>Seq</c> at emit time, starting at 1
/// - assigned in <see cref="Emit"/> under the buffer lock (one reader thread per stream), so
/// reconnects never renumber and the server's (RunId, Seq) primary key can absorb any duplicate
/// delivery.
/// </para>
/// <para>
/// <b>Bounded memory, honest loss.</b> The buffer holds at most <c>capacity</c> <em>unsent</em>
/// lines; beyond that the OLDEST unsent line is dropped and counted. The loss is reported by a
/// synthetic marker line, synthesized on the next send after drops occurred, carrying the next
/// sequence number. Drops are accounted by where they happened (review fix F4): while the
/// connection was down (the reconnect gap the card describes) the marker reads
/// <c>&lt;N lines dropped while reconnecting&gt;</c>; while the connection was up but the server
/// was persisting slower than the child produced (backpressure) it reads
/// <c>&lt;N lines dropped under backpressure&gt;</c>. Without a connectivity probe (tests) every
/// drop counts as a reconnect gap. The marker never displaces a buffered line - every real line
/// survives to be sent, and the marker says exactly how many were not.
/// </para>
/// <para>
/// <b>Duplicate delivery.</b> A send that times out may still have been processed by the server;
/// the batch returns to the front of the buffer and is re-sent after the reconnect. The overlap
/// is absorbed server-side - at-least-once on the wire, exactly-once in the database.
/// </para>
/// <para>
/// <b>Definitive rejections.</b> A server answer carrying the <see cref="LogStreamRejection"/>
/// prefix (unknown or foreign run, run assigned elsewhere, dead token) can never succeed on
/// retry: the batch - and everything still buffered behind it - is dropped, one clear stderr line
/// records the loss, and the drain ends. Every other failure (transport, connection closed, a
/// non-hub server fault arriving as SignalR's generic "unexpected error" completion) keeps the
/// buffered lines and waits for the reconnect.
/// </para>
/// </summary>
public sealed class RunLogBuffer
{
    /// <summary>The reconnect-gap marker's exact text (the card's honest marker).</summary>
    public const string DroppedMarkerFormat = "<{0} lines dropped while reconnecting>";

    /// <summary>The backpressure marker's exact text: drops that happened while the connection
    /// was up and the server was the bottleneck (review fix F4).</summary>
    public const string BackpressureMarkerFormat = "<{0} lines dropped under backpressure>";

    /// <summary>Lines per send: matches the server's default flush size, one batch per flush.</summary>
    public const int SendBatchSize = 100;

    /// <summary>Retry delay after a send failed while the client still believes it is connected.</summary>
    private static readonly TimeSpan SendRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly List<LogLineMessage> _pending = [];
    private readonly SemaphoreSlim _lineArrived = new(0, int.MaxValue);
    private readonly Guid _runId;
    private readonly int _capacity;
    private readonly Func<bool>? isConnected;
    private readonly TextWriter errorLog;
    private long _nextSeq = 1;
    private int _droppedWhileDisconnected;
    private int _droppedUnderBackpressure;
    private bool _inputClosed;

    public RunLogBuffer(Guid runId, int capacity, TextWriter errorLog, Func<bool>? isConnected = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _runId = runId;
        _capacity = capacity;
        this.errorLog = errorLog;
        this.isConnected = isConnected;
    }

    /// <summary>How many unsent lines are currently held.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>How many lines were dropped while the connection was down, since the last
    /// reconnecting marker. Without a connectivity probe every drop is accounted here (review
    /// fix F4: the card's reconnect-gap marker stays the default).</summary>
    public int DroppedCount
    {
        get
        {
            lock (_gate)
            {
                return _droppedWhileDisconnected;
            }
        }
    }

    /// <summary>How many lines were dropped while the connection was up and the server was the
    /// bottleneck, since the last backpressure marker.</summary>
    public int DroppedUnderBackpressureCount
    {
        get
        {
            lock (_gate)
            {
                return _droppedUnderBackpressure;
            }
        }
    }

    /// <summary>The highest sequence number assigned so far (0 when nothing was emitted).</summary>
    public long LastSeq
    {
        get
        {
            lock (_gate)
            {
                return _nextSeq - 1;
            }
        }
    }

    /// <summary>
    /// Assigns the line its sequence and buffers it; called from the process' output reader
    /// threads (one per stream - the lock makes the sequence assignment unique and the buffer
    /// consistent). Overflow drops the oldest unsent line, honestly counted.
    /// </summary>
    /// <param name="stepId">
    /// The step the line belongs to (card F-015), or <see langword="null"/> for the definition phase and
    /// the lines between steps; the server persists it so the run page can group the log per step.
    /// </param>
    public void Emit(string stream, string message, Guid? stepId = null)
    {
        bool wasEmpty;
        lock (_gate)
        {
            wasEmpty = _pending.Count == 0;
            _pending.Add(new LogLineMessage(_nextSeq++, stream, message, DateTimeOffset.UtcNow, stepId));
            while (_pending.Count > _capacity)
            {
                _pending.RemoveAt(0);

                // The probe runs under the buffer lock; it only reads the client's connection
                // flag, which never takes the buffer lock (no lock-ordering cycle).
                if (isConnected?.Invoke() == true)
                {
                    _droppedUnderBackpressure++;
                }
                else
                {
                    _droppedWhileDisconnected++;
                }
            }
        }

        if (wasEmpty)
        {
            _lineArrived.Release();
        }
    }

    /// <summary>Signals that the run's process has ended: no more lines will be emitted.</summary>
    public void CompleteInput()
    {
        lock (_gate)
        {
            _inputClosed = true;
            if (_pending.Count == 0)
            {
                _lineArrived.Release();
            }
        }
    }

    /// <summary>
    /// Drains the buffer until the input is closed and everything buffered (plus the loss marker)
    /// was handed to the server, or shutdown arrives. Waits on the connection while it is down -
    /// reconnects resume with the same sequence numbers, so the drain continues where it stopped.
    /// </summary>
    public async Task DrainAsync(ILogStreamSink client, CancellationToken shutdown)
    {
        while (true)
        {
            List<LogLineMessage>? batch = null;
            var drained = false;
            lock (_gate)
            {
                if (_droppedWhileDisconnected > 0)
                {
                    SynthesizeMarkerLocked(DroppedMarkerFormat, _droppedWhileDisconnected);
                    _droppedWhileDisconnected = 0;
                }

                if (_droppedUnderBackpressure > 0)
                {
                    SynthesizeMarkerLocked(BackpressureMarkerFormat, _droppedUnderBackpressure);
                    _droppedUnderBackpressure = 0;
                }

                if (_pending.Count > 0)
                {
                    if (client.IsConnected)
                    {
                        var take = Math.Min(SendBatchSize, _pending.Count);
                        batch = _pending.Take(take).ToList();
                        _pending.RemoveRange(0, take);
                    }

                    // Disconnected: the batch stays buffered; the wait below is the reconnect.
                }
                else if (_inputClosed && _droppedWhileDisconnected == 0 && _droppedUnderBackpressure == 0)
                {
                    drained = true;
                }
            }

            if (drained)
            {
                return;
            }

            if (batch is not null)
            {
                try
                {
                    await client.SendLinesAsync(_runId, batch, shutdown);
                    continue;
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    // Shutdown: the whole run is abandoned (no completed report either) - F-010
                    // owns the lost run; these unsent lines are part of that story.
                    return;
                }
                catch (HubException rejection) when (IsDefinitiveRejection(rejection))
                {
                    // The server refused the batch definitively (unknown or foreign run, run
                    // assigned elsewhere, dead token): no retry can ever deliver these lines.
                    // One clear stderr line records the loss; the drain ends.
                    int remaining;
                    lock (_gate)
                    {
                        remaining = _pending.Count;
                        _pending.Clear();
                    }

                    await errorLog.WriteLineAsync(
                        $"Zenkins Worker: the server permanently rejected the log lines of run {_runId} "
                        + $"({rejection.Message}) - dropping {batch.Count} sent + {remaining} buffered line(s).");
                    return;
                }
                catch (Exception)
                {
                    // Lost response: the batch may or may not have landed. Put it back at the
                    // front; the resend after the reconnect is absorbed by the (RunId, Seq) key.
                    lock (_gate)
                    {
                        _pending.InsertRange(0, batch);
                    }
                }
            }

            try
            {
                if (!client.IsConnected)
                {
                    await client.WaitForConnectedAsync(shutdown);
                }
                else if (batch is not null)
                {
                    // A send failed while the client still considers itself connected: retry the
                    // same batch at a bounded rate until the state actually changes.
                    await Task.Delay(SendRetryDelay, shutdown);
                }
                else
                {
                    await _lineArrived.WaitAsync(shutdown);
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Caller holds the lock. Synthesizes one honest-loss marker as the next line.
    /// The marker never displaces a buffered line: the capacity bounds real unsent lines, and
    /// the marker is the accounting entry that says what happened to the dropped ones.</summary>
    private void SynthesizeMarkerLocked(string format, int droppedCount)
    {
        _pending.Add(new LogLineMessage(
            _nextSeq++,
            LogStream.StdErr,
            string.Format(format, droppedCount),
            DateTimeOffset.UtcNow,

            // A loss marker is not a step's line: it belongs to the run, not to whichever step happened
            // to be running when the buffer overflowed.
            StepId: null));
    }

    /// <summary>
    /// Whether this HubException is the server's definitive refusal - a message carrying the
    /// <see cref="LogStreamRejection.Prefix"/> (unknown or foreign run, run assigned elsewhere,
    /// dead token). SignalR forwards the hub-thrown message verbatim, so the machine prefix is
    /// the stable discriminator (review fix F1); SignalR's own generic completion texts ("An
    /// unexpected error occurred invoking 'AppendLinesAsync' on the server.") carry no prefix and
    /// stay retryable, as do transport failures.
    /// </summary>
    private static bool IsDefinitiveRejection(HubException rejection) =>
        LogStreamRejection.IsRejection(rejection.Message);
}
