using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Auth;

// The worker project's namespace is Zenkins.Worker, which shadows the entity type name inside any
// Zenkins.* namespace; the alias keeps the entity readable here.
using WorkerEntity = Zenkins.Server.Data.Entities.Worker;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// The real-path credential helpers of card Q-006's worker-API probes: registration tokens minted
/// through the Owner's form, workers registered through the token-authenticated API, claims and
/// reports driven through the same endpoints the product's worker binary uses. Nothing here writes
/// rows directly - every state the probes assert against was produced by the surface under audit.
/// </summary>
public static class TenantIsolationWorkerApiProbes
{
    private static int _registrationCounter;

    /// <summary>
    /// Registers one worker for org A through the real API (Owner-minted registration token). A
    /// unique name per call: re-registering an existing name would revoke the earlier worker's
    /// token, which live probes of this class still hold.
    /// </summary>
    public static async Task<(Guid WorkerId, string WorkerToken)> RegisterAlphaWorkerAsync(
        TenantProbeHostFixture host)
    {
        using var alice = await TenantIsolationAuditProbes.SignInAsync(
            host, TenantIsolationProbeAssertions.OrgAOwnerEmail);
        var registrationToken = await TenantIsolationAuditProbes.MintRegistrationTokenAsync(alice);
        return await TenantIsolationAuditProbes.RegisterWorkerAsync(
            host, registrationToken, $"alpha-audit-agent-{Interlocked.Increment(ref _registrationCounter)}");
    }

    /// <summary>Registers one worker for org B through the real API (Owner-minted registration token).</summary>
    public static async Task<(Guid WorkerId, string WorkerToken)> RegisterBetaWorkerAsync(
        TenantProbeHostFixture host)
    {
        using var carol = await TenantIsolationAuditProbes.SignInAsync(
            host, TenantIsolationProbeAssertions.OrgBOwnerEmail);
        var registrationToken = await TenantIsolationAuditProbes.MintRegistrationTokenAsync(carol);
        return await TenantIsolationAuditProbes.RegisterWorkerAsync(
            host, registrationToken, $"beta-audit-agent-{Interlocked.Increment(ref _registrationCounter)}");
    }

    /// <summary>Claims and accepts one run with the given worker's client (the probe's own setup step).</summary>
    public static async Task ClaimAndAcceptAsync(HttpClient workerClient, Guid expectedRunId)
    {
        var (statusCode, claimedRunId, _, _) = await ClaimAsync(workerClient);
        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Equal(expectedRunId, claimedRunId);
        await AssertAcceptedAsync(workerClient, expectedRunId);
    }

    /// <summary>One claim call; returns the claimed run id and the org fields it presented (null for 204).</summary>
    public static async Task<(HttpStatusCode Status, Guid? RunId, Guid? ProjectId, string? RepositoryUrl)> ClaimAsync(
        HttpClient workerClient)
    {
        using var response = await workerClient.PostAsync("/api/workers/claim", null, TestContext.Current.CancellationToken);
        if ((int)response.StatusCode == 204)
        {
            return (HttpStatusCode.NoContent, null, null, null);
        }

        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (
            response.StatusCode,
            payload.RootElement.GetProperty("runId").GetGuid(),
            payload.RootElement.GetProperty("projectId").GetGuid(),
            payload.RootElement.GetProperty("repositoryUrl").GetString());
    }

    /// <summary>Posts the accepted report and asserts the run went Running.</summary>
    public static async Task AssertAcceptedAsync(HttpClient workerClient, Guid runId)
    {
        using var response = await workerClient.PostAsync(
            $"/api/workers/runs/{runId}/accepted",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>One heartbeat; the parsed cancel channel (the F-009 fallback).</summary>
    public static async Task<(HttpStatusCode Status, List<(Guid runId, string reason)> cancelRequestedRuns)>
        HeartbeatAsync(HttpClient workerClient)
    {
        using var response = await workerClient.PostAsJsonAsync("/api/workers/heartbeat", new
        {
            os = "Linux",
            architecture = "X64",
            version = "1.0.0",
            tags = "dotnet",
            cpuCount = 4,
            maxConcurrency = 1,
        }, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var payload = JsonDocument.Parse(body);
        var cancels = payload.RootElement.GetProperty("cancelRequestedRuns")
            .EnumerateArray()
            .Select(entry => (entry.GetProperty("runId").GetGuid(), entry.GetProperty("reason").GetString() ?? ""))
            .ToList();
        return (response.StatusCode, cancels);
    }

    /// <summary>One run-scoped secret fetch; the value only for a 200.</summary>
    public static async Task<(HttpStatusCode Status, string Value)> FetchRunSecretAsync(
        HttpClient workerClient, Guid runId, string name)
    {
        using var response = await workerClient.PostAsync(
            $"/api/workers/runs/{runId}/secrets",
            new StringContent($$"""{"name":"{{name}}"}""", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        if ((int)response.StatusCode != 200)
        {
            return (response.StatusCode, string.Empty);
        }

        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (HttpStatusCode.OK, payload.RootElement.GetProperty("value").GetString() ?? "");
    }

    /// <summary>
    /// One run-scoped parameter-file fetch (card F-041); the raw bytes only for a 200. The probe reads them as a
    /// byte array on purpose: the route answers octets, and the contract it ships is the one the worker decodes.
    /// </summary>
    public static async Task<(HttpStatusCode Status, byte[] Bytes)> FetchRunParameterFileAsync(
        HttpClient workerClient, Guid runId, string name)
    {
        using var response = await workerClient.PostAsync(
            $"/api/workers/runs/{runId}/parameter-files",
            new StringContent($$"""{"name":"{{name}}"}""", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        if ((int)response.StatusCode != 200)
        {
            return (response.StatusCode, []);
        }

        return (
            HttpStatusCode.OK,
            await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>One run-scoped Git credential fetch; the material JSON only for a 200.</summary>
    public static async Task<(HttpStatusCode Status, JsonElement Material)> FetchGitCredentialAsync(
        HttpClient workerClient, Guid runId)
    {
        using var response = await workerClient.GetAsync(
            $"/api/workers/runs/{runId}/git-credential", TestContext.Current.CancellationToken);
        if ((int)response.StatusCode != 200)
        {
            return (response.StatusCode, default);
        }

        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (HttpStatusCode.OK, payload.RootElement.Clone());
    }
}

/// <summary>
/// Card Q-006's probes of the F-013 credential delivery: the ciphertext decrypts only for the
