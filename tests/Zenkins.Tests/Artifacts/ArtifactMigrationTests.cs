using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Artifacts;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Shared.Artifacts;
using Zenkins.Tests.Runs;
using Zenkins.Tests.Tenancy;

namespace Zenkins.Tests.Artifacts;

/// <summary>
/// Card F-016's migration contract and tenancy registration, pinned as tests (docs/development.md's migration
/// contract: one PostgreSQL-shaped migration set, and the tenancy harness must cover every tenant-owned
/// entity):
/// <list type="bullet">
/// <item>the generated files carry Npgsql's type names (<c>uuid</c>, <c>timestamp with time zone</c>,
/// <c>character varying</c>) - a SQLite-generated set would carry none of them and would silently break the
/// single-history contract;</item>
/// <item>the artifact table, its unique <c>(RunId, Name)</c> index and both cascades are in the migration;</item>
/// <item><see cref="Artifact"/> is registered with the tenancy probes, so the isolation harness's
/// reflection guard cannot be satisfied by leaving the type out.</item>
/// </list>
/// </summary>
[Trait("Category", "Artifacts")]
public sealed class ArtifactMigrationTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_artifacts_migration_is_postgresql_shaped_and_creates_the_tenant_owned_table()
    {
        var migrationsDirectory = MigrationsDirectory();
        var migration = Path.Combine(migrationsDirectory, "20261113130215_AddArtifacts.cs");
        var designer = Path.Combine(migrationsDirectory, "20261113130215_AddArtifacts.Designer.cs");
        var snapshot = Path.Combine(migrationsDirectory, "ZenkinsDbContextModelSnapshot.cs");

        Assert.True(File.Exists(migration), $"the migration file is missing: {migration}");
        Assert.True(File.Exists(designer), $"the migration's designer file is missing: {designer}");

        var up = await File.ReadAllTextAsync(migration, CancellationToken);

        // Npgsql shapes exactly: a SQLite-generated set would have none of these strings.
        Assert.Contains("type: \"uuid\"", up, StringComparison.Ordinal);
        Assert.Contains("type: \"timestamp with time zone\"", up, StringComparison.Ordinal);
        Assert.Contains("type: \"character varying(512)\"", up, StringComparison.Ordinal);
        Assert.Contains("name: \"Artifacts\"", up, StringComparison.Ordinal);

        // The unique index is the duplicate-name guard, and both cascades are what makes the row a child of its
        // run and its organization.
        Assert.Contains("IX_Artifacts_RunId_Name", up, StringComparison.Ordinal);
        Assert.Contains("unique: true", up, StringComparison.Ordinal);
        Assert.Contains("principalTable: \"BuildRuns\"", up, StringComparison.Ordinal);
        Assert.Contains("principalTable: \"Organizations\"", up, StringComparison.Ordinal);
        Assert.Contains("onDelete: ReferentialAction.Cascade", up, StringComparison.Ordinal);

        // The check constraints keep the two string discriminators honest at the storage layer.
        Assert.Contains("CK_Artifacts_Kind", up, StringComparison.Ordinal);
        Assert.Contains("CK_Artifacts_Status", up, StringComparison.Ordinal);

        var designerText = await File.ReadAllTextAsync(designer, CancellationToken);
        Assert.Contains("Npgsql", designerText, StringComparison.Ordinal);

        var snapshotText = await File.ReadAllTextAsync(snapshot, CancellationToken);
        Assert.Contains("Artifact", snapshotText, StringComparison.Ordinal);
        Assert.DoesNotContain("Sqlite", snapshotText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_artifact_entity_is_covered_by_the_tenancy_probes()
    {
        // A reflection guard already fails when a tenant-owned entity is missing; this is the explicit line the
        // card's checklist asks for, so the coverage is visible in a diff of the artifact feature.
        Assert.Contains(typeof(Artifact), TenantIsolationProbes.CoveredEntityTypes);
        Assert.Contains(
            typeof(Artifact),
            typeof(ZenkinsDbContext).Assembly.GetTypes().Where(type => typeof(Shared.Tenancy.ITenantOwned).IsAssignableFrom(type)));

        // And the fixture seeds one artifact per organization, which is what makes the read probe
        // non-vacuous (the probe refuses to pass on an empty table).
        Assert.NotEqual(TwoOrgsFixture.ArtifactAId, TwoOrgsFixture.ArtifactBId);
        Assert.NotEqual(TwoOrgsFixture.ArtifactAName, TwoOrgsFixture.ArtifactBName);
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task A_test_host_booting_the_real_Program_never_creates_the_store_inside_the_repository()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        // A full test run must not leave src/Server/App_Data/artifacts behind: `**/App_Data/` hides it from
        // `git status`, so such pollution is invisible and would accumulate with every run. The shared host
        // points the store at its own temp directory, and the startup assertion creates that one - not the
        // repository's.
        using var host = await RunTestHost.CreateAsync();

        var serverProject = RepositoryServerProjectDirectory();
        var repository = Directory.GetParent(Directory.GetParent(serverProject)!.FullName)!.FullName;
        var options = host.Host.Services.GetRequiredService<ArtifactOptions>();

        Assert.False(
            ArtifactPathRules.IsInside(Path.GetFullPath(repository), Path.GetFullPath(options.RootPath)),
            $"the test host resolved the artifact store to '{options.RootPath}', inside the repository");
        Assert.False(
            Directory.Exists(Path.Combine(serverProject, "App_Data", "artifacts")),
            "a test host created src/Server/App_Data/artifacts inside the repository");
        Assert.True(
            Directory.Exists(options.RootPath),
            $"the startup assertion should have created the store root '{options.RootPath}'");
    }

    /// <summary>The repository's <c>src/Server</c> project directory (found by walking up from the test binary).</summary>
    private static string RepositoryServerProjectDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Server");
            if (File.Exists(Path.Combine(candidate, "Zenkins.Server.csproj")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("the repository's server project directory could not be located");
    }

    private static string MigrationsDirectory()
    {
        // The test binary lives in tests/Zenkins.Tests/bin/<configuration>/<tfm>; walk up to the repository and
        // read the product's migration folder (the files under test are the shipped ones).
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Server", "Data", "Migrations");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("the repository's migration directory could not be located");
    }
}
