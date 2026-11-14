using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.LogStreaming;
using Zenkins.Shared.LogStreaming;
using Zenkins.Server.Webhooks;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// The shared helpers of card Q-006's audit probes: sign-in through the real login form, mint
/// registration tokens and worker tokens through the real API, create secrets/credentials with
/// real ciphertext (so the material-absence scans are meaningful), rotate webhook secrets to real
/// plaintext, mint subscription tickets through the real <see cref="RunLogAccess"/>, and build hub
/// connections routed through the in-memory TestServer. Nothing here bypasses a product path: every
/// credential the probes use was issued by the surface the audit is testing.
/// </summary>
public static class TenantIsolationAuditProbes
{
    /// <summary>Signs in through the real login form; the tenant then comes from the membership-derived selection.</summary>
    public static async Task<HttpClient> SignInAsync(TenantProbeHostFixture host, string email)
    {
        var client = host.CreateClient();
        var response = await AuthTestHost.LoginAsync(
            client,
            email,
            TwoOrgsFixture.UserPassword,
            returnUrl: "/projects");
        if ((int)response.StatusCode is not (302 or 303))
        {
            client.Dispose();
            throw new InvalidOperationException($"Signing in as '{email}' failed with {(int)response.StatusCode}.");
        }

        return client;
    }

