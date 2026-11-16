using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Dashboard;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Probes;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.RunLifecycle;
using Zenkins.Tests.Runs;
using Zenkins.Tests.Workers;
using Zenkins.Shared.Probes;

namespace Zenkins.Tests.Probes;

/// <summary>
/// Card I-005's test host: the product's own services on top of <see cref="RunLifecycleHost"/> (so a fact
/// can run the real in-process worker beside the real probe API), with the probe knobs compressed (a short
/// caller wait, lease and dedupe window) so no fact waits on production time - the same "time compressed,
/// never faked" discipline the run-lifecycle hosts use.
/// </summary>
public sealed class GitProbeTestHost : IDisposable
{
    /// <summary>The default wait the facts use: long enough for an in-process claim, short enough to be a test.</summary>
    public const double DefaultWaitSeconds = 5;

    private GitProbeTestHost(RunLifecycleHost inner) => Inner = inner;

    public RunLifecycleHost Inner { get; }

    public AuthTestHost Host => Inner.Host;

    /// <summary>
    /// Creates the host with the probe knobs compressed. <paramref name="extraConfiguration"/> is applied
    /// last, so a fact can shorten the wait further (the no-worker fact runs against a 0.5s wait).
    /// </summary>
    public static async Task<GitProbeTestHost> CreateAsync(
        Action<IConfigurationBuilder>? extraConfiguration = null)
    {
        var defaults = new Dictionary<string, string?>
        {
            [ProbeOptions.WaitSecondsConfigurationKey] = DefaultWaitSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            [ProbeOptions.PollMillisecondsConfigurationKey] = "25",
            [ProbeOptions.LeaseSecondsConfigurationKey] = "30",
            [ProbeOptions.DedupeSecondsConfigurationKey] = "10",
            [ProbeOptions.RetentionSecondsConfigurationKey] = "120",

            // The worker's probe loop polls quickly in-process, like the run claim does in these hosts.
            ["Workers:OfflineAfterSeconds"] = "3600",
        };

        return new GitProbeTestHost(await RunLifecycleHost.CreateAsync(builder =>
        {
            builder.AddInMemoryCollection(defaults);
            extraConfiguration?.Invoke(builder);
        }));
    }

    public Task<Guid> OrganizationIdAsync() => Inner.OrganizationIdAsync();

    /// <summary>Seeds a Pipeline project pointed at a repository the test controls (the F-014 pattern).</summary>
    public Task<Guid> SeedProjectAsync(
        string name,
        string repositoryUrl,
        string defaultBranch = "main") =>
        Inner.SeedProjectAsync(name, repositoryUrl, defaultBranch);

    /// <summary>
    /// Registers a worker through the real API (Owner-minted token), stating the card's capability flag.
    /// A worker registered with <paramref name="canProbe"/> = false is what a pre-I-005 binary looks like.
    /// </summary>
    public async Task<(string Token, Guid WorkerId)> RegisterWorkerAsync(
        string name = "probe-agent",
        bool canProbe = true,
        int maxConcurrency = 1,
        HttpClient? ownerClient = null)
    {
        var owner = ownerClient ?? await Inner.SignInOwnerAsync();
        var (registrationToken, mintResponse) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        Assert.Equal(HttpStatusCode.OK, mintResponse.StatusCode);
        using var registered = await WorkerTestHost.RegisterAsync(
            owner,
            registrationToken,
            WorkerTestHost.RegistrationPayload(
                name: $"{name}-{Interlocked.Increment(ref _workerSequence)}",
                tags: "ci",
                maxConcurrency: maxConcurrency,
                canProbe: canProbe));
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var token = await WorkerTestHost.ReadStringPropertyAsync(registered, "workerToken");
        var workerId = Guid.Parse(await WorkerTestHost.ReadStringPropertyAsync(registered, "workerId"));
        return (token, workerId);
    }

    private int _workerSequence;

    /// <summary>
    /// One requesting call, through the product's scoped service under the starter organization's tenant.
    /// This is the surface F-023/F-025 will use - there is no HTTP route for it by design.
    /// </summary>
    public async Task<GitProbeRequestResult> RequestProbeAsync(
        Guid projectId,
        CancellationToken cancellationToken) =>
        await RequestProbeAsync(await OrganizationIdAsync(), projectId, cancellationToken);

    /// <summary>The same call under an explicit organization (the tenancy fact's second tenant).</summary>
    public Task<GitProbeRequestResult> RequestProbeAsync(
        Guid organizationId,
        Guid projectId,
        CancellationToken cancellationToken) =>
        Host.InScopeAsync(async services =>
        {
            var tenant = services.GetRequiredService<TenantContext>();
            tenant.OrganizationId = organizationId;
            return await services.GetRequiredService<GitProbeService>().RequestAsync(projectId, cancellationToken);
        });

    /// <summary>Seeds a project in an arbitrary organization (the tenancy fact's second tenant).</summary>
    public Task<Guid> SeedProjectInOrganizationAsync(
        Guid organizationId,
        string name,
        string repositoryUrl) =>
        Inner.Inner.InSystemScopeAsync(async context =>
        {
            var project = new Project
            {
                OrganizationId = organizationId,
                Name = name,
                RepositoryUrl = repositoryUrl,
                DefaultBranch = "main",
                PipelinePath = "pipeline.lua",
            };
            context.Projects.Add(project);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return project.Id;
        });

