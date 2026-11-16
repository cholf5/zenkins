using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Webhooks;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Webhooks;

/// <summary>
/// Cards F-028 + F-029 on the webhook path. A push to a disabled project - or to one whose queue is at its
/// cap - must be recorded honestly and answered <c>2xx</c>: a permanent <c>4xx</c> makes most git hosts
/// disable the webhook automatically, which would silently break the "re-enable it and the trigger works
/// again" promise. The refusal is the delivery's outcome, not an error, and no run row is created.
/// </summary>
[Trait("Category", "Webhooks")]
public sealed class WebhookProjectGateTests
{
    [Fact]
    public async Task A_disabled_project_records_project_disabled_and_answers_accepted()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var webhookId = await host.SeedWebhookAsync("disabled-hook");

        var disable = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{host.ProjectId}/settings",
            $"/projects/{host.ProjectId}/disable",
            new Dictionary<string, string> { ["Reason"] = "mid-migration" });
        Assert.Equal(HttpStatusCode.Redirect, disable.StatusCode);

        using var response = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub(WebhooksTestHost.DefaultBranch, WebhookPayloads.Commit('a')),
            host.SecretFor("disabled-hook"),
            deliveryId: "disabled-project-delivery");

        // 2xx on purpose (the card's decision): the git host must not disable the webhook.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(WebhookOutcomes.ProjectDisabled, body, StringComparison.Ordinal);

        var delivery = Assert.Single(await host.ReadDeliveriesAsync(webhookId));
        Assert.Equal(WebhookOutcomes.ProjectDisabled, delivery.Outcome);
        Assert.Null(delivery.RunId);
        Assert.Empty(await host.ReadRunsAsync());

        // The webhook's own "last delivery" line says the same thing.
        var webhook = await host.ReadWebhookAsync(webhookId);
        Assert.Equal(WebhookOutcomes.ProjectDisabled, webhook!.LastDeliveryOutcome);

        // Re-enabling restores the trigger: the next distinct push queues exactly one run.
        var enable = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{host.ProjectId}/settings",
            $"/projects/{host.ProjectId}/enable",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, enable.StatusCode);

        using var afterEnable = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub(WebhooksTestHost.DefaultBranch, WebhookPayloads.Commit('b')),
            host.SecretFor("disabled-hook"),
            deliveryId: "after-enable-delivery");
        Assert.Equal(HttpStatusCode.Accepted, afterEnable.StatusCode);

        var run = Assert.Single(await host.ReadRunsAsync());
        Assert.Equal(WebhookPayloads.Commit('b'), run.GitCommit);

        // Both deliveries are on record, and the refused one stayed refused.
        var deliveries = await host.ReadDeliveriesAsync(webhookId);
        Assert.Equal(2, deliveries.Count);
        Assert.Single(deliveries, delivery => delivery.Outcome == WebhookOutcomes.ProjectDisabled);
        Assert.Single(deliveries, delivery => delivery.Outcome == WebhookOutcomes.Queued);
    }

    [Fact]
    public async Task A_full_queue_records_queue_full_and_queues_nothing()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var webhookId = await host.SeedWebhookAsync("full-queue-hook");

        var saved = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{host.ProjectId}/settings",
            $"/projects/{host.ProjectId}/limits",
            new Dictionary<string, string>
            {
                ["MaxConcurrentRuns"] = string.Empty,
                ["QuietPeriodSeconds"] = string.Empty,
                ["MaxQueuedRuns"] = "1",
            });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        using var first = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub(WebhooksTestHost.DefaultBranch, WebhookPayloads.Commit('c')),
            host.SecretFor("full-queue-hook"),
            deliveryId: "cap-delivery-1");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        using var second = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub(WebhooksTestHost.DefaultBranch, WebhookPayloads.Commit('d')),
            host.SecretFor("full-queue-hook"),
            deliveryId: "cap-delivery-2");
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Contains(
            WebhookOutcomes.QueueFull,
            await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        // One run, from the accepted delivery; the refused one produced no orphan row.
        var run = Assert.Single(await host.ReadRunsAsync());
        Assert.Equal(WebhookPayloads.Commit('c'), run.GitCommit);
        Assert.Equal(BuildRunStatus.Queued, run.Status);

        var deliveries = await host.ReadDeliveriesAsync(webhookId);
        Assert.Equal(2, deliveries.Count);
        Assert.Single(deliveries, delivery => delivery.Outcome == WebhookOutcomes.Queued);
        var refused = Assert.Single(deliveries, delivery => delivery.Outcome == WebhookOutcomes.QueueFull);
        Assert.Null(refused.RunId);
    }
}
