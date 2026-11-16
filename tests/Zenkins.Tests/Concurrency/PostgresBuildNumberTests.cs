using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Xunit.Sdk;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Runs;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.Concurrency;

/// <summary>
/// The xunit collection every <c>Category=Postgres</c> fact joins. One collection, because each fact drops
/// and recreates the database's <c>public</c> schema: classes run in parallel by default, and two suites doing
/// that at once would delete each other's tables mid-run.
/// </summary>
public static class PostgresCollection
{
    /// <summary>The collection name (a plain name, no <c>CollectionDefinition</c>: xunit serializes the
    /// classes that share it without needing a fixture).</summary>
    public const string Name = "Postgres";
}

/// <summary>
/// Card F-036's numbering under the one condition SQLite cannot show: a <b>real</b> second writer taking the
/// number between the product's <c>MAX(Number)</c> read and its insert.
/// <para>
/// <b>Why this suite exists.</b> The retry in <c>RunService.SaveStagedRunAsync</c> is a savepoint rollback,
/// and a savepoint only ever saves you from a *poisoned transaction* - which is PostgreSQL behaviour. On
/// SQLite every write serializes on the database lock, so the collision cannot be constructed and the retry
/// is dead code there. Without this fact the only provider where the retry can run would have no test for it.
/// </para>
/// <para>
/// <b>How the collision is made deterministic.</b> A second connection inserts run <c>#2</c> and does not
/// commit. Under READ COMMITTED the product's <c>MAX(Number)</c> read cannot see it, so the product picks
/// <c>#2</c> as well; its INSERT then blocks on the unique <c>(ProjectId, Number)</c> index until the rival
/// commits, at which point the statement fails with <c>23505</c> instead of silently writing a duplicate. The
/// test waits for that lock wait to appear in <c>pg_stat_activity</c> before it commits the rival, so it never
/// depends on a sleep - and it asserts the product still answers <see cref="RunOperationStatus.Success"/> with
/// <c>#3</c>, which is only reachable through the retry (without it the call would throw).
/// </para>
/// <para>
/// <b>Prerequisite.</b> The same disposable database as <see cref="PostgresProjectCapTests"/>, named by
/// <c>ZENKINS_TEST_POSTGRES</c>; without it the fact skips with the recorded reason.
/// </para>
/// </summary>
[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresBuildNumberTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    /// <summary>A commit-shaped value, because a webhook run is created with the commit its push named.</summary>
    private static readonly string Commit = new('a', 40);

    [Fact(Timeout = 5 * 60 * 1000)]
    public async Task A_number_taken_by_another_connection_is_retried_once_and_the_sequence_stays_1_to_n()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var skipReason = PostgresProjectCapTests.UnavailabilityReason();
        if (skipReason is not null)
        {
            throw SkipException.ForSkip(skipReason);
        }

        var connectionString = Environment.GetEnvironmentVariable(
            PostgresProjectCapTests.ConnectionStringVariable)!;
        await PostgresProjectCapTests.ResetDatabaseAsync(connectionString, cancellationToken);

        using var host = await RunTestHost.CreateAsync(
            configureAppConfiguration: configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Postgres",
                    ["ConnectionStrings:Default"] = connectionString,
                    ["Database:ApplyMigrationsOnStartup"] = "true",
                }),
            configureTestServices: services =>
            {
                services.RemoveAll<ZenkinsDataOptions>();
                services.AddSingleton(new ZenkinsDataOptions(DatabaseProvider.Postgres, connectionString));
            });

        var organizationId = await host.OrganizationIdAsync();
        var owner = await host.Inner.SignInOwnerAsync();
        var projectId = await host.Inner.Inner.AddProjectAsync(organizationId, "pg-build-numbers");

        // #1, through the product's own form: the sequence starts at a real, migrated row.
        await RunTestHost.TriggerRunAsync(owner, projectId);

        var tenant = new TenantContext { OrganizationId = organizationId };
        var systemScope = new SystemDataScope();
        var options = new DbContextOptionsBuilder<ZenkinsDbContext>()
            .UseZenkinsDatabase(new ZenkinsDataOptions(DatabaseProvider.Postgres, connectionString))
            .Options;

        // ---- The rival: an UNCOMMITTED #2 that a READ COMMITTED read cannot see -------------------------
        await using var rival = new ZenkinsDbContext(options, tenant, systemScope);
        await using var rivalTransaction = await rival.Database.BeginTransactionAsync(cancellationToken);
        await rival.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "BuildRuns" ("Id", "OrganizationId", "ProjectId", "Status", "TriggerType",
                                     "RequiredTags", "PipelinePath", "GitBranch", "Number",
                                     "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, {2}, 'Queued', 'Webhook', '', 'pipeline.lua', 'main', 2, now(), now());
            """,
            new object[] { Guid.NewGuid(), organizationId, projectId },
            cancellationToken);

        // ---- The product path: the staging routine every trigger source shares ---------------------------
        // Resolved from the host so the savepoint/retry is the real implementation, and given the caller's
        // own context and transaction - which is exactly what StagesQueuedRunAsync expects of a webhook.
        using var scope = host.Host.Server.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().OrganizationId = organizationId;
        var runs = scope.ServiceProvider.GetRequiredService<RunService>();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>();

        var staging = Task.Run(async () =>
        {
            await using var stager = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var stagingTransaction = await stager.Database.BeginTransactionAsync(cancellationToken);
            var result = await runs.QueueWebhookRunAsync(
                stager,
                projectId,
                "main",
                Commit,
                [],
                cancellationToken);
            await stagingTransaction.CommitAsync(cancellationToken);
            return result;
        }, cancellationToken);

        // Wait for the collision itself, not for a clock: the staging INSERT is blocked on the unique index
        // the rival is holding. Only then does the rival commit, which is what turns the block into 23505.
        Assert.True(
            await WaitForBuildRunsLockWaitAsync(options, tenant, systemScope, cancellationToken),
            "The staging insert never waited on the BuildRuns unique index, so the collision this fact exists "
            + "for did not happen; the assertions below would then prove nothing about the retry.");
        Assert.False(staging.IsCompleted, "The staging call finished before the rival committed - it cannot have collided.");

        await rivalTransaction.CommitAsync(cancellationToken);
        var staged = await staging;

        // The retry recovered: the product answered success, and the run it queued is #3 - the number after
        // the one the rival took. Without the savepoint retry this call would have thrown the 23505.
        Assert.Equal(RunOperationStatus.Success, staged.Status);
        Assert.NotNull(staged.RunId);

        var rows = await host.ReadRunsAsync(organizationId);
        Assert.Equal(3, rows.Count);
        Assert.Equal([1, 2, 3], rows.Select(run => run.Number).OrderBy(number => number).ToArray());
        Assert.Equal(3, rows.Single(run => run.Id == staged.RunId!.Value).Number);
        Assert.Equal(3, rows.Select(run => run.Number).Distinct().Count());

        // ...and the run the retry produced is a whole row, not a half-written one: the same snapshot the
        // webhook asked for, on the number the retry re-read.
        var retried = rows.Single(run => run.Id == staged.RunId!.Value);
        Assert.Equal(Commit, retried.GitCommit);
        Assert.Equal(BuildRunTriggerType.Webhook, retried.TriggerType);
        Assert.Equal(projectId, retried.ProjectId);
    }

    /// <summary>
    /// Waits until some backend other than this one is blocked on the <c>BuildRuns</c> unique index - the
    /// staging INSERT's lock wait. A bounded poll (20 s), so a regression fails with a readable message
    /// instead of hanging.
    /// </summary>
    private static async Task<bool> WaitForBuildRunsLockWaitAsync(
        DbContextOptions<ZenkinsDbContext> options,
        TenantContext tenant,
        SystemDataScope systemScope,
        CancellationToken cancellationToken)
    {
        await using var probe = new ZenkinsDbContext(options, tenant, systemScope);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var waiting = await probe.Database
                .SqlQueryRaw<int>(
                    """
                    SELECT COUNT(*) AS "Value"
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND pid <> pg_backend_pid()
                      AND wait_event_type = 'Lock'
                    """)
                .ToListAsync(cancellationToken);
            if (waiting.Single() > 0)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        return false;
    }
}
