using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Runs;
using WorkerGit = Zenkins.Worker.Git;

namespace Zenkins.Tests.Git;

/// <summary>
/// The worker side of the credential pipeline (card F-013): the client fetches the material for its own
/// run, and a 404 - the single answer for every miss - means "no credential", not an error.
/// </summary>
[Trait("Category", "Git")]
public sealed class GitCredentialClientTests
{
    private const string TokenValue = "zkpat_0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task The_client_fetches_the_material_for_its_own_run()
    {
        using var setup = await GitCredentialFixture.CreateAsync();

        using var http = setup.Host.Host.CreateClient();
        var client = new WorkerGit.GitCredentialClient(http);

        var material = await client.FetchAsync(
            setup.WorkerToken,
            setup.RunId,
            TestContext.Current.CancellationToken);

        Assert.NotNull(material);
        Assert.Equal(WorkerGit.GitCredentialKind.HttpsToken, material.Kind);
        Assert.Equal(TokenValue, material.Secret);
        Assert.Equal("x-access-token", material.UserName);

        // The material never renders its secret, even by accident.
        Assert.DoesNotContain(TokenValue, material.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_without_material_for_this_worker_is_null_not_an_error()
    {
        using var setup = await GitCredentialFixture.CreateAsync();
        using var http = setup.Host.Host.CreateClient();
        var client = new WorkerGit.GitCredentialClient(http);

        // Another worker of the same organization: the same bodyless 404 the product gives everyone.
        Assert.Null(await client.FetchAsync(
            setup.OtherWorkerToken,
            setup.RunId,
            TestContext.Current.CancellationToken));

        // An unknown run id, for the owning worker: also null.
        Assert.Null(await client.FetchAsync(
            setup.WorkerToken,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_rejected_token_is_a_credential_failure()
    {
        using var setup = await GitCredentialFixture.CreateAsync();
        using var http = setup.Host.Host.CreateClient();
        var client = new WorkerGit.GitCredentialClient(http);

        var failure = await Assert.ThrowsAsync<WorkerGit.GitException>(() => client.FetchAsync(
            "zwrk_not_a_real_token",
            setup.RunId,
            TestContext.Current.CancellationToken));

        Assert.Equal(WorkerGit.GitFailureKind.AuthenticationRejected, failure.Kind);
        Assert.DoesNotContain(TokenValue, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_malformed_response_never_echoes_its_body()
    {
        // A stub server answering 200 with something that is not the material shape: the message must
        // describe the shape, never repeat the body (which could be the secret).
        var handler = new StubHandler(HttpStatusCode.OK, """{"unexpected":"zkpat_leaked_in_body_value"}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new WorkerGit.GitCredentialClient(http);

        var failure = await Assert.ThrowsAsync<WorkerGit.GitException>(
            () => client.FetchAsync("zwrk_token", Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Equal(WorkerGit.GitFailureKind.Failed, failure.Kind);
        Assert.DoesNotContain("zkpat_leaked_in_body_value", failure.Message, StringComparison.Ordinal);
    }

    private sealed class StubHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}

/// <summary>A credential, a project referencing it, and a run claimed by the worker that will fetch it.</summary>
internal sealed class GitCredentialFixture : IDisposable
{
    private GitCredentialFixture(
        RunTestHost host,
        Guid credentialId,
        Guid projectId,
        Guid runId,
        string workerToken,
        string otherWorkerToken)
    {
        Host = host;
        CredentialId = credentialId;
        ProjectId = projectId;
        RunId = runId;
        WorkerToken = workerToken;
        OtherWorkerToken = otherWorkerToken;
    }

    public RunTestHost Host { get; }

    public Guid CredentialId { get; }

    public Guid ProjectId { get; }

    public Guid RunId { get; }

    public string WorkerToken { get; }

    public string OtherWorkerToken { get; }

    public static async Task<GitCredentialFixture> CreateAsync()
    {
        var host = await RunTestHost.CreateAsync();
        using var owner = await host.Inner.SignInOwnerAsync();

        Guid credentialId;
        using (var create = await Zenkins.Tests.Organizations.OrganizationTestHost.PostFormAsync(
            owner,
            "/",
            "/git-credentials/create",
            new Dictionary<string, string>
            {
                ["Name"] = "PIPELINE_TOKEN",
                ["Kind"] = "HttpsToken",
                ["Value"] = "zkpat_0123456789abcdef0123456789abcdef",
                ["UserName"] = "x-access-token",
            }))
        {
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
            using var document = JsonDocument.Parse(
                await create.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            credentialId = document.RootElement.GetProperty("id").GetGuid();
        }

        var (projectId, projectOwner) = await host.CreateProjectAsync();
        projectOwner.Dispose();
        await host.InSystemScopeAsync(async context =>
        {
            var projects = await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId);
            projects[0].GitCredentialId = credentialId;
            await context.SaveChangesAsync();
            return true;
        });

        var queuedRunId = await host.AddRunAsync(projectId);
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "pipeline-agent");
        var (otherWorkerToken, _) = await host.RegisterWorkerAsync(name: "other-pipeline-agent");

        using var client = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(client, workerToken);
        var runId = await RunTestHost.ReadClaimedRunIdAsync(claimed);
        using var accepted = await RunTestHost.AcceptAsync(client, workerToken, runId);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(queuedRunId, runId);

        return new GitCredentialFixture(host, credentialId, projectId, runId, workerToken, otherWorkerToken);
    }

    public void Dispose() => Host.Dispose();
}
