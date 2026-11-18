using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Folders;

namespace Zenkins.Tests.Folders;

/// <summary>
/// Card F-042's migration fact, on a real database rather than on the snapshot: a database that already
/// holds projects is upgraded from the migration <em>before</em> this card to the current one, and every
/// pre-existing row survives the new folders columns and the new unique index.
/// <para>
/// <b>Why this is the fact that matters.</b> The card changes what "unique" means (organization-wide to
/// folder-wide) and adds two derived columns to a populated table. The safe claim is not "the old index
/// was stronger" - it is that after the upgrade the old rows still satisfy the new index, which is what
/// the duplicate insert below proves. The project names are deliberately mixed-case (<c>Build</c>), so
/// the assertion also covers the backfilled case-insensitive key.
/// </para>
/// </summary>
[Trait("Category", "Folders")]
public sealed class FolderMigrationUpgradeTests
{
    /// <summary>The migration immediately before F-042's own (F-041's parameter-file migration).</summary>
    private const string PreviousMigration = "20261118131532_AddRunParameterFiles";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_upgrade_keeps_every_existing_project_and_the_new_index_covers_them()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"zenkins-f042-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            var tenantContext = new TenantContext();
            var systemDataScope = new SystemDataScope();
            var organizationId = Guid.NewGuid();
            var firstProjectId = Guid.NewGuid();
            var secondProjectId = Guid.NewGuid();

            // ---- 1. The database as it was before this card.
            await using (var context = CreateContext(databasePath, tenantContext, systemDataScope))
            {
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync(PreviousMigration, CancellationToken);

                await using var connection = new SqliteConnection($"Data Source={databasePath}");
                await connection.OpenAsync(CancellationToken);

                await ExecuteAsync(
                    connection,
                    "INSERT INTO \"Organizations\" (\"Id\", \"Name\", \"CreatedAt\", \"UpdatedAt\") "
                    + $"VALUES ('{organizationId}', 'Upgrade Org', '2026-11-18 10:00:00+00:00', '2026-11-18 10:00:00+00:00')");

                // Two pre-existing projects in one organization, one of them mixed-case: the old
                // organization-wide index allowed both, and the case-insensitive key must not merge them.
                await InsertProjectAsync(connection, firstProjectId, organizationId, "Build");
                await InsertProjectAsync(connection, secondProjectId, organizationId, "release");
            }

            // ---- 2. The upgrade under test.
            await using (var context = CreateContext(databasePath, tenantContext, systemDataScope))
            {
                await context.Database.MigrateAsync(CancellationToken);
            }

