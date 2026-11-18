using Microsoft.EntityFrameworkCore;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Runs;
using Zenkins.Shared.Workers;
using Zenkins.Tests.Concurrency;

namespace Zenkins.Tests.Runs;

/// <summary>
/// Card F-033's migration on the provider it is generated for, in the direction the fast suite cannot see: a
/// database that already has runs is upgraded, and those runs must stay <b>claimable by the same rule</b>.
/// <para>
/// <b>Why this fact is about claimability, not about a column.</b> <c>RequiredRequirement</c> is added with a
/// database default of <c>''</c>, so every historical run reads "no requirement" in that column - and its
/// <c>RequiredTags</c> still says <c>linux,arm64</c>. The upgrade is only correct if the claim rule reads that
/// pair the way it read the tags alone: a Worker carrying both labels gets the run, a Worker carrying one does
/// not. A default that were read as "satisfies everything" (or as "contradiction") would pass a
/// column-is-present check and fail here.
/// </para>
/// </summary>
[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresWorkerRequirementUpgradeTests
{
    [Fact(Timeout = 5 * 60 * 1000)]
    public async Task A_run_written_before_the_column_is_still_claimed_by_its_legacy_tag_list()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var skipReason = PostgresProjectCapTests.UnavailabilityReason();
        if (skipReason is not null)
        {
            throw Xunit.Sdk.SkipException.ForSkip(skipReason);
        }

        var connectionString = Environment.GetEnvironmentVariable(PostgresProjectCapTests.ConnectionStringVariable)!;
        await PostgresProjectCapTests.ResetDatabaseAsync(connectionString, cancellationToken);

        var options = new DbContextOptionsBuilder<ZenkinsDbContext>()
            .UseZenkinsDatabase(new ZenkinsDataOptions(DatabaseProvider.Postgres, connectionString))
            .Options;

        var organizationId = Guid.NewGuid();
        await using var context = new ZenkinsDbContext(
            options,
            new TenantContext { OrganizationId = organizationId },
            new SystemDataScope());

        var migrations = context.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(migrations, id => id.EndsWith("_AddRunRequiredRequirement", StringComparison.Ordinal));
        Assert.True(index > 0, $"the migration set does not contain _AddRunRequiredRequirement (found: {string.Join(", ", migrations)}).");
        await context.Database.MigrateAsync(migrations[index - 1], cancellationToken);

        // A run as this product wrote them before the column existed: the tag list, no normalized form.
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Organizations" ("Id", "Name", "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, {2}, {2});
            """,
            new object[] { organizationId, "requirement-upgrade", now },
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
            VALUES ({0}, {1}, {2}, 1, 'Queued', 'Manual', 'linux,arm64', 'pipeline.lua', 'main', {3}, {3});
            """,
            new object[] { runId, organizationId, projectId, now },
            cancellationToken);

        await context.Database.MigrateAsync(cancellationToken);

        // 1. The upgrade met the row and left the tags alone; the new column reads its default.
        context.ChangeTracker.Clear();
        var run = await context.BuildRuns.IgnoreQueryFilters().SingleAsync(r => r.Id == runId, cancellationToken);
        Assert.Equal("linux,arm64", run.RequiredTags);
        Assert.Equal(string.Empty, run.RequiredRequirement);

        // 2. The claim rule reads that pair as the tag list it always was - one predicate, the same answers.
        Assert.True(RunValidation.WorkerSatisfies(run.RequiredRequirement, run.RequiredTags, "linux,arm64"));
        Assert.True(RunValidation.WorkerSatisfies(run.RequiredRequirement, run.RequiredTags, "arm64,linux,extra"));
        Assert.False(RunValidation.WorkerSatisfies(run.RequiredRequirement, run.RequiredTags, "linux"));
        Assert.False(RunValidation.WorkerSatisfies(run.RequiredRequirement, run.RequiredTags, string.Empty));

        // 3. ...and the fallback is the parser's own reading of the legacy list, not a second rule.
        Assert.Equal(
            WorkerRequirement.FromTags("linux,arm64").StorageForm,
            RunValidation.RequirementFor(run.RequiredRequirement, run.RequiredTags).StorageForm);
    }
}
