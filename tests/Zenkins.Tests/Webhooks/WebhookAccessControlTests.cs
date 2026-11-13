using System.Net;
using System.Text;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Webhooks;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Webhooks;

/// <summary>
/// Card F-017's access control: the delivery endpoint is anonymous but signature-authenticated, the
/// settings forms are cookie + antiforgery (any Member may manage a project's webhooks - project edit
/// rights), and another organization's ids are the same bare 404 as unknown ones.
/// </summary>
[Trait("Category", "Webhooks")]
public sealed class WebhookAccessControlTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_anonymous_form_post_cannot_create_a_webhook()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var anonymous = host.CreateDeliveryClient();

        using var response = await anonymous.PostAsync(
            $"/projects/{host.ProjectId}/webhooks/create",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["Name"] = "sneaky" }),
            CancellationToken);

        // The cookie challenge wins before antiforgery: a sign-in redirect (or a bare 401), never a 200.
        Assert.True((int)response.StatusCode is 302 or 401);
        Assert.Empty(await host.ReadWebhooksAsync());
    }

    [Fact]
    public async Task A_member_of_the_organization_can_create_rotate_and_delete_webhooks()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var member = await host.CreateMemberAsync();

        var (webhookId, secret, _, created) = await host.CreateWebhookAsync(member, "member-managed");
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.NotEqual(Guid.Empty, webhookId);

        // D-003: the webhook list lives on the project's settings tab now.
        var page = await AuthTestHost.GetStringAsync(member, $"/projects/{host.ProjectId}/settings");
        Assert.Contains("member-managed", page, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, page, StringComparison.Ordinal);

        using var rotated = await OrganizationTestHost.PostFormAsync(
            member,
            $"/projects/{host.ProjectId}/settings",
            $"/projects/{host.ProjectId}/webhooks/{webhookId}/rotate",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);

        using var deleted = await OrganizationTestHost.PostFormAsync(
            member,
            $"/projects/{host.ProjectId}/settings",
            $"/projects/{host.ProjectId}/webhooks/{webhookId}/delete",
            new Dictionary<string, string>());
        Assert.True((int)deleted.StatusCode is 302 or 303);
        Assert.Empty(await host.ReadWebhooksAsync());
    }

    [Fact]
    public async Task A_member_of_another_organization_cannot_create_list_rotate_or_delete_webhooks()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var (webhookId, _, _, _) = await host.CreateWebhookAsync(owner, "private");

        await host.CreateForeignOrganizationAsync();
        using var foreign = await host.SignInMemberAsync(WebhooksTestHost.ForeignOwnerEmail);

        // Create: the project is not in her tenant, so the service answers 404 (never 403 - an id must not
        // become an existence oracle).
        using (var create = await OrganizationTestHost.PostFormAsync(
                   foreign,
                   "/",
                   $"/projects/{host.ProjectId}/webhooks/create",
                   new Dictionary<string, string> { ["Name"] = "hijack" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
        }

        // Rotate and delete: the webhook belongs to another tenant, so the same 404.
        using (var rotate = await OrganizationTestHost.PostFormAsync(
                   foreign,
                   "/",
                   $"/projects/{host.ProjectId}/webhooks/{webhookId}/rotate",
                   new Dictionary<string, string>()))
        {
            Assert.Equal(HttpStatusCode.NotFound, rotate.StatusCode);
        }

        using (var delete = await OrganizationTestHost.PostFormAsync(
                   foreign,
                   "/",
                   $"/projects/{host.ProjectId}/webhooks/{webhookId}/delete",
                   new Dictionary<string, string>()))
        {
            Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        }

        // List: the settings tab (where the webhook list lives, card D-003) and the project page
        // itself are both a 404 for her.
        using (var page = await foreign.GetAsync($"/projects/{host.ProjectId}/settings", CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        }

        using (var page = await foreign.GetAsync($"/projects/{host.ProjectId}", CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        }

        // Nothing changed: still exactly the Owner's one webhook, untouched.
        var webhook = Assert.Single(await host.ReadWebhooksAsync());
        Assert.Equal(webhookId, webhook.Id);
        Assert.Equal("private", webhook.Name);
    }

    [Fact]
    public async Task A_delivery_for_another_organizations_webhook_is_authenticated_only_by_its_secret()
    {
        // There is no caller tenant on a delivery: a git host has no session and the URL carries no
        // organization. So "another tenant's id" cannot become a 404 - the webhook row itself selects the
        // tenant - and the only thing that decides is the secret. The 404-equal paths are unknown,
        // disabled and deleted ids (asserted above and in the secret-lifecycle tests); the cross-tenant
        // 404 belongs to the management routes, where a caller tenant exists.
        using var host = await WebhooksTestHost.CreateAsync();
        var foreignOrganizationId = await host.CreateForeignOrganizationAsync();
        var foreignProjectId = await host.AddProjectAsync(
            foreignOrganizationId,
            "bravo-app",
            "https://example.test/bravo.git",
            "main");
        var foreignWebhookId = await host.SeedWebhookAsync(
            "bravo-hook",
            secret: "bravo-secret",
            organizationId: foreignOrganizationId,
            projectId: foreignProjectId);

        var body = Encoding.UTF8.GetBytes(
            WebhookPayloads.GitHub("main", WebhookPayloads.Commit('b')));

        // A wrong secret is the same bare 401 any webhook gives: no tenant, project or org detail.
        using (var wrongSecret = await host.DeliverAsync(
                   foreignWebhookId,
                   body,
                   signature: WebhooksTestHost.Sign(body, "not-bravo-secret"),
                   deliveryId: "foreign-1"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
            Assert.Null(wrongSecret.Content.Headers.ContentType);
            Assert.Empty(await wrongSecret.Content.ReadAsByteArrayAsync(CancellationToken));
        }

        // The correct secret works, and the run lands in that organization - never in the caller's view.
        using (var accepted = await host.DeliverAsync(
                   foreignWebhookId,
                   body,
                   signature: WebhooksTestHost.Sign(body, "bravo-secret"),
                   deliveryId: "foreign-2"))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        Assert.Empty(await host.ReadRunsAsync());
        var foreignRuns = await host.InSystemScopeAsync(context =>
            context.SystemQueryAsync<BuildRun>(run => run.OrganizationId == foreignOrganizationId));
        var foreignRun = Assert.Single(foreignRuns);
        Assert.Equal(foreignProjectId, foreignRun.ProjectId);
        Assert.Equal(BuildRunTriggerType.Webhook, foreignRun.TriggerType);
    }

    [Fact]
    public async Task The_page_shows_the_endpoint_url_the_branch_filter_and_the_last_outcome()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var (webhookId, secret, _, _) = await host.CreateWebhookAsync(
            owner,
            "page-facts",
            branchFilter: "main,release/*",
            requiredTags: "ci");

        var beforeDelivery = await AuthTestHost.GetStringAsync(owner, $"/projects/{host.ProjectId}/settings");
        Assert.Contains($"/webhooks/{webhookId:D}", beforeDelivery, StringComparison.Ordinal);

        using (var response = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("feature/x", WebhookPayloads.Commit('c')),
                   secret,
                   deliveryId: "page-facts-1"))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{host.ProjectId}/settings");
        Assert.Contains("page-facts", page, StringComparison.Ordinal);
        Assert.Contains("release/*", page, StringComparison.Ordinal);
        Assert.Contains("ignored:branch", page, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, page, StringComparison.Ordinal);

        // The run that the filter did select (main) is what the outcome line then shows.
        using (var response = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("main", WebhookPayloads.Commit('d')),
                   secret,
                   deliveryId: "page-facts-2"))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        var webhook = await host.ReadWebhookAsync(webhookId);
        Assert.NotNull(webhook);
        Assert.Equal(WebhookOutcomes.Queued, webhook.LastDeliveryOutcome);
        Assert.NotNull(webhook.LastDeliveryAt);
        Assert.Equal(BuildRunTriggerType.Webhook, Assert.Single(await host.ReadRunsAsync()).TriggerType);
    }
}
