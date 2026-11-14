using Microsoft.Extensions.Configuration;
using Xunit;
using Zenkins.Server.Artifacts;
using Zenkins.Server.Data;
using Zenkins.Server.Secrets;
using Zenkins.Worker;

namespace Zenkins.Tests;

/// <summary>
/// Card I-003's path discipline, pinned where every resolver lives: a relative persistent path - the
/// <c>App_Data</c> defaults and any configured relative value - resolves from
/// <see cref="AppContext.BaseDirectory"/>, the publish/install directory, and never from the process
/// working directory, the content root or <c>Assembly.Location</c>. A service-packaged server or
/// single-file worker runs with an arbitrary working directory (launchd starts jobs in <c>/</c>), so
/// the data must follow its binaries. The one deliberate exception is the design-time EF factory,
/// which passes its own source-tree base explicitly and is covered by that file's documentation.
/// </summary>
[Trait("Category", "Release")]
public sealed class ReleasePackagingTests
{
    [Fact]
    public void The_default_sqlite_database_resolves_from_the_application_base_directory()
    {
        var options = ZenkinsDataOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Equal(DatabaseProvider.Sqlite, options.Provider);
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "App_Data", "zenkins.db"),
            SqliteDataSource(options.ConnectionString));
    }

    [Fact]
    public void A_relative_artifact_root_resolves_from_the_application_base_directory()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [ArtifactOptions.RootPathConfigurationKey] = "data/artifacts",
            }).Build();

        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "data", "artifacts"),
            ArtifactOptions.ResolveRootPath(configuration));
    }

    [Fact]
    public void The_default_artifact_root_lands_beside_the_default_database()
    {
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "App_Data", "artifacts"),
            ArtifactOptions.ResolveRootPath(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void An_absolute_artifact_root_passes_through_unchanged()
    {
        var absolute = Path.Combine(Path.GetTempPath(), $"zenkins-i003-{Guid.NewGuid():N}", "store");

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [ArtifactOptions.RootPathConfigurationKey] = absolute,
            }).Build();

        Assert.Equal(Path.GetFullPath(absolute), ArtifactOptions.ResolveRootPath(configuration));
    }

    [Fact]
    public void A_relative_key_ring_resolves_from_the_application_base_directory()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [SecretDataProtection.KeysDirectoryConfigurationKey] = "custom/keys",
            }).Build();

        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "custom", "keys"),
            SecretDataProtection.ResolveKeysDirectory(configuration));
    }

    [Fact]
    public void The_postgres_fallback_key_ring_lands_beside_the_publish_directory()
    {
        var dataOptions = new ZenkinsDataOptions(
            DatabaseProvider.Postgres,
            "Host=localhost;Port=5432;Database=zenkins;Username=zenkins;Password=zenkins");

        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "App_Data", "keys"),
            SecretDataProtection.ResolveKeysDirectory(new ConfigurationBuilder().Build(), dataOptions));
    }

    [Fact]
    public void The_default_worker_work_root_resolves_from_the_application_base_directory()
    {
        var configuration = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://127.0.0.1:5067",
        });

        var workRoot = AssertGetValue(configuration).ResolveWorkRoot();
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "work"), workRoot);
    }

    [Fact]
    public void A_relative_worker_work_root_resolves_from_the_application_base_directory()
    {
        var configuration = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://127.0.0.1:5067",
            [WorkerConfiguration.WorkRootVariable] = "runs",
        });

        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "runs"),
            AssertGetValue(configuration).ResolveWorkRoot());
    }

    [Fact]
    public void An_absolute_worker_work_root_passes_through_unchanged()
    {
        var absolute = Path.Combine(Path.GetTempPath(), $"zenkins-i003-work-{Guid.NewGuid():N}");
        var configuration = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://127.0.0.1:5067",
            [WorkerConfiguration.WorkRootVariable] = absolute,
        });

        Assert.Equal(Path.GetFullPath(absolute), AssertGetValue(configuration).ResolveWorkRoot());
    }

    /// <summary>The Data Source of a SQLite connection string, as the path the provider would open.</summary>
    private static string SqliteDataSource(string connectionString)
    {
        var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        Assert.False(string.IsNullOrWhiteSpace(dataSource), $"no Data Source in '{connectionString}'.");
        return Path.GetFullPath(dataSource);
    }

    private static WorkerConfiguration AssertGetValue(WorkerConfigurationResult result)
    {
        Assert.Null(result.Error);
        Assert.NotNull(result.Configuration);
        return result.Configuration;
    }
}
