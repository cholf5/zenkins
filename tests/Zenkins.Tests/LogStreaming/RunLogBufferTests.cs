using Xunit;
using Zenkins.Shared.LogStreaming;
using Zenkins.Worker;

namespace Zenkins.Tests.LogStreaming;

/// <summary>
/// Unit tests for the worker-side bounded unsent-line buffer (card F-008): per-run monotonic
/// sequences, drop-oldest overflow with honest counting, the synthetic reconnect marker, and
/// duplicate-safe resend after a lost response. These run against fakes - no server involved.
/// </summary>
[Trait("Category", "LogStreaming")]
public sealed class RunLogBufferTests
{
    [Fact]
    public async Task Buffer_assigns_per_run_monotonic_sequences_starting_at_one()
    {
        var buffer = new RunLogBuffer(Guid.NewGuid(), capacity: 100, TextWriter.Null);
        var sink = new FakeSink(connected: true);

        buffer.Emit(LogStream.StdOut, "first");
        buffer.Emit(LogStream.StdErr, "second");
        buffer.CompleteInput();
        await buffer.DrainAsync(sink, TestContext.Current.CancellationToken);

        var sent = sink.SentSingleBatch();
        Assert.Equal(new long[] { 1, 2 }, sent.Select(line => line.Seq).ToArray());
        Assert.Equal(LogStream.StdOut, sent[0].Stream);
        Assert.Equal(LogStream.StdErr, sent[1].Stream);
    }

    [Fact]
    public async Task Buffer_drops_the_oldest_unsent_lines_when_over_capacity_and_counts_them()
    {
        var buffer = new RunLogBuffer(Guid.NewGuid(), capacity: 5, TextWriter.Null);
        for (var i = 1; i <= 8; i++)
        {
            buffer.Emit(LogStream.StdOut, $"line-{i}");
        }

        Assert.Equal(5, buffer.PendingCount);
        Assert.Equal(3, buffer.DroppedCount);
        Assert.Equal(8, buffer.LastSeq);

        buffer.CompleteInput();
        await buffer.DrainAsync(new FakeSink(connected: true), TestContext.Current.CancellationToken);

        // The marker is synthesized at drain time and carries the NEXT sequence number.
        Assert.Equal(9, buffer.LastSeq);
        Assert.Equal(0, buffer.DroppedCount);
    }

    [Fact]
    public async Task Drain_synthesizes_the_honest_marker_after_drops()
    {
        var buffer = new RunLogBuffer(Guid.NewGuid(), capacity: 5, TextWriter.Null);
        for (var i = 1; i <= 8; i++)
        {
            buffer.Emit(LogStream.StdOut, $"line-{i}");
        }

        var sink = new FakeSink(connected: true);
        buffer.CompleteInput();
        await buffer.DrainAsync(sink, TestContext.Current.CancellationToken);

        var sent = sink.SentAll();
        var marker = sent.Single(line => line.Message.Contains("lines dropped", StringComparison.Ordinal));
        Assert.Equal("<3 lines dropped while reconnecting>", marker.Message);
        Assert.Equal(LogStream.StdErr, marker.Stream);
        Assert.Equal(9, marker.Seq);

        // The surviving lines are the newest ones; the dropped oldest ones are simply gone.
        Assert.Equal(
            ["line-4", "line-5", "line-6", "line-7", "line-8", "<3 lines dropped while reconnecting>"],
            sent.Select(line => line.Message));

        // The marker never displaces a buffered line: five real lines survive in a five-line buffer.
        Assert.Equal(6, sent.Count);
    }

