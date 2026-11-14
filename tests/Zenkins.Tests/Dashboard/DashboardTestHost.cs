using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Dashboard;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Workers;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Tenancy;
using Xunit;

// The worker project's namespace shadows the entity type; same alias as the tenancy probes use.
using WorkerEntity = Zenkins.Server.Data.Entities.Worker;

namespace Zenkins.Tests.Dashboard;

/// <summary>
/// Counts every database command the contexts created through a given
/// <see cref="DbContextOptions{TContext}"/> run (card D-002's query-ceiling assertion). The dashboard
/// must cost a fixed number of commands no matter how many runs exist.
/// </summary>
public sealed class DbCommandCounter : DbCommandInterceptor
{
    private int _count;

    public int Count => _count;

    public void Reset() => _count = 0;

    // EF Core 10 fires CommandCreated once per created command - synchronous or asynchronous
    // execution alike - so this single hook counts every database command the options run.
    public override DbCommand CommandCreated(CommandEndEventData eventData, DbCommand result)
    {
        Interlocked.Increment(ref _count);
        return base.CommandCreated(eventData, result);
    }
}

/// <summary>
/// Builds the dashboard read model over a fixture's SQLite file with an optional command counter
/// attached. The <see cref="ZenkinsDbContextFactory"/> is constructed by hand (not resolved from a
/// host) so the interceptor sees exactly the read model's own commands - the fixture's own helper
/// contexts carry no interceptor and never pollute the count.
/// </summary>
public static class DashboardQueryFactory
{
    public static DashboardQuery Create(TwoOrgsFixture fixture, DbCommandCounter? counter = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ZenkinsDbContext>()
            .UseZenkinsDatabase(new ZenkinsDataOptions(DatabaseProvider.Sqlite, fixture.ConnectionString));
        if (counter is not null)
        {
            optionsBuilder.AddInterceptors(counter);
        }

        var contextFactory = new ZenkinsDbContextFactory(
            optionsBuilder.Options,
            fixture.TenantContext,
            fixture.SystemDataScope);
        return new DashboardQuery(
            contextFactory,
            fixture.TenantContext,
            NullLogger<DashboardQuery>.Instance);
    }
}

/// <summary>
/// Seeding helpers for the dashboard tests (card D-002). Rows are written through the system scope -
/// the same discipline <see cref="TwoOrgsFixture.Seed"/> uses - because tenant-owned inserts need an
/// explicit organization. Every helper returns the ids the assertions read back.
/// </summary>
public static class DashboardSeed
{
    public static async Task<Guid> AddWorkerAsync(
        TwoOrgsFixture fixture,
        Guid organizationId,
        string name,
        string status = WorkerStatus.Online,
        DateTimeOffset? lastHeartbeatAt = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = fixture.SystemDataScope.BeginScope();
        await using var context = fixture.CreateContext();
        var worker = new WorkerEntity
        {
            OrganizationId = organizationId,
            Name = name,
            Os = "Linux",
            Architecture = "X64",
            Version = "1.0.0",
            Tags = "dotnet",
            CpuCount = 4,
            MaxConcurrency = 1,
            Status = status,
            LastHeartbeatAt = lastHeartbeatAt,
        };
        context.Workers.Add(worker);
        await context.SaveChangesAsync(cancellationToken);
        return worker.Id;
    }

