using System.Net;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Webhooks;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Webhooks;

/// <summary>
/// Card F-020 on the webhook surface, carried as regression facts (card Q-012): a webhook triggers on a
/// git push, so a Freestyle project - which has no repository, no branch filter and no commit to pin -
/// must not be able to own one.
/// <para>
/// <b>Two layers, two facts.</b> The management path refuses the kind outright
/// (<see cref="WebhookService.CreateAsync"/>), and the delivery path is defence in depth: a delivery that
/// somehow reaches a Freestyle project's webhook is recorded as
/// <see cref="WebhookOutcomes.IgnoredNoRepository"/> and queues nothing. The second layer is unreachable
/// through the product (creation refuses the kind and the kind is immutable), which is why the fact
/// seeds the webhook row directly: the branch is the thing under test, and a refactor that deleted it as
/// dead code would otherwise be invisible.
/// </para>
/// </summary>
[Trait("Category", "Webhooks")]
public sealed class WebhookFreestyleGateTests
{
    [Fact]
    public async Task A_freestyle_project_cannot_be_given_a_webhook()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var freestyleId = await CreateFreestyleProjectAsync(owner, "freestyle-no-hooks");

        // The settings form is the product's own path: a Member names the webhook exactly as the card
        // renders, and the kind - not a missing field - is what refuses it.
        var response = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{freestyleId}/settings",
            $"/projects/{freestyleId}/webhooks/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = "never-fires",
                ["BranchFilter"] = string.Empty,
                ["RequiredTags"] = string.Empty,
            });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(
            "webhookError=project-not-pipeline",
            response.Headers.Location!.OriginalString,
            StringComparison.Ordinal);

        // Nothing was minted: a webhook row for that project would be a trigger that can never fire.
        Assert.Empty(await host.InSystemScopeAsync(context =>
            context.SystemQueryAsync<Webhook>(webhook => webhook.ProjectId == freestyleId)));
    }

    [Fact]
    public async Task A_delivery_to_a_freestyle_webhook_is_recorded_as_ignored_without_queueing_a_run()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var freestyleId = await CreateFreestyleProjectAsync(owner, "freestyle-delivery");

        // Seeded directly: creation refuses this kind, so the only way to reach the delivery path's own
        // Freestyle branch is to place the row it would have refused (the branch is the fact here).
        // The filter names the pushed branch on purpose, and the assertion names the outcome itself: a
        // delivery that lost the kind guard must not quietly land on the unrelated `ignored:branch`
        // (measured: with the guard deleted, this kind has no default branch and the delivery fails
        // outright), so the fact cannot stay green for the wrong reason.
        var webhookId = await host.SeedWebhookAsync(
            "freestyle-seeded",
            branchFilter: "main",
            projectId: freestyleId);
        var body = WebhookPayloads.GitHub("main", WebhookPayloads.Commit('c'));

        using var response = await host.DeliverSignedAsync(
            webhookId,
            body,
            host.SecretFor("freestyle-seeded"),
            deliveryId: "freestyle-delivery");

        // Accepted, not refused: an ignored delivery is a recorded fact, and the git host must not see a
        // 4xx (which would make it disable the webhook) for something the product decided to ignore.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains(
            WebhookOutcomes.IgnoredNoRepository,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        var delivery = Assert.Single(await host.ReadDeliveriesAsync(webhookId));
        Assert.Equal(WebhookOutcomes.IgnoredNoRepository, delivery.Outcome);
        Assert.Null(delivery.RunId);

        // And the honest record is on the webhook row too.
        Assert.Equal(
            WebhookOutcomes.IgnoredNoRepository,
            (await host.ReadWebhookAsync(webhookId))!.LastDeliveryOutcome);

        // Nothing was queued: there is no repository to clone, so there is no run to queue.
        Assert.Empty(await host.InSystemScopeAsync(context =>
            context.SystemQueryAsync<BuildRun>(run => run.ProjectId == freestyleId)));
    }

    /// <summary>Creates a Freestyle project through the real create form and returns its id.</summary>
    private static async Task<Guid> CreateFreestyleProjectAsync(HttpClient owner, string name)
    {
        var response = await OrganizationTestHost.PostFormAsync(
            owner,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = name,
                ["Kind"] = ProjectKinds.Freestyle,
            });
        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"Creating the freestyle project failed with {(int)response.StatusCode}.");
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/projects/", location, StringComparison.Ordinal);
        return Guid.Parse(location["/projects/".Length..].Split('?')[0]);
    }
}
