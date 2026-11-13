using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Xunit;
using Zenkins.Server.Webhooks;

namespace Zenkins.Tests.Webhooks;

/// <summary>
/// Card Q-003 (debt handed over by F-017): the limiter class itself, pinned at unit scale. The
/// endpoint-level tests (<see cref="WebhookRateLimitTests"/>) prove what a delivery sees; these prove
/// the properties the class documents but nothing asserted:
/// <list type="bullet">
/// <item>the bucket map never exceeds <see cref="WebhookOptions.MaxRateLimitBuckets"/> (eviction at the
/// bound, and eviction only ever resets a counter - a flood is never refused);</item>
/// <item>the <b>fixed</b> window hands out a full second burst across a minute boundary (the documented
/// 2× peak-rate trade);</item>
/// <item>a backwards clock jump fails <b>open</b> for that window, and the rolled-back window is limited
/// again inside it;</item>
/// <item>per-bucket concurrency: parallel acquires for one id consume exactly the budget - the
/// per-window monitor is what makes the counter honest.</item>
/// </list>
/// The bound is asserted through the limiter's own window map (the documented property is about that
/// state, and no public surface reports it); every other assertion is behavioural.
/// </summary>
[Trait("Category", "Webhooks")]
public sealed class WebhookRateLimiterUnitTests
{
    /// <summary>The limiter's own window arithmetic, so "just past the boundary" is exactly that.</summary>
    private static readonly long WindowTicks = TimeSpan.FromMinutes(1).Ticks;

    [Fact]
    public void A_spent_budget_is_refused_with_a_retry_after_until_the_window_ends()
    {
        var clock = new FakeClock();
        var limiter = new WebhookRateLimiter(Options(perMinute: 2, maxBuckets: 100), clock);
        var webhookId = Guid.NewGuid();

        Assert.True(limiter.TryAcquire(webhookId, out _));
        Assert.True(limiter.TryAcquire(webhookId, out _));

        // The third delivery in the same window is refused, and Retry-After is the time left in the
        // window - never 0, never more than the window itself.
        Assert.False(limiter.TryAcquire(webhookId, out var retryAfter));
        Assert.InRange(retryAfter, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

        // The next window hands the budget out again: the refusal was the window's, not a permanent one.
        clock.AdvancePastWindowBoundary();
        Assert.True(limiter.TryAcquire(webhookId, out _));
    }

    [Fact]
    public void The_fixed_window_hands_out_a_full_second_burst_at_a_minute_boundary()
    {
        var clock = new FakeClock();
        var limiter = new WebhookRateLimiter(Options(perMinute: 3, maxBuckets: 100), clock);
        var webhookId = Guid.NewGuid();

        // The first window's full budget...
        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAcquire(webhookId, out _), $"delivery {i + 1} of the first window");
        }

        Assert.False(limiter.TryAcquire(webhookId, out _));