    /// <summary>
    /// Creates a second organization with its own Owner session (the real registration/Invite path), for
    /// the tenancy fact's foreign probe.
    /// </summary>
    public async Task<(Guid OrganizationId, HttpClient Owner)> CreateForeignOrganizationAsync(
        string name,
        string ownerEmail)
    {
        await Inner.RegisterUserAsync(ownerEmail);
        var organizationId = await Inner.AddOrganizationAsync(
            name,
            (ownerEmail, Zenkins.Server.Data.Entities.OrganizationRole.Owner));
        var owner = await Inner.SignInAsync(ownerEmail);
        return (organizationId, owner);
    }

    /// <summary>The singleton registry (the in-memory queue the facts observe).</summary>
    public Task<GitProbeRegistry> RegistryAsync() =>
        Host.InScopeAsync(services => Task.FromResult(services.GetRequiredService<GitProbeRegistry>()));

    /// <summary>One probe's current snapshot, or null.</summary>
    public async Task<GitProbeSnapshot?> FindProbeAsync(Guid probeId) =>
        (await RegistryAsync()).Find(probeId);

    /// <summary>
    /// Waits until a probe is in flight (the requesting call runs in the background in several facts) and
    /// returns it. Polls the registry, so no fact sleeps for a guessed duration.
    /// </summary>
    public async Task<GitProbeSnapshot> WaitForUnsettledProbeAsync(TimeSpan timeout)
    {
        var registry = await RegistryAsync();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var pending = await Host.InScopeAsync(services =>
            {
                var found = services.GetRequiredService<GitProbeRegistry>();
                return Task.FromResult(found.Snapshots.FirstOrDefault(probe => !probe.IsSettled));
            });
            if (pending is not null)
            {
                return pending;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException(
            $"No probe was in flight within {timeout.TotalSeconds:0.#}s. The registry holds {registry.Count} probe(s).");
    }

    // ------------------------------------------------------------------
    // The worker API, called exactly as the product's worker binary does
    // ------------------------------------------------------------------

    /// <summary>POSTs the probe claim with the worker token; returns the raw response (204 when idle).</summary>
    public static Task<HttpResponseMessage> ClaimProbeAsync(HttpClient client, string? workerToken) =>
        SendAsync(client, workerToken, "/api/workers/probes/claim", payload: null);

    /// <summary>Claims one probe and returns its id (asserts a 200).</summary>
    public static async Task<GitProbeClaimView> ClaimOneProbeAsync(HttpClient client, string workerToken)
    {
        using var response = await ClaimProbeAsync(client, workerToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = body.RootElement;
        return new GitProbeClaimView(
            root.GetProperty("probeId").GetGuid(),
            root.GetProperty("projectId").GetGuid(),
            root.GetProperty("repositoryUrl").GetString()!,
            root.GetProperty("leaseExpiresAt").GetDateTimeOffset());
    }

    /// <summary>POSTs one probe report (token-authenticated).</summary>
    public static Task<HttpResponseMessage> ReportProbeAsync(
        HttpClient client,
        string workerToken,
        Guid probeId,
        string outcome,
        IEnumerable<GitProbeRef>? heads = null,
        string? headSha = null,
        string? headBranch = null,
        string? message = null) =>
        SendAsync(
            client,
            workerToken,
            $"/api/workers/probes/{probeId}/report",
            new
            {
                outcome,
                heads = (heads ?? []).Select(head => new { name = head.Name, sha = head.Sha }).ToArray(),
                headSha,
                headBranch,
                message,
            });

    /// <summary>GETs the probe-scoped git credential.</summary>
    public static Task<HttpResponseMessage> FetchProbeCredentialAsync(
        HttpClient client,
        string workerToken,
        Guid probeId) =>
        SendAsync(
            client,
            workerToken,
            $"/api/workers/probes/{probeId}/git-credential",
            payload: null,
            method: HttpMethod.Get);

    // ------------------------------------------------------------------
    // Assertions' read side
    // ------------------------------------------------------------------

    /// <summary>One project row across tenants (the last-probe columns).</summary>
    public Task<Project> ReadProjectRowAsync(Guid projectId) => Inner.Inner.ReadProjectAsync(projectId);

    /// <summary>Every run row of the organization, cross-tenant - the "a probe is not a run" evidence.</summary>
    public async Task<IReadOnlyList<BuildRun>> ReadRunsAsync() =>
        await Inner.Inner.ReadRunsAsync(await OrganizationIdAsync());

    /// <summary>The dashboard summary as the real dashboard query renders it.</summary>
    public Task<DashboardSummary?> DashboardAsync() =>
        Host.InScopeAsync(async services =>
        {
            var tenant = services.GetRequiredService<TenantContext>();
            tenant.OrganizationId = await OrganizationIdAsync();
            return await services.GetRequiredService<DashboardQuery>().GetSummaryAsync();
        });

    /// <summary>A fresh worker-API client (the product's shape: bearer tokens, no cookies).</summary>
    public HttpClient CreateWorkerClient() => Host.CreateClient();

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string? bearerToken,
        string url,
        object? payload,
        HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Post, url);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        if (bearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return client.SendAsync(request);
    }

    public void Dispose() => Inner.Dispose();
}

/// <summary>The claim's payload as a fact reads it (the wire shape, not the server's record).</summary>
public sealed record GitProbeClaimView(
    Guid ProbeId,
    Guid ProjectId,
    string RepositoryUrl,
    DateTimeOffset LeaseExpiresAt);
