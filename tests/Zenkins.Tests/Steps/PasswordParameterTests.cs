using Xunit;
using Zenkins.Server.Projects;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Pipelines;
using Zenkins.Tests.Runs;
using Zenkins.Worker.Git;

namespace Zenkins.Tests.Steps;

/// <summary>
/// Card F-041's redaction: a <c>password</c> parameter is a per-run input rather than a stored Secret, but a step
/// that prints it must still log the mask. The run is the product's own path - the trigger form, the claim, the real
/// executor, the real redaction filter - so the value travels exactly the way it would on a worker.
/// </summary>
[Trait("Category", "Steps")]
public sealed class PasswordParameterTests
{
    private const string ParameterName = "DEPLOY_PW";
    private const string ParameterValue = "deploy-pw-4b9e2c";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_step_that_echoes_a_password_parameter_logs_the_mask()
    {
        var pipeline = $$"""
            pipeline {
              name = "password-parameter",
              steps = {
                { name = "Echo", run = { [[{{PipelineTestData.DotNet}}]], [[{{PipelineTestData.ArgvEcho}}]],
                    "env", "{{ParameterName}}" } },
              },
            }
            """;

        using var repository = PipelineRepository.Create(pipeline);
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(
            host,
            repository,
            prepareProject: (projectId, owner) => SavePasswordParameterAsync(owner, projectId),
            parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ParameterName] = ParameterValue,
            });

        var outcome = await run.ExecuteAsync();
        Assert.Equal(0, outcome.ExitCode);

        var messages = run.Lines.Select(line => line.Message).ToArray();

        // The child really read the variable (ArgvEcho prints <unset> when it is missing), and the mask is what
        // the run's surfaces record.
        Assert.Contains(messages, message => message.Contains(ParameterValue, StringComparison.Ordinal) is false
            && message.Contains(GitLogFilter.Replacement, StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Contains(ParameterValue, StringComparison.Ordinal));
        Assert.DoesNotContain(
            await run.ReadLogRowsAsync(),
            row => row.Message.Contains(ParameterValue, StringComparison.Ordinal));
        Assert.DoesNotContain(ParameterValue, await run.ReadRunPageAsync(), StringComparison.Ordinal);
    }

    /// <summary>Saves the project's one password parameter, as the editor form does.</summary>
    private static async Task SavePasswordParameterAsync(HttpClient owner, Guid projectId)
    {
        var draftsJson = System.Text.Json.JsonSerializer.Serialize(
            new[] { new ProjectParameterDraft(ParameterName, ProjectParameterTypes.Password, null, null, null) },
            ProjectParameters.DraftsOptions);

        using var response = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}",
            $"/projects/{projectId}/parameters",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ParametersJson"] = draftsJson });

        Assert.True(
            (int)response.StatusCode is 302 or 303
                && response.Headers.Location!.OriginalString.Contains("notice=parameters-saved", StringComparison.Ordinal),
            $"saving the parameter answered {(int)response.StatusCode}: {response.Headers.Location}.");
    }
}
