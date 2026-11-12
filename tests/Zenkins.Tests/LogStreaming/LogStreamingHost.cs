using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.LogStreaming;
using Zenkins.Shared.LogStreaming;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Runs;
using Zenkins.Tests.Workers;

// The worker project's namespace shadows entity types; same aliases as the other hosts use.
using WorkerTokenEntity = Zenkins.Server.Data.Entities.WorkerToken;

namespace Zenkins.Tests.LogStreaming;

/// <summary>
/// F-008's HTTP/SignalR test host: everything <see cref="RunTestHost"/> does (workers, runs,
/// claim/accept/complete through the real API), plus the pieces the log-streaming tests need:
/// hub connections routed through the in-memory TestServer (long polling - TestServer has no
/// WebSocket support), subscription tickets minted through the real service, cross-tenant reads
/// of the <see cref="BuildLog"/> table, and the ingest statistics.
/// </summary>
public sealed class LogStreamingHost : IDisposable
{
    private LogStreamingHost(RunTestHost inner) => Inner = inner;

    public RunTestHost Inner { get; }

    /// <summary>The raw <see cref="AuthTestHost"/> under the wrappers.</summary>
    public AuthTestHost Host => Inner.Host;

    public static async Task<LogStreamingHost> CreateAsync(
        Action<IConfigurationBuilder>? configureAppConfiguration = null) =>
        new(await RunTestHost.CreateAsync(configureAppConfiguration));

    public async Task<Guid> OrganizationIdAsync() => await Inner.OrganizationIdAsync();

    public Task<(Guid ProjectId, HttpClient Owner)> CreateProjectAsync(string name = "log-project") =>
        Inner.CreateProjectAsync(name);

    public Task<(string Token, Guid WorkerId)> RegisterWorkerAsync(string name = "log-agent") =>
        Inner.RegisterWorkerAsync(name: name);

    public Task<Guid> AddRunAsync(Guid projectId, Guid organizationId = default) =>
        Inner.AddRunAsync(projectId, organizationId: organizationId);

    /// <summary>Registers a second account through the real form (organization-free until one is added).</summary>
    public Task<HttpClient> RegisterUserAsync(string email) => Inner.Inner.Inner.RegisterUserAsync(email);

    /// <summary>Seeds an organization through the system scope, optionally with memberships.</summary>
    public Task<Guid> AddOrganizationAsync(
        string name,
        params (string Email, OrganizationRole Role)[] members) =>
        Inner.Inner.Inner.AddOrganizationAsync(name, members);

    public Task<User> ReadUserAsync(string email) => Inner.Inner.Inner.ReadUserAsync(email);

    public Task<BuildRun> ReadRunAsync(Guid runId) => Inner.ReadRunAsync(runId);

    /// <summary>
    /// Builds a SignalR connection to one of the host's hubs. Long polling only (TestServer
    /// cannot upgrade WebSockets), routed through the TestServer handler, with the credential
    /// presented exactly like the product does (bearer on negotiate/poll, access_token on the
    /// WebSocket handshake - long polling exercises the header path).
    /// </summary>
    public HubConnection BuildHubConnection(string path, string? credential = null)
    {
        var handler = Host.Server.CreateHandler();
        return new HubConnectionBuilder()
            .WithUrl(new Uri("http://localhost" + path), options =>
            {
                if (credential is not null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(credential);
                }

                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => handler;
            })
            .Build();
    }

    /// <summary>A connection to the worker upstream hub with a worker token (null = unauthenticated).</summary>
    public HubConnection BuildWorkerHubConnection(string? workerToken) =>
        BuildHubConnection("/hubs/worker-logs", workerToken);

    /// <summary>A connection to the browser hub with a subscription ticket (null = unauthenticated).</summary>
    public HubConnection BuildBrowserHubConnection(string? ticket) =>
        BuildHubConnection("/hubs/run-logs", ticket);

