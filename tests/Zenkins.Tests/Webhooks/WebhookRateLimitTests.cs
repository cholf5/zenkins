using System.Globalization;
using System.Net;
using Xunit;
using Zenkins.Server.Webhooks;

namespace Zenkins.Tests.Webhooks;

/// <summary>
/// Card F-017's rate limit: 429 with <c>Retry-After</c>, no run, and two properties that would otherwise
/// be easy to miss - changing the delivery id must not buy a fresh budget, and an id that does not exist
/// must be limited exactly like one that does (or 429-vs-404 becomes an existence oracle).
/// </summary>
[Trait("Category", "Webhooks")]
public sealed class WebhookRateLimitTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> Budget(int perMinute) =>
        new(StringComparer.Ordinal) { [WebhookOptions.RateLimitPerMinuteConfigurationKey] = perMinute.ToString() };

    [Fact]
    public async Task The_budget_answers_429_with_retry_after_and_creates_no_run()
    {
        using var host = await WebhooksTestHost.CreateAsync(Budget(3));
        var webhookId = await host.SeedWebhookAsync("limit");
        var secret = host.SecretFor("limit");

        // Every request carries a different delivery id: a limiter that keyed on the delivery would let all
        // of them through.
        for (var index = 0; index < 3; index++)
        {
            using var accepted = await host.DeliverSignedAsync(
                webhookId,
                WebhookPayloads.GitHub("main", WebhookPayloads.Commit('1')),
                secret,
                deliveryId: $"budget-{index}");
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using var limited = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("main", WebhookPayloads.Commit('2')),
            secret,
            deliveryId: "budget-over");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        // Retry-After is a positive number of seconds inside the window, so a host can back off honestly.
        Assert.True(limited.Headers.TryGetValues("Retry-After", out var values));
        var retryAfter = int.Parse(values.Single(), CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 1, 60);

        // The refused request created nothing; the three accepted ones did.
        Assert.Equal(3, (await host.ReadRunsAsync()).Count);
    }

    [Fact]
    public async Task An_oversized_body_from_a_webhook_out_of_budget_is_a_429_not_a_413()
    {
        using var host = await WebhooksTestHost.CreateAsync(Budget(1));
        var webhookId = await host.SeedWebhookAsync("budget-before-body");
        var secret = host.SecretFor("budget-before-body");

        using (var accepted = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("main", WebhookPayloads.Commit('8')),
                   secret,
                   deliveryId: "budget-first"))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        // The budget is spent, and the next request is an oversized body. The limiter runs BEFORE the body is
        // read (review m5), so the answer is 429 - a body flood cannot buy itself past the budget by being
        // unparseable or too large to hold.
        var oversized = System.Text.Encoding.UTF8.GetBytes(new string('x', (1024 * 1024) + 1));
        using var limited = await host.DeliverAsync(
            webhookId,
            oversized,
            signature: WebhooksTestHost.Sign(oversized, secret),
            deliveryId: "budget-oversized");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.TryGetValues("Retry-After", out var values));
        Assert.InRange(int.Parse(values.Single(), CultureInfo.InvariantCulture), 1, 60);

        // Nothing was buffered for it and no run was added: the refusal happened at the gate.
        Assert.Equal(0, host.Telemetry.OversizedRejections);
        Assert.Single(await host.ReadRunsAsync());
    }

    [Fact]
    public async Task An_unknown_id_spends_the_same_budget_so_429_says_nothing_about_existence()
    {
        using var host = await WebhooksTestHost.CreateAsync(Budget(2));
        var existing = await host.SeedWebhookAsync("oracle");
        var secret = host.SecretFor("oracle");
        var fictitious = Guid.NewGuid();

        // A real webhook: two accepted deliveries, then the budget is spent.
        for (var index = 0; index < 2; index++)
        {
            using var accepted = await host.DeliverSignedAsync(
                existing,
                WebhookPayloads.GitHub("main", WebhookPayloads.Commit('3')),
                secret,
                deliveryId: $"oracle-existing-{index}");
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using (var limited = await host.DeliverSignedAsync(
                   existing,
                   WebhookPayloads.GitHub("main", WebhookPayloads.Commit('4')),
                   secret,
                   deliveryId: "oracle-existing-over"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        }

        // A fictitious id: the same three requests produce 404, 404, then 429 - the budget is spent on the
        // same counter, so the last answer cannot be told apart from the real webhook's.
        var body = WebhookPayloads.GitHub("main", WebhookPayloads.Commit('5'));
        for (var index = 0; index < 2; index++)
        {
            using var miss = await host.DeliverSignedAsync(
                fictitious,
                body,
                secret,
                deliveryId: $"oracle-absent-{index}");
            Assert.Equal(HttpStatusCode.NotFound, miss.StatusCode);
        }

        using var absentLimited = await host.DeliverSignedAsync(
            fictitious,
            body,
            secret,
            deliveryId: "oracle-absent-over");
        Assert.Equal(HttpStatusCode.TooManyRequests, absentLimited.StatusCode);

        Assert.Equal(2, (await host.ReadRunsAsync()).Count);
    }
}
