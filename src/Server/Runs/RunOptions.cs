using System.Globalization;

namespace Zenkins.Server.Runs;

/// <summary>
/// Scheduler knobs for card F-006 (lease window, sweep interval) and F-009 (the default per-run
/// timeout), read from configuration once at startup (same shape as
/// <see cref="Zenkins.Server.Data.ZenkinsDataOptions"/>). Tests use a short lease window
/// (<c>Runs:LeaseSeconds</c>) and, for the deadline sweeper, a sub-minute default timeout
/// (<c>Runs:DefaultTimeoutMinutes</c> accepts fractional minutes) instead of waiting on real time.
/// </summary>
/// <param name="LeaseSeconds">
/// How long a claim's lease lasts. The worker must report <c>accepted</c> inside this window or
/// the lease sweeper returns the run to the queue.
/// </param>
/// <param name="SweepIntervalSeconds">
/// How often the background lease sweeper runs. It assigns nothing itself - the pull model does
/// the assignment - it only expires leases.
/// </param>
/// <param name="DefaultTimeout">
/// The execution deadline of a run without a per-run override (F-009): the sweeper asks the worker
/// to cancel a <c>Running</c> run at <c>StartedAt + timeout</c>, and marks it <c>Lost</c> honestly
/// if no terminal report arrives within the F-010 grace period. Fractional minutes are accepted so
/// tests can run the real deadline path against sub-minute values.
/// </param>
public sealed record RunOptions(int LeaseSeconds, int SweepIntervalSeconds, TimeSpan DefaultTimeout)
{
    public const string LeaseSecondsConfigurationKey = "Runs:LeaseSeconds";
    public const string SweepIntervalSecondsConfigurationKey = "Runs:SweepIntervalSeconds";
    public const string DefaultTimeoutMinutesConfigurationKey = "Runs:DefaultTimeoutMinutes";

    public static readonly RunOptions Defaults =
        new(LeaseSeconds: 120, SweepIntervalSeconds: 15, DefaultTimeout: TimeSpan.FromMinutes(60));

    public static RunOptions FromConfiguration(IConfiguration configuration)
    {
        var leaseSeconds = ReadSeconds(configuration, LeaseSecondsConfigurationKey, Defaults.LeaseSeconds);
        var sweepIntervalSeconds =
            ReadSeconds(configuration, SweepIntervalSecondsConfigurationKey, Defaults.SweepIntervalSeconds);
        return new RunOptions(leaseSeconds, sweepIntervalSeconds, ReadDefaultTimeout(configuration));
    }

    private static int ReadSeconds(IConfiguration configuration, string key, int fallback)
    {
        var text = configuration[key];
        return int.TryParse(text, out var parsed) && parsed >= 1 ? parsed : fallback;
    }

    private static TimeSpan ReadDefaultTimeout(IConfiguration configuration)
    {
        var text = configuration[DefaultTimeoutMinutesConfigurationKey];
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
            && minutes > 0
                ? TimeSpan.FromMinutes(minutes)
                : Defaults.DefaultTimeout;
    }
}
