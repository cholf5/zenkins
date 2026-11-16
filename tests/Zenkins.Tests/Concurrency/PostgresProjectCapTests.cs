using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.Concurrency;

/// <summary>
/// F-029's and Q-016's concurrency facts on a real PostgreSQL server - the <b>claim</b> cap and the
/// <b>trigger</b> queue cap - plus the controls that keep each fact honest about what it does and does not
/// prove.
/// <para>
/// <b>Why a suite of its own.</b> On SQLite the race cannot be observed: every claim begins with a write
/// (the worker-row no-op <c>UPDATE</c>), so the database's single writer serializes the statements even
/// when the project lock is what is being tested. The committed SQLite regression
/// (<see cref="ConcurrencyProjectCapTests"/>) therefore proves that the limit <em>exists</em>; it cannot
/// prove that the limit is enforced <em>by the project row lock</em>. This suite runs against real
/// PostgreSQL, where two claims of two different workers genuinely interleave, and it carries two facts:
/// </para>
/// <list type="number">
/// <item><b>The claim path never admits a second slot</b> to a <c>MaxConcurrentRuns = 1</c> project, no
/// matter how the concurrent claims interleave (the AC's property).</item>
/// <item><b>The negative control:</b> the same two statements a check-then-act implementation would use -
/// count the project's active runs, then lease - <em>do</em> admit two when run as a sequence, through the
/// product's own compare-and-set. This fact is deliberately provider-independent (it produces two active
/// runs on SQLite as well) and it is <b>not</b> about the eight concurrent claims overlapping: it shows
/// that <b>the count is load-bearing</b> - the compare-and-set alone, without the count, cannot hold the
/// cap. The evidence that the <em>lock</em> is what makes the count trustworthy is a mutation record, not
/// this control: deleting the claim's project-row lock (keeping the count) turns fact 1 red on
/// PostgreSQL, and the card records that run. Keep the two apart when reading either of them.</item>
/// </list>
/// <para>
/// <b>The trigger half (card Q-016).</b> <c>Concurrent_triggers_never_overfill_the_queue_and_the_naive_sequence_would</c>
/// holds the trigger path to the same standard: one <c>MaxQueuedRuns = 1</c> project, six concurrent real
/// trigger posts, three rounds, exactly one admission per round - with its own count-then-insert control.
/// Deleting <c>RunService.StageQueuedRunAsync</c>'s project row lock (the count kept) turns it red in round
/// 1 while every SQLite <c>queue-full</c> fact stays green.
/// </para>
/// <para>
/// <b>Prerequisite and skip.</b> The suite needs a disposable PostgreSQL database: set
/// <c>ZENKINS_TEST_POSTGRES</c> to a connection string (for example
/// <c>Host=pg.example.test;Port=5433;Database=zenkins_test;Username=zenkins_test</c>). Two guards keep the
/// destructive part from reaching a real database: the variable must be set <em>and</em> the connection
/// string's database name must contain <c>test</c> (see <see cref="UnavailabilityReason"/>); the database's
/// <c>public</c> schema is dropped and recreated before each fact, so nothing in it survives. Without the
/// variable - or with a database name that does not look disposable - the facts <b>skip with that reason
/// recorded</b> (the GitSshd discipline), and because the suite is excluded from the fast filter
/// (development.md §2) it costs the default loop nothing.
/// </para>
/// <para>
/// <b>One collection, because every Category=Postgres fact drops <c>public</c>.</b> Every class in this
/// category joins <see cref="PostgresCollection"/>: xunit runs different classes in parallel, and two suites
/// that each recreate the schema would delete each other's tables mid-run. A new PostgreSQL fact belongs in
/// this collection - joining it is cheaper than rediscovering this paragraph.
/// </para>
/// </summary>
[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class PostgresProjectCapTests
{
    /// <summary>Environment variable holding the connection string of a disposable PostgreSQL database.</summary>
    public const string ConnectionStringVariable = "ZENKINS_TEST_POSTGRES";

    /// <summary>
    /// Why the suite cannot run here, or <see langword="null"/> when it can. Two named skips, and one
    /// deliberate hole: a variable that is set, looks disposable and then fails to connect is a real
    /// failure - the server is expected to be up.
    /// <para>
    /// <b>The second guard is not documentation, it is a mechanical check.</b> The fact drops the
    /// database's <c>public</c> schema, so a typo pointing at a real database would delete it. The
    /// connection string is therefore only accepted when its database name contains <c>test</c> - the
    /// cheapest rule that cannot be satisfied by accident - and anything else skips with the reason below.
    /// </para>
    /// </summary>
    public static string? UnavailabilityReason()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return $"the Postgres suite needs a real PostgreSQL server: set {ConnectionStringVariable} to "
                + "the connection string of a disposable database (its public schema is dropped and "
                + "recreated before each fact). Without it the facts skip - the GitSshd discipline - and "
                + "the suite is excluded from the fast filter (development.md §2).";
        }

        var database = DatabaseName(connectionString);
        return database is not null && database.Contains("test", StringComparison.OrdinalIgnoreCase)
            ? null
            : $"the Postgres suite drops and recreates the database's public schema, so "
                + $"{ConnectionStringVariable} must name a disposable database whose name contains \"test\" "
                + $"(it names \"{database ?? "<none>"}\"). Point it at a throwaway database, never at a "
                + "real one; the suite skips rather than risking it.";
    }

    /// <summary>The database name in a connection string, or <see langword="null"/> when it names none.</summary>
    private static string? DatabaseName(string connectionString)
    {
        try
        {
            var builder = new System.Data.Common.DbConnectionStringBuilder
            {
                ConnectionString = connectionString,
            };

            foreach (var key in new[] { "Database", "Initial Catalog" })
            {
                if (builder.TryGetValue(key, out var value) && value is string name && name.Length > 0)
                {
                    return name;
                }
            }

            return null;
        }
        catch (ArgumentException)
        {
            // A malformed connection string names no database this suite may drop: same skip, same reason.
            return null;
        }
    }

    [Fact(Timeout = 10 * 60 * 1000)]
    public async Task Concurrent_claims_never_take_a_second_slot_and_the_naive_sequence_would()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var skipReason = UnavailabilityReason();
        if (skipReason is not null)
        {
            throw Xunit.Sdk.SkipException.ForSkip(skipReason);
        }

        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable)!;
        await ResetDatabaseAsync(connectionString, cancellationToken);

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
                // AuthTestHost points every context at a temp SQLite file; this replaces that decision with
                // the real server under test. Registered last, so it wins.
                services.RemoveAll<ZenkinsDataOptions>();
                services.AddSingleton(new ZenkinsDataOptions(DatabaseProvider.Postgres, connectionString));
            });

        var organizationId = await host.OrganizationIdAsync();
        var owner = await host.Inner.SignInOwnerAsync();

        // ---- Fact 1: the claim path (the AC's property), five rounds, verdict read from the rows -----
        const int rounds = 5;
        const int claimsPerWorker = 4;
        var worstActive = 0;
        var admittedPerRound = new List<int>();

        for (var round = 1; round <= rounds; round++)
        {
            var projectId = await host.Inner.Inner.AddProjectAsync(
                organizationId,
                $"pg-cap-race-{round}");
            Assert.Equal(
                HttpStatusCode.Redirect,
                (await RunTestHost.SaveLimitsAsync(owner, projectId, maxConcurrentRuns: 1)).StatusCode);

            // Tag-disjoint runs, each worker covering exactly one: the two claims walk different first
            // candidates, which is the only shape where both can be inside the slot decision at once.
            await host.AddRunAsync(projectId, requiredTags: "alpha", organizationId: organizationId);
            await host.AddRunAsync(projectId, requiredTags: "bravo", organizationId: organizationId);

            var (tokenA, _) = await host.RegisterWorkerAsync(
                name: $"pg-cap-a-{round}", tags: "alpha", ownerClient: owner);
            var (tokenB, _) = await host.RegisterWorkerAsync(
                name: $"pg-cap-b-{round}", tags: "bravo", ownerClient: owner);
            using var client = host.Host.CreateClient();

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var claims = Enumerable.Range(0, claimsPerWorker * 2)
                .Select(index => Task.Run(async () =>
                {
                    var token = index < claimsPerWorker ? tokenA : tokenB;
                    await start.Task.WaitAsync(cancellationToken);
                    using var response = await RunTestHost.ClaimAsync(client, token);
                    return response.StatusCode;
                }))
                .ToArray();
            start.SetResult();
            var statuses = await Task.WhenAll(claims);

            var rows = await host.ReadRunsAsync(organizationId);
            var active = rows.Count(run =>
                run.ProjectId == projectId
                && run.WorkerId is not null
                && (run.Status == BuildRunStatus.Running || run.Status == BuildRunStatus.Queued));
            var admitted = statuses.Count(status => status == HttpStatusCode.OK);
            worstActive = Math.Max(worstActive, active);
            admittedPerRound.Add(admitted);

            Assert.True(
                active <= 1,
                $"round {round}: the MaxConcurrentRuns=1 project holds {active} active runs ({admitted} of "
                + $"{statuses.Length} concurrent claims admitted) on PostgreSQL - the project's cap was "
                + "decided check-then-act.");
            Assert.Equal(active, admitted);

            // Drain the round so its rows cannot influence the next one.
            await DrainProjectAsync(host, projectId);
        }

        // The ceiling was exercised, not skirted: every round admitted exactly one run.
        Assert.Equal(1, worstActive);
        Assert.All(admittedPerRound, admitted => Assert.Equal(1, admitted));

        // ---- Fact 2: the negative control ----------------------------------------------------------
        // Count, then lease - the two statements a non-atomic implementation would run, against the same
        // database, through the product's own compare-and-set primitive. Two runs of a cap-1 project, two
        // workers: both counts read zero (nothing is active yet) and both leases then succeed, leaving two
        // active runs - the anomaly fact 1 shows the claim path never produces. Because this is a sequence,
        // not a race, it is deterministic: it fails loudly if the provider ever stops exposing the window,
        // which is exactly when fact 1 would stop meaning anything.
        var controlProjectId = await host.Inner.Inner.AddProjectAsync(organizationId, "pg-cap-control");
        Assert.Equal(
            HttpStatusCode.Redirect,
            (await RunTestHost.SaveLimitsAsync(owner, controlProjectId, maxConcurrentRuns: 1)).StatusCode);
        var controlRunIds = new[]
        {
            await host.AddRunAsync(controlProjectId, requiredTags: "alpha", organizationId: organizationId),
            await host.AddRunAsync(controlProjectId, requiredTags: "bravo", organizationId: organizationId),
        };
        var controlWorkerIds = new[]
        {
            (await host.RegisterWorkerAsync(name: "pg-control-a", tags: "alpha", ownerClient: owner)).WorkerId,
            (await host.RegisterWorkerAsync(name: "pg-control-b", tags: "bravo", ownerClient: owner)).WorkerId,
        };

        var tenant = new TenantContext { OrganizationId = organizationId };
        var systemScope = new SystemDataScope();
        var options = new DbContextOptionsBuilder<ZenkinsDbContext>()
            .UseZenkinsDatabase(new ZenkinsDataOptions(DatabaseProvider.Postgres, connectionString))
            .Options;
        var now = DateTimeOffset.UtcNow;
        var leaseExpiresAt = now.AddMinutes(5);

        await using var first = new ZenkinsDbContext(options, tenant, systemScope);
        await using var second = new ZenkinsDbContext(options, tenant, systemScope);

        // The check both claims would do first - and both read zero, the cap of 1 is not reached.
        Assert.Equal(0, await ActiveRunCountAsync(first, controlProjectId));
        Assert.Equal(0, await ActiveRunCountAsync(second, controlProjectId));

        // ...then both lease, each its own run: the check-then-act window, reproduced deterministically.
        Assert.Equal(
            1,
            await first.TryLeaseBuildRunAsync(
                controlRunIds[0], controlWorkerIds[0], leaseExpiresAt, now, cancellationToken));
        Assert.Equal(
            1,
            await second.TryLeaseBuildRunAsync(
                controlRunIds[1], controlWorkerIds[1], leaseExpiresAt, now, cancellationToken));

        Assert.Equal(2, await ActiveRunCountAsync(first, controlProjectId));

        await DrainProjectAsync(host, controlProjectId);
    }

    private static Task<int> ActiveRunCountAsync(ZenkinsDbContext context, Guid projectId) =>
        context.BuildRuns.CountAsync(run => run.ProjectId == projectId
            && (run.Status == BuildRunStatus.Running
                || (run.Status == BuildRunStatus.Queued && run.WorkerId != null)));

    /// <summary>
    /// Card Q-016: the <b>trigger</b> side of the same standard the claim side is held to. Six concurrent
    /// triggers against a project whose queue cap is one admit exactly one run per round - and the
    /// negative control shows what the same database does when the count is trusted without the lock.
    /// <para>
    /// <b>Why it needs PostgreSQL.</b> On SQLite every trigger's first write serializes the whole
    /// transaction, so the queue-control facts in the fast suite prove the cap <em>exists</em>; they
    /// cannot prove the project row lock in <c>RunService.StageQueuedRunAsync</c> is what enforces it.
    /// Deleting that one call (keeping the count) leaves the SQLite facts green and turns this fact red in
    /// round 1 - the mutation record is on card F-029.
    /// </para>
    /// </summary>
    [Fact(Timeout = 10 * 60 * 1000)]
    public async Task Concurrent_triggers_never_overfill_the_queue_and_the_naive_sequence_would()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var skipReason = UnavailabilityReason();
        if (skipReason is not null)
        {
            throw Xunit.Sdk.SkipException.ForSkip(skipReason);
        }

        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable)!;
        await ResetDatabaseAsync(connectionString, cancellationToken);

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

        // Every read below goes through a context that carries the organization: the tenant filter is
        // what makes a project's rows visible, and the product's system scope is for cross-tenant reads
        // (the drain), not for a verdict about one project.
        var tenant = new TenantContext { OrganizationId = organizationId };
        var options = new DbContextOptionsBuilder<ZenkinsDbContext>()
            .UseZenkinsDatabase(new ZenkinsDataOptions(DatabaseProvider.Postgres, connectionString))
            .Options;
        await using var counter = new ZenkinsDbContext(options, tenant, new SystemDataScope());

        // ---- Fact 1: the trigger path (the AC's property), three rounds, verdict read from the rows ----
        const int rounds = 3;
        const int triggersPerRound = 6;
        const int cap = 1;
        var admittedPerRound = new List<int>();
        var worstQueued = 0;

        for (var round = 1; round <= rounds; round++)
        {
            var projectId = await host.Inner.Inner.AddProjectAsync(organizationId, $"pg-trigger-cap-{round}");
            Assert.Equal(
                HttpStatusCode.Redirect,
                (await RunTestHost.SaveLimitsAsync(owner, projectId, maxQueuedRuns: cap)).StatusCode);

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = Enumerable.Range(0, triggersPerRound)
                .Select(_ => Task.Run(async () =>
                {
                    await start.Task.WaitAsync(cancellationToken);
                    using var response = await RunTestHost.StartRunAsync(owner, projectId);
                    return (
                        Status: response.StatusCode,
                        Location: response.Headers.Location?.OriginalString ?? string.Empty);
                }))
                .ToArray();
            start.SetResult();
            var answers = await Task.WhenAll(calls);

            var queued = await QueuedRunCountAsync(counter, projectId, cancellationToken);
            var admitted = answers.Count(answer =>
                (int)answer.Status is 302 or 303
                && !answer.Location.Contains("runError=", StringComparison.Ordinal));
            var refused = answers.Count(answer =>
                answer.Location.Contains("runError=queue-full", StringComparison.Ordinal));
            worstQueued = Math.Max(worstQueued, queued);
            admittedPerRound.Add(admitted);

            Assert.True(
                queued <= cap,
                $"round {round}: the MaxQueuedRuns=1 project holds {queued} queued runs ({admitted} of "
                + $"{triggersPerRound} concurrent triggers admitted) on PostgreSQL - the cap was decided "
                + "check-then-act.");
            Assert.True(
                queued == admitted,
                $"round {round}: {admitted} of {triggersPerRound} triggers reported a queued run but the "
                + $"project holds {queued}. Answers: {string.Join(
                    " | ",
                    answers.Select(answer => $"{(int)answer.Status} {answer.Location}"))}");
            Assert.Equal(triggersPerRound - admitted, refused);

            await DrainProjectAsync(host, projectId);
        }

        // The ceiling was exercised, not skirted: every round admitted exactly one run and refused five.
        Assert.Equal(1, worstQueued);
        Assert.All(admittedPerRound, admitted => Assert.Equal(1, admitted));

        // ---- Fact 2: the negative control ----------------------------------------------------------
        // Count, then insert - the two statements a non-lock implementation would run, against the same
        // database, through the product's own context and entity. Both counts read zero (the queue is
        // empty, the cap of one is not reached) and both inserts then succeed, leaving two queued runs in
        // a one-run queue: the anomaly fact 1 shows the trigger path never produces. A sequence, not a
        // race, so it is deterministic - it fails loudly if the provider ever stops exposing the window,
        // which is exactly when fact 1 would stop meaning anything.
        var controlProjectId = await host.Inner.Inner.AddProjectAsync(organizationId, "pg-trigger-control");
        Assert.Equal(
            HttpStatusCode.Redirect,
            (await RunTestHost.SaveLimitsAsync(owner, controlProjectId, maxQueuedRuns: cap)).StatusCode);

        await using var first = new ZenkinsDbContext(options, tenant, new SystemDataScope());
        await using var second = new ZenkinsDbContext(options, tenant, new SystemDataScope());

        // The check each trigger would do first - and both read zero, cap not reached.
        Assert.Equal(0, await QueuedRunCountAsync(first, controlProjectId, cancellationToken));
        Assert.Equal(0, await QueuedRunCountAsync(second, controlProjectId, cancellationToken));

        // ...then both insert their run: the check-then-act window, reproduced deterministically.
        await InsertQueuedRunAsync(first, controlProjectId, organizationId, number: 1, cancellationToken);
        await InsertQueuedRunAsync(second, controlProjectId, organizationId, number: 2, cancellationToken);

        Assert.Equal(2, await QueuedRunCountAsync(first, controlProjectId, cancellationToken));

        await DrainProjectAsync(host, controlProjectId);
    }

    /// <summary>The queue cap's own predicate, counted the way the staging routine counts it.</summary>
    private static Task<int> QueuedRunCountAsync(
        ZenkinsDbContext context,
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        context.BuildRuns.CountAsync(
            run => run.ProjectId == projectId && run.Status == BuildRunStatus.Queued,
            cancellationToken);

    /// <summary>
    /// One queued run inserted through the product's own context and entity (the negative control's
    /// "insert" half). The build number is supplied by the caller: card F-036's unique
    /// <c>(ProjectId, Number)</c> index must not be what this control trips over.
    /// </summary>
    private static async Task InsertQueuedRunAsync(
        ZenkinsDbContext context,
        Guid projectId,
        Guid organizationId,
        int number,
        CancellationToken cancellationToken)
    {
        context.BuildRuns.Add(new BuildRun
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            Number = number,
            RequiredTags = string.Empty,
            TriggerType = BuildRunTriggerType.Manual,
            GitBranch = "main",
            PipelinePath = "pipeline.lua",
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Finishes/cancels every run of one project so the next round starts from an empty queue (the verdict
    /// of each round is read before this runs).
    /// </summary>
    private static Task DrainProjectAsync(RunTestHost host, Guid projectId) =>
        host.InSystemScopeAsync(async context =>
        {
            var runs = await context.SystemQueryAsync<BuildRun>(run => run.ProjectId == projectId);
            foreach (var run in runs)
            {
                run.Status = BuildRunStatus.Canceled;
                run.FailureReason = "drained-by-the-test";
                run.WorkerId = null;
                run.LeaseExpiresAt = null;
            }

            await context.SaveChangesAsync();
            return true;
        });

    /// <summary>
    /// Empties the disposable database: dropping the <c>public</c> schema takes the tables (and the
    /// migration history) with it, so the host's startup migration recreates the model and the bootstrap
    /// registration is a genuine first run. Two statements in one command (Npgsql batches them).
    /// </summary>
    internal static async Task ResetDatabaseAsync(string connectionString, CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<ZenkinsDbContext>()
            .UseZenkinsDatabase(new ZenkinsDataOptions(DatabaseProvider.Postgres, connectionString))
            .Options;

        await using var context = new ZenkinsDbContext(options, new TenantContext(), new SystemDataScope());
        await context.Database.ExecuteSqlRawAsync(
            "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;",
            cancellationToken);
    }
}
