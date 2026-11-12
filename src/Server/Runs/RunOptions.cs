namespace Zenkins.Server.Runs;

/// <summary>
/// Scheduler knobs for card F-006, read from configuration once at startup (same shape as
/// <see cref="Zenkins.Server.Data.ZenkinsDataOptions"/>). Tests use a short lease window
/// (<c>Runs:LeaseSeconds</c>) instead of waiting on real time.
/// </summary>
/// <param name="LeaseSeconds">
/// How long a claim's lease lasts. The worker must report <c>accepted</c> inside this window or
/// the lease sweeper returns the run to the queue.
/// </param>
/// <param name="SweepIntervalSeconds">
/// How often the background lease sweeper runs. It assigns nothing itself - the pull model does
/// the assignment - it only expires leases.
/// </param>
public sealed record RunOptions(int LeaseSeconds, int SweepIntervalSeconds)
{
    public const string LeaseSecondsConfigurationKey = "Runs:LeaseSeconds";
    public const string SweepIntervalSecondsConfigurationKey = "Runs:SweepIntervalSeconds";

    public static readonly RunOptions Defaults = new(LeaseSeconds: 120, SweepIntervalSeconds: 15);

    public static RunOptions FromConfiguration(IConfiguration configuration)
    {
        var leaseSeconds = ReadSeconds(configuration, LeaseSecondsConfigurationKey, Defaults.LeaseSeconds);
        var sweepIntervalSeconds =
            ReadSeconds(configuration, SweepIntervalSecondsConfigurationKey, Defaults.SweepIntervalSeconds);
        return new RunOptions(leaseSeconds, sweepIntervalSeconds);
    }

    private static int ReadSeconds(IConfiguration configuration, string key, int fallback)
    {
        var text = configuration[key];
        return int.TryParse(text, out var parsed) && parsed >= 1 ? parsed : fallback;
    }
}
