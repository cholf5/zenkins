using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.LogStreaming;
using Zenkins.Worker;
using Zenkins.Tests.Runs;
using WorkerTokenCrypto = Zenkins.Server.Workers.WorkerTokenCrypto;

namespace Zenkins.Tests.LogStreaming;

/// <summary>
/// Review-fix tests for the worker log stream's definitive-rejection protocol (F-008):
/// <list type="bullet">
/// <item>F1: a server rejection (here: a foreign-organization run) is a machine-prefixed
/// <c>HubException</c>, and the worker's drain recognizes it, ends within a bounded time, drops
/// the batch and everything buffered, and leaves one clear stderr line - instead of the old
/// substring discriminator that never matched SignalR's forwarded rejection texts and made the
/// drain re-send forever.</item>
/// <item>F2: a dead worker token mid-connection fails observably and cannot reconnect. The hub
/// does not abort the connection first (review fix F2): it answers with the same prefixed
/// rejection, and the authentication middleware guarantees the next call is a bare 401 - over
/// the TestServer's long polling that middleware re-authentication is per-request and directly
/// observable (401 on the next append, fresh negotiate rejected); over the production WebSocket
/// transport the hub method's own re-validation throws the prefixed answer the foreign-run test
/// pins.</item>
/// </list>
/// Both run against the real server and the real hub endpoint (long polling through the
/// TestServer), with the drain driven through the real <see cref="RunLogBuffer"/>.
/// </summary>
[Trait("Category", "LogStreaming")]
public sealed class WorkerLogRejectionTests
{
    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_foreign_run_rejection_ends_the_drain_bounded_with_a_clear_stderr_line()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await LogStreamingHost.CreateAsync();

