namespace Zenkins.Server.Polling;

/// <summary>
/// The knobs of card F-025's poller: how often the sweeper ticks, how long one poll's probe may wait, and how
/// far a failing remote's interval may stretch.
/// <para>
/// <b>Backoff, not a storm.</b> A poll that cannot see the remote doubles its next wait (interval, 2x, 4x …)
/// up to <see cref="MaxBackoffSeconds"/>, and one successful probe resets it. A host that is down for a day
/// costs two probes an hour, not one per interval.
/// </para>
/// </summary>
public sealed class PollOptions
{
    public const string SweepIntervalSecondsKey = "Polling:SweepIntervalSeconds";
    public const string ProbeWaitSecondsKey = "Polling:ProbeWaitSeconds";
    public const string MaxBackoffSecondsKey = "Polling:MaxBackoffSeconds";
    public const string MaxProjectsPerTickKey = "Polling:MaxProjectsPerTick";

    /// <summary>The shortest interval a project may declare (card F-025: minutes, minimum 1).</summary>
    public const int MinIntervalMinutes = 1;

    /// <summary>The default interval the settings form offers when polling is switched on.</summary>
    public const int DefaultIntervalMinutes = 5;

    /// <summary>How often the sweeper looks for due projects (tests compress it and call the seam directly).</summary>
    public double SweepIntervalSeconds { get; set; } = 30;

    /// <summary>How long one poll's probe may wait for a worker before it is settled as no-worker.</summary>
    public double ProbeWaitSeconds { get; set; } = 10;

    /// <summary>The cap of the backoff curve, in minutes (the settings card states it without injecting
    /// the options).</summary>
    public const int MaxBackoffMinutes = 30;

    /// <summary>The cap of the backoff curve: a failing remote is retried at most this often.</summary>
    public double MaxBackoffSeconds { get; set; } = MaxBackoffMinutes * 60;

    /// <summary>How many projects one tick polls (the tick's own bound).</summary>
    public int MaxProjectsPerTick { get; set; } = 10;

    /// <summary>The cap as a span.</summary>
    public TimeSpan MaxBackoff => TimeSpan.FromSeconds(MaxBackoffSeconds);

    /// <summary>The probe's caller wait.</summary>
    public TimeSpan ProbeWait => TimeSpan.FromSeconds(ProbeWaitSeconds);

    /// <summary>
    /// The interval a project's next poll waits: its declared interval doubled per consecutive failure,
    /// capped. A project with no interval (polling off) never polls, so the answer is meaningless - the
    /// sweeper never asks.
    /// </summary>
    public TimeSpan EffectiveInterval(int? intervalMinutes, int failureStreak)
    {
        var minutes = intervalMinutes is { } declared && declared >= MinIntervalMinutes
            ? declared
            : DefaultIntervalMinutes;
        var multiplier = Math.Pow(2, Math.Clamp(failureStreak, 0, 16));
        var seconds = Math.Min(minutes * 60 * multiplier, MaxBackoffSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Reads the knobs from configuration, falling back to the production defaults.</summary>
    public static PollOptions FromConfiguration(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var options = new PollOptions();

        if (double.TryParse(read(SweepIntervalSecondsKey), out var sweep) && sweep > 0)
        {
            options.SweepIntervalSeconds = sweep;
        }

        if (double.TryParse(read(ProbeWaitSecondsKey), out var wait) && wait > 0)
        {
            options.ProbeWaitSeconds = wait;
        }

        if (double.TryParse(read(MaxBackoffSecondsKey), out var backoff) && backoff > 0)
        {
            options.MaxBackoffSeconds = backoff;
        }

        if (int.TryParse(read(MaxProjectsPerTickKey), out var projects) && projects > 0)
        {
            options.MaxProjectsPerTick = projects;
        }

        return options;
    }
}
