using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Zenkins.Shared.LogStreaming;
using Zenkins.Shared.Pipelines;
using Zenkins.Server.Components.Shared;

namespace Zenkins.Tests.RunUi;

/// <summary>
/// Card D-004's mid-run attach: a page opened while the run is executing shows what has happened so
/// far (history through the real browser hub) and then reaches the run's end over the same
/// connection - live batches and the completed report's flush - with no manual reload and no
/// reconnect. The view model is the panel's own state machine, so the assertion is what the operator
/// would see: the newest line at the tail, once the run is terminal.
/// </summary>
[Trait("Category", "RunUi")]
public sealed class RunLogMidRunAttachTests
{
    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_page_opened_mid_run_shows_history_then_reaches_the_end_without_a_reload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync("Build", "Deploy");

        await run.ReportStepAsync(1, PipelineStepStatus.Success);
        await run.ReportStepAsync(2, PipelineStepStatus.Success);
        var build = run.StepIds[0]!.Value;
        var deploy = run.StepIds[1]!.Value;

        await using var worker = run.BuildWorkerAsync();
        await worker.StartAsync(cancellationToken);

        // The run is already under way when the operator opens the page: 15 lines exist.
        var existing = Enumerable.Range(1, 15)
            .Select(seq => new LogLineMessage(
                seq,
                LogStream.StdOut,
                $"early line {seq}",
                DateTimeOffset.UtcNow,
                seq <= 8 ? build : deploy))
            .ToArray();
        await run.AppendAsync(worker, existing);

        // The page opens mid-run: join, receive the history so far.
        var viewModel = new RunLogViewModel();
        var sawFifteen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawThirty = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handle(string channel, IReadOnlyList<LogLineMessage> lines)
        {
            viewModel.Add(lines);
            foreach (var line in lines)
            {
                if (line.Seq >= 15)
                {
                    sawFifteen.TrySetResult();
                }

                if (line.Seq >= 30)
                {
                    sawThirty.TrySetResult();
                }
            }
        }

        await using var browser = await run.BuildBrowserAsync();
        browser.On<IReadOnlyList<LogLineMessage>>("HistoryLines", lines =>
        {
            Handle("history", lines);
            return Task.CompletedTask;
        });
        browser.On<IReadOnlyList<LogLineMessage>>("ReceiveLines", lines =>
        {
            Handle("live", lines);
            return Task.CompletedTask;
        });
        await browser.StartAsync(cancellationToken);
        await browser.InvokeAsync("JoinRunAsync", run.RunId, cancellationToken);
        await sawFifteen.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        var headers = new Dictionary<Guid, string> { [build] = "1. Build", [deploy] = "2. Deploy" };
        Assert.Equal(15, viewModel.Count);
        var midRun = viewModel.Plan(headers);
        Assert.Equal(15, midRun.Entries[^1].Line.Seq);

        // The run continues and finishes while the same connection stays open - no reload, no
        // re-join, nothing manual.
        var rest = Enumerable.Range(16, 15)
            .Select(seq => new LogLineMessage(
                seq,
                LogStream.StdOut,
                $"late line {seq}",
                DateTimeOffset.UtcNow,
                deploy))
            .ToArray();
        await run.AppendAsync(worker, rest);
        await run.CompleteAsync();
        Assert.Equal("Success", (await host.Streaming.ReadRunAsync(run.RunId)).Status);

        await sawThirty.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        // The panel followed the run to its end: every line, tail at the newest.
        Assert.Equal(30, viewModel.Count);
        var finished = viewModel.Plan(headers);
        Assert.Equal(30, finished.Entries.Count);
        Assert.Equal(30, finished.Entries[^1].Line.Seq);
        Assert.Equal("late line 30", finished.Entries[^1].Line.Message);

        // "Without a manual reload" - literally: the connection never dropped or reconnected.
        Assert.Equal(HubConnectionState.Connected, browser.State);
        Assert.Equal(HubConnectionState.Connected, worker.State);
    }
}
