namespace Zenkins.Worker;

/// <summary>
/// Capped exponential backoff with jitter (card F-005). Delays double from one second up to a
/// thirty-second cap, then stay there; each delay is widened by ±25% jitter so a fleet of workers
/// that lost the server together does not retry in lockstep.
/// <para>
/// The scheduler is stateless per sequence: <see cref="NextDelay"/> advances the attempt,
/// <see cref="Reset"/> returns to the initial delay. The jitter source is injectable so tests can
/// pin the bounds.
/// </para>
/// </summary>
public sealed class BackoffScheduler(
    TimeSpan initialDelay,
    TimeSpan maximumDelay,
    Random? random = null)
{
    public const double JitterFraction = 0.25;

    private readonly Random _random = random ?? Random.Shared;
    private int _attempt;

    public static BackoffScheduler Default() => new(
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(30));

    /// <summary>The delay for the next attempt. Monotone (non-decreasing) until the cap.</summary>
    public TimeSpan NextDelay()
    {
        _attempt++;
        var baseMilliseconds = Math.Min(
            initialDelay.TotalMilliseconds * Math.Pow(2, _attempt - 1),
            maximumDelay.TotalMilliseconds);

        var jitterRange = baseMilliseconds * JitterFraction;
        var jittered = baseMilliseconds + (_random.NextDouble() * 2 - 1) * jitterRange;
        var lower = Math.Max(0, baseMilliseconds - jitterRange);
        var upper = Math.Max(lower, baseMilliseconds + jitterRange);

        return TimeSpan.FromMilliseconds(Math.Clamp(jittered, lower, upper));
    }

    public void Reset() => _attempt = 0;
}
