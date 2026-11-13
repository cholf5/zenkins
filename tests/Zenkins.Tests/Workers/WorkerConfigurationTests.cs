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

    // ------------------------------------------------------------------
    // F-013: the Git knobs
    // ------------------------------------------------------------------

    [Fact]
    public void The_git_knobs_default_to_a_full_clone_without_a_pinned_known_hosts_file()
    {
        var result = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://localhost",
        });

        var configuration = AssertGetValue(result);

        // Depth 0 is a full clone: a shallow clone is opt-in, because a truncated history cannot resolve
        // an older pinned commit.
        Assert.Equal(0, configuration.GitDepth);
        Assert.Null(configuration.GitKnownHostsPath);
        Assert.Equal(TimeSpan.FromMinutes(5), configuration.GitOperationTimeout);
    }

    [Fact]
    public void The_git_knobs_parse_and_reach_the_workspace_options()
    {
        var result = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://localhost",
            [WorkerConfiguration.GitDepthVariable] = "25",
            [WorkerConfiguration.GitKnownHostsVariable] = "/etc/zenkins/known_hosts",
            [WorkerConfiguration.GitTimeoutSecondsVariable] = "90",
        });

        var configuration = AssertGetValue(result);
        Assert.Equal(25, configuration.GitDepth);
        Assert.Equal("/etc/zenkins/known_hosts", configuration.GitKnownHostsPath);
        Assert.Equal(TimeSpan.FromSeconds(90), configuration.GitOperationTimeout);

        var work = new RunWorkDirectory(
            Guid.NewGuid(),
            "run-root",
            "run-root/repository",
            "run-root/artifacts",
            "run-root/temp");
        var options = configuration.BuildGitWorkspaceOptions(
            work,
            runDeadline: DateTimeOffset.UtcNow.AddMinutes(3),
            log: _ => { });

        Assert.Equal(25, options.Depth);
        Assert.Equal("/etc/zenkins/known_hosts", options.KnownHostsPath);
        Assert.Equal(TimeSpan.FromSeconds(90), options.OperationTimeout);
        Assert.NotNull(options.RunDeadline);
        Assert.Same(work, options.Work);
    }

    [Theory]
    [InlineData(WorkerConfiguration.GitDepthVariable, "-1")]
    [InlineData(WorkerConfiguration.GitDepthVariable, "not-a-number")]
    [InlineData(WorkerConfiguration.GitDepthVariable, "100001")]
    [InlineData(WorkerConfiguration.GitTimeoutSecondsVariable, "0")]
    [InlineData(WorkerConfiguration.GitTimeoutSecondsVariable, "86401")]
    [InlineData(WorkerConfiguration.GitTimeoutSecondsVariable, "soon")]
    public void An_invalid_git_knob_is_one_clear_line_naming_the_variable(string variable, string value)
    {
        var environment = new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://localhost",
            [variable] = value,
        };

        var result = WorkerConfiguration.FromEnvironment(environment);

        Assert.Null(result.Configuration);
        Assert.NotNull(result.Error);
        Assert.StartsWith("configuration error: " + variable, result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", result.Error, StringComparison.Ordinal);
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
