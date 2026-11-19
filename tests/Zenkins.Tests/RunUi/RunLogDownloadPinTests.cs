using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;
using Zenkins.Shared.LogStreaming;
using Zenkins.Server.Components.Shared;
using Zenkins.Tests.Localization;

namespace Zenkins.Tests.RunUi;

/// <summary>
/// Card D-004's TC-003: the download is not the view. The panel is paused and filtered to one step -
/// the viewport holds a handful of lines - and the full-log download still returns every line of the
/// run in Seq order, byte-identical to what the untouched pre-card endpoint produces for the same
/// fixture. The bytes are pinned by constructing the expected file from the fixture's own definition,
/// independently of the endpoint's code path.
/// </summary>
[Trait("Category", "RunUi")]
public sealed class RunLogDownloadPinTests
{
    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_paused_and_filtered_panel_changes_nothing_about_the_download()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync("Build", "Deploy", "Verify");

        // Three recorded steps; the third (Verify) will never print a line.
        await run.ReportStepAsync(1, Zenkins.Shared.Pipelines.PipelineStepStatus.Success);
        await run.ReportStepAsync(2, Zenkins.Shared.Pipelines.PipelineStepStatus.Success);
        await run.ReportStepAsync(3, Zenkins.Shared.Pipelines.PipelineStepStatus.Success);
        var build = run.StepIds[0]!.Value;
        var deploy = run.StepIds[1]!.Value;

        await using var worker = run.BuildWorkerAsync();
        await worker.StartAsync(cancellationToken);
        await run.AppendAsync(worker,
            new LogLineMessage(1, LogStream.StdOut, "cloning the repository", DateTimeOffset.UtcNow),
            new LogLineMessage(2, LogStream.StdErr, "clone noted a warning", DateTimeOffset.UtcNow),
            new LogLineMessage(3, LogStream.StdOut, "compile alpha", DateTimeOffset.UtcNow, build),
            new LogLineMessage(4, LogStream.StdOut, "compile beta", DateTimeOffset.UtcNow, build),
            new LogLineMessage(5, LogStream.StdOut, "deploying the build output", DateTimeOffset.UtcNow, deploy),
            new LogLineMessage(6, LogStream.StdOut, "deployed", DateTimeOffset.UtcNow, deploy));
        await run.CompleteAsync();

        // The panel's side of TC-003: the log arrives as history, then the operator pauses and
        // narrows the viewport to Deploy.
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

        var headers = new Dictionary<Guid, string> { [build] = "1. Build", [deploy] = "2. Deploy" };
        viewModel.Pause();
        viewModel.SetStepFilter(deploy);

        var plan = viewModel.Plan(headers, TestText.English);
        Assert.True(plan.IsPaused);
        Assert.Equal(6, plan.TotalLines);
        Assert.Equal(2, plan.Entries.Count); // the view: Deploy's two lines only
        Assert.All(plan.Entries, entry => Assert.Equal(deploy, entry.Line.StepId));

        // The download: everything, in order - the view and the file are two different things.
        var downloaded = await run.DownloadLogAsync();

        var expected =
            "cloning the repository\n" +
            "clone noted a warning\n" +
            "compile alpha\n" +
            "compile beta\n" +
            "deploying the build output\n" +
            "deployed\n";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), downloaded);

        // The same bytes read back as text: the pin holds at the byte level, not just shape.
        Assert.Equal(expected, Encoding.UTF8.GetString(downloaded));
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_download_of_a_step_stamped_run_is_unchanged_by_attribution()
    {
        // Step stamps live on the BuildLog row but never in the download: the endpoint streams the
        // messages only, and it did exactly that before this card. A step-attributed fixture pins it.
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync("Build");

        await run.ReportStepAsync(1, Zenkins.Shared.Pipelines.PipelineStepStatus.Success);
        var build = run.StepIds[0]!.Value;

        await using var worker = run.BuildWorkerAsync();
        await worker.StartAsync(cancellationToken);
        await run.AppendAsync(worker,
            new LogLineMessage(1, LogStream.StdOut, "before the step", DateTimeOffset.UtcNow),
            new LogLineMessage(2, LogStream.StdOut, "inside the step", DateTimeOffset.UtcNow, build));
        await run.CompleteAsync();

        Assert.Equal(
            "before the step\ninside the step\n",
            Encoding.UTF8.GetString(await run.DownloadLogAsync()));
    }
}
