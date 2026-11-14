using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Git;
using Zenkins.Shared.Pipelines;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.RunUi;

/// <summary>
/// Card F-018's run-side messaging, on the page an Owner actually reads: a clone that could not
/// authenticate says <b>which</b> credential situation caused it - nothing assigned, or a credential
/// the remote refused - instead of leaving a bare <c>git-auth-rejected</c> (or, worse, a silent
/// anonymous clone) as the only trace.
/// <para>
/// The credential state is shown on every run of the project, not only on failures: an Owner reading a
/// green run of a private repository can see whether it authenticated with the assigned credential.
/// </para>
/// </summary>
[Trait("Category", "RunUi")]
public sealed class RunDetailCredentialMessageTests
{
    private const string TokenValue = "zkpat_0123456789abcdef0123456789abcdef";
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_run_without_an_assigned_credential_says_so_and_names_the_fix()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync("Build");

        await run.CompleteAsync(exitCode: null, failureReason: PipelineFailureReasons.GitCredentialNotAssigned);

        var page = await run.ReadRunPageAsync();

        // The recorded code is still visible (the closed vocabulary is the contract)...
        Assert.Contains(PipelineFailureReasons.GitCredentialNotAssigned, page, StringComparison.Ordinal);

        // ...and the page says what it means, including the part that matters: the worker never borrowed
        // a credential from its own host.
        Assert.Contains("id=\"run-failure-explanation\"", page, StringComparison.Ordinal);
        Assert.Contains("none is assigned to this project", page, StringComparison.Ordinal);
        Assert.Contains("never borrows a credential from the worker host", page, StringComparison.Ordinal);

        // The project's credential state is on the page, so the run explains itself even before the
        // reader gets to the log.
        Assert.Contains("id=\"run-git-credential\"", page, StringComparison.Ordinal);
        Assert.Contains("No credential is assigned to this project", page, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_rejected_credential_points_at_rotation_when_the_project_has_one()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync("Build");
        var projectId = await ReadProjectIdAsync(host, run.RunId);

        // Attach a credential through the settings tab's own forms (no seeded column): the run page's
        // answer must be driven by the same state the product writes.
        var credentialId = await CreateAndAttachAsync(run.Owner, projectId);

        await run.CompleteAsync(exitCode: null, failureReason: PipelineFailureReasons.GitAuthRejected);

        var page = await run.ReadRunPageAsync();

        Assert.Contains("id=\"run-failure-explanation\"", page, StringComparison.Ordinal);
        Assert.Contains("The remote rejected the project", page, StringComparison.Ordinal);
        Assert.Contains("rotate", page, StringComparison.Ordinal);
        Assert.Contains("assigned credential; fetched by the worker", page, StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, credentialId);

        // A credential id, a kind and a timestamp - never the value.
        Assert.DoesNotContain(TokenValue, page, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task A_running_run_states_the_credential_situation_too()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await RunUiHost.CreateAsync();
        var run = await host.CreateRunAsync("Build"); // left running

        var page = await run.ReadRunPageAsync();

        Assert.Contains("id=\"run-git-credential\"", page, StringComparison.Ordinal);
        Assert.Contains("No credential is assigned to this project", page, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"run-failure\"", page, StringComparison.Ordinal);
    }

    private static async Task<Guid> ReadProjectIdAsync(RunUiHost host, Guid runId)
    {
        var runs = await host.Streaming.Inner.InSystemScopeAsync(
            context => context.SystemQueryAsync<BuildRun>(candidate => candidate.Id == runId));
        return Assert.Single(runs).ProjectId;
    }

    /// <summary>Creates a credential on the project's settings tab and attaches it; returns its id.</summary>
    private static async Task<Guid> CreateAndAttachAsync(HttpClient owner, Guid projectId)
    {
        var settingsPath = $"/projects/{projectId}/settings";
        using var created = await OrganizationTestHost.PostFormAsync(
            owner,
            settingsPath,
            $"{GitCredentialEndpoints.ManagementPath}/create",
            new Dictionary<string, string>
            {
                ["Name"] = "RUN_PAGE_TOKEN",
                ["Kind"] = "HttpsToken",
                ["Value"] = TokenValue,
                ["UserName"] = "x-access-token",
                ["ProjectId"] = projectId.ToString(),
            });
        Assert.True((int)created.StatusCode is 302 or 303, $"The create answered {(int)created.StatusCode}.");

        var list = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/git-credentials");
        using var document = System.Text.Json.JsonDocument.Parse(list);
        var credentialId = Assert.Single(document.RootElement.EnumerateArray()).GetProperty("id").GetGuid();

        using var attached = await OrganizationTestHost.PostFormAsync(
            owner,
            settingsPath,
            $"/projects/{projectId}/git-credential/attach",
            new Dictionary<string, string> { ["GitCredentialId"] = credentialId.ToString() });
        Assert.True((int)attached.StatusCode is 302 or 303, $"The attach answered {(int)attached.StatusCode}.");

        return credentialId;
    }
}
