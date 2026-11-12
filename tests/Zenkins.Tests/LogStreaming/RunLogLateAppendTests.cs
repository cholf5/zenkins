using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.LogStreaming;
using Zenkins.Shared.LogStreaming;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.LogStreaming;

/// <summary>
/// Review-fix test for F-008's post-eviction append (F3): when a run reaches its terminal state
/// the ingest evicts the run's pending state; a worker drain still retrying behind the completed
/// report (the <c>HasEverConnected</c> escape hatch in <c>WorkerRunLoop</c>) can then append late
/// lines. The fix flushes those lines inline and recreates no per-run state: nothing is silently
/// dropped and the finished run leaves no ingest memory behind (the old PendingRun leak).
/// </summary>
[Trait("Category", "LogStreaming")]
public sealed class RunLogLateAppendTests
{
    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_late_append_to_a_completed_run_is_persisted_and_leaves_no_ingest_state()
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

        // A partial batch (5 < 100): the ingest buffers it and arms the flush timer - state exists.
        await worker.InvokeAsync("AppendLinesAsync", runId, Batch(1, 5), cancellationToken);
        Assert.Equal(1, host.PendingRunCount);

        // The run completes; the ingest's final flush persists the tail and evicts the state.
        await RunTestHost.CompleteAsync(client, token, runId, exitCode: 0);
        Assert.Equal("Success", (await host.ReadRunAsync(runId)).Status);
        Assert.Equal(0, host.PendingRunCount);

        // The late append (Seq 6..8) arrives after the eviction.
        await worker.InvokeAsync("AppendLinesAsync", runId, Batch(6, 3), cancellationToken);

        // The lines are persisted - inline now; the 250 ms timer flushed them before the fix -
        // and the run's log stays complete and ordered.
        var rows = await host.ReadLogRowsAsync(runId);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (rows.Count < 8 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken);
            rows = await host.ReadLogRowsAsync(runId);
        }

        Assert.Equal(8, rows.Count);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(i + 1, rows[i].Seq);
            Assert.Equal($"line-{i + 1}", rows[i].Message);
        }

        // And no per-run state lingers for the finished run: the leak is gone. (Before the fix
        // this stayed at 1 forever - the late append had recreated a PendingRun that nothing
        // would ever evict again.)
        Assert.Equal(0, host.PendingRunCount);
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
