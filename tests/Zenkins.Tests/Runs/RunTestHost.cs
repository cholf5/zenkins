using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Microsoft.Extensions.Hosting;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Runs;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Workers;

// The worker project's namespace shadows the entity types; same alias as WorkerTestHost uses.
using WorkerEntity = Zenkins.Server.Data.Entities.Worker;
using WorkerTokenEntity = Zenkins.Server.Data.Entities.WorkerToken;
using WorkerTokenCrypto = Zenkins.Server.Workers.WorkerTokenCrypto;

namespace Zenkins.Tests.Runs;

/// <summary>
/// F-006's HTTP test host: everything <see cref="WorkerTestHost"/> does (worker registration
/// through the real API), plus helpers for triggering runs through the real form, the worker
/// claim/accept/complete endpoints, cross-tenant reads of the run table, and direct lease/sweeper
/// control so the tests never wait on real time.
/// </summary>
public sealed class RunTestHost : IDisposable
{
    private RunTestHost(WorkerTestHost inner) => Inner = inner;

    public WorkerTestHost Inner { get; }

    /// <summary>The raw <see cref="AuthTestHost"/> under the two wrappers: fresh cookie-jar clients, scopes.</summary>
    public AuthTestHost Host => Inner.Inner.Host;

    public static Task<RunTestHost> CreateAsync() => CreateAsync(null);

    /// <param name="configureAppConfiguration">e.g. a short <c>Runs:LeaseSeconds</c> for lease tests.</param>
    public static async Task<RunTestHost> CreateAsync(
        Action<IConfigurationBuilder>? configureAppConfiguration) =>
        new(await WorkerTestHost.CreateAsync(configureAppConfiguration));