        // ...and the second window's full budget, seconds later: the documented fixed-window trade is a
        // peak rate of up to 2× the limit across the boundary. A sliding window would have refused.
        clock.AdvancePastWindowBoundary();
        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAcquire(webhookId, out _), $"delivery {i + 1} of the second window");
        }

        Assert.False(limiter.TryAcquire(webhookId, out _));
    }

    [Fact]
    public void A_backwards_clock_jump_fails_open_and_the_rolled_back_window_is_limited_again()
    {
        var clock = new FakeClock();
        var limiter = new WebhookRateLimiter(Options(perMinute: 1, maxBuckets: 100), clock);
        var webhookId = Guid.NewGuid();

        Assert.True(limiter.TryAcquire(webhookId, out _));
        Assert.False(limiter.TryAcquire(webhookId, out _));

        // An NTP correction or a manual clock change moves the computed window index backwards; the
        // class documents that this resets the counters - the limiter fails OPEN for that window, it
        // does not keep refusing with a count that belongs to the future.
        clock.Now = clock.Now.AddMinutes(-2);
        Assert.True(limiter.TryAcquire(webhookId, out _), "the backwards jump must fail open, not stay refused");

        // Fail-open means the rolled-back window is treated as fresh, not unlimited: its own budget
        // applies inside it.
        Assert.False(limiter.TryAcquire(webhookId, out _));
    }

    [Fact]
    public void A_flood_of_distinct_ids_stays_within_the_bucket_cap_and_is_never_refused_for_it()
    {
        const int cap = 4;
        var clock = new FakeClock();
        var limiter = new WebhookRateLimiter(Options(perMinute: 60, maxBuckets: cap), clock);

        // Twenty never-seen ids against a cap of four: eviction makes room, and eviction only ever
        // resets a counter - no acquisition is refused for the bound's sake (the class documents that
        // "evicting is safe because a bucket is a counter, not an authorization").
        for (var i = 0; i < 20; i++)
        {
            Assert.True(
                limiter.TryAcquire(Guid.NewGuid(), out _),
                $"acquisition {i + 1} was refused - eviction must make room, not refuse");
            Assert.True(
                BucketCount(limiter) <= cap,
                $"the bucket map held {BucketCount(limiter)} entries after acquisition {i + 1} (cap {cap})");
        }
    }

    [Fact]
    public async Task Concurrent_acquires_for_one_id_never_exceed_the_budget()
    {
        const int budget = 500;
        const int threads = 8;
        const int attemptsPerThread = 125;
        var clock = new FakeClock();
        var limiter = new WebhookRateLimiter(Options(perMinute: budget, maxBuckets: 100), clock);
        var webhookId = Guid.NewGuid();

        var accepted = 0;
        var refused = 0;
        var barrier = new Barrier(threads);
        var failures = new ConcurrentBag<string>();
        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(30));
            for (var i = 0; i < attemptsPerThread; i++)
            {
                if (limiter.TryAcquire(webhookId, out var retryAfter))
                {
                    Interlocked.Increment(ref accepted);
                }
                else
                {
                    if (retryAfter <= TimeSpan.Zero)
                    {
                        failures.Add("a refusal answered a non-positive Retry-After");
                    }

                    Interlocked.Increment(ref refused);
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Empty(failures);
        Assert.Equal(threads * attemptsPerThread, accepted + refused);

        // The whole point of the per-window monitor: races may cost deliveries their slot, but the
        // budget is never oversubscribed - exactly `budget` acquisitions are accepted in the window.
        Assert.Equal(budget, accepted);
        Assert.Equal(threads * attemptsPerThread - budget, refused);
    }

    // ------------------------------------------------------------------
    // Plumbing
    // ------------------------------------------------------------------

    private static WebhookOptions Options(int perMinute, int maxBuckets) =>
        WebhookOptions.Defaults with { RateLimitPerMinute = perMinute, MaxRateLimitBuckets = maxBuckets };

    /// <summary>
    /// The limiter's window map, read for the bound assertion. The documented property ("the state is
    /// <see cref="WebhookOptions.MaxRateLimitBuckets"/> entries at most") is about this private state;
    /// no public surface reports it, so the probe reads the field directly and says so.
    /// </summary>
    private static int BucketCount(WebhookRateLimiter limiter)
    {
        var field = typeof(WebhookRateLimiter).GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "The limiter's window map field is gone; the bound assertion has nothing to read.");
        return ((ICollection)field.GetValue(limiter)!).Count;
    }

    /// <summary>A wall clock the test steers: the limiter reads <see cref="TimeProvider.GetUtcNow"/>.</summary>
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-14T09:27:33.1978423+00:00");

        public override DateTimeOffset GetUtcNow() => Now;

        /// <summary>Steps one tick past the current window's end, i.e. into the next fixed window.</summary>
        public void AdvancePastWindowBoundary() =>
            Now = new DateTimeOffset((Now.UtcTicks / WindowTicks + 1) * WindowTicks + 1, TimeSpan.Zero);
    }
}
