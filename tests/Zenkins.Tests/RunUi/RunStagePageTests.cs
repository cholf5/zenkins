using Xunit;
using Zenkins.Shared.Pipelines;

namespace Zenkins.Tests.RunUi;

/// <summary>
/// Card F-030 on the real run page (prerendered HTML through the authenticated route): a 40-step build
/// reads as stage sections in declaration order, the failed section is open and its first failed step is the
/// one the page jumps to, a successful section is collapsed, and a run whose spec predates the field still
/// renders the flat list it always did.
/// </summary>
[Trait("Category", "RunUi")]
public sealed class RunStagePageTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task Stage_sections_render_in_order_with_the_failed_one_open_and_the_successful_ones_collapsed()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync(
        [
            new PipelineStep("Restore", Run: ["dotnet", "restore"], Stage: "Build"),
            new PipelineStep("Compile", Run: ["dotnet", "build"], Stage: "Build"),
            new PipelineStep("Unit", Run: ["dotnet", "test"], Stage: "Test"),
            new PipelineStep("Publish", Run: ["dotnet", "publish"], Stage: "Package"),
        ]);

        await run.ReportStepAsync(1, PipelineStepStatus.Success);
        await run.ReportStepAsync(2, PipelineStepStatus.Success);
        await run.ReportStepAsync(3, PipelineStepStatus.Failed, exitCode: 2);
        await run.ReportStepAsync(4, PipelineStepStatus.Skipped, failureReason: PipelineStepFailureReasons.PreviousStepFailed);
        await run.CompleteAsync(exitCode: 2, failureReason: PipelineFailureReasons.StepFailed);

        var page = await run.ReadRunPageAsync();

        // Three sections, in declaration order, each holding its own steps.
        var build = page.IndexOf("id=\"run-stage-Build\"", StringComparison.Ordinal);
        var test = page.IndexOf("id=\"run-stage-Test\"", StringComparison.Ordinal);
        var package = page.IndexOf("id=\"run-stage-Package\"", StringComparison.Ordinal);
        var stepList = page.IndexOf("id=\"run-steps\"", StringComparison.Ordinal);
        Assert.True(stepList >= 0 && build > stepList && test > build && package > test);

        var buildSection = Section(page, "Build");
        var testSection = Section(page, "Test");
        var packageSection = Section(page, "Package");

        // The step count and the badge are the section's own numbers.
        Assert.Contains("2 step(s)", buildSection, StringComparison.Ordinal);
        Assert.Contains("1 step(s)", testSection, StringComparison.Ordinal);
        Assert.Contains("Success", buildSection, StringComparison.Ordinal);
        Assert.Contains("Failed", testSection, StringComparison.Ordinal);

        // The card's fold defaults: failed open, successful collapsed.
        Assert.DoesNotContain("<details open", buildSection, StringComparison.Ordinal);
        Assert.Contains("<details open", testSection, StringComparison.Ordinal);
        Assert.DoesNotContain("<details open", packageSection, StringComparison.Ordinal);

        // The existing per-step log jump is unchanged inside a section.
        Assert.Contains("id=\"run-step-log-Restore\"", buildSection, StringComparison.Ordinal);
        Assert.Contains("id=\"run-step-log-Unit\"", testSection, StringComparison.Ordinal);

        // The page jumps to the first failed step: exactly one row carries the marker, and it is Unit.
        Assert.Equal(1, Count(page, "data-run-first-failed=\"true\""));
        Assert.Contains(
            $"id=\"run-step-Unit\" class=\"bb:break-all\" data-run-first-failed=\"true\"",
            testSection,
            StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_v2_run_renders_the_single_unnamed_list_it_always_did()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        // A spec frozen before F-030: version 2, no stage field on any step.
        var run = await host.CreateRunAsync(
            [
                new PipelineStep("Restore", Run: ["dotnet", "restore"]),
                new PipelineStep("Build", Run: ["dotnet", "build"]),
                new PipelineStep("Package", Run: ["dotnet", "publish"]),
            ],
            version: 2);

        await run.ReportStepAsync(1, PipelineStepStatus.Success);
        await run.ReportStepAsync(2, PipelineStepStatus.Success);
        await run.ReportStepAsync(3, PipelineStepStatus.Success);
        await run.CompleteAsync();

        var page = await run.ReadRunPageAsync();

        // No empty stage headers and no invented "Stage 1": the steps are the direct children of the list.
        Assert.Contains("id=\"run-steps\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("run-stage-", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<details", page, StringComparison.Ordinal);
        foreach (var name in run.StepNames)
        {
            Assert.Contains($"id=\"run-step-{name}\"", page, StringComparison.Ordinal);
        }
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task An_unstaged_step_renders_outside_every_stage_section()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        // An unstaged step before the first section AND one after a named section, with a second named section
        // behind it: the trailing one is the case that matters, because "absorbed into the previous stage" is a
        // no-op for a leading step but moves this one inside a section the author never chose.
        var run = await host.CreateRunAsync(
        [
            new PipelineStep("Notify", Run: ["notify"]),
            new PipelineStep("Compile", Run: ["dotnet", "build"], Stage: "Build"),
            new PipelineStep("Report", Run: ["report"]),
            new PipelineStep("Unit", Run: ["dotnet", "test"], Stage: "Test"),
        ]);

        var page = await run.ReadRunPageAsync();

        // The page renders exactly the two named sections - an unstaged step is neither a section of its own nor a
        // second section sharing the previous stage's name (which is what "absorbed into the previous stage"
        // produces: a duplicate `run-stage-Build`).
        var sections = StageSections(page);
        Assert.Equal(2, sections.Count);

        // Before the first section, and between the two: outside every `run-stage-` element, not merely outside
        // the first `</details>` the page happens to contain.
        var notify = page.IndexOf("id=\"run-step-Notify\"", StringComparison.Ordinal);
        var report = page.IndexOf("id=\"run-step-Report\"", StringComparison.Ordinal);
        Assert.True(notify >= 0 && notify < sections[0].Start);
        Assert.True(report > sections[0].End && report < sections[1].Start);
        Assert.All(
            sections,
            section => Assert.DoesNotContain(
                "run-step-Report",
                page[section.Start..section.End],
                StringComparison.Ordinal));
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_running_stage_stays_open_so_an_in_flight_run_cannot_hide_its_steps()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync(
        [
            new PipelineStep("Compile", Run: ["dotnet", "build"], Stage: "Build"),
        ]);

        await run.ReportStepAsync(1, PipelineStepStatus.Running);

        var page = await run.ReadRunPageAsync();

        Assert.Contains("<details open", Section(page, "Build"), StringComparison.Ordinal);
        Assert.Contains("Running", Section(page, "Build"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every stage section the page renders, as (Start, End) offsets, in page order. A section starts at its
    /// <c>run-stage-</c> id and ends at the <c>&lt;/li&gt;</c> that closes the item after its
    /// <c>&lt;/details&gt;</c> - the step rows inside are <c>&lt;li&gt;</c> elements too, so the first
    /// <c>&lt;/li&gt;</c> after the start is not the section's end.
    /// </summary>
    private static List<(int Start, int End)> StageSections(string page)
    {
        var sections = new List<(int Start, int End)>();
        var start = 0;
        while ((start = page.IndexOf("id=\"run-stage-", start, StringComparison.Ordinal)) >= 0)
        {
            var details = page.IndexOf("</details>", start, StringComparison.Ordinal);
            Assert.True(details > start, "a stage section never closes its details element.");
            var end = page.IndexOf("</li>", details, StringComparison.Ordinal);
            Assert.True(end > details, "a stage section never closes its list item.");
            sections.Add((start, end));
            start = end;
        }

        return sections;
    }

    private static string Section(string page, string stage)
    {
        var start = page.IndexOf($"id=\"run-stage-{stage}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"the page has no section '{stage}'.");
        var end = page.IndexOf("</details>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"the page's section '{stage}' never closes.");
        return page[start..end];
    }

    private static int Count(string page, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = page.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
