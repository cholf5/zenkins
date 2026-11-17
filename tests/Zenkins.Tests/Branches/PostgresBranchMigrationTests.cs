using Microsoft.EntityFrameworkCore;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Concurrency;

namespace Zenkins.Tests.Branches;

/// <summary>
/// Card F-023's migration on the provider the migration set is generated for. The SQLite suites prove
/// the behaviour; this one proves what history becomes:
/// <list type="bullet">
/// <item>a project row written on the pre-F-023 schema reads <c>BranchDiscovery = 'single'</c> - the
/// default is applied by the migration to existing rows, not only by the C# initializer;</item>
/// <item>the branch table starts empty, and an existing run keeps its number and its branch snapshot;</item>
/// <item>the branch key is ordinal: <c>Main</c> and <c>main</c> coexist (PostgreSQL's comparison here is
/// case-sensitive, and the model relies on that);</item>
/// <item>both closed vocabularies and the orphan-evidence pairing are enforced by the database;</item>
/// <item>deleting the container project cascades its branch rows - and only its own.</item>
/// </list>
/// </summary>
[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresBranchMigrationTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact(Timeout = 5 * 60 * 1000)]
    public async Task Branch_discovery_defaults_history_to_single_and_the_branch_table_is_keyed_by_project_and_branch()
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

        // Seed then migrate: apply the set up to F-022, write history on that schema, then apply F-023's
        // migration over it. An empty-database migrate would never exercise the column's default.
        await context.Database.MigrateAsync("20261117073131_AddProjectPipelineSourceAndInlineScript", cancellationToken);

        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Organizations" ("Id", "Name", "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, {2}, {2});
            """,
            new object[] { organizationId, "branch-migration", now },
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Projects" ("Id", "OrganizationId", "Name", "Kind", "RepositoryUrl",
                                    "DefaultBranch", "PipelinePath", "QuietPeriodSeconds",
                                    "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, 'historical', 'pipeline', 'https://example.test/r.git',
                    'main', 'pipeline.lua', 0, {2}, {2});
            """,
            new object[] { projectId, organizationId, now },
            cancellationToken);

        // A historical run with a build number and a branch: F-023 must not renumber or rewrite it, and
        // the branch list must be able to list it without a branch row.
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "BuildRuns" ("Id", "OrganizationId", "ProjectId", "Number", "Status",
                                     "TriggerType", "RequiredTags", "PipelinePath", "GitBranch",
                                     "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, {2}, 7, 'Success', 'Manual', '', 'pipeline.lua', 'release/1.0', {3}, {3});
            """,
            new object[] { runId, organizationId, projectId, now },
            cancellationToken);

        await context.Database.MigrateAsync(cancellationToken);

        // 1. The default landed on the existing row through the migration itself.
        context.ChangeTracker.Clear();
        var historical = await context.Projects
            .IgnoreQueryFilters()
            .SingleAsync(project => project.Id == projectId, cancellationToken);
        Assert.Equal(BranchDiscoveryModes.Single, historical.BranchDiscovery);

        var rawDefault = await context.Database
            .SqlQuery<string>($"SELECT \"BranchDiscovery\" AS \"Value\" FROM \"Projects\" WHERE \"Id\" = {projectId}")
            .SingleAsync(cancellationToken);
        Assert.Equal("single", rawDefault);

        // 2. The branch table is empty, and the run kept its number and branch.
        Assert.Empty(await context.BranchJobs.IgnoreQueryFilters().ToListAsync(cancellationToken));
        var run = await context.BuildRuns
            .IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.Id == runId, cancellationToken);
        Assert.Equal(7, run.Number);
        Assert.Equal("release/1.0", run.GitBranch);

        // 3. The key is ordinal: two branches differing only in case are two jobs.
        context.BranchJobs.Add(new BranchJob
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            Branch = "Main",
            PipelineState = BranchPipelineStates.HasPipeline,
            ObservedCommit = new string('a', 40),
        });
        context.BranchJobs.Add(new BranchJob
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            Branch = "main",
            PipelineState = BranchPipelineStates.NoPipeline,
            ObservedCommit = new string('b', 40),
        });
        await context.SaveChangesAsync(cancellationToken);
        Assert.Equal(2, await context.BranchJobs.IgnoreQueryFilters().CountAsync(cancellationToken));

        // The same branch twice is refused by the unique index (the guard, not a pre-flight query).
        context.BranchJobs.Add(new BranchJob
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            Branch = "main",
        });
        var duplicate = await Assert.ThrowsAnyAsync<Exception>(
            () => context.SaveChangesAsync(cancellationToken));
        Assert.Contains("IX_BranchJobs_ProjectId_Branch", duplicate.ToString(), StringComparison.Ordinal);
        context.ChangeTracker.Clear();

        // 4. The closed vocabularies and the evidence pairing are database rules, not comments.
        var badMode = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
            """UPDATE "Projects" SET "BranchDiscovery" = 'every' WHERE "Id" = {0};""",
            new object[] { projectId },
            cancellationToken));
        Assert.Contains("CK_Projects_BranchDiscovery", badMode.ToString(), StringComparison.Ordinal);

        var badState = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
            """UPDATE "BranchJobs" SET "PipelineState" = 'maybe' WHERE "Branch" = 'main';""",
            cancellationToken));
        Assert.Contains("CK_BranchJobs_PipelineState", badState.ToString(), StringComparison.Ordinal);

        var orphanWithoutEvidence = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
            """UPDATE "BranchJobs" SET "OrphanedAt" = {0} WHERE "Branch" = 'main';""",
            new object[] { now },
            cancellationToken));
        Assert.Contains("CK_BranchJobs_OrphanedEvidence", orphanWithoutEvidence.ToString(), StringComparison.Ordinal);

        // 5. Deleting the container project cascades its branch rows - and only its own project's.
        var otherProjectId = Guid.NewGuid();
        context.Projects.Add(new Project
        {
            Id = otherProjectId,
            OrganizationId = organizationId,
            Name = "other",
            RepositoryUrl = "https://example.test/other.git",
            DefaultBranch = "main",
        });
        context.BranchJobs.Add(new BranchJob
        {
            OrganizationId = organizationId,
            ProjectId = otherProjectId,
            Branch = "main",
        });
        await context.SaveChangesAsync(cancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            """DELETE FROM "Projects" WHERE "Id" = {0};""",
            new object[] { projectId },
            cancellationToken);

        var remaining = await context.BranchJobs.IgnoreQueryFilters().ToListAsync(cancellationToken);
        Assert.Single(remaining);
        Assert.Equal(otherProjectId, remaining[0].ProjectId);
    }
}
