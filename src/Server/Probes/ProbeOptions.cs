using System.Globalization;

namespace Zenkins.Server.Probes;

/// <summary>
/// The probe knobs (card I-005), read from configuration once at startup (the same shape as
/// <see cref="Runs.RunOptions"/>). Tests use sub-second waits and short leases instead of sleeping on
/// production time; nothing here is a schedule - F-023/F-025 own when a probe is requested.
/// </summary>
/// <param name="WaitSeconds">
/// How long one requesting caller waits for an answer before the Server settles the probe as
/// <c>no-worker</c>. This is the card's "bounded wait": the Server says nobody is there instead of the
/// caller waiting forever.
/// </param>
/// <param name="PollMilliseconds">How often the waiting caller re-reads the probe's state.</param>
/// <param name="LeaseSeconds">
/// How long one Worker's lease on a probe lasts. A worker that dies before reporting loses no data - the
/// probe is re-offered to the next claimant after this window.
/// </param>
/// <param name="DedupeSeconds">
/// How long a completed probe stays joinable, so every caller inside the interval shares one git
/// conversation (and one credential use) rather than starting its own.
/// </param>
/// <param name="RetentionSeconds">How long a settled probe is remembered at all (memory bound).</param>
public sealed record ProbeOptions(
    double WaitSeconds,
    int PollMilliseconds,
    int LeaseSeconds,
    int DedupeSeconds,
    int RetentionSeconds)
{
    public const string WaitSecondsConfigurationKey = "Probes:WaitSeconds";
    public const string PollMillisecondsConfigurationKey = "Probes:PollMilliseconds";
    public const string LeaseSecondsConfigurationKey = "Probes:LeaseSeconds";
    public const string DedupeSecondsConfigurationKey = "Probes:DedupeSeconds";
    public const string RetentionSecondsConfigurationKey = "Probes:RetentionSeconds";

    /// <summary>
    /// The defaults: a 60s caller wait, a 120s lease, a 15s dedupe window and 10 minutes of retention.
    /// The Worker's own <c>ZENKINS_PROBE_TIMEOUT_SECONDS</c> (30s by default) sits well inside the wait,
    /// so a hung remote is a typed <c>git-timeout</c> the caller actually receives.
    /// </summary>
    public static readonly ProbeOptions Defaults =
        new(
            WaitSeconds: 60,
            PollMilliseconds: 250,
            LeaseSeconds: 120,
            DedupeSeconds: 15,
            RetentionSeconds: 600);

    /// <summary>How long a caller waits, as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Wait => TimeSpan.FromSeconds(WaitSeconds);

    /// <summary>The dedupe window, as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan DedupeWindow => TimeSpan.FromSeconds(DedupeSeconds);

    /// <summary>The settled-probe retention, as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Retention => TimeSpan.FromSeconds(RetentionSeconds);

    /// <summary>The lease window, as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Lease => TimeSpan.FromSeconds(LeaseSeconds);

    /// <summary>The poll interval, as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan PollInterval => TimeSpan.FromMilliseconds(PollMilliseconds);

    public static ProbeOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new ProbeOptions(
            ReadDouble(configuration, WaitSecondsConfigurationKey, Defaults.WaitSeconds),
            ReadInt(configuration, PollMillisecondsConfigurationKey, Defaults.PollMilliseconds),
            ReadInt(configuration, LeaseSecondsConfigurationKey, Defaults.LeaseSeconds),
            ReadInt(configuration, DedupeSecondsConfigurationKey, Defaults.DedupeSeconds),
            ReadInt(configuration, RetentionSecondsConfigurationKey, Defaults.RetentionSeconds));
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 1
            ? parsed
            : fallback;

    /// <summary>A positive fractional value (tests want a 0.4s wait, not one real second).</summary>
    private static double ReadDouble(IConfiguration configuration, string key, double fallback) =>
        double.TryParse(configuration[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
            ? parsed
            : fallback;
}