        // Organization A: project + run, claimed and accepted (the run row exists, assigned to A's worker).
        var (projectId, _) = await host.CreateProjectAsync();
        var (tokenA, _) = await host.RegisterWorkerAsync(name: "drain-reject-a");
        var runId = await host.AddRunAsync(projectId);
        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, tokenA);
        await RunTestHost.AcceptAsync(client, tokenA, runId);

        // Organization B: its perfectly valid worker token streams into A's run - a definitive
        // rejection ("The run does not exist in this worker's organization.") behind the prefix.
        var bOrganizationId = await host.AddOrganizationAsync("drain-reject-org-b");
        var tokenB = await host.Inner.CreateWorkerTokenDirectlyAsync(bOrganizationId, name: "drain-reject-b");

        await using var connection = host.BuildWorkerHubConnection(tokenB);
        await connection.StartAsync(cancellationToken);

        var stderr = new StringWriter();
        var buffer = new RunLogBuffer(runId, capacity: 100, stderr);
        buffer.Emit(LogStream.StdOut, "line-1");
        buffer.Emit(LogStream.StdOut, "line-2");
        buffer.CompleteInput();

        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = buffer.DrainAsync(new LiveHubSink(connection), shutdown.Token);
        try
        {
            // Bounded: with the old "due to an error on the server" discriminator the drain never
            // recognized the rejection and this await timed out (the red proof).
            await pump.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        finally
        {
            await shutdown.CancelAsync();
            try
            {
                await pump;
            }
            catch (OperationCanceledException)
            {
                // The red-proof path: the still-retrying pump stops with the shutdown signal.
            }
        }

        var stderrText = stderr.ToString();
        Assert.Contains("permanently rejected", stderrText, StringComparison.Ordinal);
        Assert.Contains(LogStreamRejection.Prefix, stderrText, StringComparison.Ordinal);
        Assert.Contains("The run does not exist in this worker's organization.", stderrText, StringComparison.Ordinal);

        // The buffer terminated: the rejected batch and everything behind it is dropped.
        Assert.Equal(0, buffer.PendingCount);
        Assert.Equal(0, buffer.DroppedCount);
        Assert.Equal(2, buffer.LastSeq); // no renumbering, no marker displacing real lines

        // Nothing ever reached A's log table.
        Assert.Empty(await host.ReadLogRowsAsync(runId));
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_dead_token_fails_observably_and_cannot_reconnect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await LogStreamingHost.CreateAsync();

        var (projectId, _) = await host.CreateProjectAsync();
        var (token, _) = await host.RegisterWorkerAsync(name: "dead-token-agent");
        var runId = await host.AddRunAsync(projectId);
        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, token);
        await RunTestHost.AcceptAsync(client, token, runId);

        await using var worker = host.BuildWorkerHubConnection(token);
        await worker.StartAsync(cancellationToken);
        await worker.InvokeAsync("AppendLinesAsync", runId, Batch(1, 5), cancellationToken);

        await RevokeTokenAsync(host, token);

        // The dead credential fails OBSERVABLY (review fix F2): no silent success, no silent
        // drop. Over the TestServer's long polling every request re-authenticates, so the next
        // append's poll/send is a bare 401 from the authentication middleware - the guarantee the
        // fix's rationale relies on ("the middleware already ensures the next call is a 401; no
        // abort needed"). On the production WebSocket transport the middleware cannot re-run;
        // there the hub method's own per-call re-validation throws the prefixed rejection that
        // the foreign-run test pins, and the drain ends on it - the same answer either way.
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
            worker.InvokeAsync("AppendLinesAsync", runId, Batch(6, 5), cancellationToken));
        Assert.Contains("401", failure.Message, StringComparison.Ordinal);

        // A fresh connection with the dead token is a bare 401 at negotiate time: the dead
        // credential cannot accumulate connections.
        await using var fresh = host.BuildWorkerHubConnection(token);
        var negotiate = await Assert.ThrowsAnyAsync<Exception>(
            () => fresh.StartAsync(cancellationToken));
        Assert.False(worker.State == HubConnectionState.Connected);
        _ = negotiate;

        // Exactly the pre-revocation batch landed; the rejected ones never did.
        var rows = await host.ReadLogRowsAsync(runId);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (rows.Count < 5 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken);
            rows = await host.ReadLogRowsAsync(runId);
        }

        Assert.Equal(5, rows.Count);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        Assert.Equal(5, (await host.ReadLogRowsAsync(runId)).Count);
    }

    private static async Task RevokeTokenAsync(LogStreamingHost host, string plaintextToken)
    {
        var hash = WorkerTokenCrypto.Hash(plaintextToken);
        await host.Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await services.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync();
            using var scope = systemDataScope.BeginScope();
            var row = (await context.SystemQueryAsync<WorkerToken>(
                candidate => candidate.TokenHash == hash && candidate.Kind == WorkerTokenKind.Worker)).Single();
            row.RevokedAt = DateTimeOffset.UtcNow;
            return await context.SaveChangesAsync();
        });
    }

    private static LogLineMessage[] Batch(int firstSeq, int count)
    {
        var lines = new LogLineMessage[count];
        for (var i = 0; i < count; i++)
        {
            var seq = firstSeq + i;
            lines[i] = new LogLineMessage(seq, LogStream.StdOut, $"line-{seq}", DateTimeOffset.UtcNow);
        }

        return lines;
    }

    /// <summary>
    /// An <see cref="ILogStreamSink"/> over a real hub connection - the product's
    /// <see cref="LogStreamClient"/> would also run its reconnect loop, which this test does not
    /// need: the connection stays up (F2 guarantees no abort), so a simple delegation suffices.
    /// </summary>
    private sealed class LiveHubSink(HubConnection connection) : ILogStreamSink
    {
        public bool IsConnected => connection.State == HubConnectionState.Connected;

        public bool HasEverConnected => true;

        public Task WaitForConnectedAsync(CancellationToken shutdown) =>
            connection.State == HubConnectionState.Connected
                ? Task.CompletedTask
                : Task.Delay(Timeout.Infinite, shutdown);

        public Task SendLinesAsync(Guid runId, IReadOnlyList<LogLineMessage> lines, CancellationToken shutdown) =>
            connection.InvokeAsync("AppendLinesAsync", runId, lines, shutdown);
    }
}
