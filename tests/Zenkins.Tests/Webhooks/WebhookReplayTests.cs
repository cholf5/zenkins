using System.Net;
using System.Text.Json;
using Xunit;
using Zenkins.Server.Data.Entities;

namespace Zenkins.Tests.Webhooks;

/// <summary>
/// Card F-017's replay protection (TC-003): the delivery id and the body hash, the two windows, the
/// concurrent-replay proof and the sweeper.
/// <para>
/// The concurrency test is the one that would otherwise pass for the wrong reason: ten identical
/// deliveries sent in parallel must create exactly one run, which only a database-enforced unique key (not
/// a "have I seen this?" check followed by an insert) can guarantee.
/// </para>
/// </summary>
[Trait("Category", "Webhooks")]
public sealed class WebhookReplayTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ten_concurrent_identical_deliveries_create_exactly_one_run()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("concurrent");
        var secret = host.SecretFor("concurrent");
        var payload = WebhookPayloads.GitHub("main", WebhookPayloads.Commit('d'));

        const int attempts = 10;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var deliveries = Enumerable.Range(0, attempts)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task.WaitAsync(CancellationToken);
                using var response = await host.DeliverSignedAsync(
                    webhookId,
                    payload,
                    secret,
                    deliveryId: "same-delivery-id");

                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                return await ReadStatusAsync(response);
            }))
            .ToArray();

        start.SetResult();
        var statuses = await Task.WhenAll(deliveries);

        // Exactly one winner created the run; the nine losers answered the stored outcome as a duplicate.
        Assert.Equal(1, statuses.Count(status => status == "queued"));
        Assert.Equal(attempts - 1, statuses.Count(status => status == "duplicate"));

        var run = Assert.Single(await host.ReadRunsAsync());
        Assert.Equal(WebhookPayloads.Commit('d'), run.GitCommit);
        Assert.Single(await host.ReadDeliveriesAsync(webhookId));
    }

    [Fact]
    public async Task A_body_without_a_delivery_id_is_deduplicated_inside_the_window_and_allowed_after_it()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("hash-window");
        var secret = host.SecretFor("hash-window");
        var payload = WebhookPayloads.GitHub("main", WebhookPayloads.Commit('e'));

        // No delivery id: the body hash is the key, within the short window.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var response = await host.DeliverSignedAsync(webhookId, payload, secret);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(attempt == 0 ? "queued" : "duplicate", await ReadStatusAsync(response));
        }

        Assert.Single(await host.ReadRunsAsync());

        // Past the window the same body is a new delivery again (an identical push can legitimately repeat).
        await host.AgeDeliveriesAsync(webhookId, TimeSpan.FromMinutes(11));

        using (var afterWindow = await host.DeliverSignedAsync(webhookId, payload, secret))
        {
            Assert.Equal("queued", await ReadStatusAsync(afterWindow));
        }

        Assert.Equal(2, (await host.ReadRunsAsync()).Count);
    }

    [Fact]
    public async Task A_delivery_id_outlives_the_body_window_but_not_its_ttl()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("id-ttl");
        var secret = host.SecretFor("id-ttl");
        var payload = WebhookPayloads.GitHub("main", WebhookPayloads.Commit('f'));

        using (var first = await host.DeliverSignedAsync(webhookId, payload, secret, deliveryId: "ttl-1"))
        {
            Assert.Equal("queued", await ReadStatusAsync(first));
        }

        // A delivery id is remembered for the long TTL, so the short window does not re-open it.
        await host.AgeDeliveriesAsync(webhookId, TimeSpan.FromMinutes(11));
        using (var withinTtl = await host.DeliverSignedAsync(webhookId, payload, secret, deliveryId: "ttl-1"))
        {
            Assert.Equal("duplicate", await ReadStatusAsync(withinTtl));
        }

        Assert.Single(await host.ReadRunsAsync());

        // Past the TTL the id is stale: a new delivery may claim it (and the sweeper would have pruned it).
        await host.AgeDeliveriesAsync(webhookId, TimeSpan.FromDays(8));
        using (var afterTtl = await host.DeliverSignedAsync(webhookId, payload, secret, deliveryId: "ttl-1"))
        {
            Assert.Equal("queued", await ReadStatusAsync(afterTtl));
        }

        Assert.Equal(2, (await host.ReadRunsAsync()).Count);
    }

    [Fact]
    public async Task The_sweeper_prunes_records_past_the_ttl_and_keeps_fresh_ones()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("sweep");
        var secret = host.SecretFor("sweep");

        using (var response = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("main", WebhookPayloads.Commit('a')),
                   secret,
                   deliveryId: "sweep-old"))
        {
            Assert.Equal("queued", await ReadStatusAsync(response));
        }

        await host.AgeDeliveriesAsync(webhookId, TimeSpan.FromDays(8));
        using (var response = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("main", WebhookPayloads.Commit('b')),
                   secret,
                   deliveryId: "sweep-new"))
        {
            Assert.Equal("queued", await ReadStatusAsync(response));
        }

        Assert.Equal(2, (await host.ReadDeliveriesAsync(webhookId)).Count);

        var result = await host.SweepAsync();

        Assert.Equal(1, result.PrunedDeliveries);
        var remaining = Assert.Single(await host.ReadDeliveriesAsync(webhookId));
        Assert.Equal("queued", remaining.Outcome);

        // The survivor is the fresh one: the aged record is what the sweep removed.
        Assert.True(remaining.ReceivedAt > DateTimeOffset.UtcNow.AddHours(-1));

        // Both runs still exist: pruning is bookkeeping, not history rewriting.
        Assert.Equal(2, (await host.ReadRunsAsync()).Count);
    }

    [Fact]
    public async Task A_replay_record_stamped_a_moment_in_the_future_is_still_fresh()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("future-row");
        var secret = host.SecretFor("future-row");
        var body = System.Text.Encoding.UTF8.GetBytes(
            WebhookPayloads.GitHub("main", WebhookPayloads.Commit('7')));
        const string deliveryId = "future-1";

        // Deterministic form of the ordering bug the concurrency test catches only probabilistically: a record
        // whose ReceivedAt is ahead of this request's clock. That happens for real when the winner's request
        // started before this one (the pre-fix code compared against a sample taken before its own insert) or
        // when two hosts' clocks skew. Here there is no second request and no race - the test places the row,
        // and the answer must still be "duplicate". The offset is a couple of seconds on purpose: a 1 ms
        // margin does not discriminate, because the freshness sample is taken after the lost insert.
        await host.InsertDeliveryAsync(
            webhookId,
            Zenkins.Server.Webhooks.WebhookDeliveryService.DedupeKey(body, deliveryId),
            DateTimeOffset.UtcNow.AddSeconds(2));

        var result = await host.HandleDeliveryInServiceAsync(webhookId, body, secret, deliveryId);

        Assert.Equal(Zenkins.Server.Webhooks.WebhookDeliveryStatus.Duplicate, result.Status);
        Assert.Empty(await host.ReadRunsAsync());
        Assert.Single(await host.ReadDeliveriesAsync(webhookId));
    }

    private static async Task<string?> ReadStatusAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("status", out var value) ? value.GetString() : null;
    }
}
