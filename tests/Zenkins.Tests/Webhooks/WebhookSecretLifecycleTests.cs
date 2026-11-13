using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Webhooks;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Webhooks;

/// <summary>
/// Card F-017's secret lifecycle and settings page: create and rotate return the plaintext exactly once,
/// every later read reports only "configured / rotated at", the plaintext never reaches a page or a log
/// line, and the page carries the endpoint URL and the branch filter.
/// </summary>
[Trait("Category", "Webhooks")]
public sealed class WebhookSecretLifecycleTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_shows_the_plaintext_once_and_never_again()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();

        var (webhookId, secret, html, response) = await host.CreateWebhookAsync(
            owner,
            "github-main",
            branchFilter: "main,release/*");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The one response that ever carries a plaintext secret is explicitly uncacheable: no intermediary
        // and no browser may keep the only copy.
        Assert.True(response.Headers.CacheControl?.NoStore, "the show-once document must be Cache-Control: no-store");

        Assert.NotEqual(Guid.Empty, webhookId);
        Assert.StartsWith(WebhookSecretProtector.SecretPrefix, secret, StringComparison.Ordinal);
        Assert.Contains($"/webhooks/{webhookId:D}", html, StringComparison.Ordinal);

        // Stored as ciphertext under the webhook purpose, never as the plaintext.
        var webhook = await host.ReadWebhookAsync(webhookId);
        Assert.NotNull(webhook);
        Assert.NotEqual(secret, webhook.SecretCiphertext);
        Assert.DoesNotContain(secret, webhook.SecretCiphertext, StringComparison.Ordinal);
        var decrypted = await host.Host.InScopeAsync(services =>
            Task.FromResult(services.GetRequiredService<WebhookSecretProtector>().Unprotect(webhook.SecretCiphertext)));
        Assert.Equal(secret, decrypted);

        // The settings tab (card D-003 moved the webhook cards there) shows the configuration - name,
        // endpoint, branch filter - and never the value.
        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{host.ProjectId}/settings");
        Assert.Contains("github-main", page, StringComparison.Ordinal);
        Assert.Contains($"/webhooks/{webhookId:D}", page, StringComparison.Ordinal);
        Assert.Contains("release/*", page, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, page, StringComparison.Ordinal);

        // Nor does any log line: the create path logs ids and the filter, never the value.
        Assert.DoesNotContain(host.LogLines, line => line.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rotation_returns_a_new_plaintext_and_retires_the_old_one()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();

        var (webhookId, firstSecret, _, _) = await host.CreateWebhookAsync(owner, "rotate-me");
        var payload = WebhookPayloads.GitHub("main", WebhookPayloads.Commit('7'));

        using (var accepted = await host.DeliverSignedAsync(webhookId, payload, firstSecret, deliveryId: "rotate-1"))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        var rotated = await Zenkins.Tests.Organizations.OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{host.ProjectId}/settings",
            $"/projects/{host.ProjectId}/webhooks/{webhookId}/rotate",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.True(rotated.Headers.CacheControl?.NoStore, "the rotation document must be Cache-Control: no-store");

        var rotatedHtml = await rotated.Content.ReadAsStringAsync(CancellationToken);
        var secondSecret = WebhooksTestHost.ExtractSecret(rotatedHtml);
        Assert.NotEqual(firstSecret, secondSecret);

        var webhook = await host.ReadWebhookAsync(webhookId);
        Assert.NotNull(webhook);
        Assert.NotNull(webhook.SecretRotatedAt);

        // The old secret is gone: only the new one verifies.
        using (var stale = await host.DeliverSignedAsync(webhookId, payload, firstSecret, deliveryId: "rotate-2"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        }

        using (var fresh = await host.DeliverSignedAsync(webhookId, payload, secondSecret, deliveryId: "rotate-3"))
        {
            Assert.Equal(HttpStatusCode.Accepted, fresh.StatusCode);
        }

        Assert.Equal(2, (await host.ReadRunsAsync()).Count);
        Assert.DoesNotContain(host.LogLines, line => line.Contains(secondSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_plaintext_never_appears_on_the_run_page_or_in_the_logs()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var (webhookId, secret, _, _) = await host.CreateWebhookAsync(owner, "run-page");

        using var response = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("main", WebhookPayloads.Commit('8')),
            secret,
            deliveryId: "page-1");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var run = Assert.Single(await host.ReadRunsAsync());
        var runPage = await AuthTestHost.GetStringAsync(owner, $"/runs/{run.Id}");
        Assert.Contains("webhook", runPage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, runPage, StringComparison.Ordinal);

        Assert.DoesNotContain(host.LogLines, line => line.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disabling_a_webhook_makes_it_answer_like_an_unknown_id_and_enabling_restores_it()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var (webhookId, secret, _, _) = await host.CreateWebhookAsync(owner, "toggle");
        var payload = WebhookPayloads.GitHub("main", WebhookPayloads.Commit('9'));

        var disable = await Zenkins.Tests.Organizations.OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{host.ProjectId}/settings",
            $"/projects/{host.ProjectId}/webhooks/{webhookId}/disable",
            new Dictionary<string, string>());
        Assert.True((int)disable.StatusCode is 302 or 303);

        using (var disabled = await host.DeliverSignedAsync(webhookId, payload, secret, deliveryId: "toggle-1"))
        {
            await AssertIndistinguishableFromUnknownAsync(host, disabled);
        }

        var enable = await Zenkins.Tests.Organizations.OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{host.ProjectId}/settings",
            $"/projects/{host.ProjectId}/webhooks/{webhookId}/enable",
            new Dictionary<string, string>());
        Assert.True((int)enable.StatusCode is 302 or 303);

        using (var enabled = await host.DeliverSignedAsync(webhookId, payload, secret, deliveryId: "toggle-2"))
        {
            Assert.Equal(HttpStatusCode.Accepted, enabled.StatusCode);
        }

        Assert.Single(await host.ReadRunsAsync());
    }

    [Fact]
    public async Task Deleting_a_webhook_removes_it_and_its_delivery_records()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();
        var (webhookId, secret, _, _) = await host.CreateWebhookAsync(owner, "delete-me");

        using (var response = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("main", WebhookPayloads.Commit('a')),
                   secret,
                   deliveryId: "delete-1"))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        Assert.Single(await host.ReadDeliveriesAsync(webhookId));

        var deleted = await Zenkins.Tests.Organizations.OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{host.ProjectId}/settings",
            $"/projects/{host.ProjectId}/webhooks/{webhookId}/delete",
            new Dictionary<string, string>());
        Assert.True((int)deleted.StatusCode is 302 or 303);

        Assert.Null(await host.ReadWebhookAsync(webhookId));
        Assert.Empty(await host.ReadDeliveriesAsync(webhookId));

        using var gone = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("main", WebhookPayloads.Commit('b')),
            secret,
            deliveryId: "delete-2");
        await AssertIndistinguishableFromUnknownAsync(host, gone);

        // The run the webhook created is history and stays.
        Assert.Single(await host.ReadRunsAsync());
    }

    [Fact]
    public async Task The_create_form_refuses_a_duplicate_name_and_an_invalid_filter()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();

        var (_, _, _, first) = await host.CreateWebhookAsync(owner, "duplicate");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var (_, _, _, duplicate) = await host.CreateWebhookAsync(owner, "duplicate");
        Assert.True((int)duplicate.StatusCode is 302 or 303);
        Assert.Contains("webhookError=name-duplicate", duplicate.Headers.Location!.OriginalString, StringComparison.Ordinal);

        var (_, _, _, invalid) = await host.CreateWebhookAsync(owner, "bad-filter", branchFilter: "rel*ease");
        Assert.Contains("webhookError=branch-filter-invalid", invalid.Headers.Location!.OriginalString, StringComparison.Ordinal);

        Assert.Single(await host.ReadWebhooksAsync());
    }

    [Fact]
    public async Task A_ciphertext_from_another_purpose_is_unusable_as_a_webhook_secret()
    {
        using var host = await WebhooksTestHost.CreateAsync();

        // The documented asymmetry, as a cryptographic property rather than a convention: a
        // Zenkins.Secrets.v1 blob cannot be decrypted under the webhook purpose (and vice versa), because the
        // purpose is mixed into the key derivation.
        await host.Host.InScopeAsync(services =>
        {
            var webhookProtector = services.GetRequiredService<WebhookSecretProtector>();
            var secretProtector = services.GetRequiredService<Zenkins.Server.Secrets.SecretProtector>();

            var webhookBlob = webhookProtector.Protect("whsec_precious");
            var secretsBlob = secretProtector.Protect("org-secret-value");

            Assert.Throws<System.Security.Cryptography.CryptographicException>(
                () => webhookProtector.Unprotect(secretsBlob));
            Assert.Throws<System.Security.Cryptography.CryptographicException>(
                () => secretProtector.Unprotect(webhookBlob));

            return Task.FromResult(true);
        });

        // The same property through the delivery path: a webhook row whose ciphertext came from the secrets
        // purpose cannot be used to verify a signature at all - it fails loudly, never "verified against
        // something else".
        var body = System.Text.Encoding.UTF8.GetBytes(
            WebhookPayloads.GitHub("main", WebhookPayloads.Commit('e')));
        var misconfigured = await host.SeedWebhookWithCiphertextAsync(
            "foreign-purpose",
            await host.Host.InScopeAsync(services => Task.FromResult(
                services.GetRequiredService<Zenkins.Server.Secrets.SecretProtector>().Protect("not-a-webhook"))));

        await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(
            () => host.HandleDeliveryInServiceAsync(misconfigured, body, host.SecretFor("foreign-purpose"), "purpose-1"));

        Assert.Empty(await host.ReadRunsAsync());
    }

    /// <summary>
    /// A disabled (and a deleted) webhook must answer the same bare 404 an id that never existed gets:
    /// same status, no body, no content type.
    /// </summary>
    private static async Task AssertIndistinguishableFromUnknownAsync(
        WebhooksTestHost host,
        HttpResponseMessage response)
    {
        using var unknown = await host.DeliverAsync(
            Guid.NewGuid(),
            System.Text.Encoding.UTF8.GetBytes(
                WebhookPayloads.GitHub("main", WebhookPayloads.Commit('c'))),
            signature: WebhooksTestHost.Sign(
                System.Text.Encoding.UTF8.GetBytes(
                    WebhookPayloads.GitHub("main", WebhookPayloads.Commit('c'))),
                "any-secret"),
            deliveryId: $"unknown-{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(unknown.StatusCode, response.StatusCode);
        Assert.Equal(unknown.Content.Headers.ContentType, response.Content.Headers.ContentType);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(CancellationToken));
        Assert.Empty(await unknown.Content.ReadAsByteArrayAsync(CancellationToken));
    }
}
