using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Webhooks;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.Webhooks;

/// <summary>
/// Card F-017's delivery acceptance criteria (TC-001, TC-002 and TC-004): the three payload shapes, the
/// signature matrix, the rejection matrix, the branch filter and the created run's facts.
/// </summary>
[Trait("Category", "Webhooks")]
public sealed class WebhookDeliveryTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_github_a_gitea_and_a_gitlab_push_each_start_one_run_for_their_commit()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("github-shaped");
        var secret = host.SecretFor("github-shaped");

        var commits = new[]
        {
            WebhookPayloads.Commit('a'), WebhookPayloads.Commit('b'), WebhookPayloads.Commit('c'),
        };
        var shapes = new[]
        {
            WebhookPayloads.GitHub("main", commits[0]),
            WebhookPayloads.Gitea("main", commits[1]),
            WebhookPayloads.GitLab("main", commits[2]),
        };

        for (var index = 0; index < shapes.Length; index++)
        {
            using var response = await host.DeliverSignedAsync(
                webhookId,
                shapes[index],
                secret,
                deliveryId: $"shape-{index}");
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal("queued", await ReadStatusAsync(response));
        }

        var runs = await host.ReadRunsAsync();
        Assert.Equal(3, runs.Count);
        foreach (var commit in commits)
        {
            var run = Assert.Single(runs, candidate => candidate.GitCommit == commit);
            Assert.Equal(BuildRunTriggerType.Webhook, run.TriggerType);
            Assert.Null(run.TriggerUserId);
            Assert.Equal("main", run.GitBranch);
            Assert.Equal("pipeline.lua", run.PipelinePath);
            Assert.Equal(BuildRunStatus.Queued, run.Status);
        }
    }

    [Fact]
    public async Task A_correct_signature_is_accepted_and_a_wrong_or_absent_one_is_a_bare_401()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("signature");
        var secret = host.SecretFor("signature");
        var payload = WebhookPayloads.GitHub("main", WebhookPayloads.Commit('1'));

        using (var accepted = await host.DeliverSignedAsync(
                   webhookId, payload, secret, deliveryId: "sig-ok"))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        // Wrong secret, absent signature and a signature over different bytes all answer the same bare 401:
        // no expected value, no hint about which scheme was tried, no body.
        using (var wrongSecret = await host.DeliverSignedAsync(
                   webhookId, payload, secret + "-wrong", deliveryId: "sig-wrong"))
        {
            await AssertBareAsync(wrongSecret, HttpStatusCode.Unauthorized);
        }

        using (var absent = await host.DeliverAsync(
                   webhookId,
                   Encoding.UTF8.GetBytes(payload),
                   deliveryId: "sig-absent"))
        {
            await AssertBareAsync(absent, HttpStatusCode.Unauthorized);
        }

        // The classic defect this card exists to avoid: signing the re-serialized model instead of the raw
        // body. The same payload signed in its compact form must NOT verify when it arrives pretty-printed
        // - a byte-exact HMAC over what arrived is the only thing that passes.
        var compact = JsonSerializer.Serialize(JsonDocument.Parse(payload).RootElement);
        var reformatted = JsonSerializer.Serialize(
            JsonDocument.Parse(payload).RootElement,
            new JsonSerializerOptions { WriteIndented = true });
        using (var reserialized = await host.DeliverAsync(
                   webhookId,
                   Encoding.UTF8.GetBytes(reformatted),
                   signature: WebhooksTestHost.Sign(Encoding.UTF8.GetBytes(compact), secret),
                   deliveryId: "sig-reformatted"))
        {
            await AssertBareAsync(reserialized, HttpStatusCode.Unauthorized);
        }

        Assert.Single(await host.ReadRunsAsync());
    }

    [Fact]
    public async Task The_raw_body_is_what_is_verified_so_whitespace_survives()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("raw-body");
        var secret = host.SecretFor("raw-body");

        // Pretty-printed, with newlines and trailing spaces: only a byte-exact HMAC over these exact bytes
        // can verify, which is what proves the endpoint never re-serialises the payload.
        var payload = "{\n  \"ref\": \"refs/heads/main\",\n  \"after\": \"" + WebhookPayloads.Commit('2')
            + "\"  \n}\n";

        using var response = await host.DeliverSignedAsync(
            webhookId, payload, secret, deliveryId: "raw-1");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var run = Assert.Single(await host.ReadRunsAsync());
        Assert.Equal(WebhookPayloads.Commit('2'), run.GitCommit);
    }

    [Fact]
    public async Task A_gitlab_shared_token_also_authenticates_and_its_absence_is_a_401()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("gitlab-token");
        var secret = host.SecretFor("gitlab-token");
        var payload = Encoding.UTF8.GetBytes(
            WebhookPayloads.GitLab("main", WebhookPayloads.Commit('3')));

        using (var wrong = await host.DeliverAsync(
                   webhookId, payload, gitLabToken: "not-the-secret", deliveryId: "gl-wrong"))
        {
            await AssertBareAsync(wrong, HttpStatusCode.Unauthorized);
        }

        using (var accepted = await host.DeliverAsync(
                   webhookId, payload, gitLabToken: secret, deliveryId: "gl-ok"))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        var run = Assert.Single(await host.ReadRunsAsync());
        Assert.Equal(WebhookPayloads.Commit('3'), run.GitCommit);
        Assert.Equal("main", run.GitBranch);
    }

    [Fact]
    public async Task Malformed_missing_fields_and_an_oversized_body_are_refused_with_no_run()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("rejections");
        var secret = host.SecretFor("rejections");
        var delivery = 0;

        foreach (var body in new[]
                 {
                     "{ not json",
                     "[]",
                     WebhookPayloads.WithoutAfter("main"),
                     WebhookPayloads.WithoutRef(WebhookPayloads.Commit('4')),
                     """{ "ref": "refs/heads/main", "after": "not-a-commit" }""",
                 })
        {
            using var response = await host.DeliverSignedAsync(
                webhookId, body, secret, deliveryId: $"bad-{delivery++}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // The cap is counted while reading: 1 MB + 1 byte is refused before it is held, and before parsing.
        var oversized = Encoding.UTF8.GetBytes(new string('x', 1024 * 1024 + 1));
        using (var tooLarge = await host.DeliverAsync(
                   webhookId,
                   oversized,
                   signature: WebhooksTestHost.Sign(oversized, secret),
                   deliveryId: "oversized"))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
            Assert.Empty(await tooLarge.Content.ReadAsByteArrayAsync(CancellationToken));
        }

        Assert.Empty(await host.ReadRunsAsync());
    }

    [Fact]
    public async Task A_delete_event_is_accepted_and_creates_nothing()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("delete-event");
        var secret = host.SecretFor("delete-event");

        using var response = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("main", WebhookPayloads.Deleted),
            secret,
            deliveryId: "delete-1");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("ignored:delete", await ReadStatusAsync(response));
        Assert.Equal("ignored:delete", await ReadOutcomeAsync(response));
        Assert.Empty(await host.ReadRunsAsync());
    }

    [Fact]
    public async Task A_tag_push_is_ignored_rather_than_built_as_a_branch()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("tag-push");
        var secret = host.SecretFor("tag-push");

        using var response = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.Tag(WebhookPayloads.Commit('5')),
            secret,
            deliveryId: "tag-1");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("ignored:not-branch", await ReadOutcomeAsync(response));
        Assert.Empty(await host.ReadRunsAsync());
    }

    [Fact]
    public async Task The_branch_filter_selects_exact_names_and_a_trailing_wildcard()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("filter", branchFilter: "main,release/*");
        var secret = host.SecretFor("filter");

        using (var other = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("feature/x", WebhookPayloads.Commit('6')),
                   secret,
                   deliveryId: "branch-other"))
        {
            Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);
            Assert.Equal("ignored:branch", await ReadStatusAsync(other));
            Assert.Equal("ignored:branch", await ReadOutcomeAsync(other));
        }

        Assert.Empty(await host.ReadRunsAsync());

        using (var release = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("release/1.2", WebhookPayloads.Commit('7')),
                   secret,
                   deliveryId: "branch-release"))
        {
            Assert.Equal(HttpStatusCode.Accepted, release.StatusCode);
            Assert.Equal("queued", await ReadStatusAsync(release));
        }

        var releaseRun = Assert.Single(await host.ReadRunsAsync());
        Assert.Equal("release/1.2", releaseRun.GitBranch);
        Assert.Equal(WebhookPayloads.Commit('7'), releaseRun.GitCommit);
    }

    [Fact]
    public async Task An_empty_filter_follows_the_projects_default_branch()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("default-branch", branchFilter: string.Empty);
        var secret = host.SecretFor("default-branch");

        using (var other = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("develop", WebhookPayloads.Commit('8')),
                   secret,
                   deliveryId: "default-other"))
        {
            Assert.Equal("ignored:branch", await ReadOutcomeAsync(other));
        }

        Assert.Empty(await host.ReadRunsAsync());

        using (var main = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub(WebhooksTestHost.DefaultBranch, WebhookPayloads.Commit('9')),
                   secret,
                   deliveryId: "default-main"))
        {
            Assert.Equal("queued", await ReadStatusAsync(main));
        }

        Assert.Single(await host.ReadRunsAsync());
    }

    [Fact]
    public async Task The_run_carries_the_pushed_commit_and_the_claim_uses_the_projects_repository()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("run-facts");
        var secret = host.SecretFor("run-facts");
        var commit = WebhookPayloads.Commit('b');
        var token = await host.CreateWorkerAsync();

        // The payload names https://evil.example/attacker.git; the claim must still hand the worker the
        // project's repository - a valid signature can never redirect a build.
        using var response = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("main", commit),
            secret,
            deliveryId: "facts-1");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var run = Assert.Single(await host.ReadRunsAsync());
        Assert.Equal(BuildRunTriggerType.Webhook, run.TriggerType);
        Assert.Null(run.TriggerUserId);
        Assert.Equal("main", run.GitBranch);
        Assert.Equal(commit, run.GitCommit);
        Assert.Equal("pipeline.lua", run.PipelinePath);

        using var claimed = await host.ClaimAsync(token);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        Assert.Equal(run.Id, await RunTestHost.ReadClaimedRunIdAsync(claimed));
        Assert.Equal(
            WebhooksTestHost.RepositoryUrl,
            await RunTestHost.ReadClaimStringAsync(claimed, "repositoryUrl"));
        Assert.Equal("main", await RunTestHost.ReadClaimStringAsync(claimed, "branch"));
        Assert.Equal("pipeline.lua", await RunTestHost.ReadClaimStringAsync(claimed, "pipelinePath"));
    }

    [Fact]
    public async Task A_webhook_run_whose_tags_no_worker_covers_stays_queued_and_is_still_deduplicated()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync(
            "queued",
            requiredTags: "no-worker-has-this");
        var secret = host.SecretFor("queued");
        var payload = WebhookPayloads.GitHub("main", WebhookPayloads.Commit('c'));

        // A worker exists, but its tags do not cover the run's requirement.
        var token = await host.CreateWorkerAsync(tags: "ci");

        using (var first = await host.DeliverSignedAsync(webhookId, payload, secret, deliveryId: "queued-1"))
        {
            Assert.Equal("queued", await ReadStatusAsync(first));
        }

        using (var replay = await host.DeliverSignedAsync(webhookId, payload, secret, deliveryId: "queued-1"))
        {
            Assert.Equal("duplicate", await ReadStatusAsync(replay));
        }

        using (var claimed = await host.ClaimAsync(token))
        {
            Assert.Equal(HttpStatusCode.NoContent, claimed.StatusCode);
        }

        var run = Assert.Single(await host.ReadRunsAsync());
        Assert.Equal(BuildRunStatus.Queued, run.Status);
        Assert.Equal("no-worker-has-this", run.RequiredTags);
    }


    // ------------------------------------------------------------------
    // Reviews m1, m3: the body cap and the 404 shape
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_chunked_body_over_the_cap_is_refused_without_being_buffered()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("chunked-cap");
        var secret = host.SecretFor("chunked-cap");
        var telemetry = host.Telemetry;

        // Deliberately NOT a Content-Length shortcut: a non-seekable stream makes HttpClient send the body
        // with `Transfer-Encoding: chunked`, so the server cannot know the size up front and must count while
        // reading. The old "read it all, then compare" shape passes the declared-length case and fails here.
        var oversized = new NonSeekableStream(new byte[(1024 * 1024) + 4096]);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{webhookId}")
        {
            Content = new StreamContent(oversized),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Content.Headers.ContentLength = null;
        request.Headers.TransferEncodingChunked = true;
        request.Headers.TryAddWithoutValidation(
            WebhookSignature.HubSignatureHeader,
            "sha256=" + new string('0', 64));
        request.Headers.TryAddWithoutValidation("X-GitHub-Delivery", "chunked-1");

        using var response = await host.DeliveryClient.SendAsync(request, CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(CancellationToken));

        // The observable half: one refusal, and at most the cap buffered - never the whole 1 MB + 4 KB body.
        Assert.Equal(1, telemetry.OversizedRejections);
        Assert.True(
            telemetry.BytesBuffered <= 1024 * 1024,
            $"the endpoint buffered {telemetry.BytesBuffered} bytes for a refused body (cap 1048576)");
        Assert.Empty(await host.ReadRunsAsync());

        // The same cap accepts a body under it: the refusal is the size, not the chunked shape.
        using var small = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("main", WebhookPayloads.Commit('a')),
            secret,
            deliveryId: "chunked-2");
        Assert.Equal(HttpStatusCode.Accepted, small.StatusCode);
    }

    [Fact]
    public async Task A_ref_branch_with_no_name_is_a_400_at_the_http_layer()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("empty-branch");
        var secret = host.SecretFor("empty-branch");

        // `refs/heads/` is a ref that names no branch: it is not "a ref that is not a branch" (202-ignored)
        // and it is not a usable push, so it is the payload rejection.
        using var response = await host.DeliverSignedAsync(
            webhookId,
            """{ "ref": "refs/heads/", "after": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }""",
            secret,
            deliveryId: "empty-branch-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("payload-invalid", await ReadErrorAsync(response));
        Assert.Empty(await host.ReadRunsAsync());
    }

    [Fact]
    public async Task An_unmapped_route_answers_the_same_bare_shape_as_an_unknown_webhook()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var disabledWebhookId = await host.SeedWebhookAsync("disabled-dead", enabled: false);
        var body = Encoding.UTF8.GetBytes(WebhookPayloads.GitHub("main", WebhookPayloads.Commit('b')));

        using var unknownWebhook = await host.DeliverAsync(
            Guid.NewGuid(), body, signature: WebhooksTestHost.Sign(body, "whatever"), deliveryId: "shape-unknown");
        using var disabledWebhook = await host.DeliverAsync(
            disabledWebhookId, body, signature: WebhooksTestHost.Sign(body, "whatever"), deliveryId: "shape-disabled");

        // The three webhook misses are the same answer, four-for-four.
        await AssertSameMissShapeAsync(unknownWebhook, disabledWebhook);
        Assert.Equal(HttpStatusCode.NotFound, unknownWebhook.StatusCode);

        // ...and that answer has the shape routing itself produces for a request it cannot serve: no body, no
        // content type, zero length. The status differs on purpose and the difference is not this endpoint's:
        // this app maps static assets as a GET/HEAD catch-all, so a POST to any unmapped path is routing's
        // "method not allowed" (405) - a 404 body would still be this endpoint's invention, which is exactly
        // what the test refuses to accept.
        using var unmapped = await host.DeliveryClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/no-such-endpoint-at-all"),
            CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, unmapped.StatusCode);
        Assert.Null(unmapped.Content.Headers.ContentType);
        Assert.Equal(unmapped.Content.Headers.ContentLength, unknownWebhook.Content.Headers.ContentLength);
        Assert.Empty(await unmapped.Content.ReadAsByteArrayAsync(CancellationToken));
    }

    /// <summary>
    /// Two misses must be indistinguishable in all four observable parts: status, content type, content
    /// length and the bytes themselves.
    /// </summary>
    private static async Task AssertSameMissShapeAsync(
        HttpResponseMessage expected,
        HttpResponseMessage actual)
    {
        Assert.Equal(expected.StatusCode, actual.StatusCode);
        Assert.Equal(expected.Content.Headers.ContentType, actual.Content.Headers.ContentType);
        Assert.Equal(expected.Content.Headers.ContentLength, actual.Content.Headers.ContentLength);

        var expectedBody = await expected.Content.ReadAsByteArrayAsync(CancellationToken);
        var actualBody = await actual.Content.ReadAsByteArrayAsync(CancellationToken);
        Assert.Equal(expectedBody, actualBody);
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("error", out var value) ? value.GetString() : null;
    }

    /// <summary>A read-once, unseekable stream, so HttpClient must send the body chunked.</summary>
    private sealed class NonSeekableStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------
    // Review (a): the rollback arm of the delivery transaction
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_delivery_whose_run_cannot_be_queued_leaves_no_record_and_a_retry_still_builds()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        var webhookId = await host.SeedWebhookAsync("rollback", branchFilter: "bad*");
        var secret = host.SecretFor("rollback");
        const string deliveryId = "rollback-1";

        // This ref passes the payload parser (it is a `refs/heads/*` ref) and the webhook's own filter (`bad*`),
        // but the branch name is not a valid git ref - so the run cannot be staged. That reaches the delivery
        // transaction's failure arm: the replay record was already inserted in this transaction and must
        // disappear with it, exactly as it would if the project had been deleted between the parse and the
        // enqueue.
        using (var refused = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("bad..x", WebhookPayloads.Commit('1')),
                   secret,
                   deliveryId: deliveryId))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("payload-invalid", await ReadErrorAsync(refused));
        }

        // Nothing committed: no run, and - the part that matters - no replay record that would make the retry
        // answer "duplicate" for a delivery that never built anything.
        Assert.Empty(await host.ReadRunsAsync());
        Assert.Empty(await host.ReadDeliveriesAsync(webhookId));

        // The corrected push re-sent with the SAME delivery id (a git host retrying) is a fresh delivery, not a
        // duplicate: the failed attempt did not occupy the key.
        using (var queued = await host.DeliverSignedAsync(
                   webhookId,
                   WebhookPayloads.GitHub("bad-feature", WebhookPayloads.Commit('2')),
                   secret,
                   deliveryId: deliveryId))
        {
            Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
            Assert.Equal("queued", await ReadStatusAsync(queued));
        }

        var run = Assert.Single(await host.ReadRunsAsync());
        Assert.Equal("bad-feature", run.GitBranch);
        Assert.Equal(WebhookPayloads.Commit('2'), run.GitCommit);
        Assert.Single(await host.ReadDeliveriesAsync(webhookId));
    }

    [Fact]
    public async Task Staging_a_run_for_a_project_that_is_gone_is_a_not_found()
    {
        using var host = await WebhooksTestHost.CreateAsync();

        // The other arm of the same branch: the transaction's stage step answers NotFound when the project is
        // no longer there, which the endpoint maps to the same bare 404. Pinned at the service seam because a
        // delivery cannot lose its project between the parse and the enqueue without a fault-injection hook.
        var result = await host.Host.InScopeAsync(async services =>
        {
            services.GetRequiredService<Zenkins.Server.Data.Tenancy.TenantContext>().OrganizationId =
                host.OrganizationId;
            await using var context = await services
                .GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Zenkins.Server.Data.ZenkinsDbContext>>()
                .CreateDbContextAsync(CancellationToken);
            return await services.GetRequiredService<Zenkins.Server.Runs.RunService>().QueueWebhookRunAsync(
                context,
                Guid.NewGuid(),
                "main",
                WebhookPayloads.Commit('3'),
                [],
                CancellationToken);
        });

        Assert.Equal(Zenkins.Server.Runs.RunOperationStatus.NotFound, result.Status);
        Assert.Empty(await host.ReadRunsAsync());
    }

    // ------------------------------------------------------------------
    // Shared assertions
    // ------------------------------------------------------------------

    private static async Task AssertBareAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);

        // A rejection never explains itself: no bytes, and no content type to hint at what was produced.
        Assert.Null(response.Content.Headers.ContentType);
        var body = await response.Content.ReadAsByteArrayAsync(CancellationToken);
        Assert.Empty(body);
    }

    private static async Task<string?> ReadStatusAsync(HttpResponseMessage response) =>
        await ReadStringPropertyAsync(response, "status");

    private static async Task<string?> ReadOutcomeAsync(HttpResponseMessage response) =>
        await ReadStringPropertyAsync(response, "outcome");

    private static async Task<string?> ReadStringPropertyAsync(HttpResponseMessage response, string property)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty(property, out var value) ? value.GetString() : null;
    }
}
