using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.LogStreaming;
using Zenkins.Shared.LogStreaming;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.LogStreaming;

/// <summary>
/// Card F-008's access-control tests (TC-004): unauthenticated hub connections are a bare 401
/// before any hub method exists; a foreign organization's member can neither join the run's group
/// nor download the log; a worker token of another organization cannot stream lines into a
/// foreign run; forged and expired subscription tickets are worthless.
/// </summary>
[Trait("Category", "LogStreaming")]
public sealed class RunLogAccessTests
{
    [Fact]
    public async Task Unauthenticated_negotiate_is_a_bare_401_on_both_hubs()
    {
        using var host = await LogStreamingHost.CreateAsync();
        var client = host.Host.CreateClient();

        foreach (var hubPath in new[] { "/hubs/worker-logs", "/hubs/run-logs" })
        {
            using var negotiate = await client.PostAsync(
                $"{hubPath}/negotiate?negotiateVersion=1",
                content: null,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
            var body = await negotiate.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(body.Length == 0, $"the 401 carried a body ({body.Length} bytes) - no probing detail allowed");
        }
    }

    [Fact]
    public async Task Unauthenticated_hub_connections_are_rejected()
    {
        using var host = await LogStreamingHost.CreateAsync();

        await using var worker = host.BuildWorkerHubConnection(null);
        await Assert.ThrowsAnyAsync<Exception>(
            () => worker.StartAsync(TestContext.Current.CancellationToken));

        await using var browser = host.BuildBrowserHubConnection(null);
        await Assert.ThrowsAnyAsync<Exception>(
            () => browser.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_foreign_organizations_member_cannot_join_the_run_group_or_read_lines()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await LogStreamingHost.CreateAsync();

        // Organization A: a project, a run with a worker streaming lines.
        var (projectId, _) = await host.CreateProjectAsync();
        var (token, _) = await host.RegisterWorkerAsync();
        var runId = await host.AddRunAsync(projectId);
        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, token);
        await RunTestHost.AcceptAsync(client, token, runId);

        // Organization B: its own member with a perfectly valid ticket for B.
        var secondEmail = "log-access-b@example.test";
        await host.RegisterUserAsync(secondEmail);
        var bOrganizationId = await host.AddOrganizationAsync(
            "log-access-org-b", (secondEmail, OrganizationRole.Owner));
        var bUserId = await host.ReadUserAsync(secondEmail);
        var bTicket = await host.MintTicketAsync(bUserId.Id, bOrganizationId);

        var collector = LogCollector.WaitingFor(int.MaxValue);
        await using var browserB = host.BuildBrowserHubConnection(bTicket);
        browserB.On<IReadOnlyList<LogLineMessage>>("HistoryLines", collector.OnHistory);
        browserB.On<IReadOnlyList<LogLineMessage>>("ReceiveLines", collector.OnReceive);
        await browserB.StartAsync(cancellationToken);

        // Joining A's run group is rejected with the generic answer (no existence oracle).
        await Assert.ThrowsAsync<HubException>(
            () => browserB.InvokeAsync("JoinRunAsync", runId, cancellationToken));

        // And no line ever reaches B: A streams its whole log, B's collector stays empty.
        await using var worker = host.BuildWorkerHubConnection(token);
        await worker.StartAsync(cancellationToken);
        for (var batch = 0; batch < 5; batch++)
        {
            await worker.InvokeAsync("AppendLinesAsync", runId, Batch(batch * 10 + 1, 10), cancellationToken);
        }

        await RunTestHost.CompleteAsync(client, token, runId, exitCode: 0);
        Assert.Equal("Success", (await host.ReadRunAsync(runId)).Status);

        await Task.Delay(500, cancellationToken);
        Assert.Empty(collector.Snapshot());

        // B's own organization still works: the ticket was never the problem.
        var bRunId = await host.AddRunAsync(projectId, organizationId: bOrganizationId);
        await using var browserB2 = host.BuildBrowserHubConnection(bTicket);
        await browserB2.StartAsync(cancellationToken);
        await browserB2.InvokeAsync("JoinRunAsync", bRunId, cancellationToken);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_worker_token_of_another_organization_cannot_stream_into_a_foreign_run()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await LogStreamingHost.CreateAsync();

        // Organization A: the run that must be protected.
        var (projectId, _) = await host.CreateProjectAsync();
        var (tokenA, workerIdA) = await host.RegisterWorkerAsync(name: "org-a-agent");
        var runIdA = await host.AddRunAsync(projectId);
        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, tokenA);
        await RunTestHost.AcceptAsync(client, tokenA, runIdA);

        // Organization B: a worker token minted directly through the real authentication path.
        var bOrganizationId = await host.AddOrganizationAsync("log-access-org-b-2");
        var tokenB = await host.Inner.CreateWorkerTokenDirectlyAsync(bOrganizationId, name: "org-b-agent");

        // B's token is valid (its connection is accepted), but A's run is invisible to it.
        await using var workerB = host.BuildWorkerHubConnection(tokenB);
        await workerB.StartAsync(cancellationToken);
        await Assert.ThrowsAsync<HubException>(() => workerB.InvokeAsync(
            "AppendLinesAsync", runIdA, Batch(1, 5), cancellationToken));

        var rows = await host.ReadLogRowsAsync(runIdA);
        Assert.Empty(rows);

        // A run B actually owns streams fine - the rejection was the tenant boundary, not the hub.
        var runIdB = await host.AddRunAsync(projectId, organizationId: bOrganizationId);
        // B's worker is not the assigned executor of runIdB: appending is refused as well.
        await Assert.ThrowsAsync<HubException>(() => workerB.InvokeAsync(
            "AppendLinesAsync", runIdB, Batch(1, 5), cancellationToken));
        Assert.Empty(await host.ReadLogRowsAsync(runIdB));

        // For contrast: A's own worker streams its own assigned run without friction.
        await using var workerA = host.BuildWorkerHubConnection(tokenA);
        await workerA.StartAsync(cancellationToken);
        await workerA.InvokeAsync("AppendLinesAsync", runIdA, Batch(1, 5), cancellationToken);

        // A partial batch flushes on the ingest timer (250 ms), not inline - poll for it.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var ownRows = await host.ReadLogRowsAsync(runIdA);
        while (ownRows.Count < 5 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken);
            ownRows = await host.ReadLogRowsAsync(runIdA);
        }

