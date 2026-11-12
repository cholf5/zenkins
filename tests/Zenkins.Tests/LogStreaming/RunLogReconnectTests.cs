using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Zenkins.Shared.LogStreaming;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.LogStreaming;

/// <summary>
/// Card F-008's reconnect test (TC-002): a worker that reconnects mid-run and re-delivers
/// overlapping sequence numbers must leave exactly one row per Seq. The (RunId, Seq) primary key
/// is the authority - the ingest's happy path only inserts, and the unique-violation fallback
/// skips what the database already holds.
/// </summary>
[Trait("Category", "LogStreaming")]
public sealed class RunLogReconnectTests
{
    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task Reconnecting_worker_resending_overlapping_sequences_creates_no_duplicates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await LogStreamingHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var (token, _) = await host.RegisterWorkerAsync();
        var runId = await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, token);
        await RunTestHost.AcceptAsync(client, token, runId);

        // First connection: lines 1..50 (in 50 single-line batches? No - one flush-sized batch).
        await using (var first = host.BuildWorkerHubConnection(token))
        {
            await first.StartAsync(cancellationToken);
            await first.InvokeAsync("AppendLinesAsync", runId, Range(1, 50), cancellationToken);
        }

        // The connection dies (dispose). The worker reconnects and - because the last response
        // was lost - re-delivers an overlapping window (45..60) before continuing with 61..100.
        await using (var second = host.BuildWorkerHubConnection(token))
        {
            await second.StartAsync(cancellationToken);
            await second.InvokeAsync("AppendLinesAsync", runId, Range(45, 16), cancellationToken);
            await second.InvokeAsync("AppendLinesAsync", runId, Range(61, 40), cancellationToken);
        }

        await RunTestHost.CompleteAsync(client, token, runId, exitCode: 0);
        Assert.Equal("Success", (await host.ReadRunAsync(runId)).Status);

        // Partial batches flush on the ingest timer; wait for all 100 rows before asserting.
        var rows = await host.ReadLogRowsAsync(runId);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (rows.Count < 100 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken);
            rows = await host.ReadLogRowsAsync(runId);
        }

        Assert.Equal(100, rows.Count); // 100 distinct Seqs - no duplicate rows
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(i + 1, rows[i].Seq);
            Assert.Equal($"line-{i + 1}", rows[i].Message);
        }

        // The download is the persisted log: 100 lines, no duplicates.
        using var download = await owner.GetAsync($"/runs/{runId}/log/download", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var body = await download.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(100, body.Count(character => character == '\n'));
        Assert.Contains("line-50", body, StringComparison.Ordinal);
        Assert.DoesNotContain("line-50\nline-50", body, StringComparison.Ordinal);
    }

    private static LogLineMessage[] Range(int firstSeq, int count)
    {
        var lines = new LogLineMessage[count];
        for (var i = 0; i < count; i++)
        {
            var seq = firstSeq + i;
            lines[i] = new LogLineMessage(
                seq,
                LogStream.StdOut,
                $"line-{seq}",
                DateTimeOffset.UtcNow);
        }

        return lines;
    }
}
