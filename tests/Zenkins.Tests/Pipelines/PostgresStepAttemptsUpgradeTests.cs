using Microsoft.EntityFrameworkCore;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Pipelines;
using Zenkins.Tests.Concurrency;

namespace Zenkins.Tests.Pipelines;

/// <summary>
/// Card F-032's migration on the provider it is generated for, in the one direction the fast suite cannot
/// see: <b>an existing database being upgraded</b>, not an empty one being created.
/// <para>
/// <b>Why this fact exists.</b> <c>AddStepAttempts</c> adds a non-nullable column with
/// <c>defaultValue: 1</c> plus a check constraint over <c>1..5</c>. On an empty schema that is one
/// statement; on a populated one it is the whole risk - PostgreSQL 11+ adds a column with a default
/// without rewriting the table, and every existing row must then <b>read</b> 1 (the honest "one attempt"
/// for a step recorded before this card) instead of the 0 a default-less column would leave, which the
/// constraint would reject. This product ships an install/upgrade path, so "the migration is fine" has to
/// be a fact about rows that already exist.
/// </para>
/// <para>
/// The shape is the sibling <see cref="Zenkins.Tests.Branches.PostgresBranchMigrationTests"/>'s: apply the
/// migration set up to the revision <b>before</b> this card, write rows on that schema with raw SQL,
/// apply the rest, and then assert the state of those rows.
/// </para>
/// </summary>
[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresStepAttemptsUpgradeTests
{
    [Fact(Timeout = 5 * 60 * 1000)]
    public async Task Upgrading_a_populated_database_backfills_attempts_with_one_and_keeps_the_bound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var skipReason = PostgresProjectCapTests.UnavailabilityReason();
        if (skipReason is not null)
        {
            throw Xunit.Sdk.SkipException.ForSkip(skipReason);
        }

        var connectionString = Environment.GetEnvironmentVariable(
            PostgresProjectCapTests.ConnectionStringVariable)!;
        await PostgresProjectCapTests.ResetDatabaseAsync(connectionString, cancellationToken);

        var options = new DbContextOptionsBuilder<ZenkinsDbContext>()
            .UseZenkinsDatabase(new ZenkinsDataOptions(DatabaseProvider.Postgres, connectionString))
            .Options;

        var organizationId = Guid.NewGuid();
        await using var context = new ZenkinsDbContext(
            options,
            new TenantContext { OrganizationId = organizationId },
            new SystemDataScope());

        // 1. The revision before this card, found rather than hardcoded: the next card to add a migration
        //    must not have to edit this fact.
        var migrations = context.Database.GetMigrations().ToArray();
        var attemptsIndex = Array.FindIndex(
            migrations,
            id => id.EndsWith("_AddStepAttempts", StringComparison.Ordinal));
        Assert.True(
            attemptsIndex > 0,
            $"the migration set does not contain _AddStepAttempts (found: {string.Join(", ", migrations)}).");
        var previousMigration = migrations[attemptsIndex - 1];

        await context.Database.MigrateAsync(previousMigration, cancellationToken);

        // 2. History on the pre-F-032 schema: an organization, a project, a run, and two recorded steps -
        //    the rows an upgrade meets in the field. Inserted raw, because the current model has a column
        //    this schema does not have yet.
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var firstStepId = Guid.NewGuid();
        var secondStepId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Organizations" ("Id", "Name", "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, {2}, {2});
            """,
            new object[] { organizationId, "attempts-upgrade", now },
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Projects" ("Id", "OrganizationId", "Name", "Kind", "RepositoryUrl",
                                    "DefaultBranch", "PipelinePath", "QuietPeriodSeconds",
                                    "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, 'upgraded', 'pipeline', 'https://example.test/r.git',
                    'main', 'pipeline.lua', 0, {2}, {2});
            """,
            new object[] { projectId, organizationId, now },
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "BuildRuns" ("Id", "OrganizationId", "ProjectId", "Number", "Status",
                                     "TriggerType", "RequiredTags", "PipelinePath", "GitBranch",
                                     "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, {2}, 1, 'Failed', 'Manual', '', 'pipeline.lua', 'main', {3}, {3});
            """,
            new object[] { runId, organizationId, projectId, now },
            cancellationToken);

        // Step 1 succeeded, step 2 failed with a real exit code - the two shapes a historical step has.
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "BuildSteps" ("Id", "OrganizationId", "RunId", "Index", "Name", "Status",
                                      "StartedAt", "EndedAt", "ExitCode", "FailureReason",
                                      "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, {2}, 1, 'Restore', 'Success', {3}, {4}, 0, NULL, {3}, {4});
            """,
            new object[] { firstStepId, organizationId, runId, now.AddSeconds(-2), now.AddSeconds(-1) },
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "BuildSteps" ("Id", "OrganizationId", "RunId", "Index", "Name", "Status",
                                      "StartedAt", "EndedAt", "ExitCode", "FailureReason",
                                      "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, {2}, 2, 'Build', 'Failed', {3}, {4}, 7, NULL, {3}, {4});
            """,
            new object[] { secondStepId, organizationId, runId, now.AddSeconds(-1), now },
            cancellationToken);

        // 3. The upgrade itself.
        await context.Database.MigrateAsync(cancellationToken);

        // 4a. The migration applied the bound as a validated constraint (not a NOT VALID one): the upgrade
        //     checked the rows it met.
        var validated = await context.Database
            .SqlQuery<bool>(
                $"""
                SELECT "convalidated" AS "Value" FROM "pg_constraint"
                WHERE "conname" = 'CK_BuildSteps_Attempts'
                """)
            .SingleAsync(cancellationToken);
        Assert.True(validated, "CK_BuildSteps_Attempts exists but is NOT VALID - the upgrade did not check history.");

        // 4b. Both historical rows read 1 - the honest "one attempt" for a step recorded before the column
        //     existed - through the product's own model (so the mapping and the backfill are both proven),
        //     and their other fields are untouched.
        context.ChangeTracker.Clear();
        var steps = await context.BuildSteps
            .IgnoreQueryFilters()
            .Where(step => step.RunId == runId)
            .OrderBy(step => step.Index)
            .ToListAsync(cancellationToken);

        Assert.Equal(2, steps.Count);
        Assert.All(steps, step => Assert.Equal(1, step.Attempts));
        Assert.Equal(("Restore", PipelineStepStatus.Success, (int?)0), (steps[0].Name, steps[0].Status, steps[0].ExitCode));
        Assert.Equal(("Build", PipelineStepStatus.Failed, (int?)7), (steps[1].Name, steps[1].Status, steps[1].ExitCode));

        var rawBackfill = await context.Database
            .SqlQuery<int>($"SELECT \"Attempts\" AS \"Value\" FROM \"BuildSteps\" WHERE \"RunId\" = {runId} ORDER BY \"Index\"")
            .ToListAsync(cancellationToken);
        Assert.Equal([1, 1], rawBackfill);

        // 5. The bound is the database's, on the upgraded schema, for both a new row and an update of a
        //    historical one - 0 and 6 are refused, 1..5 are the only values a row can hold.
        foreach (var refused in new[] { 0, 6 })
        {
            var tooLowOrHigh = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "BuildSteps" ("Id", "OrganizationId", "RunId", "Index", "Name", "Status",
                                          "Attempts", "CreatedAt", "UpdatedAt")
                VALUES ({0}, {1}, {2}, 3, 'Extra', 'Skipped', {3}, {4}, {4});
                """,
                new object[] { Guid.NewGuid(), organizationId, runId, refused, now },
                cancellationToken));
            Assert.Contains("CK_BuildSteps_Attempts", tooLowOrHigh.ToString(), StringComparison.Ordinal);
        }

        var rewrite = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
            """UPDATE "BuildSteps" SET "Attempts" = 0 WHERE "RunId" = {0};""",
            new object[] { runId },
            cancellationToken));
        Assert.Contains("CK_BuildSteps_Attempts", rewrite.ToString(), StringComparison.Ordinal);

        // ...and the upgraded rows still read 1 after that refused update (nothing was written).
        context.ChangeTracker.Clear();
        Assert.All(
            await context.BuildSteps.IgnoreQueryFilters().Where(step => step.RunId == runId).ToListAsync(cancellationToken),
            step => Assert.Equal(1, step.Attempts));

        // 6. A new step written through the model after the upgrade stores the attempt count it is given
        //    (the retry loop's own value), so the column is not merely a backfill.
        context.BuildSteps.Add(new BuildStep
        {
            OrganizationId = organizationId,
            RunId = runId,
            Index = 3,
            Name = "Deploy",
            Status = PipelineStepStatus.Success,
            ExitCode = 0,
            Attempts = 3,
        });
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        Assert.Equal(
            3,
            (await context.BuildSteps.IgnoreQueryFilters()
                .SingleAsync(step => step.RunId == runId && step.Index == 3, cancellationToken)).Attempts);
    }
}