        Assert.Equal(5, ownRows.Count);
        _ = workerIdA;
    }

    [Fact]
    public async Task A_forged_or_garbled_subscription_ticket_is_rejected()
    {
        using var host = await LogStreamingHost.CreateAsync();

        await using var browser = host.BuildBrowserHubConnection("totally-not-a-ticket");
        await Assert.ThrowsAnyAsync<Exception>(
            () => browser.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Subscription_tickets_reject_forgery_and_expiry()
    {
        var options = new RunLogOptions(
            FlushLineCount: 100,
            FlushIntervalMilliseconds: 250,
            HistoryChunkSize: 500,
            TicketLifetime: TimeSpan.Zero); // expires immediately - the expiry test needs no clock
        var access = new RunLogAccess(
            new EphemeralDataProtectionProvider(),
            contextFactory: null!,
            tenantContext: null!,
            options);

        var ticket = access.MintTicket("user-1", Guid.NewGuid());
        Assert.Null(access.ValidateTicket("garbage"));
        Assert.Null(access.ValidateTicket(null));
        Assert.Null(access.ValidateTicket(ticket)); // lifetime zero: already expired

        var liveOptions = options with { TicketLifetime = TimeSpan.FromHours(1) };
        var liveAccess = new RunLogAccess(
            new EphemeralDataProtectionProvider(),
            contextFactory: null!,
            tenantContext: null!,
            liveOptions);
        var liveTicket = liveAccess.MintTicket("user-1", Guid.NewGuid());
        var validated = liveAccess.ValidateTicket(liveTicket);
        Assert.NotNull(validated);
        Assert.Equal("user-1", validated!.UserId);

        // A ticket from a different data-protection purpose (or process) is worthless here.
        Assert.Null(liveAccess.ValidateTicket(ticket));
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
}
