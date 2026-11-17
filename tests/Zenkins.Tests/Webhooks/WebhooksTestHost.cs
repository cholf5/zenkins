using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Organizations;
using Zenkins.Server.Webhooks;
using Zenkins.Server.Workers;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Workers;
using WorkerTokenCrypto = Zenkins.Server.Workers.WorkerTokenCrypto;

namespace Zenkins.Tests.Webhooks;

/// <summary>
/// F-017's HTTP test host: the real <c>Program</c> pipeline over a temporary SQLite file (with the
/// artifact store and the Data Protection key ring pointed at temp directories, the F-016 discipline),
/// plus everything a delivery test needs.
/// <para>
/// It is built directly on <see cref="AuthTestHost"/> rather than on the other hosts' chains because one
/// assertion needs a DI-registered <see cref="ILoggerProvider"/>: "the webhook secret never reaches the
/// log" is only a real test if the log is actually captured.
/// </para>
/// <para>
/// The seeded shape: the bootstrap Owner (alice) of the starter organization owns one project; an
/// ordinary Member (bob) of the same organization; a second organization owned by carol, for the
/// cross-tenant probes.
/// </para>
/// </summary>
internal sealed class WebhooksTestHost : IDisposable
{
    public const string OwnerEmail = "alice@example.test";
    public const string MemberEmail = "bob@example.test";
    public const string ForeignOwnerEmail = "carol@example.test";
    public const string ForeignOrganizationName = "Bravo";
    public const string Password = AuthTestHost.ValidPassword;

    public const string ProjectName = "alpha-app";
    public const string RepositoryUrl = "https://example.test/alpha.git";
    public const string DefaultBranch = "main";

    private readonly List<HttpClient> _clients = [];
    private readonly string _secretInUse;

    private WebhooksTestHost(AuthTestHost host, CapturingLoggerProvider logs, string seedSecret)
    {
        Host = host;
        Logs = logs;
        _secretInUse = seedSecret;
    }

    public AuthTestHost Host { get; }

    /// <summary>Every log line the server wrote while this host lived (see the class doc).</summary>
    public CapturingLoggerProvider Logs { get; }

    public Guid OrganizationId { get; private set; }

    public Guid ProjectId { get; private set; }

    /// <summary>The bootstrap Owner's Identity id (the `CreatedByUserId` the product records).</summary>
    public string OwnerUserId { get; private set; } = string.Empty;

    /// <summary>A cookie-less client - what a git host is: no session, only the signature.</summary>
    public HttpClient DeliveryClient { get; private set; } = null!;

    /// <summary>Starts the host, registers the accounts and seeds one project.</summary>
    public static async Task<WebhooksTestHost> CreateAsync(IDictionary<string, string?>? settings = null)
    {
        var logs = new CapturingLoggerProvider();

        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // The sweeper must never fire on its own during a test; every sweep is an explicit tick.
            [WebhookOptions.SweepIntervalSecondsConfigurationKey] = "36000",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            configuration[key] = value;
        }

        var host = new AuthTestHost(
            configureTestServices: services => services.AddSingleton<ILoggerProvider>(logs),
            configureAppConfiguration: builder => builder.AddInMemoryCollection(configuration));

