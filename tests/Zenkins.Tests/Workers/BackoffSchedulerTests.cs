using Xunit;
using Zenkins.Worker;

namespace Zenkins.Tests.Workers;

/// <summary>
/// Card F-005's backoff scheduler: delays grow exponentially (jitter-bounded), stay under the cap,
/// and reset after a success. The ±25% jitter keeps the growth strict between small attempts, so
/// these asserts are deterministic without pinning the random source.
/// </summary>
[Trait("Category", "Workers")]
public sealed class BackoffSchedulerTests
{
    [Fact]
    public void The_first_delay_is_the_initial_delay_within_jitter()
    {
        var scheduler = new BackoffScheduler(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        var delay = scheduler.NextDelay();

        Assert.InRange(delay.TotalMilliseconds, 750, 1250);
    }

    [Fact]
    public void Delays_grow_exponentially_while_below_the_cap()
    {
        var scheduler = new BackoffScheduler(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        var previous = TimeSpan.Zero;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            // Bases 1,2,4,8,16,30s: consecutive jitter bands (±25%) do not overlap yet.
            var delay = scheduler.NextDelay();
            Assert.True(delay > previous, $"Attempt {attempt} did not grow: {delay} <= {previous}.");
            previous = delay;
        }
    }

    [Fact]
    public void Delays_never_exceed_the_cap_plus_jitter()
    {
        var scheduler = new BackoffScheduler(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        TimeSpan last = TimeSpan.Zero;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            last = scheduler.NextDelay();
            Assert.InRange(last.TotalMilliseconds, 0, 30_000 * (1 + BackoffScheduler.JitterFraction) + 1);
        }

        // And the sequence actually reached the cap's neighbourhood, not a smaller ceiling.
        Assert.True(last.TotalMilliseconds > 20_000, $"Expected the cap neighbourhood, got {last}.");
    }

    [Fact]
    public void Reset_returns_to_the_initial_delay()
    {
        var scheduler = new BackoffScheduler(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        for (var attempt = 0; attempt < 5; attempt++)
        {
            scheduler.NextDelay();
        }

        scheduler.Reset();
        var delay = scheduler.NextDelay();

        Assert.InRange(delay.TotalMilliseconds, 750, 1250);
    }

    [Fact]
    public void Jitter_stays_within_half_of_the_base_delay()
    {
        var scheduler = new BackoffScheduler(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(30));

        for (var attempt = 0; attempt < 10; attempt++)
        {
            // Attempt 1 always: base 4s, allowed 3s..5s.
            scheduler.Reset();
            var delay = scheduler.NextDelay();
            Assert.InRange(delay.TotalMilliseconds, 3_000, 5_000);
        }
    }
}
