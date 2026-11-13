using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Runs;
using Zenkins.Server.Workers;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Runs;
using Zenkins.Tests.Workers;

// The worker project's namespace shadows the entity types; same aliases as the other hosts use.
using WorkerEntity = Zenkins.Server.Data.Entities.Worker;

namespace Zenkins.Tests.RunLifecycle;

/// <summary>
/// F-009/F-010's shared test host (Category=RunLifecycle): everything <see cref="RunTestHost"/>
/// does (workers, runs, claim/accept/complete through the real API), plus the two cards' new
/// surfaces - the cancel form endpoint, the worker canceled report, the deadline and offline
/// sweeps through their background services' real seams, and the time-control helpers that keep
/// every test off real-time waits (the lease sweeper's in-memory-expiry pattern).
/// </summary>
public sealed class RunLifecycleHost : IDisposable
{
    private RunLifecycleHost(RunTestHost inner) => Inner = inner;

    public RunTestHost Inner { get; }

    /// <summary>The raw <see cref="AuthTestHost"/> under the wrappers.</summary>
    public AuthTestHost Host => Inner.Host;

    public static Task<RunLifecycleHost> CreateAsync() => CreateAsync(null);

    /// <param name="configureAppConfiguration">
    /// e.g. a short <c>Workers:OfflineAfterSeconds</c> (F-010) or a sub-minute
    /// <c>Runs:DefaultTimeoutMinutes</c> (F-009) instead of waiting on real time.
    /// </param>
    public static async Task<RunLifecycleHost> CreateAsync(
        Action<IConfigurationBuilder>? configureAppConfiguration) =>
        new(await RunTestHost.CreateAsync(configureAppConfiguration));

    public Task<Guid> OrganizationIdAsync() => Inner.OrganizationIdAsync();

    public Task<(Guid ProjectId, HttpClient Owner)> CreateProjectAsync(string name = "lifecycle-project") =>
        Inner.CreateProjectAsync(name);

    /// <summary>Signs in the starter organization's Owner.</summary>
    public Task<HttpClient> SignInOwnerAsync() => Inner.Inner.SignInOwnerAsync();

    public Task<(string Token, Guid WorkerId)> RegisterWorkerAsync(
        string name = "lifecycle-agent",
        string? tags = "ci",
        int maxConcurrency = 1)
    {
        var token = Interlocked.Increment(ref _workerSequence);
        return Inner.RegisterWorkerAsync(name: $"{name}-{token}", tags: tags, maxConcurrency: maxConcurrency);
    }

    private int _workerSequence;

    public Task<Guid> AddRunAsync(
        Guid projectId,
        string requiredTags = "",
        string command = "dotnet --version",
        Guid organizationId = default) =>
        Inner.AddRunAsync(projectId, requiredTags, command, organizationId);

    /// <summary>Registers a second account through the real form (organization-free until one is added).</summary>
    public Task<HttpClient> RegisterUserAsync(string email) => Inner.Inner.Inner.RegisterUserAsync(email);

    /// <summary>Signs in an existing account on a fresh cookie jar.</summary>
    public Task<HttpClient> SignInAsync(string email) => Inner.Inner.Inner.SignInAsync(email);

    /// <summary>Seeds an organization through the system scope, optionally with memberships.</summary>
    public Task<Guid> AddOrganizationAsync(
        string name,
        params (string Email, OrganizationRole Role)[] members) =>
        Inner.Inner.Inner.AddOrganizationAsync(name, members);

    public Task<User> ReadUserAsync(string email) => Inner.Inner.Inner.ReadUserAsync(email);

    public Task<BuildRun> ReadRunAsync(Guid runId) => Inner.ReadRunAsync(runId);

    public Task<WorkerEntity> ReadWorkerAsync(Guid workerId) => Inner.ReadWorkerAsync(workerId);

    // ------------------------------------------------------------------
    // F-009 surfaces
    // ------------------------------------------------------------------

    /// <summary>POSTs the run page's cancel form (antiforgery included, like the product's button).</summary>
    public static Task<HttpResponseMessage> CancelRunAsync(HttpClient client, Guid runId) =>
        CancelRunFromPageAsync(client, runId, $"/runs/{runId}");

    /// <summary>
    /// POSTs the cancel form with the antiforgery token extracted from <paramref name="tokenPageUrl"/>
    /// - the cross-tenant probe pattern: a foreign member cannot open the run page (404, no
    /// existence oracle), so their token comes from a page they can see.
    /// </summary>
    public static Task<HttpResponseMessage> CancelRunFromPageAsync(
        HttpClient client,
        Guid runId,
        string tokenPageUrl) =>
        OrganizationTestHost.PostFormAsync(
            client,
            tokenPageUrl,
            $"/runs/{runId}/cancel",
            new Dictionary<string, string>());