        var instance = new WebhooksTestHost(
            host,
            logs,
            seedSecret: WebhookSecretProtector.SecretPrefix + Convert.ToHexStringLower(
                RandomNumberGenerator.GetBytes(24)));
        try
        {
            using (var client = host.CreateClient())
            {
                var response = await AuthTestHost.RegisterAsync(client, OwnerEmail);
                if ((int)response.StatusCode is not (302 or 303))
                {
                    throw new InvalidOperationException(
                        $"Registering the owner failed with {(int)response.StatusCode}.");
                }
            }

            instance.OrganizationId = await instance.ReadOrganizationIdAsync(BootstrapService.StarterOrganizationName);
            instance.OwnerUserId = (await instance.ReadUserAsync(OwnerEmail)).Id;
            instance.ProjectId = await instance.AddProjectAsync(
                instance.OrganizationId,
                ProjectName,
                RepositoryUrl,
                DefaultBranch);
            instance.DeliveryClient = instance.Track(host.CreateClient());
            return instance;
        }
        catch
        {
            instance.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Accounts, projects and the seeded webhook
    // ------------------------------------------------------------------

    public async Task<HttpClient> SignInOwnerAsync() => Track(await SignInAsync(OwnerEmail));

    public async Task<HttpClient> SignInMemberAsync(string email) => Track(await SignInAsync(email));

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = Host.CreateClient();
        var response = await AuthTestHost.LoginAsync(client, email);
        if ((int)response.StatusCode is not (302 or 303))
        {
            client.Dispose();
            throw new InvalidOperationException($"Signing in as '{email}' failed with {(int)response.StatusCode}.");
        }

        return client;
    }

    /// <summary>Registers bob as a signed-in account and invites him into the starter organization.</summary>
    public async Task<HttpClient> CreateMemberAsync()
    {
        var member = Host.CreateClient();
        var registered = await AuthTestHost.RegisterAsync(member, MemberEmail);
        if ((int)registered.StatusCode is not (302 or 303))
        {
            member.Dispose();
            throw new InvalidOperationException($"Registering the member failed with {(int)registered.StatusCode}.");
        }

        using var owner = await SignInOwnerAsync();
        var invite = await OrganizationTestHost.PostFormAsync(
            owner,
            OrganizationEndpoints.MembersPath,
            "/organizations/members/invite",
            new Dictionary<string, string> { ["Email"] = MemberEmail });
        if ((int)invite.StatusCode is not (302 or 303))
        {
            throw new InvalidOperationException($"Inviting the member failed with {(int)invite.StatusCode}.");
        }

        // Resolve the member's session so the starter organization is persisted as their selection.
        _ = await AuthTestHost.GetStringAsync(member, "/");
        return Track(member);
    }

    /// <summary>Registers carol and gives her an organization of her own; returns its id.</summary>
    public async Task<Guid> CreateForeignOrganizationAsync()
    {
        using var carol = Host.CreateClient();
        var registered = await AuthTestHost.RegisterAsync(carol, ForeignOwnerEmail);
        if ((int)registered.StatusCode is not (302 or 303))
        {
            throw new InvalidOperationException($"Registering the foreign owner failed with {(int)registered.StatusCode}.");
        }

        return await AddOrganizationAsync(
            ForeignOrganizationName,
            (ForeignOwnerEmail, OrganizationRole.Owner));
    }

    /// <summary>A cookie-less delivery client (the git host's view).</summary>
    public HttpClient CreateDeliveryClient() => Track(Host.CreateClient());

    // ------------------------------------------------------------------
    // Webhooks: through the product's own forms, and seeded directly
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates a webhook through the real settings form (card D-003 moved the webhook cards to the
    /// project's settings tab; the endpoints are F-017's, unchanged) and returns the one-time
    /// plaintext from the show-once document, plus the id the document names.
    /// </summary>
    public async Task<(Guid WebhookId, string Secret, string Html, HttpResponseMessage Response)> CreateWebhookAsync(
        HttpClient client,
        string name,
        string branchFilter = "",
        string requiredTags = "")
    {
        var response = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{ProjectId}/settings",
            $"/projects/{ProjectId}/webhooks/create",
            new Dictionary<string, string>
            {
                ["Name"] = name,
                ["BranchFilter"] = branchFilter,
                ["RequiredTags"] = requiredTags,
            });

        var html = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            return (Guid.Empty, string.Empty, html, response);
        }