    /// <summary>Mints a worker registration token as a signed-in Owner (the one 200 HTML answer).</summary>
    public static async Task<string> MintRegistrationTokenAsync(HttpClient ownerClient)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            ownerClient, "/workers", "/workers/registration-tokens", new Dictionary<string, string>());
        if ((int)response.StatusCode != 200)
        {
            throw new InvalidOperationException(
                $"Minting a registration token failed with {(int)response.StatusCode}.");
        }

        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return ExtractCodeContent(html, "registration-token-plaintext");
    }

    /// <summary>Registers a worker through the real token-authenticated API; returns the plaintext worker token.</summary>
    public static async Task<(Guid WorkerId, string WorkerToken)> RegisterWorkerAsync(
        TenantProbeHostFixture host, string registrationToken, string name)
    {
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", registrationToken);
        var response = await client.PostAsJsonAsync("/api/workers/register", new
        {
            name,
            os = "Linux",
            architecture = "X64",
            version = "1.0.0",
            tags = "dotnet",
            cpuCount = 4,
            maxConcurrency = 1,
        }, TestContext.Current.CancellationToken);
        if ((int)response.StatusCode != 200)
        {
            throw new InvalidOperationException(
                $"Registering worker '{name}' failed with {(int)response.StatusCode}.");
        }

        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (
            Guid.Parse(payload.RootElement.GetProperty("workerId").GetString()!),
            payload.RootElement.GetProperty("workerToken").GetString()!);
    }

    /// <summary>Creates one secret through the Owner form; the stored ciphertext is real (the factory's key ring).</summary>
    public static async Task CreateSecretAsync(HttpClient ownerClient, string name, string value)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            "/secrets",
            "/secrets/create",
            new Dictionary<string, string> { ["Name"] = name, ["Value"] = value });
        if ((int)response.StatusCode is not (302 or 303)
            || !response.Headers.Location!.OriginalString.Contains("notice=created", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Creating secret '{name}' failed with {(int)response.StatusCode}.");
        }
    }

    /// <summary>Creates one Git credential through the Owner form (real ciphertext); returns its id.</summary>
    public static async Task<Guid> CreateGitCredentialAsync(HttpClient ownerClient, string name, string kind, string value)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            "/secrets",
            "/git-credentials/create",
            new Dictionary<string, string> { ["Name"] = name, ["Kind"] = kind, ["Value"] = value });
        if ((int)response.StatusCode != 200)
        {
            throw new InvalidOperationException(
                $"Creating Git credential '{name}' failed with {(int)response.StatusCode}.");
        }

        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return payload.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Attaches a credential to a project <b>through the product's own form</b> (card F-018). Until F-018
    /// there was no such surface and this helper seeded the column through the system scope; the audit
    /// now exercises the route a person uses, so "the assignment is tenant-scoped" is a statement about
    /// the shipped code rather than about a test shortcut.
    /// </summary>
    public static async Task AttachProjectCredentialAsync(
        HttpClient ownerClient,
        Guid projectId,
        Guid credentialId)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            $"/projects/{projectId}/settings",
            $"/projects/{projectId}/git-credential/attach",
            new Dictionary<string, string> { ["GitCredentialId"] = credentialId.ToString() });
        if ((int)response.StatusCode is not (302 or 303))
        {
            throw new InvalidOperationException(
                $"Attaching credential '{credentialId}' to project '{projectId}' failed with "
                + $"{(int)response.StatusCode}.");
        }
    }

    /// <summary>Rotates a webhook's secret through the Owner form; returns the one-time plaintext.</summary>
    public static async Task<string> RotateWebhookSecretAsync(HttpClient ownerClient, Guid projectId, Guid webhookId)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            ownerClient,
            $"/projects/{projectId}/settings",
            $"/projects/{projectId}/webhooks/{webhookId}/rotate",
            new Dictionary<string, string>());
        if ((int)response.StatusCode != 200)
        {
            throw new InvalidOperationException(
                $"Rotating webhook '{webhookId}' failed with {(int)response.StatusCode}.");
        }

        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return ExtractCodeContent(html, "webhook-secret-plaintext");
    }

    /// <summary>Mints a run-log subscription ticket through the real <see cref="RunLogAccess"/>.</summary>
    public static async Task<string> MintRunLogTicketAsync(
        TenantProbeHostFixture host, string userId, Guid organizationId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var runLogAccess = scope.ServiceProvider.GetRequiredService<RunLogAccess>();
        return runLogAccess.MintTicket(userId, organizationId);
    }

    /// <summary>
    /// Builds a hub connection routed through the in-memory TestServer (long polling - TestServer
    /// has no WebSocket support), presenting the credential exactly like the product does.
    /// </summary>
    public static HubConnection BuildHubConnection(
        TenantProbeHostFixture host, string path, string? credential)
    {
        var handler = host.Factory.Server.CreateHandler();
        return new HubConnectionBuilder()
            .WithUrl(new Uri("http://localhost" + path), options =>
            {
                if (credential is not null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(credential);
                }

                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => handler;
            })
            .Build();
    }

    /// <summary>Reads a row set across every tenant through the product's system-scope escape hatch.</summary>
    public static Task<List<TEntity>> ReadAcrossTenantsAsync<TEntity>(
        TenantProbeHostFixture host,
        System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate)
        where TEntity : class =>
        host.Data.ReadAcrossTenantsAsync(predicate, TestContext.Current.CancellationToken);

    /// <summary>Extracts the text of a <c>&lt;code id="…"&gt;</code> element (the one-time secrets).</summary>
    public static string ExtractCodeContent(string html, string elementId)
    {
        var match = Regex.Match(
            html,
            $"<code id=\"{elementId}\">(?<value>[^<]+)</code>",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidOperationException($"The page did not render the '{elementId}' element.");
        }

        return match.Groups["value"].Value;
    }

    /// <summary>Builds the GitHub/Gitea HMAC header value of one delivery (the product's own scheme).</summary>
    public static string ComputeHubSignature(string secret, string body) =>
        WebhookSignature.HubSignaturePrefix
        + Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(secret),
            System.Text.Encoding.UTF8.GetBytes(body)));
}

/// <summary>
/// Collects the lines one browser-hub subscriber receives (the same shape the F-008 tests use),
/// split by channel so a probe can assert history and live lines separately.
/// </summary>
public sealed class AuditLogCollector
{
    private readonly object _gate = new();
    private readonly List<LogLineMessage> _history = [];
    private readonly List<LogLineMessage> _live = [];

    public void OnHistory(IReadOnlyList<LogLineMessage> lines) => Add(_history, lines);

    public void OnReceive(IReadOnlyList<LogLineMessage> lines) => Add(_live, lines);

    public List<LogLineMessage> Snapshot()
    {
        lock (_gate)
        {
            return [.. _history, .. _live];
        }
    }

    private void Add(List<LogLineMessage> channel, IReadOnlyList<LogLineMessage> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            channel.AddRange(lines);
        }
    }
}
