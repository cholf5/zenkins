using Xunit;
using Zenkins.Shared.Pipelines;

namespace Zenkins.Tests.RunUi;

/// <summary>
/// Card F-031 on the real run page: the post phase renders apart from the main list, with its prefixed
/// records, and the note says exactly what a failed post step did - or did not - do to the run's result.
/// </summary>
[Trait("Category", "RunUi")]
public sealed class RunPostPageTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task Post_steps_render_in_their_own_section_after_the_main_list()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync(
            [new PipelineStep("Build", Run: ["dotnet", "build"], Stage: "Build")],
            post: new PipelinePost(
                Success: [new PipelineStep("post/Report", Run: ["report"])],
                Failure: [new PipelineStep("post/Notify", Run: ["notify"])],
                Always: [new PipelineStep("post/Cleanup", Run: ["cleanup"])]));

        await run.ReportStepAsync(1, PipelineStepStatus.Success);
        await run.ReportStepAsync(2, PipelineStepStatus.Success, name: "post/Report");
        await run.ReportStepAsync(3, PipelineStepStatus.Success, name: "post/Cleanup");
        await run.CompleteAsync();

        var page = await run.ReadRunPageAsync();

        // The post phase is its own section after the main list, and the main list does not absorb it.
        var stepList = page.IndexOf("id=\"run-steps\"", StringComparison.Ordinal);
        var post = page.IndexOf("id=\"run-post\"", StringComparison.Ordinal);
        Assert.True(stepList >= 0 && post > stepList);
        var mainList = page[stepList..post];
        Assert.Contains("id=\"run-step-Build\"", mainList, StringComparison.Ordinal);
        Assert.DoesNotContain("post/Report", mainList, StringComparison.Ordinal);

        // The recorded rows show the frozen `post/` names, in run order, under the post section.
        var postSection = page[post..];
        var report = postSection.IndexOf("id=\"run-step-post/Report\"", StringComparison.Ordinal);
        var cleanup = postSection.IndexOf("id=\"run-step-post/Cleanup\"", StringComparison.Ordinal);
        Assert.True(report >= 0 && cleanup > report);
        Assert.DoesNotContain("post/Notify", postSection, StringComparison.Ordinal);
        Assert.Contains("2 recorded", postSection, StringComparison.Ordinal);

        // Nothing failed, so the section carries no note about the run's result.
        Assert.DoesNotContain("id=\"run-post-note\"", page, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_failed_post_step_is_visible_and_the_page_says_the_result_is_unchanged()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync(
            [new PipelineStep("Build", Run: ["dotnet", "build"])],
            post: new PipelinePost(Always: [new PipelineStep("post/Broken", Run: ["cleanup"])]));

        await run.ReportStepAsync(1, PipelineStepStatus.Success);
        await run.ReportStepAsync(2, PipelineStepStatus.Failed, exitCode: 4, name: "post/Broken");
        await run.CompleteAsync(exitCode: 0);

        var page = await run.ReadRunPageAsync();

        // The run stayed successful; the post failure is a visible step of its own, with its own log link
        // and the exit code the worker reported.
        Assert.Contains("id=\"run-post\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"run-step-post/Broken\"", page, StringComparison.Ordinal);
        Assert.Contains("exit 4", page, StringComparison.Ordinal);
        Assert.Contains("id=\"run-step-log-post/Broken\"", page, StringComparison.Ordinal);

        var note = Note(page);
        Assert.Contains("A post step failed", note, StringComparison.Ordinal);
        Assert.Contains("result is unchanged", note, StringComparison.Ordinal);

        // ... and the run's own result is still success (no failure block on the page).
        Assert.DoesNotContain("id=\"run-failure\"", page, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_run_that_failed_because_of_a_post_step_says_so()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync(
            [new PipelineStep("Build", Run: ["dotnet", "build"])],
            post: new PipelinePost(Always: [new PipelineStep("post/Broken", Run: ["cleanup"])]));

        await run.ReportStepAsync(1, PipelineStepStatus.Success);
        await run.ReportStepAsync(2, PipelineStepStatus.Failed, exitCode: 4, name: "post/Broken");
        await run.CompleteAsync(exitCode: 4, failureReason: PipelineFailureReasons.PostFailed);

        var page = await run.ReadRunPageAsync();

        Assert.Contains("id=\"run-failure\"", page, StringComparison.Ordinal);
        Assert.Contains("post-failed", page, StringComparison.Ordinal);
        Assert.Contains("The run failed because a post step failed", Note(page), StringComparison.Ordinal);
        Assert.Contains("project is set to fail the run", Note(page), StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_run_without_post_steps_has_no_post_section()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync("Build");

        await run.ReportStepAsync(1, PipelineStepStatus.Success);
        await run.CompleteAsync();

        var page = await run.ReadRunPageAsync();

        Assert.DoesNotContain("id=\"run-post\"", page, StringComparison.Ordinal);
    }

    private static string Note(string page)
    {
        var start = page.IndexOf("id=\"run-post-note\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "the page has no post note.");
        var end = page.IndexOf("</p>", start, StringComparison.Ordinal);
        return page[start..end];
    }
}
