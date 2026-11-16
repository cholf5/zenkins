using System.Collections.Concurrent;

namespace Zenkins.Server.Api;

/// <summary>
/// The per-token request limiter (card F-027): a fixed one-minute window keyed by the <b>authenticated
/// token's row id</b>.
/// <para>
/// <b>Why the token id and not the plaintext.</b> The endpoint only consults the limiter after the bearer
/// token authenticated, so there is no unknown credential to key on - and the id is stable across the
/// token's lifetime while the plaintext is never held in memory after the hash lookup. Because a request
/// reaches the limiter only when its credential is valid, a 429 can never be used as an existence oracle
/// for a project or run id: the resource lookup happens in the handler, after this filter.
/// </para>
/// <para>
/// <b>Bounded memory.</b> Stale windows (a newer minute has begun, so their counts are worthless) are
/// swept periodically, and at <see cref="ApiOptions.MaxRateLimitBuckets"/> entries one existing bucket is
/// evicted. Evicting only ever resets a counter; it can never authorize anything, because the token has
/// already authenticated and the run's own validation, gate and queue still bound what a call can do.
/// </para>
/// <para>
/// <b>In-process on purpose</b> (the F-017 decision): this protects one process from one leaked token; it
/// is not an accounting record, so a second node counts its own window. The trade-offs accepted for it
/// are the same ones documented for the webhook limiter: a fixed window allows a burst across the
/// boundary of up to twice the budget, and a backwards clock jump resets the counters (fails open for
/// that window).
/// </para>
/// </summary>
public sealed class ApiRateLimiter(ApiOptions options, TimeProvider timeProvider)
{
    private static readonly long WindowTicks = TimeSpan.FromMinutes(1).Ticks;

    private readonly ConcurrentDictionary<Guid, Window> _windows = new();
    private int _acquisitions;

    /// <summary>
    /// Takes one slot for <paramref name="apiTokenId"/>. On refusal, <paramref name="retryAfter"/> is the
    /// time left in the current window (always at least one second, so the caller's <c>Retry-After</c>
    /// header is never <c>0</c>).
    /// </summary>
    public bool TryAcquire(Guid apiTokenId, out TimeSpan retryAfter)
    {
        var now = timeProvider.GetUtcNow().UtcTicks;
        var index = now / WindowTicks;

        // Periodic housekeeping: a stale window's count would be reset on its next use anyway.
        if (Interlocked.Increment(ref _acquisitions) % 1024 == 0)
        {
            SweepStaleWindows(index);
        }

        var window = _windows.GetOrAdd(apiTokenId, static _ => new Window());
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
            EnforceBucketCap();
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
    private void EnforceBucketCap()
    {
        foreach (var (id, _) in _windows)
        {
            _windows.TryRemove(id, out _);
            if (_windows.Count <= options.MaxRateLimitBuckets)
            {
                return;
            }
        }
    }

    /// <summary>One token's current window. Guarded by its own monitor (the dictionary is concurrent).</summary>
    private sealed class Window
    {
        public long Index { get; set; } = -1;

        public int Count { get; set; }
    }
}