    /// <summary>POSTs the worker's canceled report (token-authenticated, like the product's worker).</summary>
    public static Task<HttpResponseMessage> ReportCanceledAsync(
        HttpClient client,
        string workerToken,
        Guid runId,
        string? outcome)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workers/runs/{runId}/canceled")
        {
            Content = JsonContent.Create(new { outcome }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", workerToken);
        return client.SendAsync(request);
    }

    /// <summary>One deadline-sweep tick through the background service's real seam.</summary>
    public Task<RunService.DeadlineSweepResult> SweepDeadlinesAsync() =>
        Host.InScopeAsync(async services =>
        {
            var sweeper = services.GetServices<IHostedService>().OfType<LeaseSweeper>().Single();
            return await sweeper.SweepDeadlinesOnceAsync();
        });

    /// <summary>One offline-sweep tick through the monitor's real seam.</summary>
    public Task<OfflineSweepResult> SweepOfflineAsync() =>
        Host.InScopeAsync(async services =>
        {
            var monitor = services.GetServices<IHostedService>().OfType<WorkerOfflineMonitor>().Single();
            return await monitor.SweepOnceAsync();
        });

    /// <summary>Sets (or clears) a run's deadline directly - the deterministic stand-in for "the
    /// deadline passed" (deadlines are minutes by default; the clock is not).</summary>
    public Task SetDeadlineAsync(Guid runId, DateTimeOffset? deadline) =>
        InSystemScopeAsync(runId, (context, run, _) =>
        {
            run.DeadlineAt = deadline;
            return Task.FromResult(true);
        });

    /// <summary>Sets (or clears) a run's per-run timeout override directly.</summary>
    public Task SetTimeoutSecondsAsync(Guid runId, int? seconds) =>
        InSystemScopeAsync(runId, (context, run, _) =>
        {
            run.TimeoutSeconds = seconds;
            return Task.FromResult(true);
        });

    /// <summary>
    /// Backdates a worker's last heartbeat - the deterministic stand-in for "the worker went
    /// silent" (the threshold is seconds; a missed heartbeat must not be simulated by waiting).
    /// Returns the backdated value - the observation a snapshot taken before a real heartbeat
    /// would hold (the offline CAS's staleness-evidence pin).
    /// </summary>
    public Task<DateTimeOffset> BackdateHeartbeatAsync(Guid workerId, TimeSpan age) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            var worker = (await context.SystemQueryAsync<WorkerEntity>(candidate => candidate.Id == workerId)).Single();
            var backdated = DateTimeOffset.UtcNow - age;
            worker.LastHeartbeatAt = backdated;
            await context.SaveChangesAsync();
            return backdated;
        });

    /// <summary>
    /// Sends one real heartbeat and returns the raw response - the fallback cancel channel's
    /// assertion side (the payload the worker's heartbeat loop parses).
    /// </summary>
    public static async Task<JsonDocument> HeartbeatAsync(HttpClient client, string workerToken)
    {
        var response = await WorkerTestHost.HeartbeatAsync(
            client,
            workerToken,
            WorkerTestHost.HeartbeatPayload(os: "Linux", cpuCount: 8, maxConcurrency: 2));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    /// <summary>Calls the guarded queued-cancel compare-and-set directly, tenant-scoped (the
    /// CAS-discipline pin: it must refuse a run that is no longer queued).</summary>
    public Task<(int Rows, string? Status)> CancelQueuedViaCasAsync(Guid runId, string reason) =>
        Host.InScopeAsync(async services =>
        {
            var tenantContext = services.GetRequiredService<TenantContext>();
            tenantContext.OrganizationId = await OrganizationIdAsync();
            await using var context = await CreateContextAsync(services);
            var rows = await context.TryCancelQueuedBuildRunAsync(
                runId,
                reason,
                DateTimeOffset.UtcNow);
            string? status = null;
            if (rows == 0)
            {
                var run = await context.BuildRuns.AsNoTracking().SingleAsync(candidate => candidate.Id == runId);
                status = run.Status;
            }

            return (rows, status);
        });

    /// <summary>Calls the guarded run-termination compare-and-set directly, twice, system-scoped
    /// (the exactly-once pin: the second call must refuse a run that already left Running).</summary>
    public Task<(int FirstRows, int SecondRows, DateTimeOffset? EndedAt)> TerminateRunningViaCasTwiceAsync(
        Guid runId,
        string targetStatus,
        string reason)
    {
        return Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            var now = DateTimeOffset.UtcNow;
            var first = await context.TryTerminateRunningBuildRunAsync(runId, targetStatus, reason, now);
            var second = await context.TryTerminateRunningBuildRunAsync(
                runId, targetStatus, reason, now.AddMilliseconds(50));
            var run = await context.BuildRuns
                .AsNoTracking()
                .IgnoreQueryFilters()
                .SingleAsync(candidate => candidate.Id == runId);
            return (first, second, run.EndedAt);
        });
    }

    /// <summary>Runs a callback against the whole run table, cross-tenant (assertion helper).</summary>
    public Task<T> InSystemScopeAsync<T>(Func<ZenkinsDbContext, Task<T>> action) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            return await action(context);
        });

    private Task InSystemScopeAsync(
        Guid runId,
        Func<ZenkinsDbContext, BuildRun, CancellationToken, Task<bool>> action) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            var run = (await context.SystemQueryAsync<BuildRun>(candidate => candidate.Id == runId)).Single();
            await action(context, run, TestContext.Current.CancellationToken);
            await context.SaveChangesAsync();
            return true;
        });

    /// <summary>All log rows of one run, across tenants and ordered by Seq (partial-log assertions).</summary>
    public Task<IReadOnlyList<BuildLog>> ReadLogRowsAsync(Guid runId) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            var rows = await context.SystemQueryAsync<BuildLog>(log => log.RunId == runId);
            return (IReadOnlyList<BuildLog>)rows.OrderBy(log => log.Seq).ToList();
        });

    private static async Task<ZenkinsDbContext> CreateContextAsync(IServiceProvider services) =>
        await services.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>().CreateDbContextAsync();

    public void Dispose() => Inner.Dispose();
}
