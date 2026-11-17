using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Webhooks;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Webhooks;

/// <summary>
/// Card F-022 on the webhook surface: an inline Pipeline has no repository, so a webhook (which triggers
/// on git pushes) must be refused for it exactly like a Freestyle project. The guard is the one shared
/// predicate <see cref="ProjectSources.HasRepository"/> - this fact exists so a future refactor cannot
/// quietly turn it back into a per-source condition.
/// <para>
/// Two layers, two facts, copied from the F-020 shape (Q-012): creation refuses the source, and a
/// delivery that somehow reached a seeded webhook is recorded <c>ignored:no-repository</c> through the
/// real signed endpoint with no run queued. The second layer is unreachable through the product, which
/// is why the row is seeded.
/// </para>
/// </summary>
[Trait("Category", "Webhooks")]
public sealed class WebhookInlineGateTests
{
    private const string Script = """
        pipeline {
          name = "inline-hook",
          steps = { { name = "Only", run = { "echo", "hi" } } },
        }
        """;

    [Fact]
    public async Task An_inline_project_cannot_be_given_a_webhook()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var inlineId = await CreateInlineProjectAsync(owner, "inline-no-hooks");

        var response = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{inlineId}/settings",
            $"/projects/{inlineId}/webhooks/create",
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
        Assert.Empty(await host.InSystemScopeAsync(context =>
            context.SystemQueryAsync<Webhook>(webhook => webhook.ProjectId == inlineId)));
    }

    [Fact]
    public async Task A_delivery_to_a_seeded_inline_webhook_is_recorded_as_ignored_without_queueing_a_run()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var inlineId = await CreateInlineProjectAsync(owner, "inline-delivery");

        // Creation refuses this source, so the delivery branch's own guard is reached only by seeding the
        // row it would have refused. The branch filter names the pushed branch on purpose: a guard that
        // vanished would land on `ignored:branch` at best, and the assertion names the other outcome.
        var webhookId = await host.SeedWebhookAsync(
            "inline-seeded",
            branchFilter: "main",
            projectId: inlineId);
        var body = WebhookPayloads.GitHub("main", WebhookPayloads.Commit('d'));

        using var response = await host.DeliverSignedAsync(
            webhookId,
            body,
            host.SecretFor("inline-seeded"),
            deliveryId: "inline-delivery");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains(
            WebhookOutcomes.IgnoredNoRepository,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        var delivery = Assert.Single(await host.ReadDeliveriesAsync(webhookId));
        Assert.Equal(WebhookOutcomes.IgnoredNoRepository, delivery.Outcome);
        Assert.Null(delivery.RunId);
        Assert.Equal(
            WebhookOutcomes.IgnoredNoRepository,
            (await host.ReadWebhookAsync(webhookId))!.LastDeliveryOutcome);

        Assert.Empty(await host.InSystemScopeAsync(context =>
            context.SystemQueryAsync<BuildRun>(run => run.ProjectId == inlineId)));
    }

    private static async Task<Guid> CreateInlineProjectAsync(HttpClient owner, string name)
    {
        using var response = await OrganizationTestHost.PostFormAsync(
            owner,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = name,
                ["Kind"] = ProjectKinds.Pipeline,
                ["PipelineSource"] = ProjectSources.Inline,
                ["InlineScript"] = Script,
            });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/projects/", location, StringComparison.Ordinal);
        return Guid.Parse(location["/projects/".Length..].Split('?')[0]);
    }
}