            // ---- 3. Every old row survived, with the new columns derived from it.
            await using (var context = CreateContext(databasePath, tenantContext, systemDataScope))
            {
                var projects = await context.Projects
                    .IgnoreQueryFilters()
                    .OrderBy(project => project.Name)
                    .ToListAsync(CancellationToken);

                Assert.Equal(2, projects.Count);
                Assert.Equal(
                    new[] { "Build", "release" },
                    projects.Select(project => project.Name).ToArray());

                var build = projects.Single(project => project.Id == firstProjectId);
                Assert.Equal("build", build.NameKey);
                Assert.Null(build.FolderId);
                Assert.Equal(Guid.Empty, build.FolderKey);
                Assert.Equal("release", projects.Single(project => project.Id == secondProjectId).NameKey);

                // The database itself carries the derived keys: lower(Name) for the migration's backfill
                // and the empty folder key, exactly what FolderService compares against.
                var stored = await QueryAsync(
                    context,
                    "SELECT \"Name\", \"NameKey\", \"FolderKey\", \"FolderId\" FROM \"Projects\" ORDER BY \"Name\"");
                Assert.Equal(2, stored.Count);
                Assert.Equal("Build", stored[0][0]);
                Assert.Equal("build", stored[0][1]);
                Assert.Equal(Guid.Empty.ToString(), stored[0][2]);
                Assert.Null(stored[0][3]);

                // ---- 4. The new constraint holds over the migrated data: a second root name that only
                // differs in case is refused by the *database*, not by a pre-flight query. This is the
                // exact insert a concurrent second create would make.
                var duplicate = await Assert.ThrowsAnyAsync<SqliteException>(() =>
                    InsertProjectAsync(context.Database.GetDbConnection(), Guid.NewGuid(), organizationId, "BUILD"));
                Assert.Contains("UNIQUE", duplicate.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { databasePath, databasePath + "-shm", databasePath + "-wal" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    private static ZenkinsDbContext CreateContext(
        string databasePath,
        TenantContext tenantContext,
        SystemDataScope systemDataScope) =>
        new(
            // The product's own provider configuration: SQLite is pointed at a temp file, and its
            // "pending model changes" warning (a store-type-name difference the single Postgres-first
            // migration set always produces) is suppressed exactly as the application does.
            new DbContextOptionsBuilder<ZenkinsDbContext>()
                .UseZenkinsDatabase<ZenkinsDbContext>(new ZenkinsDataOptions(
                    DatabaseProvider.Sqlite,
                    $"Data Source={databasePath}"))
                .Options,
            tenantContext,
            systemDataScope);

    /// <summary>
    /// Inserts one project into the schema as it stands, naming every NOT NULL column from an explicit
    /// map. The map is deliberately closed: a column the schema grew without this test knowing would throw
    /// instead of being silently left out.
    /// </summary>
    private static async Task InsertProjectAsync(
        DbConnection connection,
        Guid projectId,
        Guid organizationId,
        string name)
    {
        var columns = new List<(string Name, bool NotNull, string? Default, int PrimaryKey)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info('Projects')";
            await using var reader = await command.ExecuteReaderAsync(CancellationToken);
            while (await reader.ReadAsync(CancellationToken))
            {
                columns.Add((
                    reader.GetString(1),
                    reader.GetInt32(3) == 1,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt32(5)));
            }
        }

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken);
        }

        // Every NOT NULL column is named explicitly, including the ones that carry a database default:
        // the row this test writes must be the row a raw writer would write, and relying on
        // "NameKey DEFAULT ''" would quietly insert the empty key instead of the name's key.
        var required = columns
            .Where(column => column.NotNull)
            .Select(column => column.Name)
            .ToArray();

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Id"] = $"'{projectId}'",
            ["OrganizationId"] = $"'{organizationId}'",
            ["Name"] = $"'{name}'",
            ["NameKey"] = $"'{FolderValidation.NameKeyFor(name)}'",
            ["FolderId"] = "NULL",
            ["FolderKey"] = $"'{Guid.Empty}'",
            ["Kind"] = "'pipeline'",
            ["PipelineSource"] = "'repository'",
            ["BranchDiscovery"] = "'single'",
            ["RepositoryUrl"] = "'https://example.test/upgrade.git'",
            ["DefaultBranch"] = "'main'",
            ["PipelinePath"] = "'pipeline.lua'",
            ["InlineScript"] = "NULL",
            ["FreestyleSpecJson"] = "NULL",
            ["ParametersJson"] = "NULL",
            ["GitCredentialId"] = "NULL",
            ["GitCredentialAssignedAt"] = "NULL",
            ["GitCredentialAssignedByUserId"] = "NULL",
            ["Disabled"] = "0",
            ["DisabledReason"] = "NULL",
            ["DisabledAt"] = "NULL",
            ["DisabledByUserId"] = "NULL",
            ["MaxConcurrentRuns"] = "NULL",
            ["QuietPeriodSeconds"] = "0",
            ["MaxQueuedRuns"] = "NULL",
            ["PollingEnabled"] = "0",
            ["PollIntervalMinutes"] = "NULL",
            ["LastPollAt"] = "NULL",
            ["LastPollOutcome"] = "NULL",
            ["LastPollMessage"] = "NULL",
            ["PollFailureStreak"] = "0",
            ["LastProbeOutcome"] = "NULL",
            ["LastProbeAt"] = "NULL",
            ["LastProbeMessage"] = "NULL",

            // F-031: the post-step failure switch, added by a migration between this test's base revision and
            // the current head - so the duplicate insert at step 4 runs against a schema that has it.
            ["PostFailureFailsRun"] = "0",
            ["CreatedAt"] = "'2026-11-18 10:00:00+00:00'",
            ["UpdatedAt"] = "'2026-11-18 10:00:00+00:00'",
        };

        var missing = required.Where(column => !values.ContainsKey(column)).ToArray();
        Assert.True(
            missing.Length == 0,
            $"The Projects schema requires column(s) this upgrade test does not know: {string.Join(", ", missing)}.");

        var sql = $"INSERT INTO \"Projects\" ({string.Join(", ", required.Select(column => $"\"{column}\""))}) "
            + $"VALUES ({string.Join(", ", required.Select(column => values[column]))})";
        await ExecuteAsync(connection, sql);
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    private static async Task<List<string?[]>> QueryAsync(ZenkinsDbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);

        var rows = new List<string?[]>();
        while (await reader.ReadAsync(CancellationToken))
        {
            var row = new string?[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index)?.ToString();
            }

            rows.Add(row);
        }

        return rows;
    }
}
