using Microsoft.EntityFrameworkCore;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Projects;

namespace Zenkins.Tests.Concurrency;

/// <summary>
/// Card F-022's migration on the provider the migration set is generated for. The SQLite migration facts
/// only prove SQLite (dynamically typed, lenient defaults); this one applies the same set to a real
/// PostgreSQL schema and then proves the column's shape on rows that were never written by the product:
/// <list type="bullet">
/// <item>the column is <c>NOT NULL DEFAULT 'repository'</c>, so a historical Pipeline row - inserted with
/// no <c>PipelineSource</c> at all, the import/hand-migration shape - reads as a repository project and
/// never becomes inline;</item>
/// <item>the check constraint admits <c>inline</c> and refuses a third word;</item>
/// <item><c>BuildRuns.PipelineScriptText</c> exists and is nullable.</item>
/// </list>
/// </summary>
[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresInlinePipelineMigrationTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact(Timeout = 5 * 60 * 1000)]
    public async Task The_pipeline_source_column_defaults_history_to_repository_and_the_script_column_exists()
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

        // Seed-then-migrate, the shape history really has: apply everything up to the migration before
        // F-022, write a historical Pipeline row on that schema (no PipelineSource column exists yet),
        // then apply F-022's migration over it. An empty-database migrate would never exercise the
        // column's backfill, which is the thing this fact exists for.
        await context.Database.MigrateAsync("20261116124330_AddGitProbeCapability", cancellationToken);

        var historicalId = Guid.NewGuid();
        var inlineId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Organizations" ("Id", "Name", "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, {2}, {2});
            """,
            new object[] { organizationId, "inline-migration", now },
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Projects" ("Id", "OrganizationId", "Name", "Kind", "RepositoryUrl",
                                    "DefaultBranch", "PipelinePath", "QuietPeriodSeconds",
                                    "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, 'historical', 'pipeline', 'https://example.test/r.git',
                    'main', 'pipeline.lua', 0, {2}, {2});
            """,
            new object[] { historicalId, organizationId, now },
            cancellationToken);

        // The F-022 migration, over the existing row.
        await context.Database.MigrateAsync(cancellationToken);

        // An inline row, written through the product's own entity shape, must be accepted too.
        context.Projects.Add(new Project
        {
            Id = inlineId,
            OrganizationId = organizationId,
            Name = "inline",
            Kind = ProjectKinds.Pipeline,
            PipelineSource = ProjectSources.Inline,
            InlineScript = "pipeline { name = \"x\", steps = { { name = \"One\", run = { \"echo\" } } } }",
        });
        await context.SaveChangesAsync(cancellationToken);

        // A run row with the new nullable script column.
        context.BuildRuns.Add(new BuildRun
        {
            Id = runId,
            OrganizationId = organizationId,
            ProjectId = inlineId,
            Number = 1,
            TriggerType = BuildRunTriggerType.Manual,
            RequiredTags = string.Empty,
            PipelinePath = "pipeline.lua",
            GitBranch = string.Empty,
            PipelineScriptText = "pipeline { }",
        });
        await context.SaveChangesAsync(cancellationToken);

        context.ChangeTracker.Clear();
        var historical = await context.Projects
            .IgnoreQueryFilters()
            .SingleAsync(project => project.Id == historicalId, cancellationToken);
        Assert.Equal(ProjectSources.Repository, historical.PipelineSource);
        Assert.Null(historical.InlineScript);

        var inline = await context.Projects
            .IgnoreQueryFilters()
            .SingleAsync(project => project.Id == inlineId, cancellationToken);
        Assert.Equal(ProjectSources.Inline, inline.PipelineSource);

        var run = await context.BuildRuns
            .IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.Id == runId, cancellationToken);
        Assert.Equal("pipeline { }", run.PipelineScriptText);

        // The check constraint is the data layer's half of the closed vocabulary: a third source is
        // refused by the database, not only by the service.
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(
            """
            UPDATE "Projects" SET "PipelineSource" = 'elsewhere' WHERE "Id" = {0};
            """,
            new object[] { historicalId },
            cancellationToken));
        Assert.Contains("CK_Projects_PipelineSource", refused.ToString(), StringComparison.Ordinal);
    }
}
