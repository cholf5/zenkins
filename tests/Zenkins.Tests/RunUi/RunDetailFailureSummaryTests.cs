using Xunit;
using Zenkins.Shared.Pipelines;

namespace Zenkins.Tests.RunUi;

/// <summary>
/// Card D-004's failure surface on the run page itself: a failed run shows its reason, its exit code
/// and the failing step above the fold (the block is the first content on the page, before the
/// pipeline card), the step list carries the "View log" jump into the filtered panel, and nothing
/// F-009 put on the page regressed - a running run still offers the cancel button and shows no
/// failure block.
/// </summary>
[Trait("Category", "RunUi")]
public sealed class RunDetailFailureSummaryTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_failed_run_shows_reason_exit_code_and_failing_step_above_the_fold()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync("Build", "Test", "Package");

        await run.ReportStepAsync(1, PipelineStepStatus.Success);
        await run.ReportStepAsync(2, PipelineStepStatus.Failed, exitCode: 3);
        await run.ReportStepAsync(3, PipelineStepStatus.Skipped, failureReason: PipelineStepFailureReasons.PreviousStepFailed);
        await run.CompleteAsync(exitCode: 3, failureReason: PipelineFailureReasons.StepFailed);

        var page = await run.ReadRunPageAsync();

        // The failure block names the reason, the exit code and the failing step.
        Assert.Contains("id=\"run-failure\"", page, StringComparison.Ordinal);
        Assert.Contains("This run failed.", page, StringComparison.Ordinal);
        Assert.Contains("id=\"run-failure-reason\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"run-failure-exit-code\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"run-failure-step\"", page, StringComparison.Ordinal);
        // The failing step by name, with the exit code the worker reported for it.
        Assert.Contains("2. Test (exit 3)", page, StringComparison.Ordinal);
        // A skipped step is not the failing step: the summary named Test, and the step list still
        // shows why Package never ran (F-015's per-step reasons, untouched).
        Assert.Contains("an earlier step failed", page, StringComparison.Ordinal);

        // Above the fold: the block is on the page before the pipeline card's step list - the first
        // thing a reader meets, with no scrolling or expanding.
        var reason = page.IndexOf("id=\"run-failure-reason\"", StringComparison.Ordinal);
        var exitCode = page.IndexOf("id=\"run-failure-exit-code\"", StringComparison.Ordinal);
        var failingStep = page.IndexOf("id=\"run-failure-step\"", StringComparison.Ordinal);
        var stepList = page.IndexOf("id=\"run-steps\"", StringComparison.Ordinal);
        Assert.True(reason >= 0 && exitCode >= 0 && failingStep >= 0 && stepList >= 0);
        Assert.True(reason < stepList, "the failure reason is not above the fold.");
        Assert.True(exitCode < stepList, "the failure exit code is not above the fold.");
        Assert.True(failingStep < stepList, "the failing step is not above the fold.");

        // D-004's jump affordance: each recorded step links into the log panel's filter. The links
        // opt out of Blazor's enhanced navigation - without it, a same-page fragment jump would
        // re-initialize the page and throw away the panel's pause/search state - and the opt-out
        // attribute must reach the rendered page, not just the source.
        Assert.Contains("id=\"run-step-log-Build\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"run-step-log-Test\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"#run-log\"", page, StringComparison.Ordinal);
        Assert.Contains("data-enhance-nav=\"none\"", page, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_running_run_still_offers_the_cancel_button_and_shows_no_failure_block()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync("Build"); // left Running: the F-009 state, untouched

        var page = await run.ReadRunPageAsync();

        Assert.Contains("Cancel run", page, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"run-failure\"", page, StringComparison.Ordinal);
        // The step list and the panel shell are still there (the prerendered page mints no ticket,
        // so the panel's interactive controls belong to the circuit, not to this HTML).
        Assert.Contains("id=\"run-steps\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"run-log\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"run-log-download\"", page, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_run_that_failed_before_any_step_says_so_instead_of_inventing_one()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync("Build"); // definition frozen, no step ever reported

        await run.CompleteAsync(exitCode: null, failureReason: PipelineFailureReasons.PipelineMissing);

        var page = await run.ReadRunPageAsync();

        Assert.Contains("id=\"run-failure\"", page, StringComparison.Ordinal);
        Assert.Contains("pipeline-missing", page, StringComparison.Ordinal);
        Assert.Contains("No recorded step failed", page, StringComparison.Ordinal);
    }
}
