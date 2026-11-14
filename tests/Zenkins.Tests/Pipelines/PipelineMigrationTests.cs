using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Tests.Pipelines;

/// <summary>
/// Card F-014's migration, applied for real: a database migrated to the migration before this card and
/// holding a pre-existing argv run is brought to the current model, and the row must still be readable,
/// terminal and pointed at its project's branch and pipeline path - the issue's "existing rows are
/// backfilled and remain readable" clause (they are never re-executed).
/// <para>
/// The migration set is the PostgreSQL one and SQLite is dynamically typed, so this runs the same
/// migration files the product ships (docs/development.md's migration contract).
/// </para>
/// </summary>
[Trait("Category", "Pipeline")]
public sealed class PipelineMigrationTests
{
    /// <summary>The last migration before this card: everything that existed before F-014.</summary>
    private const string PreviousMigration = "20261113091057_AddGitCredentials";

    [Fact]
    public async Task A_pre_existing_argv_run_is_backfilled_with_its_projects_branch_and_pipeline_path()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"zenkins-f014-migration-{Guid.NewGuid():N}.db");
        var organizationId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        try
        {
            var migrationScope = new SystemDataScope();
            await using (var context = CreateContext(databasePath, migrationScope))
            {
                // 1. Bring the database to the state a deployment would be in before this card.
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync(PreviousMigration, TestContext.Current.CancellationToken);

                // 2. Seed a project and one argv run exactly as the old schema stored them.
                await context.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "Organizations" ("Id", "Name", "CreatedAt", "UpdatedAt")
                    VALUES ({0}, 'Legacy', '2026-01-01 00:00:00+00', '2026-01-01 00:00:00+00');

                    INSERT INTO "Projects" ("Id", "OrganizationId", "Name", "RepositoryUrl", "DefaultBranch",
                                            "PipelinePath", "GitCredentialId", "CreatedAt", "UpdatedAt")
                    VALUES ({1}, {0}, 'legacy-project', 'https://example.test/legacy.git', 'develop',
                            'ci/legacy.lua', NULL, '2026-01-01 00:00:00+00', '2026-01-01 00:00:00+00');

                    INSERT INTO "BuildRuns" ("Id", "OrganizationId", "ProjectId", "Status", "TriggerType",
                                             "TriggerUserId", "RequiredTags", "CommandJson", "CreatedAt", "UpdatedAt")
                    VALUES ({2}, {0}, {1}, 'Success', 'Manual', NULL, '', '["dotnet","--version"]',
                            '2026-01-01 00:00:00+00', '2026-01-01 00:00:00+00');
                    """,
                    // SQLite stores an EF Guid as uppercase text, and the query filter compares it as text.
                    new object[]
                    {
                        organizationId.ToString().ToUpperInvariant(),
                        projectId.ToString().ToUpperInvariant(),
                        runId.ToString().ToUpperInvariant(),
                    },
                    TestContext.Current.CancellationToken);
            }

            var systemDataScope = new SystemDataScope();
            await using (var context = CreateContext(databasePath, systemDataScope))
            {
                // 3. Apply this card's migration: the columns appear and are backfilled from the project.
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync(targetMigration: null, TestContext.Current.CancellationToken);

                using var scope = systemDataScope.BeginScope();
                var rows = await context.SystemQueryAsync<BuildRun>(
                    candidate => candidate.Id == runId,
                    TestContext.Current.CancellationToken);
                var run = rows.Single();

                Assert.Equal("develop", run.GitBranch);
                Assert.Equal("ci/legacy.lua", run.PipelinePath);
                Assert.Equal(BuildRunStatus.Success, run.Status);
                Assert.Null(run.PipelineSpecJson);
                Assert.Null(run.GitCommit); // the legacy run never reported a commit
                Assert.Equal("", run.RequiredTags);
                Assert.Equal("Manual", run.TriggerType);
            }
        }
        finally
        {
            // Q-011: the old TryDelete swallowed IOException only, so a briefly-pooled SQLite handle
            // left the database file behind; TempRootCleanup retries and records any residual.
            TempRootCleanup.DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Rolling_back_writes_a_self_describing_marker_instead_of_an_empty_command()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"zenkins-f014-down-{Guid.NewGuid():N}.db");
        var organizationId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var systemDataScope = new SystemDataScope();

        try
        {
            await using (var context = CreateContext(databasePath, systemDataScope))
            {
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync(PreviousMigration, TestContext.Current.CancellationToken);
                await context.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "Organizations" ("Id", "Name", "CreatedAt", "UpdatedAt")
                    VALUES ({0}, 'Legacy', '2026-01-01 00:00:00+00', '2026-01-01 00:00:00+00');
                    INSERT INTO "Projects" ("Id", "OrganizationId", "Name", "RepositoryUrl", "DefaultBranch",
                                            "PipelinePath", "GitCredentialId", "CreatedAt", "UpdatedAt")
                    VALUES ({1}, {0}, 'legacy-project', 'https://example.test/legacy.git', 'develop',
                            'ci/legacy.lua', NULL, '2026-01-01 00:00:00+00', '2026-01-01 00:00:00+00');
                    INSERT INTO "BuildRuns" ("Id", "OrganizationId", "ProjectId", "Status", "TriggerType",
                                             "TriggerUserId", "RequiredTags", "CommandJson", "CreatedAt", "UpdatedAt")
                    VALUES ({2}, {0}, {1}, 'Success', 'Manual', NULL, '', '["dotnet","--version"]',
                            '2026-01-01 00:00:00+00', '2026-01-01 00:00:00+00');
                    """,
                    new object[]
                    {
                        organizationId.ToString().ToUpperInvariant(),
                        projectId.ToString().ToUpperInvariant(),
                        runId.ToString().ToUpperInvariant(),
                    },
                    TestContext.Current.CancellationToken);

                // Up, then back down: the argv is gone for good, so the rollback must not pretend the
                // run had an empty command.
                await migrator.MigrateAsync(targetMigration: null, TestContext.Current.CancellationToken);
                await migrator.MigrateAsync(PreviousMigration, TestContext.Current.CancellationToken);

                await using var command = context.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT \"CommandJson\" FROM \"BuildRuns\" WHERE \"Id\" = $id";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "$id";
                parameter.Value = runId.ToString().ToUpperInvariant();
                command.Parameters.Add(parameter);
                if (command.Connection!.State != System.Data.ConnectionState.Open)
                {
                    await command.Connection.OpenAsync(TestContext.Current.CancellationToken);
                }

                var stored = (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
                Assert.Equal("""
                    ["argv-not-recoverable"]
                    """, stored);
            }
        }
        finally
        {
            // Q-011: the old TryDelete swallowed IOException only, so a briefly-pooled SQLite handle
            // left the database file behind; TempRootCleanup retries and records any residual.
            TempRootCleanup.DeleteDatabase(databasePath);
        }
    }

    private static ZenkinsDbContext CreateContext(string databasePath, SystemDataScope systemDataScope)
    {
        var options = new DbContextOptionsBuilder<ZenkinsDbContext>()
            .UseZenkinsDatabase(new ZenkinsDataOptions(
                DatabaseProvider.Sqlite,
                $"Data Source={databasePath}"))
            .Options;
        return new ZenkinsDbContext(options, new TenantContext(), systemDataScope);
    }
}