    public static async Task<Guid> AddRunAsync(
        TwoOrgsFixture fixture,
        Guid organizationId,
        Guid projectId,
        string status,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? endedAt = null,
        DateTimeOffset? createdAt = null,
        Guid? workerId = null,
        int? exitCode = null,
        string? failureReason = null,
        string? triggerType = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = fixture.SystemDataScope.BeginScope();
        await using var context = fixture.CreateContext();
        var run = new BuildRun
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            TriggerType = triggerType ?? BuildRunTriggerType.Manual,
            RequiredTags = string.Empty,
            GitBranch = "main",
            PipelinePath = "pipeline.lua",
            Status = status,
            StartedAt = startedAt,
            EndedAt = endedAt,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            WorkerId = workerId,
            ExitCode = exitCode,
            FailureReason = failureReason,
        };
        context.BuildRuns.Add(run);
        await context.SaveChangesAsync(cancellationToken);
        return run.Id;
    }

    /// <summary>A third organization (no projects, no workers, no runs) with its own Owner account,
    /// for the dashboard's specific empty states. The active organization is persisted up front, so
    /// the account's first resolution has nothing to fall back and write.</summary>
    public static async Task SeedEmptyOrganizationAsync(
        TwoOrgsFixture fixture,
        Guid organizationId,
        string organizationName,
        string userId,
        string email,
        CancellationToken cancellationToken = default)
    {
        using var scope = fixture.SystemDataScope.BeginScope();
        await using var context = fixture.CreateContext();
        context.Organizations.Add(new Organization { Id = organizationId, Name = organizationName });
        context.Users.Add(CreateUser(userId, email, organizationId));
        context.OrganizationMembers.Add(new OrganizationMember
        {
            OrganizationId = organizationId,
            UserId = userId,
            Role = OrganizationRole.Owner,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Pins a run's <c>CreatedAt</c> after the fact. <c>ZenkinsDbContext.SaveChangesAsync</c> owns the
    /// audit stamps and overwrites any seeded value, so a test that needs a specific queue time goes
    /// through this primary-key update instead (raw SQL, same discipline as <see cref="DashboardSql"/>).
    /// </summary>
    public static async Task SetRunCreatedAtAsync(
        TwoOrgsFixture fixture,
        Guid runId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlAsync(
            $"UPDATE BuildRuns SET CreatedAt = {createdAt} WHERE Id = {runId}",
            cancellationToken);
    }

    /// <summary>Adds one project so a test can shape an organization's dashboard without the form.</summary>
    public static async Task<Guid> AddProjectAsync(
        TwoOrgsFixture fixture,
        Guid organizationId,
        string name,
        CancellationToken cancellationToken = default)
    {
        using var scope = fixture.SystemDataScope.BeginScope();
        await using var context = fixture.CreateContext();
        var project = new Project
        {
            OrganizationId = organizationId,
            Name = name,
            RepositoryUrl = $"https://example.test/{name}.git",
            DefaultBranch = "main",
            PipelinePath = "pipeline.lua",
        };
        context.Projects.Add(project);
        await context.SaveChangesAsync(cancellationToken);
        return project.Id;
    }

    /// <summary>Adds <paramref name="userId"/> to <paramref name="organizationId"/> as an ordinary
    /// member (the organization-switch test needs one account on both sides).</summary>
    public static async Task AddMembershipAsync(
        TwoOrgsFixture fixture,
        Guid organizationId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        using var scope = fixture.SystemDataScope.BeginScope();
        await using var context = fixture.CreateContext();
        context.OrganizationMembers.Add(new OrganizationMember
        {
            OrganizationId = organizationId,
            UserId = userId,
            Role = OrganizationRole.Member,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private static User CreateUser(string userId, string email, Guid activeOrganizationId)
    {
        var user = new User
        {
            Id = userId,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            SecurityStamp = $"seed-security-stamp-{userId}",
            ConcurrencyStamp = $"seed-concurrency-stamp-{userId}",
            ActiveOrganizationId = activeOrganizationId,
        };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, TwoOrgsFixture.UserPassword);
        return user;
    }
}

/// <summary>
/// One <see cref="TwoOrgsFixture"/> plus a real application host for the dashboard's page-level
/// tests (card D-002). The host is <see cref="AuthenticatedTenantApplicationFactory"/> re-pointed by
/// <c>WithWebHostBuilder</c>: the background offline monitor is parked (tests drive its sweep seam
/// directly, never real time), and every <c>ZenkinsDbContext</c> is built on options that carry a
/// <see cref="DbCommandCounter"/>, so a page render's whole command count is observable. The base
/// factory must stay alive (the host builder wraps it) and is disposed last.
/// </summary>
public sealed class DashboardPageFixture : IDisposable
{
    private readonly AuthenticatedTenantApplicationFactory _baseFactory;

    public DashboardPageFixture()
    {
        Data = new TwoOrgsFixture();
        Counter = new DbCommandCounter();
        _baseFactory = new AuthenticatedTenantApplicationFactory(Data);
        Factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");

            // Parked background sweeps: the liveness test drives WorkerOfflineMonitor.SweepOnceAsync
            // itself, and nothing here may race it (nor flip a stale seeded worker behind the
            // dashboard's back).
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [WorkerMonitorOptions.MonitorIntervalSecondsConfigurationKey] = "3600",

                    // Coherent with the seeding (card B-001): the page tests seed fresh-heartbeat
                    // workers before the first client boots the host, and the monitor sweeps once at
                    // startup (before its first hourly tick), so the threshold must comfortably
                    // exceed the seed-to-read window - at 1 s, any boot slow enough to land outside
                    // it flips a seeded worker Offline and sweeps its Running run Lost behind the
                    // page's back. Ten minutes leaves that window orders of magnitude of margin, and
                    // the sweep stays real: the liveness test seeds its worker half an hour stale
                    // and drives the real sweep itself.
                    [WorkerMonitorOptions.OfflineAfterSecondsConfigurationKey] = "600",
                }));

            builder.ConfigureTestServices(services =>
            {
                // Same replacement AuthenticatedTenantApplicationFactory makes, plus the command
                // counter: the options singleton feeds the scoped context and the factory alike.
                services.RemoveAll<ZenkinsDataOptions>();
                services.AddSingleton(new ZenkinsDataOptions(DatabaseProvider.Sqlite, Data.ConnectionString));
                services.RemoveAll<DbContextOptions<ZenkinsDbContext>>();
                services.AddSingleton(new DbContextOptionsBuilder<ZenkinsDbContext>()
                    .UseZenkinsDatabase(new ZenkinsDataOptions(DatabaseProvider.Sqlite, Data.ConnectionString))
                    .AddInterceptors(Counter)
                    .Options);
            });
        });
    }

    public TwoOrgsFixture Data { get; }

    public DbCommandCounter Counter { get; }

    public WebApplicationFactory<Program> Factory { get; }

    public HttpClient CreateClient() =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("http://localhost"),
        });

    public async Task<HttpClient> SignInAsync(string email)
    {
        var client = CreateClient();
        var response = await AuthTestHost.LoginAsync(client, email, TwoOrgsFixture.UserPassword, returnUrl: "/");
        if ((int)response.StatusCode is not (302 or 303))
        {
            client.Dispose();
            throw new InvalidOperationException($"Signing in as '{email}' failed with {(int)response.StatusCode}.");
        }

        return client;
    }

    public void Dispose()
    {
        Factory.Dispose();
        _baseFactory.Dispose();
        Data.Dispose();
    }
}