    [Fact]
    public async Task Drain_marks_the_loss_of_every_line_when_everything_overflowed()
    {
        // Capacity 3, 6 emitted: three survive, three dropped - and the marker reports exactly
        // those three. (The drain never sacrifices a buffered line for the marker.)
        var buffer = new RunLogBuffer(Guid.NewGuid(), capacity: 3, TextWriter.Null);
        for (var i = 1; i <= 6; i++)
        {
            buffer.Emit(LogStream.StdOut, $"line-{i}");
        }

        Assert.Equal(3, buffer.PendingCount);
        Assert.Equal(3, buffer.DroppedCount);

        var sink = new FakeSink(connected: true);
        buffer.CompleteInput();
        await buffer.DrainAsync(sink, TestContext.Current.CancellationToken);

        var sent = sink.SentAll();
        var marker = sent.Single(line => line.Message.Contains("lines dropped", StringComparison.Ordinal));
        Assert.Equal("<3 lines dropped while reconnecting>", marker.Message);
        Assert.Equal(["line-4", "line-5", "line-6", "<3 lines dropped while reconnecting>"],
            sent.Select(line => line.Message));
    }

    [Fact]
    public async Task Drops_while_connected_are_labelled_backpressure_not_reconnecting()
    {
        // Review fix F4: the connection is up and the server is the bottleneck - the honest label
        // is backpressure, not a reconnect gap.
        var buffer = new RunLogBuffer(Guid.NewGuid(), capacity: 2, TextWriter.Null, isConnected: () => true);
        for (var i = 1; i <= 5; i++)
        {
            buffer.Emit(LogStream.StdOut, $"line-{i}");
        }

        Assert.Equal(0, buffer.DroppedCount);
        Assert.Equal(3, buffer.DroppedUnderBackpressureCount);

        var sink = new FakeSink(connected: true);
        buffer.CompleteInput();
        await buffer.DrainAsync(sink, TestContext.Current.CancellationToken);

        var sent = sink.SentAll();
        var marker = sent.Single(line => line.Message.Contains("lines dropped", StringComparison.Ordinal));
        Assert.Equal("<3 lines dropped under backpressure>", marker.Message);
        Assert.Equal(LogStream.StdErr, marker.Stream);
        Assert.Equal(6, marker.Seq);
        Assert.DoesNotContain(sent, line => line.Message.Contains("reconnecting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Drops_while_disconnected_keep_the_reconnecting_marker()
    {
        // Review fix F4: the card's original scenario - the connection is down, so the label
        // stays "while reconnecting".
        var buffer = new RunLogBuffer(Guid.NewGuid(), capacity: 2, TextWriter.Null, isConnected: () => false);
        for (var i = 1; i <= 5; i++)
        {
            buffer.Emit(LogStream.StdOut, $"line-{i}");
        }

        Assert.Equal(3, buffer.DroppedCount);
        Assert.Equal(0, buffer.DroppedUnderBackpressureCount);

        var sink = new FakeSink(connected: true);
        buffer.CompleteInput();
        await buffer.DrainAsync(sink, TestContext.Current.CancellationToken);

        var sent = sink.SentAll();
        var marker = sent.Single(line => line.Message.Contains("lines dropped", StringComparison.Ordinal));
        Assert.Equal("<3 lines dropped while reconnecting>", marker.Message);
        Assert.DoesNotContain(sent, line => line.Message.Contains("backpressure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mixed_drops_produce_both_markers_in_a_stable_order()
    {
        // The probe answers per dropped line: first drop during a reconnect gap, then two while
        // connected. The buffer accounts them separately; the drain synthesizes both markers
        // (reconnecting first, then backpressure), each carrying the next sequence number and
        // never displacing a buffered line.
        var answers = new Queue<bool>([false, true, true]);
        var buffer = new RunLogBuffer(Guid.NewGuid(), capacity: 1, TextWriter.Null, answers.Dequeue);
        for (var i = 1; i <= 4; i++)
        {
            buffer.Emit(LogStream.StdOut, $"line-{i}");
        }

        Assert.Equal(1, buffer.DroppedCount);
        Assert.Equal(2, buffer.DroppedUnderBackpressureCount);

        var sink = new FakeSink(connected: true);
        buffer.CompleteInput();
        await buffer.DrainAsync(sink, TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            "line-4",
            "<1 lines dropped while reconnecting>",
            "<2 lines dropped under backpressure>",
        ], sink.SentAll().Select(line => line.Message));
    }

    [Fact]
    public async Task Drain_resends_a_lost_batch_and_reconnects_without_renumbering()
    {
        // The sink drops the first send (lost response) and disconnects; the batch returns to the
        // buffer, the reconnect delivers the SAME sequence numbers again - the server's key does
        // the deduplication, the buffer never renumbers.
        var buffer = new RunLogBuffer(Guid.NewGuid(), capacity: 100, TextWriter.Null);
        var sink = new FakeSink(connected: false);
        var pump = buffer.DrainAsync(sink, TestContext.Current.CancellationToken);

        buffer.Emit(LogStream.StdOut, "one");
        buffer.Emit(LogStream.StdOut, "two");

        sink.FailNextSend = true;
        sink.Connect(); // connected again: the pump retries the same batch

        buffer.CompleteInput();
        await pump.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var attempts = sink.AllSendAttempts();
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, batch => Assert.Equal(new long[] { 1, 2 }, batch.Select(line => line.Seq).ToArray()));
        Assert.Equal(new long[] { 1, 2 }, sink.SentAll().Select(line => line.Seq).Distinct().ToArray());
        Assert.Equal(2, sink.SentAll().Count);
    }

    [Fact]
    public async Task Emit_is_thread_safe_across_the_two_reader_threads()
    {
        var buffer = new RunLogBuffer(Guid.NewGuid(), capacity: 10_000, TextWriter.Null);
        var sink = new FakeSink(connected: true);
        var pump = buffer.DrainAsync(sink, TestContext.Current.CancellationToken);

        // One dedicated reader thread per stream in the real runner; parallel emitters stand in
        // for them. The sequence must stay unique and gapless, the buffer consistent.
        const int perStream = 2_000;
        var streams = new[] { LogStream.StdOut, LogStream.StdErr };
        await Task.WhenAll(streams.Select(stream => Task.Run(() =>
        {
            for (var i = 0; i < perStream; i++)
            {
                buffer.Emit(stream, $"{stream}-{i}");
            }
        })));

        buffer.CompleteInput();
        await pump.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var sent = sink.SentAll();
        Assert.Equal(2 * perStream, sent.Count);
        Assert.Equal(2 * perStream, sent.Select(line => line.Seq).Distinct().Count());
        Assert.Equal(1, sent.Select(line => line.Seq).Min());
        Assert.Equal(2 * perStream, sent.Select(line => line.Seq).Max());
        Assert.Equal(0, buffer.DroppedCount);
    }

    /// <summary>A scriptable ILogStreamSink: connects on demand, can lose one send, records all.</summary>
    private sealed class FakeSink : ILogStreamSink
    {
        private readonly object _gate = new();
        private readonly List<List<LogLineMessage>> _attempts = [];
        private readonly List<LogLineMessage> _delivered = [];
        private readonly TaskCompletionSource _pendingWork = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeSink(bool connected) => IsConnected = connected;

        public bool FailNextSend { get; set; }

        public bool IsConnected { get; private set; }

        public bool HasEverConnected => true;

        public void Connect() => IsConnected = true;

        public Task WaitForConnectedAsync(CancellationToken shutdown)
        {
            if (IsConnected)
            {
                return Task.CompletedTask;
            }

            return Task.Delay(TimeSpan.FromMilliseconds(5), shutdown);
        }

        public async Task SendLinesAsync(Guid runId, IReadOnlyList<LogLineMessage> lines, CancellationToken shutdown)
        {
            lock (_gate)
            {
                _attempts.Add([.. lines]);
            }

            if (FailNextSend)
            {
                FailNextSend = false;
                throw new InvalidOperationException("simulated lost response");
            }

            lock (_gate)
            {
                _delivered.AddRange(lines);
            }

            _pendingWork.TrySetResult();
        }

        public List<LogLineMessage> SentSingleBatch()
        {
            lock (_gate)
            {
                Assert.Single(_attempts);
                return _attempts[0];
            }
        }

        public List<LogLineMessage> SentAll()
        {
            lock (_gate)
            {
                return [.. _delivered];
            }
        }

        public List<List<LogLineMessage>> AllSendAttempts()
        {
            lock (_gate)
            {
                return [.. _attempts];
            }
        }
    }
}
