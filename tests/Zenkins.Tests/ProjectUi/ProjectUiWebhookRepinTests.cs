using System.Net;
using Xunit;
using Zenkins.Server.Webhooks;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Webhooks;

namespace Zenkins.Tests.ProjectUi;

/// <summary>
/// TC-003 (card D-003): moving the webhook UI to the settings tab did not change it. Every F-017
/// behaviour is re-pinned from the new location: the plaintext appears exactly once at creation and
/// rotation (a standalone, uncacheable show-once document) and never again in any later response or
/// page; enable/disable makes a webhook answer like an unknown id and back; delete removes the
/// webhook and its delivery records; the last delivery outcome renders on the tab.
/// </summary>
[Trait("Category", "ProjectUi")]
public sealed class ProjectUiWebhookRepinTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creating_from_the_settings_tab_shows_the_plaintext_exactly_once()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var settingsPath = $"/projects/{host.ProjectId}/settings";

        // Create through the settings tab: the one show-once document.
        var (webhookId, secret, html, response) = await host.CreateWebhookAsync(owner, "once-only", branchFilter: "main,release/*");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(Guid.Empty, webhookId);
        Assert.StartsWith(WebhookSecretProtector.SecretPrefix, secret, StringComparison.Ordinal);
        Assert.Contains($"/webhooks/{webhookId:D}", html, StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl?.NoStore, "the show-once document must be Cache-Control: no-store");

        // Reload the tab: configuration is there, the plaintext is not.
        var settings = await AuthTestHost.GetStringAsync(owner, settingsPath);
        Assert.Contains("once-only", settings, StringComparison.Ordinal);
        Assert.Contains($"/webhooks/{webhookId:D}", settings, StringComparison.Ordinal);
        Assert.Contains("release/*", settings, StringComparison.Ordinal);
        Assert.Contains("Configured", settings, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, settings, StringComparison.Ordinal);

        // The overview tab answers too - and carries no plaintext either.
        var overview = await AuthTestHost.GetStringAsync(owner, $"/projects/{host.ProjectId}");
        Assert.DoesNotContain(secret, overview, StringComparison.Ordinal);

        // Nor does any log line: the create path logs ids and filters, never the value.
        Assert.DoesNotContain(host.LogLines, line => line.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rotate_enable_disable_and_delete_behave_as_F017_from_the_settings_tab()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var settingsPath = $"/projects/{host.ProjectId}/settings";

        var (webhookId, firstSecret, _, _) = await host.CreateWebhookAsync(owner, "lifecycle-hook");
        Assert.NotEqual(Guid.Empty, webhookId);

        // A first delivery lands (its outcome is what the tab then shows).
        using (var accepted = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("main", WebhookPayloads.Commit('1')),
                   firstSecret,
                   deliveryId: "repin-1"))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        // Rotate from the tab: a new plaintext exactly once, the old one retired.
        var rotated = await OrganizationTestHost.PostFormAsync(
            owner,
            settingsPath,
            $"/projects/{host.ProjectId}/webhooks/{webhookId}/rotate",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.True(rotated.Headers.CacheControl?.NoStore, "the rotation document must be Cache-Control: no-store");
        var secondSecret = WebhooksTestHost.ExtractSecret(await rotated.Content.ReadAsStringAsync(CancellationToken));
        Assert.NotEqual(firstSecret, secondSecret);

        using (var stale = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("main", WebhookPayloads.Commit('2')),
                   firstSecret,
                   deliveryId: "repin-2"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        }

        using (var fresh = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("main", WebhookPayloads.Commit('3')),
                   secondSecret,
                   deliveryId: "repin-3"))
        {
            Assert.Equal(HttpStatusCode.Accepted, fresh.StatusCode);
        }

        // A non-matching branch delivery is ignored, and the tab shows that outcome.
        using (var ignored = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("feature/x", WebhookPayloads.Commit('4')),
                   secondSecret,
                   deliveryId: "repin-4"))
        {
            Assert.Equal(HttpStatusCode.Accepted, ignored.StatusCode);
        }

        var settings = await AuthTestHost.GetStringAsync(owner, settingsPath);
        Assert.Contains("ignored:branch", settings, StringComparison.Ordinal);
        Assert.DoesNotContain(secondSecret, settings, StringComparison.Ordinal);

        var rotatedAt = await host.ReadWebhookAsync(webhookId);
        Assert.NotNull(rotatedAt);
        Assert.NotNull(rotatedAt.SecretRotatedAt);
        Assert.Equal(WebhookOutcomes.IgnoredBranch, rotatedAt.LastDeliveryOutcome);

        // Disable from the tab: the PRG lands back on the tab, and the webhook now answers like an
        // unknown id (same bodyless 404 as one that never existed).
        var disable = await OrganizationTestHost.PostFormAsync(
            owner,
            settingsPath,
            $"/projects/{host.ProjectId}/webhooks/{webhookId}/disable",
            new Dictionary<string, string>());
        Assert.True((int)disable.StatusCode is 302 or 303);
        Assert.Contains(
            $"/projects/{host.ProjectId}/settings?notice=webhook-disabled",
            disable.Headers.Location!.OriginalString,
            StringComparison.Ordinal);

        using (var disabled = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("main", WebhookPayloads.Commit('5')),
                   secondSecret,
                   deliveryId: "repin-5"))
        {
            Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
            Assert.Null(disabled.Content.Headers.ContentType);
            Assert.Empty(await disabled.Content.ReadAsByteArrayAsync(CancellationToken));
        }

        // Enable restores it.
        var enable = await OrganizationTestHost.PostFormAsync(
            owner,
            settingsPath,
            $"/projects/{host.ProjectId}/webhooks/{webhookId}/enable",
            new Dictionary<string, string>());
        Assert.True((int)enable.StatusCode is 302 or 303);
        Assert.Contains(
            $"/projects/{host.ProjectId}/settings?notice=webhook-enabled",
            enable.Headers.Location!.OriginalString,
            StringComparison.Ordinal);

        using (var enabled = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("main", WebhookPayloads.Commit('6')),
                   secondSecret,
                   deliveryId: "repin-6"))
        {
            Assert.Equal(HttpStatusCode.Accepted, enabled.StatusCode);
        }

        // Delete from the tab: the webhook and its delivery records are gone, and the URL answers
        // like an unknown id again.
        var delete = await OrganizationTestHost.PostFormAsync(
            owner,
            settingsPath,
            $"/projects/{host.ProjectId}/webhooks/{webhookId}/delete",
            new Dictionary<string, string>());
        Assert.True((int)delete.StatusCode is 302 or 303);
        Assert.Contains(
            $"/projects/{host.ProjectId}/settings?notice=webhook-deleted",
            delete.Headers.Location!.OriginalString,
            StringComparison.Ordinal);

        Assert.Null(await host.ReadWebhookAsync(webhookId));
        Assert.Empty(await host.ReadDeliveriesAsync(webhookId));

        using (var gone = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("main", WebhookPayloads.Commit('7')),
                   secondSecret,
                   deliveryId: "repin-7"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
            Assert.Empty(await gone.Content.ReadAsByteArrayAsync(CancellationToken));
        }
    }
}
