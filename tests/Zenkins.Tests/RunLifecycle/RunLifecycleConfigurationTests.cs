using Microsoft.Extensions.Configuration;
using Xunit;
using Zenkins.Server.Runs;
using Zenkins.Server.Workers;

namespace Zenkins.Tests.RunLifecycle;

/// <summary>
/// The two cards' configuration knobs, pinned: <c>Workers:OfflineAfterSeconds</c> (F-010's
/// staleness threshold, default 60, configurable so tests can use short values) and
/// <c>Runs:DefaultTimeoutMinutes</c> (F-009's default deadline, 60 minutes, fractional minutes
/// accepted so tests can exercise the real deadline path sub-minute).
/// </summary>
[Trait("Category", "RunLifecycle")]
public sealed class RunLifecycleConfigurationTests
{
    [Fact]
    public void The_offline_threshold_defaults_to_sixty_seconds_and_honours_valid_values()
    {
        Assert.Equal(60, FromWorkersConfig(null).OfflineAfterSeconds);
        Assert.Equal(5, FromWorkersConfig("5").OfflineAfterSeconds);
        Assert.Equal(1, FromWorkersConfig("1").OfflineAfterSeconds);

        // Out-of-range or unparsable values fall back to the default - never a negative or zero
        // threshold that would mark every worker Offline on the first tick.
        Assert.Equal(60, FromWorkersConfig("0").OfflineAfterSeconds);
        Assert.Equal(60, FromWorkersConfig("-3").OfflineAfterSeconds);
        Assert.Equal(60, FromWorkersConfig("soon").OfflineAfterSeconds);
    }

    [Fact]
    public void The_monitor_interval_defaults_to_fifteen_seconds_and_is_controllable_for_tests()
    {
        Assert.Equal(15, FromWorkersConfig(null).MonitorIntervalSeconds);
        Assert.Equal(3600, FromWorkersConfigWithInterval("3600").MonitorIntervalSeconds);
        Assert.Equal(15, FromWorkersConfigWithInterval("0").MonitorIntervalSeconds);
        Assert.Equal(15, FromWorkersConfigWithInterval("later").MonitorIntervalSeconds);
    }

    private static WorkerMonitorOptions FromWorkersConfigWithInterval(string? value) =>
        WorkerMonitorOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [WorkerMonitorOptions.MonitorIntervalSecondsConfigurationKey] = value,
            })
            .Build());

    [Fact]
    public void The_default_timeout_defaults_to_sixty_minutes_and_accepts_sub_minute_test_values()
    {
        Assert.Equal(TimeSpan.FromMinutes(60), FromRunsConfig(null).DefaultTimeout);
        Assert.Equal(TimeSpan.FromMinutes(30), FromRunsConfig("30").DefaultTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), FromRunsConfig("0.05").DefaultTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(250), FromRunsConfig("0.004166666666666667").DefaultTimeout);

        Assert.Equal(TimeSpan.FromMinutes(60), FromRunsConfig("0").DefaultTimeout);
        Assert.Equal(TimeSpan.FromMinutes(60), FromRunsConfig("-1").DefaultTimeout);
        Assert.Equal(TimeSpan.FromMinutes(60), FromRunsConfig("later").DefaultTimeout);
    }

    private static WorkerMonitorOptions FromWorkersConfig(string? value) =>
        WorkerMonitorOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [WorkerMonitorOptions.OfflineAfterSecondsConfigurationKey] = value,
            })
            .Build());

    private static RunOptions FromRunsConfig(string? value) =>
        RunOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RunOptions.DefaultTimeoutMinutesConfigurationKey] = value,
            })
            .Build());
}
