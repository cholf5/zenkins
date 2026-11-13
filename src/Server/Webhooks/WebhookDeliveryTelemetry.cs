namespace Zenkins.Server.Webhooks;

/// <summary>
/// Process-wide counters for the delivery endpoint (card F-017, review m1). They exist to make one property
/// observable instead of asserted: <b>an oversized body is never buffered</b>.
/// <para>
/// <see cref="BytesBuffered"/> counts bytes actually written into the bounded buffer; a refused oversized
/// body contributes at most the cap plus the single 16 KiB chunk that crossed it, never the request's own
/// size. <see cref="OversizedRejections"/> counts refusals, so a test can send a body it built itself
/// (chunked, no <c>Content-Length</c>) and prove both "refused" and "not held".
/// </para>
/// <para>
/// Deliberately not a metric series: the numbers live for the process, like
/// <see cref="Artifacts.ArtifactTransferTelemetry"/>'s, and are read by tests and by a future /metrics
/// exporter - not by the delivery path itself.
/// </para>
/// </summary>
public sealed class WebhookDeliveryTelemetry
{
    private long _bytesBuffered;
    private long _oversizedRejections;

    /// <summary>Total bytes written into the delivery endpoint's bounded buffer since start.</summary>
    public long BytesBuffered => Interlocked.Read(ref _bytesBuffered);

    /// <summary>How many deliveries were refused for exceeding <see cref="WebhookOptions.MaxBodyBytes"/>.</summary>
    public long OversizedRejections => Interlocked.Read(ref _oversizedRejections);

    /// <summary>Records one accepted body of <paramref name="bytes"/> bytes.</summary>
    public void RecordBuffered(long bytes) => Interlocked.Add(ref _bytesBuffered, bytes);

    /// <summary>
    /// Records one refusal. <paramref name="bufferedBytes"/> is what had already been held when the cap was
    /// crossed - zero when the declared length was refused outright.
    /// </summary>
    public void RecordOversizedRejection(long bufferedBytes)
    {
        if (bufferedBytes > 0)
        {
            Interlocked.Add(ref _bytesBuffered, bufferedBytes);
        }

        Interlocked.Increment(ref _oversizedRejections);
    }
}
