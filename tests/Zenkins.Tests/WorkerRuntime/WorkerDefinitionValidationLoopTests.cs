using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Probes;
using Zenkins.Shared.Pipelines;
using Zenkins.Tests.Runs;
using Zenkins.Worker;

namespace Zenkins.Tests.WorkerRuntime;

/// <summary>
/// Card F-022 (AC-6) at the composition that actually runs: the real
/// <see cref="WorkerDefinitionValidationLoop"/> against the real definition-only worker API, with the
/// real <see cref="WorkDirectoryService.PrepareValidation"/> scratch tree and the real
/// <see cref="Pipelines.PipelineDefinitionRunner"/>.
/// <para>
/// The other Validate facts play the claim/report half by hand, so they prove the scheduling and the
/// page - not that a Worker really describes the script. This one runs the loop, and asserts the spec
/// it produced has the script's own pipeline name/steps (a canned answer cannot pass), the diagnostic
/// really is <c>pipeline.lua:(line,col)</c> for a broken script, and the scratch tree is deleted.
/// </para>
/// </summary>
[Trait("Category", "WorkerRuntime")]
public sealed class WorkerDefinitionValidationLoopTests : IDisposable
{
    private readonly string _workRoot = Path.Combine(
        Path.GetTempPath(), $"zenkins-f022-validation-{Guid.NewGuid():N}");

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact(Timeout = 90 * 1000)]
    public async Task The_real_loop_describes_the_claimed_script_and_publishes_its_own_step_list()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await RunTestHost.CreateAsync(configureAppConfiguration: builder =>
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DefinitionValidationOptions.WaitSecondsKey] = "10",
                [DefinitionValidationOptions.PollMillisecondsKey] = "20",
            }));
        var owner = await host.Inner.SignInOwnerAsync();
        const string script = """
            pipeline {
              name = "real-loop-valid",
              steps = {
                { name = "Restore", run = { "echo", "restore" } },
                { name = "Build", run = { "echo", "build" } },
              },
            }
            """;
        var projectId = await InlinePipelineRunTests.CreateInlineProjectAsync(owner, "real-loop-valid", script);
        var (token, _) = await host.RegisterWorkerAsync(name: "real-validation-loop");

        var (loop, stop) = CreateLoop(host, token);
        var loopTask = loop.RunAsync(stop.Token);
        try
        {
            using var response = await RunTestHost.StartValidateAsync(owner, projectId, script);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

            var registry = await RegistryAsync(host);
            var validation = registry.LastFor(projectId);
            Assert.NotNull(validation);
            Assert.True(validation!.Valid, validation.Diagnostic);
            Assert.Equal(["Restore", "Build"], validation.StepNames);

            // The step list is the script's own: a canned spec cannot guess this name.
            Assert.Contains("real-loop-valid", validation.SpecJson!, StringComparison.Ordinal);
            Assert.True(PipelineSpecJson.TryRead(validation.SpecJson, out var spec, out var error), error);
            Assert.Equal("real-loop-valid", spec!.Name);
            Assert.Equal(["Restore", "Build"], spec.Steps.Select(step => step.Name));

            Assert.Empty(await host.ReadRunsAsync(await host.OrganizationIdAsync()));
            await WaitForScratchCleanupAsync(validation.Id);
        }
        finally
        {
            await stop.CancelAsync();
            await loopTask.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
    }

    [Fact(Timeout = 90 * 1000)]
    public async Task The_real_loop_reports_a_syntax_error_with_the_pipeline_lua_position()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await RunTestHost.CreateAsync(configureAppConfiguration: builder =>
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DefinitionValidationOptions.WaitSecondsKey] = "10",
                [DefinitionValidationOptions.PollMillisecondsKey] = "20",
            }));
        var owner = await host.Inner.SignInOwnerAsync();
        const string broken = "pipeline {";
        var projectId = await InlinePipelineRunTests.CreateInlineProjectAsync(owner, "real-loop-invalid", broken);
        var (token, _) = await host.RegisterWorkerAsync(name: "real-validation-loop-invalid");

        var (loop, stop) = CreateLoop(host, token);
        var loopTask = loop.RunAsync(stop.Token);
        try
        {
            using var response = await RunTestHost.StartValidateAsync(owner, projectId, broken);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

            var registry = await RegistryAsync(host);
            var validation = registry.LastFor(projectId);
            Assert.NotNull(validation);
            Assert.False(validation!.Valid);
            Assert.Equal(PipelineFailureReasons.PipelineSyntax, validation.FailureReason);
            Assert.StartsWith("pipeline.lua:(", validation.Diagnostic, StringComparison.Ordinal);
            Assert.Matches(@"^pipeline\.lua:\(1,\d+\)", validation.Diagnostic!);

            Assert.Empty(await host.ReadRunsAsync(await host.OrganizationIdAsync()));
            await WaitForScratchCleanupAsync(validation.Id);
        }
        finally
        {
            await stop.CancelAsync();
            await loopTask.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
    }

    private (WorkerDefinitionValidationLoop Loop, CancellationTokenSource Stop) CreateLoop(
        RunTestHost host,
        string workerToken)
    {
        Directory.CreateDirectory(_workRoot);
        var configuration = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "http://localhost",
            [WorkerConfiguration.WorkerNameVariable] = "validation-loop-agent",
            [WorkerConfiguration.WorkRootVariable] = _workRoot,
            [WorkerConfiguration.ClaimSecondsVariable] = "0.2",
        }).Configuration ?? throw new InvalidOperationException("The validation-loop configuration did not parse.");

        var loop = new WorkerDefinitionValidationLoop(
            configuration,
            new WorkerApiClient(host.Host.CreateClient()),
            workerToken,
            new WorkDirectoryService(configuration.WorkRoot),
            BackoffScheduler.Default(),
            TextWriter.Null,
            TextWriter.Null);
        return (loop, new CancellationTokenSource());
    }

    private static Task<DefinitionValidationRegistry> RegistryAsync(RunTestHost host) =>
        host.Host.InScopeAsync(services =>
            Task.FromResult(services.GetRequiredService<DefinitionValidationRegistry>()));

    private async Task WaitForScratchCleanupAsync(Guid validationId)
    {
        var root = Path.Combine(_workRoot, "validations", validationId.ToString("D"));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && Directory.Exists(root))
        {
            await Task.Delay(20, CancellationToken);
        }

        Assert.False(Directory.Exists(root), $"the validation scratch tree survived: {root}");
    }

    public void Dispose() => TestCleanup.DeleteTree(_workRoot);
}
