namespace Zenkins.Server.Artifacts;

/// <summary>
/// The artifact path's transfer counters (card F-016). They exist because "the upload/download is streamed"
/// is a claim that must be <b>measured</b>, and because a metric that cannot fail proves nothing.
/// <para>
/// <b>Three measurements, each able to fail.</b>
/// <list type="bullet">
/// <item><see cref="BytesReceived"/> / <see cref="BytesSent"/>: the bytes that actually crossed the copy
/// loop, so a test can say "48 MB went through" rather than "it returned".</item>
/// <item><see cref="PeakChunkBytes"/>: the largest number of bytes a single <c>ReadAsync</c> returned.
/// <b>It is an upper bound, not a proof of streaming.</b> A read can never return more than the buffer it
/// was given, so "≤ the copy buffer" holds by construction for any implementation that passes a bounded
/// buffer - it catches only a copy that hands <c>ReadAsync</c> a buffer as large as the artifact (or reads
/// the whole body in one call). The measurement that actually separates streaming from buffering is
/// <see cref="PeakManagedDeltaBytes"/>; the chunk metric documents the bound the loop works within.</item>
/// <item><see cref="PeakManagedDeltaBytes"/>: the high-water mark of the process's managed heap above the
/// baseline taken by <see cref="Reset"/>, sampled at every chunk boundary. This is the one that discriminates
/// streaming from buffering: an implementation that reads the whole artifact into a <c>MemoryStream</c>
/// before writing it grows the heap by at least the artifact's size, while a chunked copy stays two orders of
/// magnitude below it. A test proves the discrimination by running both shapes through the same measurement
/// (see <c>ArtifactStreamingTests</c>).</item>
/// </list>
/// The heap sample is per-process, so the test that asserts on it runs in a non-parallel collection (xUnit's
/// <c>DisableParallelization</c>) - otherwise another test's allocations would be attributed to the transfer.
/// </para>
/// </summary>
public sealed class ArtifactTransferTelemetry
{
    private long _bytesReceived;
    private long _bytesSent;
    private long _peakChunkBytes;
    private long _peakManagedDeltaBytes;
    private long _baselineBytes;
    private int _filesReceived;

    /// <summary>Total body bytes received across every artifact file stream (this process).</summary>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>Total body bytes written to download responses (this process).</summary>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>The largest single chunk a copy loop actually read (bytes), not the buffer it asked for.</summary>
    public long PeakChunkBytes => Interlocked.Read(ref _peakChunkBytes);

    /// <summary>The managed heap's high-water mark above the <see cref="Reset"/> baseline, in bytes.</summary>
    public long PeakManagedDeltaBytes => Interlocked.Read(ref _peakManagedDeltaBytes);

    /// <summary>How many files the upload endpoint has written.</summary>
    public int FilesReceived => Volatile.Read(ref _filesReceived);

    /// <summary>Clears every counter and takes a fresh heap baseline (with a full collection first).</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _baselineBytes, GC.GetTotalMemory(forceFullCollection: true));
        Interlocked.Exchange(ref _bytesReceived, 0);
        Interlocked.Exchange(ref _bytesSent, 0);
        Interlocked.Exchange(ref _peakChunkBytes, 0);
        Interlocked.Exchange(ref _peakManagedDeltaBytes, 0);
        Interlocked.Exchange(ref _filesReceived, 0);
    }

    /// <summary>Records one written file's total size (the upload endpoint calls it once per file).</summary>
    public void RecordFile(long bytes)
    {
        Interlocked.Add(ref _bytesReceived, bytes);
        Interlocked.Increment(ref _filesReceived);
    }

    /// <summary>Records one streamed response body's total size (the download path calls it once per stream).</summary>
    public void RecordSent(long bytes) => Interlocked.Add(ref _bytesSent, bytes);

    /// <summary>
    /// Records one completed chunk read: its real length (the chunk-size measurement) and a heap sample.
    /// Called from inside the copy loops, so the sample is taken while the transfer is in flight.
    /// </summary>
    public void ObserveChunk(int bytes)
    {
        Raise(ref _peakChunkBytes, bytes);
        Raise(ref _peakManagedDeltaBytes, GC.GetTotalMemory(forceFullCollection: false) - Interlocked.Read(ref _baselineBytes));
    }

    /// <summary>Monotonic max, so concurrent transfers cannot lower a peak.</summary>
    private static void Raise(ref long target, long value)
    {
        long observed;
        while (value > (observed = Interlocked.Read(ref target))
            && Interlocked.CompareExchange(ref target, value, observed) != observed)
        {
            // Another writer raised the peak between the read and the exchange; re-evaluate.
        }
    }
}
