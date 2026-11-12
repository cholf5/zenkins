using Xunit;
using Zenkins.Worker;

namespace Zenkins.Tests.Workers;

/// <summary>
/// Card F-005's configuration parsing: every failure mode the card lists must produce a clear
/// one-line error (no stack trace), and the defaults must match the card's table.
/// </summary>
[Trait("Category", "Workers")]
public sealed class WorkerConfigurationTests
{
    [Fact]
    public void A_complete_environment_parses_every_value()
    {
        var result = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://127.0.0.1:5067",
            [WorkerConfiguration.RegistrationTokenVariable] = "zreg_abc",
            [WorkerConfiguration.WorkerNameVariable] = "e2e-agent",
            [WorkerConfiguration.WorkerTagsVariable] = "dotnet,ci",
            [WorkerConfiguration.HeartbeatSecondsVariable] = "5",
            [WorkerConfiguration.DataDirectoryVariable] = "/tmp/zenkins-test",
        });

        var configuration = AssertGetValue(result);
        Assert.Equal(new Uri("http://127.0.0.1:5067"), configuration.ServerUrl);
        Assert.Equal("zreg_abc", configuration.RegistrationToken);
        Assert.Equal("e2e-agent", configuration.WorkerName);
        Assert.Equal("dotnet,ci", configuration.Tags);
        Assert.Equal(TimeSpan.FromSeconds(5), configuration.HeartbeatInterval);
        Assert.Equal("/tmp/zenkins-test", configuration.DataDirectory);
    }

    [Fact]
    public void Defaults_follow_the_card_table()
    {
        var result = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "https://zenkins.example.test",
        });

        var configuration = AssertGetValue(result);
        Assert.Null(configuration.RegistrationToken);
        Assert.Equal(Environment.MachineName, configuration.WorkerName);
        Assert.Equal(string.Empty, configuration.Tags);
        Assert.Equal(TimeSpan.FromSeconds(15), configuration.HeartbeatInterval);
        Assert.EndsWith(".zenkins", configuration.DataDirectory, StringComparison.Ordinal);

        // F-007's defaults: work root, temp cleanup on, concurrency 1, 2s claim polling.
        Assert.Equal("work", configuration.WorkRoot);
        Assert.True(configuration.CleanupTemp);
        Assert.Equal(1, configuration.MaxConcurrency);
        Assert.Equal(TimeSpan.FromSeconds(2), configuration.ClaimPollInterval);
        Assert.Equal(1, configuration.BuildRegistrationRequest().MaxConcurrency);

        // F-008's default: 10 000 unsent lines per run while reconnecting.
        Assert.Equal(10_000, configuration.LogBufferLines);
    }

    [Fact]
    public void The_f008_log_buffer_knob_parses_from_the_environment()
    {
        var result = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://localhost",
            [WorkerConfiguration.LogBufferLinesVariable] = "250",
        });

        var configuration = AssertGetValue(result);
        Assert.Equal(250, configuration.LogBufferLines);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1000001")]
    [InlineData("plenty")]
    public void An_out_of_range_log_buffer_is_a_configuration_error(string logBufferLines)
    {
        var result = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://localhost",
            [WorkerConfiguration.LogBufferLinesVariable] = logBufferLines,
        });

        Assert.Null(result.Configuration);
        Assert.NotNull(result.Error);
        Assert.Contains(WorkerConfiguration.LogBufferLinesVariable, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_f007_run_execution_knobs_parse_from_the_environment()
    {
        var result = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://localhost",
            [WorkerConfiguration.WorkRootVariable] = "/var/zenkins/work",
            [WorkerConfiguration.CleanupTempVariable] = "false",
            [WorkerConfiguration.WorkerConcurrencyVariable] = "4",
            [WorkerConfiguration.ClaimSecondsVariable] = "0.5",
        });

        var configuration = AssertGetValue(result);
        Assert.Equal("/var/zenkins/work", configuration.WorkRoot);
        Assert.False(configuration.CleanupTemp);
        Assert.Equal(4, configuration.MaxConcurrency);
        Assert.Equal(TimeSpan.FromMilliseconds(500), configuration.ClaimPollInterval);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    public void An_unparseable_cleanup_flag_is_a_configuration_error(string cleanupTemp)
    {
        var result = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://localhost",
            [WorkerConfiguration.CleanupTempVariable] = cleanupTemp,
        });

        Assert.Null(result.Configuration);
        Assert.NotNull(result.Error);
        Assert.Contains(WorkerConfiguration.CleanupTempVariable, result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("1025")]
    [InlineData("two")]
    public void An_out_of_range_concurrency_is_a_configuration_error(string concurrency)
    {
        var result = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://localhost",
            [WorkerConfiguration.WorkerConcurrencyVariable] = concurrency,
        });

        Assert.Null(result.Configuration);
        Assert.NotNull(result.Error);
        Assert.Contains(WorkerConfiguration.WorkerConcurrencyVariable, result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3601")]
    [InlineData("soon")]
    public void An_out_of_range_claim_interval_is_a_configuration_error(string claimSeconds)
    {
        var result = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://localhost",
            [WorkerConfiguration.ClaimSecondsVariable] = claimSeconds,
        });

        Assert.Null(result.Configuration);
        Assert.NotNull(result.Error);
        Assert.Contains(WorkerConfiguration.ClaimSecondsVariable, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_server_url_is_a_one_line_configuration_error()
    {
        var result = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>());

        Assert.Null(result.Configuration);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("\n", result.Error, StringComparison.Ordinal);
        Assert.Contains(WorkerConfiguration.ServerUrlVariable, result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.test")]
    [InlineData("http://")]
    public void An_unparseable_server_url_is_a_configuration_error(string serverUrl)
    {
        var result = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = serverUrl,
        });

        Assert.Null(result.Configuration);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("\n", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public void An_unparseable_heartbeat_interval_is_a_configuration_error(string interval)
    {
        var result = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://localhost",
            [WorkerConfiguration.HeartbeatSecondsVariable] = interval,
        });

        Assert.Null(result.Configuration);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("\n", result.Error, StringComparison.Ordinal);
        Assert.Contains(WorkerConfiguration.HeartbeatSecondsVariable, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_too_long_worker_name_is_a_configuration_error()
    {
        var result = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://localhost",
            [WorkerConfiguration.WorkerNameVariable] = new string('x', 201),
        });

        Assert.Null(result.Configuration);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Registration_and_heartbeat_payloads_carry_the_platform_capabilities()
    {
        var result = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://localhost",
            [WorkerConfiguration.WorkerNameVariable] = "caps-agent",
            [WorkerConfiguration.WorkerTagsVariable] = "dotnet",
        });

        var configuration = AssertGetValue(result);
        var registration = configuration.BuildRegistrationRequest();

        Assert.Equal("caps-agent", registration.Name);
        Assert.Equal(registration.Os, configuration.BuildHeartbeatRequest().Os);
        Assert.Equal(registration.Architecture, configuration.BuildHeartbeatRequest().Architecture);
        Assert.Equal(Environment.ProcessorCount, registration.CpuCount);
        Assert.False(string.IsNullOrWhiteSpace(registration.Version));
    }

    private static WorkerConfiguration AssertGetValue(WorkerConfigurationResult result)
    {
        if (result.Configuration is null)
        {
            Assert.Fail($"Expected a valid configuration but got: {result.Error}");
        }

        return result.Configuration;
    }
}