/// <summary>
/// Direct-SQL expectations for the counts test (card D-002 TC-001: "the numbers are the rows"). Raw
/// SQL on the same file is ground truth that does not share a single line of query code with
/// <see cref="DashboardQuery"/> - including its in-memory derivation.
/// </summary>
public static class DashboardSql
{
    public static async Task<int> CountRunsAsync(
        TwoOrgsFixture fixture,
        Guid organizationId,
        string status,
        CancellationToken cancellationToken = default)
    {
        await using var context = fixture.CreateContext();
        return await context.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM BuildRuns WHERE OrganizationId = {organizationId} AND Status = {status}")
            .SingleAsync(cancellationToken);
    }

    public static async Task<int> CountBusyWorkersAsync(
        TwoOrgsFixture fixture,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        const string running = "Running";
        await using var context = fixture.CreateContext();
        return await context.Database.SqlQuery<int>(
            $"""
            SELECT COUNT(DISTINCT WorkerId) AS Value FROM BuildRuns
            WHERE OrganizationId = {organizationId} AND Status = {running} AND WorkerId IS NOT NULL
            """).SingleAsync(cancellationToken);
    }

    public static async Task<int> CountWorkersAsync(
        TwoOrgsFixture fixture,
        Guid organizationId,
        string status,
        CancellationToken cancellationToken = default)
    {
        await using var context = fixture.CreateContext();
        return await context.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM Workers WHERE OrganizationId = {organizationId} AND Status = {status}")
            .SingleAsync(cancellationToken);
    }

    public static async Task<int> CountProjectsAsync(
        TwoOrgsFixture fixture,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        await using var context = fixture.CreateContext();
        return await context.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM Projects WHERE OrganizationId = {organizationId}")
            .SingleAsync(cancellationToken);
    }
}

/// <summary>Reads a rendered leaf element's text out of a static-SSR page by its id.</summary>
public static class DashboardPageReader
{
    public static string TextById(string html, string id)
    {
        var idIndex = html.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
        Assert.True(idIndex >= 0, $"The page did not render an element with id '{id}'.");
        var openTagEnd = html.IndexOf('>', idIndex);
        Assert.True(openTagEnd > idIndex, $"Element '{id}' never closed its opening tag.");
        var closeIndex = html.IndexOf("</", openTagEnd, StringComparison.Ordinal);
        Assert.True(closeIndex > openTagEnd, $"Element '{id}' has no closing tag.");
        var inner = html[(openTagEnd + 1)..closeIndex];
        return Regex.Replace(inner, "<[^>]+>", string.Empty).Trim();
    }

    public static IReadOnlyList<string> RecentRunIds(string html)
    {
        var matches = Regex.Matches(html, "id=\"dashboard-run-([0-9a-f]{8})\"");
        return matches.Select(match => match.Groups[1].Value).ToList();
    }
}
