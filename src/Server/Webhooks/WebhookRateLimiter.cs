using System.Collections.Concurrent;

namespace Zenkins.Server.Webhooks;

/// <summary>
/// The per-webhook delivery limiter (card F-017): a fixed one-minute window keyed by the <b>raw id from
/// the URL</b>, whether or not a webhook with that id exists.
/// <para>
/// <b>Why the key is the raw id.</b> Counting only ids that resolve to a webhook would make
/// "429" and "404" an existence oracle: an attacker could tell a live id from a dead one by pushing it
/// over the limit, with no valid signature at all. Counting the URL's id - the same work for a real and
/// a fictitious webhook - means the two answers carry no information about existence. That is also why
/// the endpoint consults this limiter <em>before</em> it looks the id up.
/// </para>
/// <para>
/// <b>Bounded memory.</b> An id that never existed can still be flooded, so the dictionary may not grow
/// without limit: stale windows (a newer minute has begun, so their count is worthless) are swept
/// periodically, and at <see cref="WebhookOptions.MaxRateLimitBuckets"/> entries one existing bucket is
/// evicted to make room. Evicting only ever resets a counter - it can never create a run, and the run
/// path is still gated by the signature, the dedupe index and the queue itself.
/// </para>
/// <para>
/// <b>Keyed by the webhook, never by the delivery.</b> A sender can vary <c>X-GitHub-Delivery</c> on
/// every request, so a limiter that counted distinct deliveries would be trivially bypassed. Every
/// delivery that reaches this point - duplicate or not - spends one of the webhook's slots.
/// </para>
/// <para>
/// <b>In-process on purpose.</b> This protects one process's queue from one leaked secret; it is not an
/// accounting record, so a second node counts its own window. The state is
/// <see cref="WebhookOptions.MaxRateLimitBuckets"/> entries at most.
/// </para>
/// <para>
/// <b>Known properties, accepted deliberately (review m4).</b> A <em>fixed</em> window (not a sliding one)
/// means a sender can spend a full budget at the end of one minute and another at the start of the next:
/// the peak rate is up to 2× the limit for a few hundred milliseconds. That is the trade for a counter that
/// is one number per webhook and needs no per-request bookkeeping. And the clock is wall-clock, so a
/// <em>backwards</em> jump (NTP correction, a manual clock change) moves the computed window index backwards
/// and resets the counters - the limiter fails <b>open</b> for that window. Both are acceptable because this
/// is a guard rail around a signing secret, not an authority: the run queue, the signature and the replay
/// index are what actually bound the damage, and a monotonic clock would have to be process-wide state with
/// the same fail-open gap at its own epoch.
/// </para>
/// </summary>
public sealed class WebhookRateLimiter(WebhookOptions options, TimeProvider timeProvider)
{
    private static readonly long WindowTicks = TimeSpan.FromMinutes(1).Ticks;

    private readonly ConcurrentDictionary<Guid, Window> _windows = new();
    private int _acquisitions;

    /// <summary>
    /// Takes one slot for <paramref name="webhookId"/>. On refusal, <paramref name="retryAfter"/> is the
    /// time left in the current window (always at least one second, so the <c>Retry-After</c> header the
    /// caller writes is never <c>0</c>).
    /// </summary>
    public bool TryAcquire(Guid webhookId, out TimeSpan retryAfter)
    {
        var now = timeProvider.GetUtcNow().UtcTicks;
        var index = now / WindowTicks;

        // Periodic housekeeping: a stale window's count would be reset on its next use anyway, so
        // dropping it loses nothing and keeps a flood of never-seen ids from pinning memory.
        if (Interlocked.Increment(ref _acquisitions) % 1024 == 0)
        {
            SweepStaleWindows(index);
        }

        var window = _windows.GetOrAdd(webhookId, static _ => new Window());
        lock (window)
        {
            if (window.Index != index)
            {
                window.Index = index;
                window.Count = 0;
            }

            if (window.Count >= options.RateLimitPerMinute)
            {
                retryAfter = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, (index + 1) * WindowTicks - now));
                return false;
            }

            window.Count++;
        }

        if (_windows.Count > options.MaxRateLimitBuckets)
        {
            // Bound met with every bucket live: evict one. The choice is deliberately not "the idle one"
            // (these ids are fictitious, so there is no such thing); and evicting is safe because a
            // bucket is a counter, not an authorization.
            EnforceBucketCap(index);
        }

        retryAfter = TimeSpan.Zero;
        return true;
    }

    /// <summary>Drops every bucket whose window has ended (their counts are already meaningless).</summary>
    private void SweepStaleWindows(long currentIndex)
    {
        foreach (var (id, window) in _windows)
        {
            lock (window)
            {
                if (window.Index != currentIndex)
                {
                    _windows.TryRemove(id, out _);
                }
            }
        }
    }

    /// <summary>Removes one arbitrary bucket when the cap is reached with every window live.</summary>
    private void EnforceBucketCap(long currentIndex)
    {
        foreach (var (id, window) in _windows)
        {
            // Prefer a stale one; a locked bucket is skipped rather than waited on (this is best effort).
            if (!Monitor.TryEnter(window))
            {
                continue;
            }

            try
            {
                if (window.Index != currentIndex)
                {
                    _windows.TryRemove(id, out _);
                }
            }
            finally
            {
                Monitor.Exit(window);
            }

            if (_windows.Count <= options.MaxRateLimitBuckets)
            {
                return;
            }
        }

        // Every bucket is current: give up one entry unconditionally.
        foreach (var (id, _) in _windows)
        {
            _windows.TryRemove(id, out _);
            if (_windows.Count <= options.MaxRateLimitBuckets)
            {
                return;
            }
        }
    }

    /// <summary>One webhook's current window. Guarded by its own monitor (the dictionary is concurrent).</summary>
    private sealed class Window
    {
        public long Index { get; set; } = -1;

        public int Count { get; set; }
    }
}
