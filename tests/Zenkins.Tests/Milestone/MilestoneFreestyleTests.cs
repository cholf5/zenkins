using System.Text.Json;
using Xunit;

namespace Zenkins.Tests.Milestone;

/// <summary>
/// Card F-020's process-level end-to-end: a Freestyle project - no repository anywhere in the loop -
/// runs to a real worker, whose steps are shell scripts frozen on the server at trigger time. The
/// assertions are what the processes left behind: the run and its step rows, the persisted log, the
/// run's workspace directory (the Freestyle working directory) and an artifact published from it.
/// <para>
/// Nothing product-internal is touched: the project and steps go through the real forms, the run
/// through the real trigger, and the outcome through the same database and HTTP surfaces the happy
/// path (Q-002 TC-001) reads.
/// </para>
/// </summary>
[Trait("Category", "Milestone")]
public sealed class MilestoneFreestyleTests
{
    [Fact(Timeout = 6 * 60 * 1000)]
    public async Task A_freestyle_run_executes_its_saved_scripts_in_the_workspace_and_publishes_artifacts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await MilestoneHost.StartAsync();

        // The steps: one that greets (success), one that writes an artifact into the workspace, and
        // one that asserts its own working directory is the workspace - each shell-spoken.
        var writeScript = "echo freestyle-artifact > report.txt";
        var cwdScript = OperatingSystem.IsWindows() ? "cd" : "pwd";
        var stepsJson = JsonSerializer.Serialize(new object[]
        {
            new { name = "Greet", script = "echo freestyle-greeting", envLines = "", continueOnError = false, artifactsText = "" },
            new { name = "Publish", script = writeScript, envLines = "FREESTYLE=1", continueOnError = false, artifactsText = "report.txt" },
            new { name = "Workspace", script = cwdScript, envLines = "", continueOnError = false, artifactsText = "" },
        });

        var projectId = await host.CreateFreestyleProjectAsync($"freestyle-{Guid.NewGuid():N}".ToLowerInvariant());
        await host.SaveFreestyleStepsAsync(projectId, stepsJson);
        await host.StartWorkerAsync();

        var runId = await host.TriggerRunAsync(projectId);
        var run = await host.WaitForTerminalAsync(runId);

        Assert.True(run.Status == "Success",
            $"the run ended {run.Status} ({run.FailureReason}); worker output:\n"
            + string.Join("\n", host.WorkerOutput));

        // F-020: a Freestyle run has no repository - the run's commit stays unset, the branch it
        // records is the honest empty string, and the worker never cloned anything into repository/.
        Assert.Null(run.GitCommit);
        var workRoot = Path.Combine(host.WorkRoot, runId.ToString("D"));
        Assert.Empty(Directory.GetFiles(Path.Combine(workRoot, "repository"), "*", SearchOption.AllDirectories));
        Assert.True(Directory.Exists(Path.Combine(workRoot, "workspace")), "the Freestyle workspace was not created.");

        // Three recorded steps, all Success, exactly as the editor froze them.
        var steps = await host.ReadStepsAsync(runId);
        Assert.Equal(["Greet", "Publish", "Workspace"], steps.Select(step => step.Name));
        Assert.All(steps, step =>
        {
            Assert.Equal("Success", step.Status);
            Assert.Equal(0, step.ExitCode);
            Assert.Null(step.FailureReason);
        });

        // The persisted log carries the script's own output, attributed to the step that emitted it.
        // The read is a wait: the run row turns terminal before the log flush has necessarily landed,
        // so a bare read can see fewer lines than the run produced (the first and last step's own
        // output are the honest markers for that).
        var workspacePath = Path.Combine(workRoot, "workspace");
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var lines = await host.WaitForLogLinesAsync(
            runId,
            all => all.Any(line => line.Message == "freestyle-greeting")
                && all.Any(line => line.Message.Contains(workspacePath, pathComparison)));
        var greetLines = lines.Where(line => line.StepId == steps[0].Id).Select(line => line.Message).ToArray();
        Assert.Contains(greetLines, line => line.Contains("freestyle-greeting", StringComparison.Ordinal));

        // ... and the workspace cwd evidence: the step's working directory is the run's workspace.
        var workspaceLines = lines.Where(line => line.StepId == steps[2].Id).Select(line => line.Message).ToArray();
        Assert.Contains(workspaceLines, line => line.Contains(workspacePath, pathComparison));

        // The artifact the second step wrote into the workspace was published and downloadable.
        var artifacts = await host.ReadArtifactsAsync(runId);
        var report = Assert.Single(artifacts);
        Assert.Equal(1, report.FileCount);
        Assert.True(report.TotalBytes > 0);
        var bytes = await host.DownloadAsync($"/runs/{runId}/artifacts/{report.Id}/download");
        Assert.Contains("freestyle-artifact", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact(Timeout = 6 * 60 * 1000)]
    public async Task A_failing_script_fails_its_step_the_run_and_skips_the_rest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await MilestoneHost.StartAsync();

        var stepsJson = JsonSerializer.Serialize(new object[]
        {
            new { name = "Ok", script = "echo before-failure", envLines = "", continueOnError = false, artifactsText = "" },
            new { name = "Blast", script = "exit 3", envLines = "", continueOnError = false, artifactsText = "" },
            new { name = "Unreached", script = "echo never", envLines = "", continueOnError = false, artifactsText = "" },
        });

        var projectId = await host.CreateFreestyleProjectAsync($"freestyle-fail-{Guid.NewGuid():N}".ToLowerInvariant());
        await host.SaveFreestyleStepsAsync(projectId, stepsJson);
        await host.StartWorkerAsync();

        var runId = await host.TriggerRunAsync(projectId);
        var run = await host.WaitForTerminalAsync(runId);

        // The run failed with the step's own exit code - nothing invented, F-015's rule.
        Assert.True(run.Status == "Failed" && run.ExitCode == 3,
            $"the run ended {run.Status} ({run.ExitCode}, {run.FailureReason}); worker output:\n"
            + string.Join("\n", host.WorkerOutput));

        var steps = await host.ReadStepsAsync(runId);
        Assert.Equal(3, steps.Count);
        Assert.Equal("Success", steps[0].Status);
        Assert.Equal("Failed", steps[1].Status);
        Assert.Equal(3, steps[1].ExitCode);
        Assert.Null(steps[1].FailureReason);
        Assert.Equal("Skipped", steps[2].Status);
        Assert.Contains(steps[2].FailureReason, ["previous-step-failed"], StringComparer.Ordinal);
    }
}
