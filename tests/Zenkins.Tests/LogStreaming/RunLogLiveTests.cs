using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Zenkins.Shared.LogStreaming;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.LogStreaming;

/// <summary>
/// Card F-008's live-delivery and history tests: a browser-side subscriber that joins the run's
/// group while the run is Running receives every flushed batch live (before the completed report),
/// and a subscriber joining a finished run receives the full persisted history without any live
/// traffic.
/// </summary>
[Trait("Category", "LogStreaming")]
public sealed class RunLogLiveTests
{
    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task Lines_reach_a_subscriber_while_the_run_is_still_running()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await LogStreamingHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync();
        var (token, _) = await host.RegisterWorkerAsync();
        var runId = await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, token);
        await RunTestHost.AcceptAsync(client, token, runId);

        // The subscriber joins BEFORE any line exists: the run detail panel's sequence.
        var ownerUserId = await host.ReadOwnerUserIdAsync();
        var ticket = await host.MintTicketAsync(ownerUserId, await host.OrganizationIdAsync());
        var collector = LogCollector.WaitingFor(10);
        await using var browser = host.BuildBrowserHubConnection(ticket);
        browser.On<IReadOnlyList<LogLineMessage>>("HistoryLines", collector.OnHistory);
        browser.On<IReadOnlyList<LogLineMessage>>("ReceiveLines", collector.OnReceive);
        await browser.StartAsync(cancellationToken);
        await browser.InvokeAsync("JoinRunAsync", runId, cancellationToken);

        // The worker streams the first 10 lines; the subscriber must receive them while the run
        // is still Running (the completed report only comes at the very end).
        await using var worker = host.BuildWorkerHubConnection(token);
        await worker.StartAsync(cancellationToken);
        await worker.InvokeAsync("AppendLinesAsync", runId, Range(1, 10), cancellationToken);
        await collector.WaitForTargetSeqAsync(TimeSpan.FromSeconds(30), cancellationToken);

        var runWhileStreaming = await host.ReadRunAsync(runId);
        Assert.Equal("Running", runWhileStreaming.Status);

        // The rest of the run: everything must arrive, exactly once, in order. The completed
        // report flushes the ingest tail, but the subscriber's long poll picks the broadcast up
        // asynchronously - wait for the full set instead of asserting immediately.
        await worker.InvokeAsync("AppendLinesAsync", runId, Range(11, 40), cancellationToken);
        await RunTestHost.CompleteAsync(client, token, runId, exitCode: 0);
        Assert.Equal("Success", (await host.ReadRunAsync(runId)).Status);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var received = collector.Snapshot();
        while (received.Count < 50 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken);
            received = collector.Snapshot();
        }

        Assert.Equal(50, received.Count);
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(i + 1, received[i].Seq);
            Assert.Equal($"line-{i + 1}", received[i].Message);
        }
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_finished_run_delivers_its_full_history_on_join()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await LogStreamingHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync();
        var (token, _) = await host.RegisterWorkerAsync();
        var runId = await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, token);
        await RunTestHost.AcceptAsync(client, token, runId);
        await using var worker = host.BuildWorkerHubConnection(token);
        await worker.StartAsync(cancellationToken);
        await worker.InvokeAsync("AppendLinesAsync", runId, Range(1, 30, mixedStreams: true), cancellationToken);
        await RunTestHost.CompleteAsync(client, token, runId, exitCode: 0);

        // A browser opening the finished run's page: the whole log arrives as history, ordered.
        var ownerUserId = await host.ReadOwnerUserIdAsync();
        var ticket = await host.MintTicketAsync(ownerUserId, await host.OrganizationIdAsync());
        var collector = LogCollector.WaitingFor(30);
        await using var browser = host.BuildBrowserHubConnection(ticket);
        browser.On<IReadOnlyList<LogLineMessage>>("HistoryLines", collector.OnHistory);
        browser.On<IReadOnlyList<LogLineMessage>>("ReceiveLines", collector.OnReceive);
        await browser.StartAsync(cancellationToken);
        await browser.InvokeAsync("JoinRunAsync", runId, cancellationToken);
        await collector.WaitForTargetSeqAsync(TimeSpan.FromSeconds(30), cancellationToken);

        // Give a wrongly-routed live broadcast no chance to masquerade as history: everything
        // must have come through the history channel.
        await Task.Delay(500, cancellationToken);
        var history = collector.Snapshot();
        Assert.Equal(30, history.Count);
        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(i + 1, history[i].Seq);
            Assert.Equal(i % 2 == 0 ? LogStream.StdOut : LogStream.StdErr, history[i].Stream);
        }
    }

    private static LogLineMessage[] Range(int firstSeq, int count, bool mixedStreams = false)
    {
        var lines = new LogLineMessage[count];
        for (var i = 0; i < count; i++)
        {
            var seq = firstSeq + i;
            lines[i] = new LogLineMessage(
                seq,
                !mixedStreams || seq % 2 == 1 ? LogStream.StdOut : LogStream.StdErr,
                $"line-{seq}",
                DateTimeOffset.UtcNow);
        }

        return lines;
    }
}
