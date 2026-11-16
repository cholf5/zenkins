using System.Text.Json;
using Xunit;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Pipelines;

namespace Zenkins.Tests.Milestone;

/// <summary>
/// Card F-021's process-level end-to-end: a parameter value the trigger form supplied reaches a real
/// worker's child process, for both kinds. The assertions are what the processes left behind - the
/// run row's frozen snapshot and the persisted log line the child wrote - so no worker-internal
/// object is involved.
/// </summary>
[Trait("Category", "Milestone")]
public sealed class MilestoneParameterTests
{
    [Fact(Timeout = 6 * 60 * 1000)]
    public async Task A_freestyle_script_reads_the_supplied_parameter_from_its_environment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await MilestoneHost.StartAsync();

        // The same script text on both platforms: cmd expands %NAME%, a POSIX shell $NAME.
        var script = OperatingSystem.IsWindows()
            ? "echo GREETING=%GREETING%"
            : "echo \"GREETING=$GREETING\"";
        var stepsJson = JsonSerializer.Serialize(new object[]
        {
            new { name = "Greet", script, envLines = "", continueOnError = false, artifactsText = "" },
        });

        var projectId = await host.CreateFreestyleProjectAsync($"parameters-{Guid.NewGuid():N}".ToLowerInvariant());
        await host.SaveFreestyleStepsAsync(projectId, stepsJson);
        await host.SaveParametersAsync(projectId, JsonSerializer.Serialize(new object[]
        {
            new { name = "GREETING", type = "string", defaultValue = "hello", choicesText = "", description = "" },
        }));
        await host.StartWorkerAsync();

        var runId = await host.TriggerRunAsync(
            projectId,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal) { ["GREETING"] = "from-param" });
        var run = await host.WaitForTerminalAsync(runId);

        Assert.True(run.Status == "Success",
            $"the run ended {run.Status} ({run.FailureReason}); worker output:\n"
            + string.Join("\n", host.WorkerOutput));

        // The run's own snapshot, not the definition (the default is "hello").
        var values = JsonSerializer.Deserialize(
            run.ParametersJson!, ProjectParameterJsonContext.Default.DictionaryStringString)!;
        Assert.Equal("from-param", values["GREETING"]);

        var log = await host.WaitForLogLinesAsync(
            runId,
            lines => lines.Any(line => line.Message == "GREETING=from-param"));
        Assert.Single(log, line => line.Message == "GREETING=from-param");
        Assert.DoesNotContain(log, line => line.Message == "GREETING=hello");
    }

    [Fact(Timeout = 6 * 60 * 1000)]
    public async Task A_pipeline_step_reads_the_supplied_parameter_from_its_environment()
    {
        _ = TestContext.Current.CancellationToken;
        using var repository = MilestoneRepository.Create(
            MilestonePipelines.Pipeline(
                "parameterized",
                env: null,
                PipelineTestData.Step("Greet", "env", "GREETING")));
        using var host = await MilestoneHost.StartAsync();

        var projectId = await host.CreateProjectAsync($"pipeline-parameters-{Guid.NewGuid():N}".ToLowerInvariant());
        await host.PointProjectAtAsync(projectId, repository.FileUrl);
        await host.SaveParametersAsync(projectId, JsonSerializer.Serialize(new object[]
        {
            new { name = "GREETING", type = "string", defaultValue = "hello", choicesText = "", description = "" },
        }));
        await host.StartWorkerAsync();

        var runId = await host.TriggerRunAsync(
            projectId,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal) { ["GREETING"] = "pipeline-param" });
        var run = await host.WaitForTerminalAsync(runId);

        Assert.True(run.Status == "Success",
            $"the run ended {run.Status} ({run.FailureReason}); worker output:\n"
            + string.Join("\n", host.WorkerOutput));
        Assert.True(PipelineSpecJson.TryRead(run.PipelineSpecJson, out var spec, out var error), error);
        Assert.NotNull(spec);
        Assert.Equal("parameterized", spec!.Name);

        var values = JsonSerializer.Deserialize(
            run.ParametersJson!, ProjectParameterJsonContext.Default.DictionaryStringString)!;
        Assert.Equal("pipeline-param", values["GREETING"]);

        // ArgvEcho's env mode prints exactly the value the child received.
        var log = await host.WaitForLogLinesAsync(
            runId,
            lines => lines.Any(line => line.Message == "pipeline-param"));
        Assert.Single(log, line => line.Message == "pipeline-param");
    }
}
