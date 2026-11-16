using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Runs;
using Zenkins.Tests.Tenancy;

namespace Zenkins.Tests.Api;

/// <summary>
/// F-027's HTTP test host: the real production pipeline through <see cref="RunTestHost"/> (projects, the
/// trigger form, the worker API) plus everything the API tests need - minting a token through the real
/// Owner form, a bearer client, trigger calls, and cross-tenant reads of the run/token tables.
/// </summary>
public sealed class ApiV1TestHost : IDisposable
{
    private ApiV1TestHost(RunTestHost inner) => Inner = inner;

    public RunTestHost Inner { get; }

    public static Task<ApiV1TestHost> CreateAsync(
        Action<IConfigurationBuilder>? configureAppConfiguration = null,
        Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configureTestServices = null) =>
        CreateCoreAsync(RunTestHost.CreateAsync(configureAppConfiguration, configureTestServices));

    private static async Task<ApiV1TestHost> CreateCoreAsync(Task<RunTestHost> inner) =>
        new(await inner);

    /// <summary>Signs in the starter organization's Owner.</summary>
    public Task<HttpClient> SignInOwnerAsync() => Inner.Inner.SignInOwnerAsync();

    /// <summary>Creates a project through the real form and returns (id, owner client).</summary>
    public Task<(Guid ProjectId, HttpClient Owner)> CreateProjectAsync(string name = "game-client") =>
        Inner.CreateProjectAsync(name);

    /// <summary>A client whose requests carry the given bearer token (and no cookie jar).</summary>
    public HttpClient CreateApiClient(string token)
    {
        var client = Inner.Host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// Mints a token through the real Owner form and reads the one-time plaintext from the standalone
    /// document (the only place it exists).
    /// </summary>
    public async Task<(string Plaintext, HttpResponseMessage Response)> MintTokenAsync(
        HttpClient ownerClient,
        string name = "deploy pipeline",
        bool trigger = true,
        bool read = true,
        bool cancel = true)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["Name"] = name };
        if (trigger)
        {
            fields["Trigger"] = "true";
        }

        if (read)
        {
            fields["Read"] = "true";
        }

        if (cancel)
        {
            fields["Cancel"] = "true";
        }

        var response = await OrganizationTestHost.PostFormAsync(
            ownerClient, "/api-tokens", "/api-tokens/create", fields);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (TenantIsolationAuditProbes.ExtractCodeContent(body, "api-token-plaintext"), response);
    }

    /// <summary>Mints a token and returns only its plaintext (asserting the one-time document rendered).</summary>
    public async Task<string> MintTokenPlaintextAsync(
        HttpClient ownerClient,
        string name = "deploy pipeline",
        bool trigger = true,
        bool read = true,
        bool cancel = true)
    {
        var (plaintext, response) = await MintTokenAsync(ownerClient, name, trigger, read, cancel);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return plaintext;
    }

    public static Task<HttpResponseMessage> TriggerAsync(
        HttpClient client,
        Guid projectId,
        object? body = null,
        string? idempotencyKey = null)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/projects/{projectId}/runs")
        {
            Content = JsonContent.Create(body ?? new { }),
        };
        if (idempotencyKey is not null)
        {
            message.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return client.SendAsync(message, TestContext.Current.CancellationToken);
    }

    public static Task<HttpResponseMessage> GetAsync(HttpClient client, string url) =>
        client.GetAsync(url, TestContext.Current.CancellationToken);

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    /// <summary>The run ids of one project, read across tenants (the assertion side).</summary>
    public Task<List<BuildRun>> ReadProjectRunsAsync(Guid projectId) =>
        Inner.InSystemScopeAsync(context => context.SystemQueryAsync<BuildRun>(run => run.ProjectId == projectId));

    /// <summary>Sets the project's parameter definitions directly (the editor's stored JSON).</summary>
    public Task SetParameterDefinitionsAsync(Guid projectId, string parametersJson) =>
        Inner.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId)).Single();
            project.ParametersJson = parametersJson;
            await context.SaveChangesAsync();
            return true;
        });

    /// <summary>Appends log lines to a run (the worker's ingest writes the same rows).</summary>
    public Task AddLogLinesAsync(Guid runId, params string[] lines) =>
        Inner.InSystemScopeAsync(async context =>
        {
            var run = (await context.SystemQueryAsync<BuildRun>(candidate => candidate.Id == runId)).Single();
            var next = 1L;
            foreach (var line in lines)
            {
                context.BuildLogs.Add(new BuildLog
                {
                    OrganizationId = run.OrganizationId,
                    RunId = runId,
                    Seq = next++,
                    Stream = Zenkins.Shared.LogStreaming.LogStream.StdOut,
                    Message = line,
                    Timestamp = DateTimeOffset.UtcNow,
                });
            }

            await context.SaveChangesAsync();
            return true;
        });

    /// <summary>Reads one API token row across tenants.</summary>
    public Task<ApiToken> ReadApiTokenAsync(Guid tokenId) =>
        Inner.InSystemScopeAsync(async context =>
            (await context.SystemQueryAsync<ApiToken>(token => token.Id == tokenId)).Single());

    public Task<List<ApiToken>> ReadApiTokensAsync() =>
        Inner.InSystemScopeAsync(context => context.SystemQueryAsync<ApiToken>(token => true));

    /// <summary>Card B-008: every idempotency record, read across tenants (the assertion side).</summary>
    public Task<List<ApiIdempotencyRecord>> ReadIdempotencyRecordsAsync() =>
        Inner.InSystemScopeAsync(context => context.SystemQueryAsync<ApiIdempotencyRecord>(record => true));

    /// <summary>
    /// Card B-008: ages one key past its retention - the deterministic stand-in for waiting a day
    /// (<see cref="ApiIdempotencyRecord.Retention"/> is 24 hours; the clock is not).
    /// </summary>
    public Task AgeIdempotencyRecordAsync(string key, TimeSpan age) =>
        Inner.InSystemScopeAsync(async context =>
        {
            var record = (await context.SystemQueryAsync<ApiIdempotencyRecord>(candidate => candidate.Key == key))
                .Single();
            record.CreatedAt = DateTimeOffset.UtcNow - age;
            record.ExpiresAt = record.CreatedAt + ApiIdempotencyRecord.Retention;
            await context.SaveChangesAsync();
            return true;
        });

    public void Dispose() => Inner.Dispose();
}
