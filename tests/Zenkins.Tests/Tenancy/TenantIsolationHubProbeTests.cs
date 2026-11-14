using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Shared.LogStreaming;
using Zenkins.Tests.LogStreaming;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card Q-006's hub-level probes (TC-003). The browser hub must refuse org A's session for an org B
/// run id at the hub method itself - not only at the page - and no line of org B's run may ever
/// reach org A's connection; the worker hub must refuse appends to another organization's run and
/// persist nothing. Tickets and worker tokens are minted through the real services, the connections
/// ride the same long-polling transport the F-008 tests use, and every positive control proves the
/// refusal is the tenant boundary rather than a broken feature.
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationHubProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_browser_hub_refuses_a_foreign_run_id_at_the_hub_method()
    {
        using var host = new TenantProbeHostFixture();
        var ticket = await TenantIsolationAuditProbes.MintRunLogTicketAsync(
            host, TwoOrgsFixture.OrgAOwnerUserId, TwoOrgsFixture.OrgAId);

        var collector = LogCollector.WaitingFor(1);
        await using var connection = TenantIsolationAuditProbes.BuildHubConnection(host, "/hubs/run-logs", ticket);
        connection.On<IReadOnlyList<LogLineMessage>>("HistoryLines", collector.OnHistory);
        connection.On<IReadOnlyList<LogLineMessage>>("ReceiveLines", collector.OnReceive);
        await connection.StartAsync(CancellationToken);

        // Org A's ticket, org B's run id: the join is refused at the hub method with the generic
        // rejection - unknown run, foreign run and revoked membership are indistinguishable.
        var refusal = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("JoinRunAsync", TwoOrgsFixture.BuildRunBId, CancellationToken));
        Assert.DoesNotContain(TwoOrgsFixture.OrgBId.ToString(), refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Beta", refusal.Message, StringComparison.Ordinal);

        // Nothing arrived: a refused join sends no history chunk and no live line.
        Assert.Empty(collector.Snapshot());
    }

    [Fact]
    public async Task The_browser_hub_streams_only_the_joined_runs_lines_and_never_the_foreign_organizations()
    {
        using var host = new TenantProbeHostFixture();
        var ticket = await TenantIsolationAuditProbes.MintRunLogTicketAsync(
            host, TwoOrgsFixture.OrgAOwnerUserId, TwoOrgsFixture.OrgAId);

        var collector = LogCollector.WaitingFor(1);
        await using var connection = TenantIsolationAuditProbes.BuildHubConnection(host, "/hubs/run-logs", ticket);
        connection.On<IReadOnlyList<LogLineMessage>>("HistoryLines", collector.OnHistory);
        connection.On<IReadOnlyList<LogLineMessage>>("ReceiveLines", collector.OnReceive);
        await connection.StartAsync(CancellationToken);

        // Joining org A's own run streams org A's history - and org B's line never rides along.
        await connection.InvokeAsync("JoinRunAsync", TwoOrgsFixture.BuildRunAId, CancellationToken);
        await collector.WaitForFirstLineAsync(Timeout, CancellationToken);
        Assert.Contains(collector.Snapshot(), line => line.Message == "alpha-line");
        Assert.DoesNotContain(collector.Snapshot(), line => line.Message == "beta-line");

        // Org B's worker claims and accepts org B's run, then pushes a live line through the real
        // worker hub (positive control that the push path works and that the ingest really flushed).
        var (_, betaToken) = await TenantIsolationWorkerApiProbes.RegisterBetaWorkerAsync(host);
        using var betaApiClient = host.CreateClient();
        betaApiClient.DefaultRequestHeaders.Authorization = new("Bearer", betaToken);
        await TenantIsolationWorkerApiProbes.ClaimAndAcceptAsync(betaApiClient, TwoOrgsFixture.BuildRunBId);
        await using var betaWorkerConnection =
            TenantIsolationAuditProbes.BuildHubConnection(host, "/hubs/worker-logs", betaToken);
        await betaWorkerConnection.StartAsync(CancellationToken);
        var betaLine = new LogLineMessage(500, LogStream.StdOut, "beta-live-line", DateTimeOffset.UtcNow);
        await betaWorkerConnection.InvokeAsync(
            "AppendLinesAsync", TwoOrgsFixture.BuildRunBId, new[] { betaLine }, CancellationToken);
        await WaitForLogRowsAsync(host, TwoOrgsFixture.BuildRunBId, minimumCount: 2, Timeout);

        // Org A's connection, still joined to org A's group: no beta line ever arrives, even while
        // org B's run is being streamed live.
        await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken);
        Assert.DoesNotContain(collector.Snapshot(), line => line.Message == "beta-live-line");
        Assert.DoesNotContain(collector.Snapshot(), line => line.Message == "beta-line");

        // The reverse direction at the hub method: org B's ticket (carol's session) cannot join
        // org A's run either.
        var betaTicket = await TenantIsolationAuditProbes.MintRunLogTicketAsync(
            host, TwoOrgsFixture.OrgBOwnerUserId, TwoOrgsFixture.OrgBId);
        await using var betaBrowserConnection =
            TenantIsolationAuditProbes.BuildHubConnection(host, "/hubs/run-logs", betaTicket);
        await betaBrowserConnection.StartAsync(CancellationToken);
        await Assert.ThrowsAsync<HubException>(
            () => betaBrowserConnection.InvokeAsync("JoinRunAsync", TwoOrgsFixture.BuildRunAId, CancellationToken));
    }

    [Fact]
    public async Task The_worker_hub_refuses_appends_to_another_organizations_run_and_persists_nothing()
    {
        using var host = new TenantProbeHostFixture();
        var (_, alphaToken) = await TenantIsolationWorkerApiProbes.RegisterAlphaWorkerAsync(host);
        var (_, betaToken) = await TenantIsolationWorkerApiProbes.RegisterBetaWorkerAsync(host);
        var (_, unusedAlphaToken) = await TenantIsolationWorkerApiProbes.RegisterAlphaWorkerAsync(host);

        // Org A's executing worker claims and accepts its run first, so the refusals below are the
        // tenant/assignment boundaries - not a missing run.
        using var alphaApiClient = host.CreateClient();
        alphaApiClient.DefaultRequestHeaders.Authorization = new("Bearer", alphaToken);
        await TenantIsolationWorkerApiProbes.ClaimAndAcceptAsync(alphaApiClient, TwoOrgsFixture.BuildRunAId);

        await using var betaWorkerConnection =
            TenantIsolationAuditProbes.BuildHubConnection(host, "/hubs/worker-logs", betaToken);
        await betaWorkerConnection.StartAsync(CancellationToken);

        // Org B's worker appending to org A's run: a definitive rejection carrying the machine
        // prefix, and nothing is persisted.
        var refusal = await Assert.ThrowsAsync<HubException>(() => betaWorkerConnection.InvokeAsync(
            "AppendLinesAsync", TwoOrgsFixture.BuildRunAId,
            new[] { new LogLineMessage(900, LogStream.StdOut, "smuggled-line", DateTimeOffset.UtcNow) },
            CancellationToken));
        Assert.Contains(LogStreamRejection.Prefix, refusal.Message, StringComparison.Ordinal);
        Assert.Equal(1, await LogRowCountAsync(host, TwoOrgsFixture.BuildRunAId));

        // The mirror image: org A's worker against org B's run.
        await using var alphaWorkerConnection =
            TenantIsolationAuditProbes.BuildHubConnection(host, "/hubs/worker-logs", alphaToken);
        await alphaWorkerConnection.StartAsync(CancellationToken);
        var reverse = await Assert.ThrowsAsync<HubException>(() => alphaWorkerConnection.InvokeAsync(
            "AppendLinesAsync", TwoOrgsFixture.BuildRunBId,
            new[] { new LogLineMessage(900, LogStream.StdOut, "smuggled-line", DateTimeOffset.UtcNow) },
            CancellationToken));
        Assert.Contains(LogStreamRejection.Prefix, reverse.Message, StringComparison.Ordinal);
        Assert.Equal(1, await LogRowCountAsync(host, TwoOrgsFixture.BuildRunBId));

        // A second worker of the SAME organization (valid token, honest tenant) is refused as well:
        // only the executing worker streams a run's log.
        await using var otherAlphaConnection =
            TenantIsolationAuditProbes.BuildHubConnection(host, "/hubs/worker-logs", unusedAlphaToken);
        await otherAlphaConnection.StartAsync(CancellationToken);
        var sameTenant = await Assert.ThrowsAsync<HubException>(() => otherAlphaConnection.InvokeAsync(
            "AppendLinesAsync", TwoOrgsFixture.BuildRunAId,
            new[] { new LogLineMessage(901, LogStream.StdOut, "not-my-run", DateTimeOffset.UtcNow) },
            CancellationToken));
        Assert.Contains(LogStreamRejection.Prefix, sameTenant.Message, StringComparison.Ordinal);
        Assert.Equal(1, await LogRowCountAsync(host, TwoOrgsFixture.BuildRunAId));

        // Positive control: org A's executing worker appends to its own run - the lines land with
        // org A's organization stamped, so the refusals above are the tenant/assignment boundaries
        // and not a broken ingest.
        await alphaWorkerConnection.InvokeAsync(
            "AppendLinesAsync", TwoOrgsFixture.BuildRunAId,
            new[] { new LogLineMessage(1000, LogStream.StdOut, "alpha-live-line", DateTimeOffset.UtcNow) },
            CancellationToken);
        var rows = await WaitForLogRowsAsync(host, TwoOrgsFixture.BuildRunAId, minimumCount: 2, Timeout);
        var appended = rows.Single(row => row.Seq == 1000);
        Assert.Equal(TwoOrgsFixture.OrgAId, appended.OrganizationId);
        Assert.Equal("alpha-live-line", appended.Message);
    }

    [Fact]
    public async Task The_browser_hub_authenticates_the_connection_with_the_ticket_scheme()
    {
        using var host = new TenantProbeHostFixture();

        // No ticket at all: the negotiate is a bare 401 and the connection cannot start.
        await using var anonymous = TenantIsolationAuditProbes.BuildHubConnection(host, "/hubs/run-logs", null);
        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync(CancellationToken));

        // A worker token is the wrong scheme for the browser hub: the ticket scheme validates it as
        // a ticket, the data-protected payload never parses, and the negotiate is refused.
        var (_, alphaToken) = await TenantIsolationWorkerApiProbes.RegisterAlphaWorkerAsync(host);
        await using var wrongScheme =
            TenantIsolationAuditProbes.BuildHubConnection(host, "/hubs/run-logs", alphaToken);
        await Assert.ThrowsAnyAsync<Exception>(() => wrongScheme.StartAsync(CancellationToken));

        // A garbled ticket (not data-protected by this key ring) is the same refusal.
        await using var garbage =
            TenantIsolationAuditProbes.BuildHubConnection(host, "/hubs/run-logs", "zenkins-forged-ticket");
        await Assert.ThrowsAnyAsync<Exception>(() => garbage.StartAsync(CancellationToken));

        // The mirror check for the worker hub: a browser ticket is not a worker token.
        var ticket = await TenantIsolationAuditProbes.MintRunLogTicketAsync(
            host, TwoOrgsFixture.OrgAOwnerUserId, TwoOrgsFixture.OrgAId);
        await using var browserTicketOnWorkerHub =
            TenantIsolationAuditProbes.BuildHubConnection(host, "/hubs/worker-logs", ticket);
        await Assert.ThrowsAnyAsync<Exception>(() => browserTicketOnWorkerHub.StartAsync(CancellationToken));
    }

    /// <summary>Polls the system-scope read until the run holds at least the given row count.</summary>
    private static async Task<List<BuildLog>> WaitForLogRowsAsync(
        TenantProbeHostFixture host, Guid runId, int minimumCount, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var rows = await LogRowsAsync(host, runId);
            if (rows.Count >= minimumCount)
            {
                return rows;
            }

            await Task.Delay(100, CancellationToken);
        }

        throw new TimeoutException(
            $"Run '{runId}' never reached {minimumCount} log rows within {timeout.TotalSeconds}s.");
    }

    private static async Task<int> LogRowCountAsync(TenantProbeHostFixture host, Guid runId) =>
        (await LogRowsAsync(host, runId)).Count;

    private static async Task<List<BuildLog>> LogRowsAsync(TenantProbeHostFixture host, Guid runId) =>
        await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<BuildLog>(
            host, log => log.RunId == runId);
}
