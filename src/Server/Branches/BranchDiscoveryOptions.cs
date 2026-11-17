namespace Zenkins.Server.Branches;

/// <summary>
/// The knobs of card F-023's background half: how often the sweeper ticks, how often one repository is
/// discovered, and how a branch observation that produced no verdict is retried.
/// <para>
/// <b>Backoff, not a storm.</b> A pending observation is re-requested after
/// <see cref="ObservationRetrySeconds"/> doubled per attempt, capped at
/// <see cref="ObservationRetryMaxSeconds"/>; an in-flight task nobody answers is abandoned after
/// <see cref="ObservationStaleSeconds"/> and becomes a retry. "No Worker" therefore slows down instead of
/// filling the queue.
/// </para>
/// </summary>
public sealed class BranchDiscoveryOptions
{
    public const string SweepIntervalSecondsKey = "Branches:SweepIntervalSeconds";
    public const string DiscoveryIntervalSecondsKey = "Branches:DiscoveryIntervalSeconds";
    public const string ObservationRetrySecondsKey = "Branches:ObservationRetrySeconds";
    public const string ObservationRetryMaxSecondsKey = "Branches:ObservationRetryMaxSeconds";
    public const string ObservationStaleSecondsKey = "Branches:ObservationStaleSeconds";
    public const string MaxProjectsPerTickKey = "Branches:MaxProjectsPerTick";
    public const string DiscoveryProbeWaitSecondsKey = "Branches:DiscoveryProbeWaitSeconds";

    /// <summary>How often the sweeper runs (tests compress it and call the seam directly).</summary>
    public double SweepIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// How often one multibranch project's repository is probed for heads. One probe per repository per
    /// interval - never one per branch: the probe answers every head at once.
    /// </summary>
    public double DiscoveryIntervalSeconds { get; set; } = 300;

    /// <summary>The first retry delay of an observation that produced no verdict.</summary>
    public double ObservationRetrySeconds { get; set; } = 60;

    /// <summary>The cap the doubled retry delay is bounded by.</summary>
    public double ObservationRetryMaxSeconds { get; set; } = 900;

    /// <summary>How long an unanswered in-flight observation may live before it is abandoned.</summary>
    public double ObservationStaleSeconds { get; set; } = 120;

    /// <summary>How many projects one tick discovers (the tick's own bound).</summary>
    public int MaxProjectsPerTick { get; set; } = 5;

    /// <summary>
    /// How long one discovery probe waits for a Worker before it is settled as "no worker". Shorter than
    /// the interactive probe wait on purpose: a sweep must not hold its tick for a minute per project.
    /// </summary>
    public double DiscoveryProbeWaitSeconds { get; set; } = 10;

    /// <summary>The delay before retrying a pending observation that has failed <paramref name="attempts"/> times.</summary>
    public TimeSpan RetryDelay(int attempts)
    {
        var seconds = ObservationRetrySeconds * Math.Pow(2, Math.Clamp(attempts, 0, 10));
        return TimeSpan.FromSeconds(Math.Min(seconds, ObservationRetryMaxSeconds));
    }

    /// <summary>Reads the knobs from configuration, falling back to the production defaults.</summary>
    public static BranchDiscoveryOptions FromConfiguration(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var options = new BranchDiscoveryOptions();

        if (double.TryParse(read(SweepIntervalSecondsKey), out var sweep) && sweep > 0)
        {
            options.SweepIntervalSeconds = sweep;
        }

        if (double.TryParse(read(DiscoveryIntervalSecondsKey), out var discovery) && discovery > 0)
        {
            options.DiscoveryIntervalSeconds = discovery;
        }

        if (double.TryParse(read(ObservationRetrySecondsKey), out var retry) && retry > 0)
        {
            options.ObservationRetrySeconds = retry;
        }

        if (double.TryParse(read(ObservationRetryMaxSecondsKey), out var max) && max > 0)
        {
            options.ObservationRetryMaxSeconds = max;
        }

        if (double.TryParse(read(ObservationStaleSecondsKey), out var stale) && stale > 0)
        {
            options.ObservationStaleSeconds = stale;
        }

        if (int.TryParse(read(MaxProjectsPerTickKey), out var projects) && projects > 0)
        {
            options.MaxProjectsPerTick = projects;
        }

        if (double.TryParse(read(DiscoveryProbeWaitSecondsKey), out var wait) && wait > 0)
        {
            options.DiscoveryProbeWaitSeconds = wait;
        }

        return options;
    }
}
