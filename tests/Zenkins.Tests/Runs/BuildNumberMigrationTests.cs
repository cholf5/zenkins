using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Tests.Runs;

/// <summary>
/// Card F-036's migration, applied for real (<c>docs/development.md</c>'s migration contract): a database
/// that already holds runs is migrated from this card's predecessor, and those runs come out numbered
/// deterministically - per project, in creation order, with a tie broken by id - and impossible to
/// duplicate afterwards.
/// <para>
/// The two facts the card asks for are exactly the two facts asserted here:
/// </para>
/// <list type="bullet">
/// <item><b>"Existing runs are backfilled deterministically."</b> The rows are inserted with their
/// <c>CreatedAt</c> instants in an order that differs from insertion order (and two of them share one
/// instant), so a backfill that used the row order or ignored the tie would produce a different answer.</item>
/// <item><b>"The backfill cannot produce duplicates."</b> After the migration, the unique
/// <c>(ProjectId, Number)</c> index exists and refuses a second row with an existing number - so the
/// assign-and-hope failure mode cannot hide in the schema.</item>
/// </list>
/// <para>
/// Like the other migration tests, the shipped PostgreSQL migration files are run against SQLite
/// (dynamically typed), and the <c>Down</c> direction is exercised too: dropping the column leaves the
/// history intact.
/// </para>
/// </summary>
[Trait("Category", "Runs")]
public sealed class BuildNumberMigrationTests
{
    /// <summary>The last migration before this card: runs without a build number.</summary>
    private const string PreviousMigration = "20261116063155_AddApiTokens";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Existing_runs_are_backfilled_per_project_in_creation_order_and_the_index_refuses_duplicates()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"zenkins-f036-migration-{Guid.NewGuid():N}.db");

        var organizationId = Guid.NewGuid();
        var firstProjectId = Guid.NewGuid();
        var secondProjectId = Guid.NewGuid();

        // Three runs of the first project: two share an instant (the tie the id must break), and the oldest
        // one is inserted LAST - a backfill that trusted insertion order would number them wrongly.
        var tieLowId = Guid.Parse("0197c0de-0000-0000-0000-000000000001");
        var tieHighId = Guid.Parse("0197c0de-0000-0000-0000-000000000002");
        var oldest = Guid.NewGuid();
        var otherProjectRun = Guid.NewGuid();

        try
        {
            var dataScope = new SystemDataScope();
            await using (var context = CreateContext(databasePath, dataScope))
            {
                // 1. The state a deployment is in before this card.
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync(PreviousMigration, CancellationToken);

                // 2. A history: three runs of one project (out of order, with a tie) and one of another.
                await context.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "Organizations" ("Id", "Name", "CreatedAt", "UpdatedAt")
                    VALUES ({0}, 'Legacy', '2026-01-01 00:00:00+00', '2026-01-01 00:00:00+00');

                    INSERT INTO "Projects" ("Id", "OrganizationId", "Name", "RepositoryUrl",
                                            "DefaultBranch", "PipelinePath", "Kind", "Disabled",
                                            "QuietPeriodSeconds", "CreatedAt", "UpdatedAt")
                    VALUES ({1}, {0}, 'legacy-app', 'https://example.test/legacy.git', 'main',
                            'pipeline.lua', 'pipeline', 0, 0, '2026-01-01 00:00:00+00', '2026-01-01 00:00:00+00');

                    INSERT INTO "Projects" ("Id", "OrganizationId", "Name", "RepositoryUrl",
                                            "DefaultBranch", "PipelinePath", "Kind", "Disabled",
                                            "QuietPeriodSeconds", "CreatedAt", "UpdatedAt")
                    VALUES ({2}, {0}, 'legacy-other', 'https://example.test/other.git', 'main',
                            'pipeline.lua', 'pipeline', 0, 0, '2026-01-01 00:00:00+00', '2026-01-01 00:00:00+00');

                    INSERT INTO "BuildRuns" ("Id", "OrganizationId", "ProjectId", "Status", "TriggerType",
                                             "RequiredTags", "PipelinePath", "GitBranch", "CreatedAt", "UpdatedAt")
                    VALUES
                        ({3}, {0}, {1}, 'Queued', 'Manual', '', 'pipeline.lua', 'main',
                         '2026-01-02 00:00:00+00', '2026-01-02 00:00:00+00'),
                        ({4}, {0}, {1}, 'Queued', 'Manual', '', 'pipeline.lua', 'main',
                         '2026-01-02 00:00:00+00', '2026-01-02 00:00:00+00'),
                        ({5}, {0}, {1}, 'Queued', 'Manual', '', 'pipeline.lua', 'main',
                         '2026-01-01 00:00:00+00', '2026-01-01 00:00:00+00'),
                        ({6}, {0}, {2}, 'Queued', 'Manual', '', 'pipeline.lua', 'main',
                         '2026-01-03 00:00:00+00', '2026-01-03 00:00:00+00');
                    """,
                    // SQLite stores an EF Guid as uppercase text; the query filter compares it as text.
                    new object[]
                    {
                        organizationId.ToString().ToUpperInvariant(),
                        firstProjectId.ToString().ToUpperInvariant(),
                        secondProjectId.ToString().ToUpperInvariant(),
                        tieHighId.ToString().ToUpperInvariant(),
                        tieLowId.ToString().ToUpperInvariant(),
                        oldest.ToString().ToUpperInvariant(),
                        otherProjectRun.ToString().ToUpperInvariant(),
                    },
                    CancellationToken);
            }

            await using (var context = CreateContext(databasePath, dataScope))
            {
                // 3. This card's migration.
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync(targetMigration: null, CancellationToken);

                // The oldest run is #1, then the tie - broken by id, so the smaller id is #2 and the larger
                // #3 regardless of the order they were written in.
                Assert.Equal(1, await NumberOfAsync(context, oldest));
                Assert.Equal(2, await NumberOfAsync(context, tieLowId));
                Assert.Equal(3, await NumberOfAsync(context, tieHighId));

                // Numbering is per project: the other project's first run is #1 too.
                Assert.Equal(1, await NumberOfAsync(context, otherProjectRun));

                // 4. The unique index is real: a second run with an existing number cannot be inserted.
                // Raw SQL bypasses the change tracker, so the provider's own exception surfaces here (the
                // product's SaveChanges path sees the same violation wrapped as DbUpdateException).
                var duplicate = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                    context.Database.ExecuteSqlRawAsync(
                        """
                        INSERT INTO "BuildRuns" ("Id", "OrganizationId", "ProjectId", "Status", "TriggerType",
                                                 "RequiredTags", "PipelinePath", "GitBranch", "Number",
                                                 "CreatedAt", "UpdatedAt")
                        VALUES ({0}, {1}, {2}, 'Queued', 'Manual', '', 'pipeline.lua', 'main', 1,
                                '2026-01-04 00:00:00+00', '2026-01-04 00:00:00+00');
                        """,
                        new object[]
                        {
                            Guid.NewGuid().ToString().ToUpperInvariant(),
                            organizationId.ToString().ToUpperInvariant(),
                            firstProjectId.ToString().ToUpperInvariant(),
                        },
                        CancellationToken));
                Assert.Contains("UNIQUE", duplicate.Message, StringComparison.Ordinal);
                Assert.Contains("BuildRuns.ProjectId, BuildRuns.Number", duplicate.Message, StringComparison.Ordinal);

                // 5. Review M1: the column's leftover default is gone and the value must be positive, so the
                //    *first* out-of-band insert of a project cannot silently invent `#0` (a renderable
                //    /runs/0, `Run #0`, ZENKINS_BUILD_NUMBER=0). Both statements are run against the real
                //    schema the migration produced, not against the model.
                var withoutNumber = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                    context.Database.ExecuteSqlRawAsync(
                        """
                        INSERT INTO "BuildRuns" ("Id", "OrganizationId", "ProjectId", "Status", "TriggerType",
                                                 "RequiredTags", "PipelinePath", "GitBranch",
                                                 "CreatedAt", "UpdatedAt")
                        VALUES ({0}, {1}, {2}, 'Queued', 'Manual', '', 'pipeline.lua', 'main',
                                '2026-01-05 00:00:00+00', '2026-01-05 00:00:00+00');
                        """,
                        new object[]
                        {
                            Guid.NewGuid().ToString().ToUpperInvariant(),
                            organizationId.ToString().ToUpperInvariant(),
                            firstProjectId.ToString().ToUpperInvariant(),
                        },
                        CancellationToken));
                Assert.Contains("NOT NULL", withoutNumber.Message, StringComparison.Ordinal);

                var zeroNumber = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                    context.Database.ExecuteSqlRawAsync(
                        """
                        INSERT INTO "BuildRuns" ("Id", "OrganizationId", "ProjectId", "Status", "TriggerType",
                                                 "RequiredTags", "PipelinePath", "GitBranch", "Number",
                                                 "CreatedAt", "UpdatedAt")
                        VALUES ({0}, {1}, {2}, 'Queued', 'Manual', '', 'pipeline.lua', 'main', 0,
                                '2026-01-06 00:00:00+00', '2026-01-06 00:00:00+00');
                        """,
                        new object[]
                        {
                            Guid.NewGuid().ToString().ToUpperInvariant(),
                            organizationId.ToString().ToUpperInvariant(),
                            firstProjectId.ToString().ToUpperInvariant(),
                        },
                        CancellationToken));
                Assert.Contains("CK_BuildRuns_Number", zeroNumber.Message, StringComparison.Ordinal);
            }

            await using (var context = CreateContext(databasePath, dataScope))
            {
                // 6. Down is symmetric: the column goes, the runs stay.
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync(PreviousMigration, CancellationToken);

                var columns = await context.Database
                    .SqlQueryRaw<string>("SELECT \"name\" AS \"Value\" FROM pragma_table_info('BuildRuns')")
                    .ToListAsync(CancellationToken);
                Assert.DoesNotContain("Number", columns);
                Assert.DoesNotContain("RebuiltFromRunId", columns);

                var remaining = await context.Database
                    .SqlQueryRaw<int>(
                        "SELECT COUNT(*) AS \"Value\" FROM \"BuildRuns\" WHERE \"ProjectId\" = {0}",
                        firstProjectId.ToString().ToUpperInvariant())
                    .ToListAsync(CancellationToken);
                Assert.Equal(3, Assert.Single(remaining));
            }
        }
        finally
        {
            TryDelete(databasePath);
        }
    }

    private static async Task<int> NumberOfAsync(ZenkinsDbContext context, Guid runId) =>
        Assert.Single(await context.Database.SqlQueryRaw<int>(
            "SELECT \"Number\" AS \"Value\" FROM \"BuildRuns\" WHERE \"Id\" = {0}",
            runId.ToString().ToUpperInvariant()).ToListAsync(CancellationToken));

    private static ZenkinsDbContext CreateContext(string databasePath, SystemDataScope systemDataScope)
    {
        var options = new DbContextOptionsBuilder<ZenkinsDbContext>()
            .UseZenkinsDatabase(new ZenkinsDataOptions(
                DatabaseProvider.Sqlite,
                $"Data Source={databasePath}"))
            .Options;
        return new ZenkinsDbContext(options, new TenantContext(), systemDataScope);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
