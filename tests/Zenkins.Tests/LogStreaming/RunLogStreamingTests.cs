using System.Net;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Zenkins.Shared.LogStreaming;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.LogStreaming;

/// <summary>
/// Card F-008's volume/order/exactly-once test: a 10k-line run (stdout/stderr mixed) streamed
/// through the real worker hub endpoint must land in the database exactly once, in Seq order,
/// with the ingest's batched flush provably bounded - and the download endpoint must return the
/// persisted log byte-for-byte, ordered by Seq.
/// </summary>
[Trait("Category", "LogStreaming")]
public sealed class RunLogStreamingTests
{
    private const int LineCount = 10_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    [Fact(Timeout = 3 * 60 * 1000)]
    public async Task Ten_thousand_mixed_lines_land_exactly_once_in_seq_order()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await LogStreamingHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var (token, _) = await host.RegisterWorkerAsync();
        var runId = await host.AddRunAsync(projectId);

        var client = host.Host.CreateClient();
        var claimed = await RunTestHost.ClaimAsync(client, token);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        var claimedRunId = await RunTestHost.ReadClaimedRunIdAsync(claimed);
        Assert.Equal(runId, claimedRunId);
        await RunTestHost.AcceptAsync(client, token, runId);
        Assert.Equal("Running", (await host.ReadRunAsync(runId)).Status);

        await using var connection = host.BuildWorkerHubConnection(token);
        await connection.StartAsync(cancellationToken);

        // 10k numbered lines, alternating streams, in flush-sized batches - what the real worker
        // sends (RunLogBuffer.SendBatchSize = 100 lines per AppendLinesAsync call).
        for (var batch = 0; batch < LineCount / 100; batch++)
        {
            var lines = new LogLineMessage[100];
            for (var i = 0; i < 100; i++)
            {
                var seq = batch * 100 + i + 1;
                lines[i] = new LogLineMessage(
                    seq,
                    seq % 2 == 1 ? LogStream.StdOut : LogStream.StdErr,
                    $"line-{seq}",
                    DateTimeOffset.UtcNow);
            }

            await connection.InvokeAsync("AppendLinesAsync", runId, lines, cancellationToken);
        }

        // The run is still Running while its log streams - and the lines are already queryable.
        Assert.Equal("Running", (await host.ReadRunAsync(runId)).Status);

        await RunTestHost.CompleteAsync(client, token, runId, exitCode: 0);
        Assert.Equal("Success", (await host.ReadRunAsync(runId)).Status);

        // ----- Exactly once, in Seq order, in the database. -----
        var rows = await host.ReadLogRowsAsync(runId);
        Assert.Equal(LineCount, rows.Count);
        Assert.Equal(1, rows[0].Seq);
        Assert.Equal(LineCount, rows[^1].Seq);
        for (var i = 0; i < LineCount; i++)
        {
            Assert.Equal(i + 1, rows[i].Seq);
            Assert.Equal($"line-{i + 1}", rows[i].Message);
            Assert.Equal(i % 2 == 0 ? LogStream.StdOut : LogStream.StdErr, rows[i].Stream);
        }

        // ----- The flush stayed bounded: batches of at most the flush size, never the whole log. -----
        var statistics = host.ReadIngestStatistics();
        Assert.True(statistics.TotalAppended >= LineCount,
            $"the ingest appended {statistics.TotalAppended} lines, expected at least {LineCount}");
        Assert.True(statistics.FlushCount >= LineCount / 100,
            $"the ingest flushed {statistics.FlushCount} batches, expected at least {LineCount / 100}");
        Assert.True(statistics.MaxBatchSize <= 100,
            $"the ingest flushed a batch of {statistics.MaxBatchSize} lines - the memory bound is broken");

        // ----- Download fidelity: byte-for-byte modulo encoding, ordered by Seq. -----
        using var download = await owner.GetAsync($"/runs/{runId}/log/download", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("text/plain", download.Content.Headers.ContentType?.MediaType);
        var disposition = download.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.True(disposition!.FileName?.Contains(runId.ToString(), StringComparison.Ordinal) == true,
            $"the download file name '{disposition.FileName}' does not contain the run id");

        var body = await download.Content.ReadAsStringAsync(cancellationToken);
        var expected = new StringBuilder();
        for (var i = 0; i < LineCount; i++)
        {
            expected.Append("line-").Append(i + 1).Append('\n');
        }

        Assert.Equal(expected.ToString(), body);
    }

    [Fact(Timeout = 60 * 1000)]
    public async Task Download_of_a_foreign_or_unknown_run_is_a_404_without_bytes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await LogStreamingHost.CreateAsync();
        var (projectId, _) = await host.CreateProjectAsync();
        var runId = await host.AddRunAsync(projectId);
        var (token, _) = await host.RegisterWorkerAsync();
        var client = host.Host.CreateClient();
        await RunTestHost.ClaimAsync(client, token);
        await RunTestHost.AcceptAsync(client, token, runId);
        await using var connection = host.BuildWorkerHubConnection(token);
        await connection.StartAsync(cancellationToken);
        await connection.InvokeAsync("AppendLinesAsync", runId, new[]
        {
            new LogLineMessage(1, LogStream.StdOut, "secret-line", DateTimeOffset.UtcNow),
        }, cancellationToken);
        await RunTestHost.CompleteAsync(client, token, runId, exitCode: 0);

        // A user outside the run's organization (no membership at all): no tenant can be selected,
        // so the run does not exist for them and not a single byte of the log may be returned.
        using var foreignSignIn = await host.RegisterUserAsync("foreign-log-user@example.test");

        using var download = await foreignSignIn.GetAsync($"/runs/{runId}/log/download", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        var body = await download.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(body.Length == 0, $"a foreign download leaked {body.Length} bytes");
    }
}
