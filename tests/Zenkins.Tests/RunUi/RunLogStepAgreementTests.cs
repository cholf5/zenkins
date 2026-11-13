using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Zenkins.Shared.LogStreaming;
using Zenkins.Shared.Pipelines;
using Zenkins.Server.Components.Shared;

namespace Zenkins.Tests.RunUi;

/// <summary>
/// Card D-004's TC-002: a three-step pipeline whose second step prints a marker the first step also
/// prints. The search count equals the number of matching persisted lines (never the text-matching
/// superset a naive filter would produce), the step filter keyed on StepId shows only step 2's lines -
/// fewer than the search count - and a step that produced no output says so. All counts are checked
/// against the database rows, so the panel and the step list cannot disagree.
/// </summary>
[Trait("Category", "RunUi")]
public sealed class RunLogStepAgreementTests
{
    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task Search_and_step_filter_agree_with_the_persisted_lines()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync("Build", "Deploy", "Verify");

        await run.ReportStepAsync(1, PipelineStepStatus.Success);
        await run.ReportStepAsync(2, PipelineStepStatus.Success);
        await run.ReportStepAsync(3, PipelineStepStatus.Success); // the step that produces no output
        var build = run.StepIds[0]!.Value;
        var deploy = run.StepIds[1]!.Value;
        var verify = run.StepIds[2]!.Value;

        const string marker = "release-7";
        await using var worker = run.BuildWorkerAsync();
        await worker.StartAsync(cancellationToken);
        await run.AppendAsync(worker,
            new LogLineMessage(1, LogStream.StdOut, "cloning for the pipeline", DateTimeOffset.UtcNow),
            new LogLineMessage(2, LogStream.StdOut, $"{marker} staged", DateTimeOffset.UtcNow, build),
            new LogLineMessage(3, LogStream.StdOut, $"{marker} built", DateTimeOffset.UtcNow, build),
            new LogLineMessage(4, LogStream.StdOut, $"{marker} shipped", DateTimeOffset.UtcNow, deploy),
            new LogLineMessage(5, LogStream.StdOut, "deploy finished", DateTimeOffset.UtcNow, deploy));
        await run.CompleteAsync();
        Assert.Equal("Success", (await host.Streaming.ReadRunAsync(run.RunId)).Status);

        // The panel receives the same log the browser would (history through the real hub).
        var viewModel = new RunLogViewModel();
        await using var browser = await run.BuildBrowserAsync();
        var gotHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.On<IReadOnlyList<LogLineMessage>>("HistoryLines", lines =>
        {
            viewModel.Add(lines);
            gotHistory.TrySetResult();
            return Task.CompletedTask;
        });
        await browser.StartAsync(cancellationToken);
        await browser.InvokeAsync("JoinRunAsync", run.RunId, cancellationToken);
        await gotHistory.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        var headers = new Dictionary<Guid, string>
        {
            [build] = "1. Build",
            [deploy] = "2. Deploy",
            [verify] = "3. Verify",
        };

        // ----- The search count equals the matching persisted lines. -----
        viewModel.SetSearch(marker);
        var searched = viewModel.Plan(headers);
        var rows = await host.Streaming.ReadLogRowsAsync(run.RunId);
        var databaseMatches = rows.Count(row => row.Message.Contains(marker, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, databaseMatches);
        Assert.Equal(databaseMatches, searched.MatchCount);

        // ----- The step filter is keyed on StepId: only step 2's lines, fewer than the search. -----
        viewModel.SetSearch(string.Empty);
        viewModel.SetStepFilter(deploy);
        var filtered = viewModel.Plan(headers);
        Assert.Equal(2, filtered.Entries.Count);
        Assert.All(filtered.Entries, entry => Assert.Equal(deploy, entry.Line.StepId));
        Assert.True(
            filtered.Entries.Count < searched.MatchCount,
            "the step filter's lines were not fewer than the search count.");
        // The marker itself appeared under Build too - the filter dropped those lines because their
        // StepId differs, not because of any text match.
        Assert.DoesNotContain(filtered.Entries, entry => entry.Line.Seq is 2 or 3);

        // ----- The counts agree with the step list's own record: the database rows per step. -----
        var plan = viewModel.Plan(headers);
        Assert.Equal(rows.Count(row => row.StepId == build), plan.StepCounts[build]);
        Assert.Equal(rows.Count(row => row.StepId == deploy), plan.StepCounts[deploy]);
        Assert.Equal(0, plan.StepCounts[verify]);

        // ----- A step with no output says it produced none. -----
        viewModel.SetStepFilter(verify);
        var silent = viewModel.Plan(headers);
        Assert.Empty(silent.Entries);
        Assert.Equal("No output was captured for 3. Verify.", silent.EmptyMessage);
    }
}
