using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Tests.Git;

/// <summary>
/// Card F-018's migration, applied for real (<c>docs/development.md</c>'s migration contract): a
/// database migrated to the card's predecessor, holding a project that already references a credential,
/// is brought to the current model - and that row survives, with the new attribution columns reading
/// "not recorded" rather than a guessed person or timestamp. The <c>Down</c> direction is exercised too,
/// so the migration is symmetric: dropping the columns leaves the project and its assignment intact.
/// <para>
/// The migration set is the PostgreSQL one and SQLite is dynamically typed, so this runs the same
/// migration files the product ships (the same way the secrets rotation migration test does).
/// </para>
/// </summary>
[Trait("Category", "Git")]
public sealed class GitCredentialAssignmentMigrationTests
{
    /// <summary>The last migration before this card: projects without assignment attribution.</summary>
    private const string PreviousMigration = "20261114102208_AddSecretRotationAttribution";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_project_that_predates_the_attribution_columns_survives_and_reports_not_recorded()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"zenkins-f018-migration-{Guid.NewGuid():N}.db");
        var organizationId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();

        try
        {
            var dataScope = new SystemDataScope();
            await using (var context = CreateContext(databasePath, dataScope))
            {
                // 1. The state a deployment is in before this card.
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync(PreviousMigration, CancellationToken);

                // 2. One organization, one credential and one project that already references it - the
                //    row shape the F-013 column allowed but nothing in the product wrote.
                await context.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "Organizations" ("Id", "Name", "CreatedAt", "UpdatedAt")
                    VALUES ({0}, 'Legacy', '2026-01-01 00:00:00+00', '2026-01-01 00:00:00+00');

                    INSERT INTO "GitCredentials" ("Id", "OrganizationId", "Name", "Kind", "Ciphertext",
                                                  "CreatedByUserId", "CreatedAt", "UpdatedAt")
                    VALUES ({1}, {0}, 'LEGACY_TOKEN', 'HttpsToken', 'legacy-ciphertext-opaque-41c',
                            'owner-before-attribution', '2026-01-01 00:00:00+00', '2026-01-01 00:00:00+00');

                    INSERT INTO "Projects" ("Id", "OrganizationId", "Name", "RepositoryUrl",
                                            "DefaultBranch", "PipelinePath", "GitCredentialId",
                                            "CreatedAt", "UpdatedAt")
                    VALUES ({2}, {0}, 'legacy-app', 'https://example.test/legacy.git', 'main',
                            'pipeline.lua', {1}, '2026-01-01 00:00:00+00', '2026-01-01 00:00:00+00');
                    """,
                    // SQLite stores an EF Guid as uppercase text, and the query filter compares it as text.
                    new object[]
                    {
                        organizationId.ToString().ToUpperInvariant(),
                        credentialId.ToString().ToUpperInvariant(),
                        projectId.ToString().ToUpperInvariant(),
                    },
                    CancellationToken);
            }

            await using (var context = CreateContext(databasePath, dataScope))
            {
                // 3. This card's migration: the two columns appear, and the existing row is untouched.
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync(targetMigration: null, CancellationToken);

                using var scope = dataScope.BeginScope();
                var project = (await context.SystemQueryAsync<Project>(
                    candidate => candidate.Id == projectId,
                    CancellationToken)).Single();

                // The reference the project already had is still there...
                Assert.Equal(credentialId, project.GitCredentialId);
                Assert.Equal("legacy-app", project.Name);

                // ...and the new attribution is null, which the page renders as "Not recorded" - never as a
                // guessed operator or an invented instant.
                Assert.Null(project.GitCredentialAssignedAt);
                Assert.Null(project.GitCredentialAssignedByUserId);

                // The columns are writable, so a real assignment persists exactly as the service writes it.
                project.GitCredentialAssignedAt = DateTimeOffset.UtcNow;
                project.GitCredentialAssignedByUserId = "owner-after-attribution";
                await context.SaveChangesAsync(CancellationToken);

                var saved = (await context.SystemQueryAsync<Project>(
                    candidate => candidate.Id == projectId,
                    CancellationToken)).Single();
                Assert.Equal("owner-after-attribution", saved.GitCredentialAssignedByUserId);
                Assert.NotNull(saved.GitCredentialAssignedAt);
            }

            await using (var context = CreateContext(databasePath, dataScope))
            {
                // 4. Down is symmetric: the columns go, the project and its assignment stay. The model
                //    still expects the columns, so the verification is raw SQL over the same database.
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync(PreviousMigration, CancellationToken);

                var columns = await context.Database
                    .SqlQueryRaw<string>("SELECT \"name\" AS \"Value\" FROM pragma_table_info('Projects')")
                    .ToListAsync(CancellationToken);
                Assert.DoesNotContain("GitCredentialAssignedAt", columns);
                Assert.DoesNotContain("GitCredentialAssignedByUserId", columns);

                var names = await context.Database
                    .SqlQueryRaw<string>(
                        "SELECT \"Name\" AS \"Value\" FROM \"Projects\" WHERE \"Id\" = {0}",
                        projectId.ToString().ToUpperInvariant())
                    .ToListAsync(CancellationToken);
                Assert.Equal("legacy-app", Assert.Single(names));
            }
        }
        finally
        {
            TryDelete(databasePath);
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
