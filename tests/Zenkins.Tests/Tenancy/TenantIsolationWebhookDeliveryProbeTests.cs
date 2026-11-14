using System.Net;
using System.Text.Json;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Webhooks;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card Q-006's probes of the one anonymous, signature-authenticated surface: the webhook delivery
/// endpoint. A git host has no session, so the signature is the whole credential - the probes prove
/// that org A's secret cannot deliver to org B's webhook, that B's own secret still works (the
/// signature path itself), and that a disabled id is refused exactly like an unknown one. Both
/// secrets exist as real ciphertext here: each organization rotates its seeded webhook through the
/// Owner's settings form first, so the signature comparisons are the product's own.
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationWebhookDeliveryProbeTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_delivery_signed_with_another_organizations_secret_is_refused_and_queues_nothing()
    {
        using var host = new TenantProbeHostFixture();
        using var alice = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);
        using var carol = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgBOwnerEmail);
        var alphaSecret = await TenantIsolationAuditProbes.RotateWebhookSecretAsync(
            alice, TwoOrgsFixture.ProjectAId, TwoOrgsFixture.WebhookAId);
        _ = await TenantIsolationAuditProbes.RotateWebhookSecretAsync(
            carol, TwoOrgsFixture.ProjectBId, TwoOrgsFixture.WebhookBId);

        var payload = WebhookPayloads.GitHub("main", WebhookPayloads.Commit('a'));
        using var client = host.CreateClient();

        // Org A's secret, org B's webhook id: refused without a body, and org B queues nothing.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{TwoOrgsFixture.WebhookBId}")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(
            Zenkins.Server.Webhooks.WebhookSignature.HubSignatureHeader,
            TenantIsolationAuditProbes.ComputeHubSignature(alphaSecret, payload));
        using var response = await client.SendAsync(request, CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(CancellationToken));

        var betaRuns = await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<BuildRun>(
            host, run => run.OrganizationId == TwoOrgsFixture.OrgBId);
        Assert.Single(betaRuns); // the seeded run only - the refused delivery created no run
        var betaDeliveries = await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<WebhookDelivery>(
            host, delivery => delivery.WebhookId == TwoOrgsFixture.WebhookBId);
        Assert.Single(betaDeliveries); // the seeded record only - the refused delivery wrote nothing
    }

    [Fact]
    public async Task A_delivery_signed_with_the_webhooks_own_secret_queues_a_run_in_its_organization()
    {
        using var host = new TenantProbeHostFixture();
        // Positive control for the signature path itself: org B's secret on org B's webhook queues
        // a run in org B - proving the refusals elsewhere are the tenant boundary, not a broken feature.
        using var carol = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgBOwnerEmail);
        var betaSecret = await TenantIsolationAuditProbes.RotateWebhookSecretAsync(
            carol, TwoOrgsFixture.ProjectBId, TwoOrgsFixture.WebhookBId);

        var payload = WebhookPayloads.GitHub(TwoOrgsFixture.ProjectBBranch, WebhookPayloads.Commit('b'));
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{TwoOrgsFixture.WebhookBId}")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(
            Zenkins.Server.Webhooks.WebhookSignature.HubSignatureHeader,
            TenantIsolationAuditProbes.ComputeHubSignature(betaSecret, payload));
        using var response = await client.SendAsync(request, CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("queued", json.RootElement.GetProperty("status").GetString());

        var betaRuns = await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<BuildRun>(
            host, run => run.OrganizationId == TwoOrgsFixture.OrgBId);
        Assert.Equal(2, betaRuns.Count); // the seeded run + the webhook's
        Assert.Contains(betaRuns, run => run.Id != TwoOrgsFixture.BuildRunBId
            && run.ProjectId == TwoOrgsFixture.ProjectBId);
    }

    [Fact]
    public async Task A_disabled_webhook_id_answers_the_same_bare_404_as_an_unknown_one()
    {
        using var host = new TenantProbeHostFixture();
        using var carol = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgBOwnerEmail);
        var betaSecret = await TenantIsolationAuditProbes.RotateWebhookSecretAsync(
            carol, TwoOrgsFixture.ProjectBId, TwoOrgsFixture.WebhookBId);

        // Disable org B's webhook, then deliver with a VALID signature: a disabled id is refused
        // exactly like an unknown one - 404, no body - and no delivery record is written.
        var disable = await OrganizationTestHost.PostFormAsync(
            carol,
            $"/projects/{TwoOrgsFixture.ProjectBId}/settings",
            $"/projects/{TwoOrgsFixture.ProjectBId}/webhooks/{TwoOrgsFixture.WebhookBId}/disable",
            new Dictionary<string, string>());
        MembershipAuthorizationProbes.AssertRedirect(
            disable, $"/projects/{TwoOrgsFixture.ProjectBId}/settings?notice=webhook-disabled");

        var payload = WebhookPayloads.GitHub(TwoOrgsFixture.ProjectBBranch, WebhookPayloads.Commit('c'));
        using var client = host.CreateClient();
        foreach (var webhookId in new[] { TwoOrgsFixture.WebhookBId, Guid.NewGuid() })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{webhookId}")
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Add(
                Zenkins.Server.Webhooks.WebhookSignature.HubSignatureHeader,
                TenantIsolationAuditProbes.ComputeHubSignature(betaSecret, payload));
            using var response = await client.SendAsync(request, CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(CancellationToken));
        }
    }
}
