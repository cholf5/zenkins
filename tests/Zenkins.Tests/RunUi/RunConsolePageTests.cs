using System.Net;
using Xunit;
using Zenkins.Shared.Pipelines;

namespace Zenkins.Tests.RunUi;

/// <summary>
/// Card F-047's page: the run's log has its own route, with the header, the toolbar and the step
/// list, and a foreign run id is the same 404 the run page gives. The interactive connection and
/// the follow behaviour need a browser (D-004-B3 records why TestServer cannot host that circuit);
/// these facts pin the static shell and the navigation the run page hands over.
/// </summary>
[Trait("Category", "RunUi")]
public sealed class RunConsolePageTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_console_page_renders_the_header_toolbar_and_step_list()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync("Build", "Test");

        var page = await run.ReadConsolePageAsync();
        Assert.Contains("Console", page, StringComparison.Ordinal);
        Assert.Contains("id=\"run-log-controls\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"run-log-pause\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"run-log-step-filter\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"run-console-steps\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"run-log-download\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"run-log-timestamps\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"run-log-wrap\"", page, StringComparison.Ordinal);
        Assert.Contains("Build", page, StringComparison.Ordinal);
        Assert.Contains("Test", page, StringComparison.Ordinal);
        Assert.Contains("Running", page, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_console_page_offers_the_run_page_and_all_runs_links()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync("Build");

        var page = await run.ReadConsolePageAsync();

        Assert.Contains("Back to run", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/projects/", page, StringComparison.Ordinal);
        Assert.Contains("All runs", page, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_step_route_opens_the_same_console()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync("Build", "Test");
        await run.ReportStepAsync(1, PipelineStepStatus.Success);
        var stepId = run.StepIds[0];
        Assert.NotNull(stepId);

        var page = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(
            run.Owner, $"/runs/{run.RunId}/console?step={stepId}");

        Assert.Contains("id=\"run-console-page\"", page, StringComparison.Ordinal);
        // The query is the initial filter, not decoration: the step select carries the id.
        Assert.Contains($"id=\"run-log-step-filter\"", page, StringComparison.Ordinal);
        Assert.Contains($"value=\"{stepId}\"", page, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task An_unknown_step_query_shows_all_steps()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync("Build", "Test");
        var unknown = Guid.NewGuid();

        var page = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(
            run.Owner, $"/runs/{run.RunId}/console?step={unknown}");

        var selectStart = page.IndexOf("id=\"run-log-step-filter\"", StringComparison.Ordinal);
        Assert.True(selectStart >= 0, "the step filter select is missing.");
        var valueStart = page.IndexOf("value=\"", selectStart, StringComparison.Ordinal);
        Assert.True(valueStart > selectStart, "the step filter select has no value attribute.");
        Assert.StartsWith("value=\"\"", page[valueStart..], StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_foreign_run_id_is_a_404()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync("Build");

        using var response = await run.Owner.GetAsync(
            $"/runs/{Guid.NewGuid()}/console", CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
