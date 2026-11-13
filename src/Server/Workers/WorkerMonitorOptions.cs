using Microsoft.Extensions.Configuration;

namespace Zenkins.Server.Workers;

/// <summary>
/// Heartbeat-staleness knobs for card F-010, read from configuration once at startup. A worker
/// whose last heartbeat is older than <see cref="OfflineAfterSeconds"/> is marked Offline by the
/// background monitor (PRD §3.4.5: it then receives no new assignments and its running runs are
/// marked Lost). The default of 60 s tolerates a few missed heartbeats at the worker's default
/// 15 s interval - a single missed beat must never flap a worker Offline. Tests configure a short
/// threshold plus a long monitor interval (controllable timing: they drive the monitor's sweep
/// seam directly and must not race its background tick) and drive both directly.
/// </summary>
/// <param name="OfflineAfterSeconds">
/// How long a worker may go without a heartbeat before the monitor marks it Offline.
/// </param>
/// <param name="MonitorIntervalSeconds">
/// How often the background monitor evaluates staleness. Detection latency is bounded by this
/// tick; the exactly-once discipline of the Online → Offline flip does not depend on it.
/// </param>
public sealed record WorkerMonitorOptions(int OfflineAfterSeconds, int MonitorIntervalSeconds)
{
    public const string OfflineAfterSecondsConfigurationKey = "Workers:OfflineAfterSeconds";
    public const string MonitorIntervalSecondsConfigurationKey = "Workers:MonitorIntervalSeconds";

    /// <summary>A few missed heartbeats at the default 15 s interval (card decision).</summary>
    public const int DefaultOfflineAfterSeconds = 60;

    /// <summary>The house sweep cadence (the lease sweeper's interval).</summary>
    public const int DefaultMonitorIntervalSeconds = 15;

    public static WorkerMonitorOptions FromConfiguration(IConfiguration configuration)
    {
        var offlineAfterText = configuration[OfflineAfterSecondsConfigurationKey];
        var offlineAfter = int.TryParse(offlineAfterText, out var offlineAfterParsed)
            && offlineAfterParsed >= 1
                ? offlineAfterParsed
                : DefaultOfflineAfterSeconds;

        var intervalText = configuration[MonitorIntervalSecondsConfigurationKey];
        var interval = int.TryParse(intervalText, out var intervalParsed) && intervalParsed >= 1
            ? intervalParsed
            : DefaultMonitorIntervalSeconds;

        return new WorkerMonitorOptions(offlineAfter, interval);
    }
}
