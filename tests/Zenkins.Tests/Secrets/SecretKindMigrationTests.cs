using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Secrets;

namespace Zenkins.Tests.Secrets;

/// <summary>
/// Card F-040's migration, applied for real (docs/development.md's migration contract): a database migrated to
/// the migration before this card, holding a secret that predates the kind column, is brought to the current
/// model - and that row must still be readable, as a <b>text</b> secret.
/// <para>
/// <b>Why this is a fact and not a default in the entity.</b> The backfill is the column's
/// <c>DEFAULT 'text'</c>; a migration without it (or one that added the column as nullable and left the values
/// alone) would make every pre-existing secret undeliverable - the kind is what the worker decodes the payload
/// with, so a missing one is not a cosmetic gap.
/// </para>
/// <para>
/// The migration set is the PostgreSQL one and SQLite is dynamically typed, so this runs the same migration
/// files the product ships.
/// </para>
/// </summary>
[Trait("Category", "Secrets")]
public sealed class SecretKindMigrationTests
{
    /// <summary>The last migration before card F-040: the secrets table without a kind.</summary>
    private const string PreviousMigration = "20261118084051_WidenRunRequiredRequirement";

    private const string LegacyName = "LEGACY_TEXT_TOKEN";
    private const string LegacyCiphertext = "legacy-ciphertext-opaque-5d07";

    [Fact]
    public async Task A_secret_written_before_the_kind_column_survives_as_a_text_secret()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"zenkins-f040-migration-{Guid.NewGuid():N}.db");
        var organizationId = Guid.NewGuid();
        var secretId = Guid.NewGuid();

        try
        {
            var migrationScope = new SystemDataScope();
            await using (var context = CreateContext(databasePath, migrationScope))
            {
                // 1. The state a deployment is in before this card: no Kind column at all.
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync(PreviousMigration, TestContext.Current.CancellationToken);

                // 2. One organization and one secret exactly as the old schema stored them - the plaintext was a
                //    bare string, so nothing about the row says what kind it is.
                await context.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "Organizations" ("Id", "Name", "CreatedAt", "UpdatedAt")
                    VALUES ({0}, 'Legacy', '2026-01-01 00:00:00+00', '2026-01-01 00:00:00+00');

                    INSERT INTO "Secrets" ("Id", "OrganizationId", "Name", "Ciphertext",
                                           "CreatedByUserId", "CreatedAt", "UpdatedAt")
                    VALUES ({1}, {0}, {2}, {3}, 'owner-before-kinds-31c9',
                            '2026-01-01 00:00:00+00', '2026-01-01 00:00:00+00');
                    """,
                    // SQLite stores an EF Guid as uppercase text, and the query filter compares it as text.
                    new object[]
                    {
                        organizationId.ToString().ToUpperInvariant(),
                        secretId.ToString().ToUpperInvariant(),
                        LegacyName,
                        LegacyCiphertext,
                    },
                    TestContext.Current.CancellationToken);
            }

            var systemDataScope = new SystemDataScope();
            await using (var context = CreateContext(databasePath, systemDataScope))
            {
                // 3. This card's migration: the column appears with its backfill, and the old row is untouched.
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync(targetMigration: null, TestContext.Current.CancellationToken);

                using var scope = systemDataScope.BeginScope();
                var rows = await context.SystemQueryAsync<Secret>(
                    candidate => candidate.Id == secretId,
                    TestContext.Current.CancellationToken);
                var secret = rows.Single();

                // The row it was is still the row it is...
                Assert.Equal(LegacyName, secret.Name);
                Assert.Equal(organizationId, secret.OrganizationId);
                Assert.Equal(LegacyCiphertext, secret.Ciphertext);

                // ...and the new column answers `text`, which is what the ciphertext always was. Anything else
                // (a null, an empty string, a guess at a richer kind) would change what the row means.
                Assert.Equal(SecretKinds.Text, secret.Kind);

                // The declared kind is also the one the payload decoder accepts for it: an old bare string decodes
                // as the text payload it is, so a pre-F-040 secret stays deliverable.
                var payload = SecretPayload.Decode(secret.Kind, "a bare old value");
                Assert.Equal(SecretKinds.Text, payload.Kind);
                Assert.Equal("a bare old value", payload.Value);
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
