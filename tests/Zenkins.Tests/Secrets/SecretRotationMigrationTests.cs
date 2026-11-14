using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Tests.Secrets;

/// <summary>
/// Card D-006's migration, applied for real (docs/development.md's migration contract): a database
/// migrated to the migration before this card, holding a secret that was already rotated, is brought to
/// the current model - and that row must still be readable with every value it had.
/// <para>
/// The point is not that SQLite accepts <c>ADD COLUMN</c>: it is that <b>existing rows survive</b> and land
/// in the honest "rotation not recorded" state (<c>RotatedByUserId</c> null with <c>RotatedAt</c> set),
/// which is the branch the page renders as "not recorded" rather than as a guessed creator.
/// </para>
/// <para>
/// The migration set is the PostgreSQL one and SQLite is dynamically typed, so this runs the same
/// migration files the product ships.
/// </para>
/// </summary>
[Trait("Category", "Secrets")]
public sealed class SecretRotationMigrationTests
{
    /// <summary>The last migration before this card: the secrets table without rotation attribution.</summary>
    private const string PreviousMigration = "20261113203304_AddWorkerDisabled";

    private const string LegacyName = "LEGACY_ROTATED_TOKEN";
    private const string LegacyCiphertext = "legacy-ciphertext-opaque-8c41";
    private const string LegacyCreator = "owner-before-attribution-6f2a";

    [Fact]
    public async Task A_secret_rotated_before_attribution_exists_survives_and_reports_no_rotator()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"zenkins-d006-migration-{Guid.NewGuid():N}.db");
        var organizationId = Guid.NewGuid();
        var secretId = Guid.NewGuid();

        try
        {
            var migrationScope = new SystemDataScope();
            await using (var context = CreateContext(databasePath, migrationScope))
            {
                // 1. The state a deployment is in before this card.
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync(PreviousMigration, TestContext.Current.CancellationToken);

                // 2. One organization and one secret exactly as the old schema stored them: the value was
                //    rotated (RotatedAt set) before the attribution column existed.
                await context.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "Organizations" ("Id", "Name", "CreatedAt", "UpdatedAt")
                    VALUES ({0}, 'Legacy', '2026-01-01 00:00:00+00', '2026-01-01 00:00:00+00');

                    INSERT INTO "Secrets" ("Id", "OrganizationId", "Name", "Ciphertext", "RotatedAt",
                                           "CreatedByUserId", "CreatedAt", "UpdatedAt")
                    VALUES ({1}, {0}, {2}, {3}, '2026-02-01 10:00:00+00', {4},
                            '2026-01-01 00:00:00+00', '2026-02-01 10:00:00+00');
                    """,
                    // SQLite stores an EF Guid as uppercase text, and the query filter compares it as text.
                    new object[]
                    {
                        organizationId.ToString().ToUpperInvariant(),
                        secretId.ToString().ToUpperInvariant(),
                        LegacyName,
                        LegacyCiphertext,
                        LegacyCreator,
                    },
                    TestContext.Current.CancellationToken);
            }

            var systemDataScope = new SystemDataScope();
            await using (var context = CreateContext(databasePath, systemDataScope))
            {
                // 3. This card's migration: the column appears, and the existing row is untouched.
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync(targetMigration: null, TestContext.Current.CancellationToken);

                using var scope = systemDataScope.BeginScope();
                var rows = await context.SystemQueryAsync<Secret>(
                    candidate => candidate.Id == secretId,
                    TestContext.Current.CancellationToken);
                var secret = rows.Single();

                // Everything that identified or protected the secret is still there...
                Assert.Equal(LegacyName, secret.Name);
                Assert.Equal(organizationId, secret.OrganizationId);
                Assert.Equal(LegacyCiphertext, secret.Ciphertext);
                Assert.Equal(LegacyCreator, secret.CreatedByUserId);
                Assert.NotNull(secret.RotatedAt);

                // ...and the new attribution is null, which the service reads as "not recorded" - never as
                // "rotated by nobody" invented text (D-006's honest pre-attribution branch).
                Assert.Null(secret.RotatedByUserId);
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