        return (ExtractWebhookId(html), ExtractSecret(html), html, response);
    }

    /// <summary>
    /// Seeds a webhook with a known secret (the delivery tests' fast path). The ciphertext is produced by
    /// the product's protector, so the delivery path decrypts exactly what the create form would store.
    /// </summary>
    public Task<Guid> SeedWebhookAsync(
        string name,
        string branchFilter = DefaultBranch,
        string requiredTags = "",
        string? secret = null,
        bool enabled = true,
        Guid? organizationId = null,
        Guid? projectId = null) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            var protector = services.GetRequiredService<WebhookSecretProtector>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            var webhook = new Webhook
            {
                OrganizationId = organizationId ?? OrganizationId,
                ProjectId = projectId ?? ProjectId,
                Name = name,
                SecretCiphertext = protector.Protect(secret ?? SecretFor(name)),
                BranchFilter = branchFilter,
                RequiredTags = requiredTags,
                Enabled = enabled,
                CreatedByUserId = OwnerUserId,
            };
            context.Set<Webhook>().Add(webhook);
            await context.SaveChangesAsync();
            return webhook.Id;
        });

    /// <summary>
    /// Seeds a webhook with a ciphertext the caller supplies - used to prove that a blob protected under a
    /// different purpose can never stand in for a webhook secret (review m7).
    /// </summary>
    public Task<Guid> SeedWebhookWithCiphertextAsync(string name, string ciphertext) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            var webhook = new Webhook
            {
                OrganizationId = OrganizationId,
                ProjectId = ProjectId,
                Name = name,
                SecretCiphertext = ciphertext,
                BranchFilter = DefaultBranch,
                RequiredTags = string.Empty,
                CreatedByUserId = OwnerUserId,
            };
            context.Set<Webhook>().Add(webhook);
            await context.SaveChangesAsync();
            return webhook.Id;
        });

    /// <summary>The deterministic secret a seeded webhook is created with.</summary>
    public string SecretFor(string name) => $"{_secretInUse}-{name}";

    /// <summary>Simulates a disabled webhook without going through the form (delivery tests).</summary>
    public Task DisableWebhookAsync(Guid webhookId) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            var webhook = (await context.SystemQueryAsync<Webhook>(row => row.Id == webhookId)).Single();
            webhook.Enabled = false;
            await context.SaveChangesAsync();
            return true;
        });

    public Task<Webhook?> ReadWebhookAsync(Guid webhookId) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            return (await context.SystemQueryAsync<Webhook>(row => row.Id == webhookId)).SingleOrDefault();
        });

    public Task<List<Webhook>> ReadWebhooksAsync() =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            return await context.SystemQueryAsync<Webhook>(row => row.OrganizationId == OrganizationId);
        });

    public Task<List<WebhookDelivery>> ReadDeliveriesAsync(Guid webhookId) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            return await context.SystemQueryAsync<WebhookDelivery>(row => row.WebhookId == webhookId);
        });

    /// <summary>
    /// Moves a delivery record's <c>ReceivedAt</c> into the past - the deterministic stand-in for waiting
    /// out the dedupe window (the window is minutes; the clock is not).
    /// </summary>
    public Task AgeDeliveriesAsync(Guid webhookId, TimeSpan age) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            var receivedAt = DateTimeOffset.UtcNow - age;

            // A guarded UPDATE: the context's timestamp routine rewrites UpdatedAt on every save, so the
            // only way to place a row in the past is to bypass it.
            await context.Set<WebhookDelivery>()
                .IgnoreQueryFilters()
                .Where(row => row.WebhookId == webhookId)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.ReceivedAt, receivedAt));
            return true;
        });

    /// <summary>One sweeper tick through the product's own hosted service.</summary>
    public Task<WebhookSweepResult> SweepAsync() =>
        Host.InScopeAsync(services =>
            services.GetServices<IHostedService>()
                .OfType<WebhookDeliverySweeper>()
                .Single()
                .SweepOnceAsync(TestContext.Current.CancellationToken));

    // ------------------------------------------------------------------
    // Card F-023: definition tasks (the branch observation) and probes
    // ------------------------------------------------------------------

    /// <summary>
    /// Claims one definition task through the real worker API and returns it, or null for the 204
    /// answer. Used to play a Worker by hand, exactly like the run claim helpers.
    /// </summary>
    public async Task<DefinitionClaim?> ClaimDefinitionAsync(HttpClient client, string workerToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/workers/definitions/claim");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", workerToken);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return null;
        }

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = body.RootElement;
        return new DefinitionClaim(
            root.GetProperty("validationId").GetGuid(),
            root.TryGetProperty("kind", out var kind) ? kind.GetString() : null,
            root.TryGetProperty("script", out var script) && script.ValueKind == System.Text.Json.JsonValueKind.String
                ? script.GetString()
                : null,
            root.TryGetProperty("repositoryUrl", out var repository) ? repository.GetString() : null,
            root.TryGetProperty("branch", out var branch) ? branch.GetString() : null,
            root.TryGetProperty("commit", out var commit) ? commit.GetString() : null,
            root.TryGetProperty("pipelinePath", out var path) ? path.GetString() : null);
    }

    /// <summary>Reports one definition task's answer through the real worker API.</summary>
    public Task<HttpResponseMessage> ReportDefinitionAsync(
        HttpClient client,
        string workerToken,
        Guid validationId,
        string outcome,
        string? diagnostic = null,
        string? failureReason = null) =>
        WorkerTestHost.SendWorkerJsonAsync(
            client,
            workerToken,
            $"/api/workers/definitions/{validationId}/report",
            new { outcome, diagnostic, failureReason });

    /// <summary>
    /// Plays one whole observation by hand: claim the oldest task, report the given answer. Returns the
    /// claimed task (null when nothing was queued).
    /// </summary>
    public async Task<DefinitionClaim?> ObserveOnceAsync(
        string workerToken,
        string outcome,
        string? diagnostic = null,
        string? failureReason = null)
    {
        using var client = CreateDeliveryClient();
        var claim = await ClaimDefinitionAsync(client, workerToken);
        if (claim is null)
        {
            return null;
        }

        using var response = await ReportDefinitionAsync(
            client, workerToken, claim.ValidationId, outcome, diagnostic, failureReason);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return claim;
    }

    /// <summary>Polls the in-memory definition registry until a repository observation exists.</summary>
    public async Task WaitForObservationAsync(Guid projectId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var found = await Host.InScopeAsync(services =>
            {
                var registry = services.GetRequiredService<Zenkins.Server.Probes.DefinitionValidationRegistry>();
                return Task.FromResult(registry.Snapshots.Any(entry =>
                    entry.ProjectId == projectId
                    && entry.Kind == Zenkins.Shared.Pipelines.DefinitionValidationKinds.RepositoryBranch));
            });
            if (found)
            {
                return;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("No branch observation was queued in time.");
    }

    /// <summary>Waits for the in-flight probe a discovery pass starts (card F-023).</summary>
    public async Task<Zenkins.Server.Probes.GitProbeSnapshot> WaitForUnsettledProbeAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var pending = await Host.InScopeAsync(services => Task.FromResult(
                services.GetRequiredService<Zenkins.Server.Probes.GitProbeRegistry>().Snapshots
                    .FirstOrDefault(probe => !probe.IsSettled)));
            if (pending is not null)
            {
                return pending;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("No probe was in flight within the timeout.");
    }

    /// <summary>Reads the definition registry (the in-memory task queue).</summary>
    public Task<IReadOnlyList<Zenkins.Server.Probes.DefinitionValidationEntry>> ObservationsAsync() =>
        Host.InScopeAsync(services => Task.FromResult(
            (IReadOnlyList<Zenkins.Server.Probes.DefinitionValidationEntry>)services
                .GetRequiredService<Zenkins.Server.Probes.DefinitionValidationRegistry>()
                .Snapshots));

    /// <summary>One definition task as the tests read it.</summary>
    public sealed record DefinitionClaim(
        Guid ValidationId,
        string? Kind,
        string? Script,
        string? RepositoryUrl,
        string? Branch,
        string? Commit,
        string? PipelinePath);

    // ------------------------------------------------------------------
    // Deliveries
    // ------------------------------------------------------------------

    /// <summary>
    /// POSTs a raw body to the delivery endpoint. <paramref name="signature"/> and
    /// <paramref name="gitLabToken"/> are sent verbatim (pass a wrong value to test a refusal); setting
    /// neither sends no signature header at all.
    /// </summary>
    public async Task<HttpResponseMessage> DeliverAsync(
        Guid webhookId,
        byte[] body,
        string? signature = null,
        string? gitLabToken = null,
        string? deliveryId = null,
        HttpClient? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{webhookId}")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation(WebhookSignature.HubSignatureHeader, signature);
        }

        if (gitLabToken is not null)
        {
            request.Headers.TryAddWithoutValidation(WebhookSignature.GitLabTokenHeader, gitLabToken);
        }

        if (deliveryId is not null)
        {
            request.Headers.TryAddWithoutValidation("X-GitHub-Delivery", deliveryId);
        }

        return await (client ?? DeliveryClient).SendAsync(
            request,
            TestContext.Current.CancellationToken);
    }

    /// <summary>A correctly signed delivery with <paramref name="secret"/> (the test's own HMAC).</summary>
    public Task<HttpResponseMessage> DeliverSignedAsync(
        Guid webhookId,
        string body,
        string secret,
        string? deliveryId = null,
        HttpClient? client = null) =>
        DeliverAsync(
            webhookId,
            Encoding.UTF8.GetBytes(body),
            Sign(Encoding.UTF8.GetBytes(body), secret),
            deliveryId: deliveryId,
            client: client);

    /// <summary>The delivery endpoint's buffer/refusal counters (review m1).</summary>
    public WebhookDeliveryTelemetry Telemetry => Host.Services.GetRequiredService<WebhookDeliveryTelemetry>();

    /// <summary>
    /// Runs one delivery through the scoped <see cref="WebhookDeliveryService"/> directly - no HTTP, no
    /// handler, no request pipeline. The endpoint has already spent the budget by the time it calls the
    /// service, so this seam skips the limiter on purpose; it exists for the deterministic freshness test
    /// (review m2), which must not depend on a race.
    /// </summary>
    public Task<WebhookDeliveryResult> HandleDeliveryInServiceAsync(
        Guid webhookId,
        byte[] body,
        string? secret = null,
        string? deliveryId = null) =>
        Host.InScopeAsync(services => services.GetRequiredService<WebhookDeliveryService>().HandleAsync(
            webhookId,
            secret is null ? null : Sign(body, secret),
            null,
            deliveryId,
            body,
            TestContext.Current.CancellationToken));

    /// <summary>
    /// Inserts a replay record directly, so a test can place one in the future or the distant past without
    /// waiting on a clock or a race.
    /// </summary>
    public Task InsertDeliveryAsync(
        Guid webhookId,
        string dedupeKey,
        DateTimeOffset receivedAt,
        string outcome = "queued",
        Guid? runId = null) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            context.Set<WebhookDelivery>().Add(new WebhookDelivery
            {
                OrganizationId = OrganizationId,
                WebhookId = webhookId,
                DedupeKey = dedupeKey,
                Outcome = outcome,
                RunId = runId,
                ReceivedAt = receivedAt,
            });
            await context.SaveChangesAsync();
            return true;
        });

    /// <summary>The GitHub-compatible signature of <paramref name="body"/>, computed independently.</summary>
    public static string Sign(byte[] body, string secret) =>
        WebhookSignature.HubSignaturePrefix
        + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    // ------------------------------------------------------------------
    // Runs and the worker API
    // ------------------------------------------------------------------

    public Task<List<BuildRun>> ReadRunsAsync() =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            return await context.SystemQueryAsync<BuildRun>(run => run.ProjectId == ProjectId);
        });

    public Task<BuildRun> ReadRunAsync(Guid runId) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            return (await context.SystemQueryAsync<BuildRun>(run => run.Id == runId)).Single();
        });

    /// <summary>
    /// Registers a worker with a directly created token (the product's authentication path, no form):
    /// the token row is what the claim endpoint resolves. Returns the plaintext token.
    /// </summary>
    public Task<string> CreateWorkerAsync(
        string name = "webhook-agent",
        string tags = "ci",
        bool canProbe = true) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            var worker = new Zenkins.Server.Data.Entities.Worker
            {
                OrganizationId = OrganizationId,
                Name = name,
                Os = "Linux",
                Architecture = "X64",
                Version = "1.0.0",
                Tags = tags,
                CpuCount = 4,
                MaxConcurrency = 8,
                Status = WorkerStatus.Online,
                CanProbe = canProbe,
            };
            context.Workers.Add(worker);

            var token = WorkerTokenCrypto.Generate(WorkerTokenCrypto.WorkerPrefix);
            context.WorkerTokens.Add(new WorkerToken
            {
                OrganizationId = OrganizationId,
                Kind = WorkerTokenKind.Worker,
                WorkerId = worker.Id,
                TokenHash = WorkerTokenCrypto.Hash(token),
            });
            await context.SaveChangesAsync();
            return token;
        });

    /// <summary>Claims one run as the worker (the real claim endpoint).</summary>
    public Task<HttpResponseMessage> ClaimAsync(string workerToken) =>
        WorkerTestHost.SendWorkerJsonAsync(DeliveryClient, workerToken, "/api/workers/claim", new { });

    // ------------------------------------------------------------------
    // Reads through the product's data layer / assertions
    // ------------------------------------------------------------------

    public Task<T> InSystemScopeAsync<T>(Func<ZenkinsDbContext, Task<T>> action) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            return await action(context);
        });

    public async Task<Guid> ReadOrganizationIdAsync(string name) =>
        await Host.InScopeAsync(async services =>
        {
            await using var context = await CreateContextAsync(services);
            return (await context.Organizations.SingleAsync(organization => organization.Name == name)).Id;
        });

    public Task<User> ReadUserAsync(string email) =>
        Host.InScopeAsync(async services =>
        {
            await using var context = await CreateContextAsync(services);
            return await context.Users.SingleAsync(user => user.Email == email);
        });

    public Task<Guid> AddProjectAsync(
        Guid organizationId,
        string name,
        string repositoryUrl,
        string defaultBranch,
        string branchDiscovery = Zenkins.Shared.Projects.BranchDiscoveryModes.Single) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            var project = new Project
            {
                OrganizationId = organizationId,
                Name = name,
                RepositoryUrl = repositoryUrl,
                DefaultBranch = defaultBranch,
                BranchDiscovery = branchDiscovery,
            };
            context.Projects.Add(project);
            await context.SaveChangesAsync();
            return project.Id;
        });

    // ------------------------------------------------------------------
    // F-023: branch discovery
    // ------------------------------------------------------------------

    /// <summary>Switches one project's discovery mode through the row (the form path is a separate fact).</summary>
    public Task SetBranchDiscoveryAsync(Guid projectId, string mode) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            var project = (await context.SystemQueryAsync<Project>(row => row.Id == projectId)).Single();
            project.BranchDiscovery = mode;
            await context.SaveChangesAsync();
            return true;
        });

    /// <summary>
    /// Runs an action with the active tenant selected, so a scoped service that reads through the
    /// query filter (BranchService, RunService) sees the seeded organization's rows - the same
    /// selection a signed-in request makes.
    /// </summary>
    public Task<T> WithTenantAsync<T>(Func<IServiceProvider, Task<T>> action) =>
        Host.InScopeAsync(async services =>
        {
            services.GetRequiredService<TenantContext>().OrganizationId = OrganizationId;
            return await action(services);
        });

    /// <summary>Every branch job of the seeded project, read across tenants like the other readers.</summary>
    public Task<List<BranchJob>> ReadBranchesAsync(Guid? projectId = null) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();
            var target = projectId ?? ProjectId;
            return await context.SystemQueryAsync<BranchJob>(row => row.ProjectId == target);
        });

    /// <summary>
    /// Seeds one branch job with a known verdict (the discovery-observation seam). The evidence commit is
    /// carried with the state, exactly as a worker's answer would carry it.
    /// </summary>
    public Task<Guid> SeedBranchAsync(
        string branch,
        string state = BranchPipelineStates.Unknown,
        string? commit = null,
        DateTimeOffset? orphanedAt = null,
        string? orphanedEvidence = null,
        Guid? projectId = null,
        string? pendingCommit = null,
        DateTimeOffset? pendingSince = null,
        int observationAttempts = 0,
        string pendingTags = "") =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            var row = new BranchJob
            {
                OrganizationId = OrganizationId,
                ProjectId = projectId ?? ProjectId,
                Branch = branch,
                PipelineState = state,
                LastCommit = commit,
                ObservedCommit = state == BranchPipelineStates.Unknown ? null : commit,
                OrphanedAt = orphanedAt,
                OrphanedEvidence = orphanedEvidence,
                PendingCommit = pendingCommit,
                PendingSince = pendingSince,
                ObservationAttempts = observationAttempts,
                PendingTags = pendingTags,
            };
            context.BranchJobs.Add(row);
            await context.SaveChangesAsync();
            return row.Id;
        });

    public Task<Guid> AddOrganizationAsync(
        string name,
        params (string Email, OrganizationRole Role)[] members) =>
        Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            var organization = new Organization { Name = name };
            context.Organizations.Add(organization);

            foreach (var (email, role) in members)
            {
                var user = await context.Users.SingleAsync(candidate => candidate.Email == email);
                context.OrganizationMembers.Add(new OrganizationMember
                {
                    OrganizationId = organization.Id,
                    UserId = user.Id,
                    Role = role,
                });
            }

            await context.SaveChangesAsync();
            return organization.Id;
        });

    /// <summary>Every log line this host captured, for the "never logged" assertions.</summary>
    public IReadOnlyList<string> LogLines => Logs.Lines;

    /// <summary>Reads the show-once document's secret.</summary>
    public static string ExtractSecret(string html)
    {
        var match = Regex.Match(
            html,
            "id=\"webhook-secret-plaintext\">(?<secret>whsec_[A-Za-z0-9_-]+)<",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"The response did not contain a one-time webhook secret. Body: {Truncate(html)}");
        }

        return match.Groups["secret"].Value;
    }

    /// <summary>Reads the webhook id the show-once document names.</summary>
    public static Guid ExtractWebhookId(string html)
    {
        var match = Regex.Match(
            html,
            "/webhooks/(?<id>[0-9a-fA-F-]{36})",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"The response did not name a webhook id. Body: {Truncate(html)}");
        }

        return Guid.Parse(match.Groups["id"].Value);
    }

    private static string Truncate(string text) => text[..Math.Min(text.Length, 400)];

    private HttpClient Track(HttpClient client)
    {
        _clients.Add(client);
        return client;
    }

    private static Task<ZenkinsDbContext> CreateContextAsync(IServiceProvider services) =>
        services.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>().CreateDbContextAsync();

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        Host.Dispose();
    }
}

/// <summary>
/// Captures every log line the host writes. Registered as an <see cref="ILoggerProvider"/> in the test
/// host's DI, so "the plaintext never reaches the log" is asserted against the real logging pipeline
/// instead of against a code review.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(string line) => _lines.Enqueue(line);

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var message = formatter(state, exception);
            owner.Add(exception is null ? $"{category}: {message}" : $"{category}: {message} {exception}");
        }
    }
}