    public async Task<Guid> OrganizationIdAsync() =>
        await Inner.Inner.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);

    /// <summary>Creates a project through the real form and returns (projectId, owner client).</summary>
    public async Task<(Guid ProjectId, HttpClient Owner)> CreateProjectAsync(string name = "game-client")
    {
        var owner = await Inner.SignInOwnerAsync();
        var projectId = await Inner.Inner.AddProjectAsync(await OrganizationIdAsync(), name);
        return (projectId, owner);
    }

    /// <summary>
    /// Seeds a project whose repository is <paramref name="repositoryUrl"/> (card F-014): the pipeline
    /// tests point at a local <c>file://</c> fixture repository, which the project form deliberately
    /// refuses - the form protects a product path, a test needs a repository it can actually clone.
    /// </summary>
    public Task<Guid> SeedProjectAsync(
        string name,
        string repositoryUrl,
        string defaultBranch = "main",
        string pipelinePath = "pipeline.lua") =>
        InSystemScopeAsync(async context =>
        {
            var project = new Project
            {
                OrganizationId = await OrganizationIdAsync(),
                Name = name,
                RepositoryUrl = repositoryUrl,
                DefaultBranch = defaultBranch,
                PipelinePath = pipelinePath,
            };
            context.Projects.Add(project);
            await context.SaveChangesAsync();
            return project.Id;
        });

    /// <summary>Registers a worker through the real API; returns (worker token plaintext, worker id).</summary>
    public async Task<(string Token, Guid WorkerId)> RegisterWorkerAsync(
        string name = "build-agent-1",
        string? tags = "ci",
        int maxConcurrency = 1,
        HttpClient? ownerClient = null)
    {
        var owner = ownerClient ?? await Inner.SignInOwnerAsync();
        var (registrationToken, mintResponse) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        Assert.Equal(HttpStatusCode.OK, mintResponse.StatusCode);
        using var registered = await WorkerTestHost.RegisterAsync(
            owner,
            registrationToken,
            WorkerTestHost.RegistrationPayload(name: name, tags: tags, maxConcurrency: maxConcurrency));
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var token = await WorkerTestHost.ReadStringPropertyAsync(registered, "workerToken");
        var workerId = Guid.Parse(await WorkerTestHost.ReadStringPropertyAsync(registered, "workerId"));
        return (token, workerId);
    }

    /// <summary>
    /// A worker token + row created directly in the database - the cross-tenant probe's second
    /// organization has no Owner session to mint tokens through, and none is needed: the token
    /// row exercises the real authentication path.
    /// </summary>
    public async Task<string> CreateWorkerTokenDirectlyAsync(
        Guid organizationId,
        string name = "foreign-agent",
        string tags = "ci",
        int maxConcurrency = 1)
    {
        var plaintext = await Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            var worker = new WorkerEntity
            {
                OrganizationId = organizationId,
                Name = name,
                Os = "Linux",
                Architecture = "X64",
                Version = "1.0.0",
                Tags = tags,
                CpuCount = 4,
                MaxConcurrency = maxConcurrency,
            };
            context.Workers.Add(worker);

            var token = WorkerTokenCrypto.Generate(WorkerTokenCrypto.WorkerPrefix);
            context.WorkerTokens.Add(new WorkerTokenEntity
            {
                OrganizationId = organizationId,
                Kind = WorkerTokenKind.Worker,
                WorkerId = worker.Id,
                TokenHash = WorkerTokenCrypto.Hash(token),
            });
            await context.SaveChangesAsync();
            return token;
        });
        return plaintext;
    }

    /// <summary>Seeds a queued run directly (fast path for the queue-heavy tests).</summary>
    /// <param name="branch">F-014: the branch the run builds.</param>
    /// <param name="pipelinePath">F-014: the pipeline file this run looks for (snapshot at trigger time).</param>
    public async Task<Guid> AddRunAsync(
        Guid projectId,
        string requiredTags = "",
        string branch = "main",
        Guid organizationId = default,
        string pipelinePath = "pipeline.lua")
    {
        var runId = await Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            var run = new BuildRun
            {
                OrganizationId = organizationId == default ? await OrganizationIdAsync() : organizationId,
                ProjectId = projectId,
                TriggerType = BuildRunTriggerType.Manual,
                RequiredTags = requiredTags,
                GitBranch = branch,
                PipelinePath = pipelinePath,
            };
            context.BuildRuns.Add(run);
            await context.SaveChangesAsync();
            return run.Id;
        });
        return runId;
    }

    // ------------------------------------------------------------------
    // Product surfaces
    // ------------------------------------------------------------------

    /// <summary>POSTs the start-run form with antiforgery and returns the raw response.</summary>
    public static Task<HttpResponseMessage> StartRunAsync(
        HttpClient client,
        Guid projectId,
        string? branch = null,
        string? requiredTags = null,
        string? timeoutMinutes = null) =>
        OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{projectId}",
            $"/projects/{projectId}/runs/start",
            new Dictionary<string, string>
            {
                // F-014: a run selects a branch, never a command line. Blank means the project default.
                ["Branch"] = branch ?? string.Empty,
                ["RequiredTags"] = requiredTags ?? string.Empty,
                ["TimeoutMinutes"] = timeoutMinutes ?? string.Empty,
            });

    /// <summary>Triggers a run through the real form and returns the new run's id (asserts PRG).</summary>
    public static async Task<Guid> TriggerRunAsync(
        HttpClient client,
        Guid projectId,
        string? branch = null,
        string? requiredTags = null,
        string? timeoutMinutes = null)
    {
        var response = await StartRunAsync(client, projectId, branch, requiredTags, timeoutMinutes);
        Assert.True((int)response.StatusCode is 302 or 303,
            $"Expected a redirect, got {(int)response.StatusCode}.");
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/runs/", location, StringComparison.Ordinal);
        return Guid.Parse(location["/runs/".Length..]);
    }

    public static Task<HttpResponseMessage> ClaimAsync(HttpClient client, string? workerToken) =>
        WorkerTestHost.SendWorkerJsonAsync(client, workerToken, "/api/workers/claim", new { });

    public static Task<HttpResponseMessage> AcceptAsync(HttpClient client, string workerToken, Guid runId) =>
        WorkerTestHost.SendWorkerJsonAsync(
            client,
            workerToken,
            $"/api/workers/runs/{runId}/accepted",
            new { });

    /// <summary>Posts the F-014 definition report (resolved commit + frozen spec).</summary>
    public static Task<HttpResponseMessage> ReportDefinitionAsync(
        HttpClient client,
        string workerToken,
        Guid runId,
        string? pipelineSpecJson,
        string? gitCommit = null) =>
        WorkerTestHost.SendWorkerJsonAsync(
            client,
            workerToken,
            $"/api/workers/runs/{runId}/definition",
            new { gitCommit, pipelineSpecJson });

    public static Task<HttpResponseMessage> CompleteAsync(
        HttpClient client,
        string workerToken,
        Guid runId,
        int? exitCode,
        string? failureReason = null) =>
        WorkerTestHost.SendWorkerJsonAsync(
            client,
            workerToken,
            $"/api/workers/runs/{runId}/completed",
            new { exitCode, failureReason });

    public static async Task<Guid> ReadClaimedRunIdAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("runId").GetGuid();
    }

    /// <summary>Reads one property of the claim payload (F-014's repository/branch/pipeline path).</summary>
    public static async Task<string> ReadClaimStringAsync(HttpResponseMessage response, string property)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty(property).GetString()
            ?? throw new InvalidOperationException($"The claim response's {property} was null.");
    }

    public static async Task<string> ReadReportStatusAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("status").GetString()
            ?? throw new InvalidOperationException("The report response had no status.");
    }

    // ------------------------------------------------------------------
    // Database reads and direct state control (assertion side + time control)
    // ------------------------------------------------------------------

    public Task<BuildRun> ReadRunAsync(Guid runId) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            return (await context.SystemQueryAsync<BuildRun>(run => run.Id == runId)).Single();
        });

    public Task<IReadOnlyList<BuildRun>> ReadRunsAsync(Guid organizationId) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            return (IReadOnlyList<BuildRun>)await context.SystemQueryAsync<BuildRun>(
                run => run.OrganizationId == organizationId);
        });

    /// <summary>Runs a callback against the whole run table, cross-tenant (assertion helper).</summary>
    public Task<T> InSystemScopeAsync<T>(Func<ZenkinsDbContext, Task<T>> action) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            return await action(context);
        });

    public Task<WorkerEntity> ReadWorkerAsync(Guid workerId) => Inner.ReadWorkerAsync(workerId);

    /// <summary>Marks a worker Offline directly - F-010's staleness monitor does not exist yet.</summary>
    public Task SetWorkerStatusAsync(Guid workerId, string status) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            var worker = (await context.SystemQueryAsync<WorkerEntity>(candidate => candidate.Id == workerId)).Single();
            worker.Status = status;
            await context.SaveChangesAsync();
            return true;
        });

    /// <summary>
    /// Moves a run's lease into the past directly - the deterministic stand-in for "advance past
    /// the lease window" (the window is seconds; the clock is not).
    /// </summary>
    public Task ExpireLeaseAsync(Guid runId) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            var run = (await context.SystemQueryAsync<BuildRun>(candidate => candidate.Id == runId)).Single();
            run.LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await context.SaveChangesAsync();
            return true;
        });

    /// <summary>One lease-sweep tick through the background service's real seam.</summary>
    public Task<int> SweepAsync() =>
        Host.InScopeAsync(async services =>
        {
            // AddHostedService registers the implementation as IHostedService; find it there.
            var sweeper = services.GetServices<IHostedService>().OfType<LeaseSweeper>().Single();
            return await sweeper.SweepOnceAsync();
        });

    private static async Task<ZenkinsDbContext> CreateContextAsync(IServiceProvider services) =>
        await services.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>().CreateDbContextAsync();

    public void Dispose() => Inner.Dispose();
}
