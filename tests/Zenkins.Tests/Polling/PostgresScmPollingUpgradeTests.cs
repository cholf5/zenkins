using Microsoft.EntityFrameworkCore;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Tests.Concurrency;

namespace Zenkins.Tests.Polling;

/// <summary>
/// Card F-025's migration on the provider it is generated for, in the one direction the fast suite cannot
/// see: <b>an existing database being upgraded</b>, not an empty one being created.
/// <para>
/// <b>Why this fact exists (and why it is the sibling of F-032's).</b> <c>AddScmPolling</c> adds six columns
/// to <c>Projects</c>, two of them non-nullable with a default (<c>PollingEnabled</c> = false,
/// <c>PollFailureStreak</c> = 0) - exactly the shape F-032's <c>Attempts</c> had. Creating a schema from zero
/// never meets a column default: the column simply exists. An upgrade does, and every project row that was
/// there before polling existed must read "polling off, no failures" rather than whatever a default-less
/// column would leave. It also creates two tables (<c>BranchHeads</c>, <c>ChangeTriggers</c>), which an
/// upgrade has to bring into being next to populated neighbours.
/// </para>
/// </summary>
[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresScmPollingUpgradeTests
{
    [Fact(Timeout = 5 * 60 * 1000)]
    public async Task Upgrading_a_populated_database_leaves_polling_off_and_creates_both_new_tables()
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

        // 1. The revision before this card, found rather than hardcoded - the next card to add a migration
        //    must not have to edit this fact.
        var migrations = context.Database.GetMigrations().ToArray();
        var pollingIndex = Array.FindIndex(
            migrations,
            id => id.EndsWith("_AddScmPolling", StringComparison.Ordinal));
        Assert.True(
            pollingIndex > 0,
            $"the migration set does not contain _AddScmPolling (found: {string.Join(", ", migrations)}).");
        var previousMigration = migrations[pollingIndex - 1];

        await context.Database.MigrateAsync(previousMigration, cancellationToken);

        // 2. History on the pre-F-025 schema: an organization, two projects (a pipeline one with a
        //    repository and the settings a real row carries, and a repository-free one) and a run - the
        //    neighbours an upgrade meets in the field.
        var pipelineProjectId = Guid.NewGuid();
        var freestyleProjectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Organizations" ("Id", "Name", "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, {2}, {2});
            """,
            new object[] { organizationId, "polling-upgrade", now },
            cancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Projects" ("Id", "OrganizationId", "Name", "Kind", "RepositoryUrl",
                                    "DefaultBranch", "PipelinePath", "QuietPeriodSeconds",
                                    "MaxQueuedRuns", "Disabled", "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, 'polled', 'pipeline', 'https://example.test/r.git',
                    'main', 'pipeline.lua', 30, 7, false, {2}, {2});
            """,
            new object[] { pipelineProjectId, organizationId, now },
            cancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Projects" ("Id", "OrganizationId", "Name", "Kind", "RepositoryUrl",
                                    "DefaultBranch", "PipelinePath", "QuietPeriodSeconds",
                                    "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, 'stepless', 'freestyle', '', 'main', 'pipeline.lua', 0, {2}, {2});
            """,
            new object[] { freestyleProjectId, organizationId, now },
            cancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "BuildRuns" ("Id", "OrganizationId", "ProjectId", "Number", "Status",
                                     "TriggerType", "RequiredTags", "PipelinePath", "GitBranch",
                                     "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, {2}, 1, 'Success', 'Manual', '', 'pipeline.lua', 'main', {3}, {3});
            """,
            new object[] { runId, organizationId, pipelineProjectId, now },
            cancellationToken);

        // 3. The upgrade itself.
        await context.Database.MigrateAsync(cancellationToken);

        // 4a. Every pre-existing project reads "polling off, no failures, never polled" - the honest state
        //     for a row written before the feature existed - through the product's own model.
        context.ChangeTracker.Clear();
        var projects = await context.Projects
            .IgnoreQueryFilters()
            .Where(project => project.OrganizationId == organizationId)
            .OrderBy(project => project.Name)
            .ToListAsync(cancellationToken);

        Assert.Equal(2, projects.Count);
        Assert.All(projects, project =>
        {
            Assert.False(project.PollingEnabled);
            Assert.Equal(0, project.PollFailureStreak);
            Assert.Null(project.PollIntervalMinutes);
            Assert.Null(project.LastPollAt);
            Assert.Null(project.LastPollOutcome);
            Assert.Null(project.LastPollMessage);
        });

        // ...and the rest of each row is what it was: the upgrade added columns, it did not rewrite settings.
        var polled = projects.Single(project => project.Id == pipelineProjectId);
        Assert.Equal(("polled", "pipeline", "https://example.test/r.git", "main", 30, 7, false),
            (polled.Name, polled.Kind, polled.RepositoryUrl, polled.DefaultBranch,
                polled.QuietPeriodSeconds, polled.MaxQueuedRuns ?? 0, polled.Disabled));
        var stepless = projects.Single(project => project.Id == freestyleProjectId);
        Assert.Equal(("stepless", "freestyle", ""), (stepless.Name, stepless.Kind, stepless.RepositoryUrl));

        // 4b. The two new tables exist on the upgraded schema, and the rows that were already there were not
        //     disturbed by their creation.
        var createdTables = await context.Database
            .SqlQuery<string>(
                $"""
                SELECT "table_name" AS "Value" FROM "information_schema"."tables"
                WHERE "table_schema" = 'public' AND "table_name" IN ('BranchHeads', 'ChangeTriggers')
                ORDER BY "table_name"
                """)
            .ToListAsync(cancellationToken);
        Assert.Equal(["BranchHeads", "ChangeTriggers"], createdTables);

        Assert.Equal(1, await context.BuildRuns.CountAsync(run => run.Id == runId, cancellationToken));

        // 4c. Both new tables accept a row through the model, so they are not merely present: the upgrade
        //     produced the columns, the keys and the mapping the code needs.
        context.BranchHeads.Add(new BranchHead
        {
            OrganizationId = organizationId,
            ProjectId = pipelineProjectId,
            Branch = "main",
            Sha = new string('a', 40),
            LastOutcome = "first-sighting",
        });
        context.ChangeTriggers.Add(new ChangeTrigger
        {
            OrganizationId = organizationId,
            ProjectId = pipelineProjectId,
            Branch = "main",
            Commit = new string('a', 40),
            TriggerType = BuildRunTriggerType.Poll,
        });
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        Assert.Equal(1, await context.BranchHeads.CountAsync(head => head.ProjectId == pipelineProjectId, cancellationToken));
        Assert.Equal(1, await context.ChangeTriggers.CountAsync(row => row.ProjectId == pipelineProjectId, cancellationToken));

        // 5. Turning polling on for the upgraded project stores the interval it is given: the columns are
        //    writable, not just backfilled.
        var upgradeTarget = (await context.Projects
            .IgnoreQueryFilters()
            .SingleAsync(project => project.Id == pipelineProjectId, cancellationToken));
        upgradeTarget.PollingEnabled = true;
        upgradeTarget.PollIntervalMinutes = 15;
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        var enabled = await context.Projects
            .IgnoreQueryFilters()
            .SingleAsync(project => project.Id == pipelineProjectId, cancellationToken);
        Assert.True(enabled.PollingEnabled);
        Assert.Equal(15, enabled.PollIntervalMinutes);
    }
}
