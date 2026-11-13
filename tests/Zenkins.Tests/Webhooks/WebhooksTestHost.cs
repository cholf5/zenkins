using System.Collections.Concurrent;
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
    /// Creates a webhook through the real settings form and returns the one-time plaintext from the
    /// show-once document, plus the id the document names.
    /// </summary>
    public async Task<(Guid WebhookId, string Secret, string Html, HttpResponseMessage Response)> CreateWebhookAsync(
        HttpClient client,
        string name,
        string branchFilter = "",
        string requiredTags = "")
    {
        var response = await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{ProjectId}",
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
    public Task<string> CreateWorkerAsync(string name = "webhook-agent", string tags = "ci") =>
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
        string defaultBranch) =>
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
            };
            context.Projects.Add(project);
            await context.SaveChangesAsync();
            return project.Id;
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
