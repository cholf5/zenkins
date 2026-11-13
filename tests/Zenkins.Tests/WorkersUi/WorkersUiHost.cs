using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Workers;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Runs;

// The worker project's namespace is Zenkins.Worker, which shadows the entity type name inside any
// Zenkins.* namespace; the alias keeps the entity readable here (same as the other hosts).
using WorkerEntity = Zenkins.Server.Data.Entities.Worker;

namespace Zenkins.Tests.WorkersUi;

/// <summary>
/// Card D-005's HTTP test host: everything <see cref="RunTestHost"/> does (projects, worker
/// registration through the real API, the real claim/accept/complete endpoints, cross-tenant
/// reads), plus the page reads and Owner form posts the workers pages' tests repeat, and the
/// offline monitor's sweep seam so heartbeat ageing goes through F-010's real path - the page is
/// never compared against a recomputation of its own.
/// </summary>
internal sealed class WorkersUiHost : IDisposable
{
    private WorkersUiHost(RunTestHost inner) => Runs = inner;

    public RunTestHost Runs { get; }

    /// <summary>The raw <see cref="AuthTestHost"/> under the wrappers.</summary>
    public AuthTestHost Host => Runs.Host;

    public static Task<WorkersUiHost> CreateAsync() => CreateAsync(null);

    /// <param name="configureAppConfiguration">e.g. a short <c>Workers:OfflineAfterSeconds</c> for the sweeper tests.</param>
    public static async Task<WorkersUiHost> CreateAsync(
        Action<IConfigurationBuilder>? configureAppConfiguration) =>
        new(await RunTestHost.CreateAsync(configureAppConfiguration));

    /// <summary>The /workers roster's HTML, through the real authenticated page.</summary>
    public Task<string> ReadRosterAsync(HttpClient client) =>
        AuthTestHost.GetStringAsync(client, "/workers");

    /// <summary>The /workers/{id} detail page's HTML, through the real authenticated page.</summary>
    public Task<string> ReadWorkerDetailAsync(HttpClient client, Guid workerId) =>
        AuthTestHost.GetStringAsync(client, $"/workers/{workerId}");

    /// <summary>
    /// POSTs one of the worker's Owner control forms (disable/enable/delete) with the antiforgery
    /// token taken from the given page, and returns the raw response (PRG redirect or rejection).
    /// </summary>
    public Task<HttpResponseMessage> PostWorkerActionAsync(
        HttpClient client,
        Guid workerId,
        string action,
        string pageUrl = "/workers") =>
        OrganizationTestHost.PostFormAsync(client, pageUrl, $"/workers/{workerId}/{action}", new Dictionary<string, string>());

    /// <summary>
    /// Ages a heartbeat directly - the deterministic stand-in for "the worker went silent" (the
    /// threshold is seconds; the clock is not). The sweep itself runs through the real monitor.
    /// </summary>
    public Task<DateTimeOffset> BackdateHeartbeatAsync(Guid workerId, TimeSpan age) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            var worker = (await context.SystemQueryAsync<WorkerEntity>(
                candidate => candidate.Id == workerId)).Single();
            var backdated = DateTimeOffset.UtcNow - age;
            worker.LastHeartbeatAt = backdated;
            await context.SaveChangesAsync();
            return backdated;
        });

    /// <summary>One offline-sweep tick through the background service's real seam (F-010).</summary>
    public Task<OfflineSweepResult> SweepOfflineAsync() =>
        Host.InScopeAsync(async services =>
        {
            // AddHostedService registers the implementation as IHostedService; find it there.
            var monitor = services.GetServices<IHostedService>().OfType<WorkerOfflineMonitor>().Single();
            return await monitor.SweepOnceAsync();
        });

    private static async Task<ZenkinsDbContext> CreateContextAsync(IServiceProvider services) =>
        await services.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>().CreateDbContextAsync();

    public void Dispose() => Runs.Dispose();
}
