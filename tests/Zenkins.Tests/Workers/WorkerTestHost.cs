using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Tests.Organizations;

// The worker project's namespace is Zenkins.Worker, which shadows the entity type name inside any
// Zenkins.* namespace; the alias keeps the entity readable here.
using WorkerEntity = Zenkins.Server.Data.Entities.Worker;

namespace Zenkins.Tests.Workers;

/// <summary>
/// F-004's HTTP test host: everything <see cref="OrganizationTestHost"/> does, plus helpers for the
/// worker API (register/heartbeat with bearer tokens), the Owner token forms, and cross-tenant
/// reads of the worker tables through the product's system scope.
/// </summary>
public sealed class WorkerTestHost : IDisposable
{
    private WorkerTestHost(OrganizationTestHost inner) => Inner = inner;

    public OrganizationTestHost Inner { get; }

    public static Task<WorkerTestHost> CreateAsync() => CreateAsync(null);

    /// <summary>Optional extra configuration sources (F-006 uses them for the short lease window).</summary>
    public static Task<WorkerTestHost> CreateAsync(
        Action<IConfigurationBuilder>? configureAppConfiguration,
        Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configureTestServices = null) =>
        CreateCoreAsync(OrganizationTestHost.CreateAsync(configureAppConfiguration, configureTestServices));

    private static async Task<WorkerTestHost> CreateCoreAsync(Task<OrganizationTestHost> inner) =>
        new(await inner);

    /// <summary>Signs in the starter organization's Owner.</summary>
    public Task<HttpClient> SignInOwnerAsync() => Inner.SignInAsync(OrganizationTestHost.OwnerEmail);

    /// <summary>
    /// Mints a registration token through the real Owner form (antiforgery included) and returns
    /// the plaintext from the one-time display document.
    /// </summary>
    public static async Task<(string Plaintext, HttpResponseMessage Response)> MintRegistrationTokenAsync(
        HttpClient ownerClient)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            "/workers",
            "/workers/registration-tokens",
            new Dictionary<string, string>());
        var body = await response.Content.ReadAsStringAsync();

        return (ExtractRegistrationToken(body), response);
    }

    public static string ExtractRegistrationToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            ">(?<token>zreg_[A-Za-z0-9_-]{20,})<",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"The mint response did not contain a one-time registration token. Body: {html[..Math.Min(html.Length, 400)]}");
        }

        return match.Groups["token"].Value;
    }

    public static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client,
        string registrationToken,
        object payload) =>
        SendWorkerJsonAsync(client, registrationToken, "/api/workers/register", payload);

    public static Task<HttpResponseMessage> HeartbeatAsync(
        HttpClient client,
        string workerToken,
        object payload) =>
        SendWorkerJsonAsync(client, workerToken, "/api/workers/heartbeat", payload);

    public static Task<HttpResponseMessage> RegisterWithoutTokenAsync(HttpClient client, object payload) =>
        SendWorkerJsonAsync(client, null, "/api/workers/register", payload);

    public static Task<HttpResponseMessage> HeartbeatWithoutTokenAsync(HttpClient client, object payload) =>
        SendWorkerJsonAsync(client, null, "/api/workers/heartbeat", payload);

    /// <summary>A canonical registration payload for the tests. <c>tags</c> may be null (not sent).
    /// </summary>
    /// <param name="canProbe">
    /// Card I-005: the Git probe capability. <see langword="false"/> is what a pre-probe Worker sends; the
    /// server reads "absent" the same way, so the default here is the old-worker shape.
    /// </param>
    public static object RegistrationPayload(
        string name = "build-agent-1",
        string os = "Linux",
        string architecture = "X64",
        string version = "1.0.0",
        string? tags = "dotnet,ci",
        int cpuCount = 8,
        int maxConcurrency = 2,
        bool canProbe = false) => new
    {
        name,
        os,
        architecture,
        version,
        tags,
        cpuCount,
        maxConcurrency,
        canProbe,
    };

    /// <param name="canProbe">
    /// Card I-005: the refreshed probe capability. <see langword="null"/> omits it (an old Worker's
    /// heartbeat), which leaves the server's stored value alone.
    /// </param>
    public static object HeartbeatPayload(
        string? os = null,
        string? architecture = null,
        string? version = null,
        string? tags = null,
        int? cpuCount = null,
        int? maxConcurrency = null,
        bool? canProbe = null) => new
    {
        os,
        architecture,
        version,
        tags,
        cpuCount,
        maxConcurrency,
        canProbe,
    };

    /// <summary>Reads the JSON string property the worker API returns (camelCase).</summary>
    public static async Task<string> ReadStringPropertyAsync(HttpResponseMessage response, string property)
    {
        // Deliberately no stream disposal: the response content must stay readable for further reads.
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty(property).GetString()
            ?? throw new InvalidOperationException($"Property '{property}' was null.");
    }

    /// <summary>All workers of one organization, read across tenants for assertions.</summary>
    public async Task<IReadOnlyList<WorkerEntity>> ReadWorkersAsync(Guid organizationId) =>
        await Inner.Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            return (IReadOnlyList<WorkerEntity>)await context.SystemQueryAsync<WorkerEntity>(
                worker => worker.OrganizationId == organizationId);
        });

    public async Task<WorkerEntity> ReadWorkerAsync(Guid workerId) =>
        await Inner.Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            return (await context.SystemQueryAsync<WorkerEntity>(worker => worker.Id == workerId)).Single();
        });

    /// <summary>All worker tokens of one organization, read across tenants for assertions.</summary>
    public async Task<IReadOnlyList<WorkerToken>> ReadWorkerTokensAsync(Guid organizationId) =>
        await Inner.Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            return (IReadOnlyList<WorkerToken>)await context.SystemQueryAsync<WorkerToken>(
                token => token.OrganizationId == organizationId);
        });

    /// <summary>Every worker token in the database, across tenants (for "nothing was created").</summary>
    public async Task<IReadOnlyList<WorkerToken>> ReadAllWorkerTokensAsync() =>
        await Inner.Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            return (IReadOnlyList<WorkerToken>)await context.SystemQueryAsync<WorkerToken>(_ => true);
        });

    /// <summary>
    /// Revokes one worker token row directly - the same server-side effect a same-name
    /// re-registration has - so the dedicated revoked-heartbeat test does not depend on the
    /// re-registration path to reach a revoked worker token.
    /// </summary>
    public async Task RevokeWorkerTokenAsync(string plaintextWorkerToken)
    {
        var hash = Zenkins.Server.Workers.WorkerTokenCrypto.Hash(plaintextWorkerToken);
        await Inner.Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await CreateContextAsync(services);
            using var scope = systemDataScope.BeginScope();

            var token = (await context.SystemQueryAsync<WorkerToken>(
                candidate => candidate.TokenHash == hash)).Single();
            token.RevokedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
            return true;
        });
    }

    private static async Task<ZenkinsDbContext> CreateContextAsync(IServiceProvider services) =>
        await services.GetRequiredService<IDbContextFactory<ZenkinsDbContext>>().CreateDbContextAsync();

    internal static Task<HttpResponseMessage> SendWorkerJsonAsync(
        HttpClient client,
        string? bearerToken,
        string url,
        object payload)
    {
        // Deliberately not disposed here: disposing the request disposes its JSON content, and the
        // in-memory TestServer may still be reading it when the send completes.
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return client.SendAsync(request);
    }

    public void Dispose() => Inner.Dispose();
}