    /// <summary>
    /// Mints a subscription ticket through the real <see cref="RunLogAccess"/> (the same call the
    /// run detail page makes with its session values).
    /// </summary>
    public Task<string> MintTicketAsync(string userId, Guid organizationId) =>
        Host.InScopeAsync(services =>
        {
            var access = services.GetRequiredService<RunLogAccess>();
            return Task.FromResult(access.MintTicket(userId, organizationId));
        });

    public Task<string> ReadOwnerUserIdAsync() =>
        Host.InScopeAsync(async services =>
        {
            var context = await services.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync();
            var user = await context.Users.SingleAsync(candidate =>
                candidate.Email == Organizations.OrganizationTestHost.OwnerEmail);
            return user.Id;
        });

    /// <summary>Reads another organization's user id (for foreign-ticket tests).</summary>
    public Task<string> ReadUserIdAsync(string email) =>
        Host.InScopeAsync(async services =>
        {
            var context = await services.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync();
            var user = await context.Users.SingleAsync(candidate => candidate.Email == email);
            return user.Id;
        });

    /// <summary>All log rows of one run, across tenants and ordered by Seq (assertion side).</summary>
    public Task<IReadOnlyList<BuildLog>> ReadLogRowsAsync(Guid runId) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await services.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync();
            using var scope = systemDataScope.BeginScope();
            var rows = await context.SystemQueryAsync<BuildLog>(log => log.RunId == runId);
            return (IReadOnlyList<BuildLog>)rows.OrderBy(log => log.Seq).ToList();
        });

    /// <summary>The ingest's bounded-batch counters.</summary>
    public RunLogIngestStatistics ReadIngestStatistics() =>
        Host.Services.GetRequiredService<RunLogIngest>().Statistics;

    /// <summary>How many runs currently hold ingest state (the F3 leak-guard test seam).</summary>
    public int PendingRunCount => Host.Services.GetRequiredService<RunLogIngest>().PendingRunCount;

    /// <summary>The worker token rows of one organization (for foreign-worker probes).</summary>
    public Task<IReadOnlyList<WorkerTokenEntity>> ReadWorkerTokensAsync(Guid organizationId) =>
        Inner.Inner.ReadWorkerTokensAsync(organizationId);

    public void Dispose() => Inner.Dispose();
}

/// <summary>Collects the lines one browser-hub subscriber receives, split by channel.</summary>
public sealed class LogCollector
{
    private readonly object _gate = new();
    private readonly List<LogLineMessage> _history = [];
    private readonly List<LogLineMessage> _live = [];
    private readonly TaskCompletionSource _firstLine = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _reachedLine = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LogCollector(int firstTargetSeq) => FirstTargetSeq = firstTargetSeq;

    /// <summary>The Seq whose arrival completes <see cref="WaitForTargetSeqAsync"/>.</summary>
    public int FirstTargetSeq { get; }

    public static LogCollector WaitingFor(int targetSeq) => new(targetSeq);

    public void OnHistory(IReadOnlyList<LogLineMessage> lines) => Add(_history, lines);

    public void OnReceive(IReadOnlyList<LogLineMessage> lines) => Add(_live, lines);

    /// <summary>Everything received, history chunks and live batches together.</summary>
    public List<LogLineMessage> Snapshot()
    {
        lock (_gate)
        {
            return [.. _history, .. _live];
        }
    }

    private void Add(List<LogLineMessage> channel, IReadOnlyList<LogLineMessage> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            channel.AddRange(lines);
            foreach (var line in lines)
            {
                if (line.Seq >= FirstTargetSeq)
                {
                    _reachedLine.TrySetResult();
                }
            }
        }

        _firstLine.TrySetResult();
    }

    public Task WaitForFirstLineAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _firstLine.Task.WaitAsync(timeout, cancellationToken);

    public Task WaitForTargetSeqAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _reachedLine.Task.WaitAsync(timeout, cancellationToken);
}
