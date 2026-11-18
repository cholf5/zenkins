using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.Api;

/// <summary>
/// Card F-027's contract tests for the organization API: the trigger (201, the form's validation and
/// codes, the F-028/F-029 409s), the run resource, the log tail, cancel, paging, the idempotency key, and
/// the four refusal shapes (401/403/404/429) that must never leak existence.
/// </summary>
[Trait("Category", "Api")]
public sealed class ApiV1RunApiTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    /// <summary>
    /// A named status assertion (card Q-017): the tag dimension, the tag-set dimension and the parameter
    /// dimension are separate facts, and a red one must say which payload difference moved so a mutation
    /// of the fingerprint cannot be mistaken for another dimension's failure.
    /// </summary>
    private static void AssertStatus(HttpStatusCode expected, HttpStatusCode actual, string dimension) =>
        Assert.True(
            actual == expected,
            $"{dimension}: expected {(int)expected}, got {(int)actual} - "
                + (expected == HttpStatusCode.OK ? "a legal resend must still replay." : "a refused payload must be refused."));

    [Fact]
    public async Task A_token_triggers_a_run_and_the_reply_carries_the_run_id()
    {
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var token = await host.MintTokenPlaintextAsync(owner, "deploy");
        using var api = host.CreateApiClient(token);

        using var response = await ApiV1TestHost.TriggerAsync(api, projectId, new { branch = "main" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = await ApiV1TestHost.ReadJsonAsync(response);
        var runId = body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/runs/{runId}", response.Headers.Location!.OriginalString);

        // The run really is queued for the project, attributed to the API (nobody was signed in).
        var runs = await host.ReadProjectRunsAsync(projectId);
        var run = Assert.Single(runs);
        Assert.Equal(runId, run.Id);
        Assert.Equal(BuildRunTriggerType.Api, run.TriggerType);
        Assert.Null(run.TriggerUserId);
        Assert.Equal(BuildRunStatus.Queued, run.Status);

        // TC-001: the resource the 201 points at reads back the same run.
        using var read = await ApiV1TestHost.GetAsync(api, $"/api/v1/runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var runBody = await ApiV1TestHost.ReadJsonAsync(read);
        Assert.Equal(runId, runBody.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(projectId, runBody.RootElement.GetProperty("projectId").GetGuid());
        Assert.Equal("Queued", runBody.RootElement.GetProperty("status").GetString());
        Assert.Equal("Api", runBody.RootElement.GetProperty("triggerType").GetString());

        // TC-001: the token's last-used mark moved.
        var tokens = await host.ReadApiTokensAsync();
        Assert.Contains(tokens, row => row.Name == "deploy" && row.LastUsedAt is not null);
    }

    [Fact]
    public async Task The_api_answers_the_form_s_parameter_codes()
    {
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        await host.SetParameterDefinitionsAsync(
            projectId,
            """[{"name":"GREETING","type":"string","defaultValue":"hello"}]""");
        var token = await host.MintTokenPlaintextAsync(owner, "params");
        using var api = host.CreateApiClient(token);

        // An unknown parameter name is the form's own refusal code, not a binder's 400.
        using var refused = await ApiV1TestHost.TriggerAsync(
            api,
            projectId,
            new { parameters = new Dictionary<string, string> { ["NOPE"] = "x" } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using (var refusedBody = await ApiV1TestHost.ReadJsonAsync(refused))
        {
            Assert.Equal("parameter-unknown", refusedBody.RootElement.GetProperty("error").GetString());
            Assert.Equal(
                "parameter-unknown",
                refusedBody.RootElement.GetProperty("fieldErrors")[0].GetProperty("code").GetString());
        }

        // The form answers the same code for the same input (parity, not a second rule).
        var formResponse = await RunTestHost.StartRunAsync(
            owner,
            projectId,
            parameters: new Dictionary<string, string> { ["NOPE"] = "x" });
        Assert.Equal(HttpStatusCode.Redirect, formResponse.StatusCode);
        Assert.Contains("runError=parameter-unknown", formResponse.Headers.Location!.OriginalString, StringComparison.Ordinal);

        // A valid value is accepted and frozen into the run snapshot.
        using var accepted = await ApiV1TestHost.TriggerAsync(
            api,
            projectId,
            new { parameters = new Dictionary<string, string> { ["GREETING"] = "hi" } });
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var runs = await host.ReadProjectRunsAsync(projectId);
        var run = Assert.Single(runs);
        Assert.Contains("\"GREETING\":\"hi\"", run.ParametersJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disabled_project_answers_409_project_disabled_and_queues_nothing()
    {
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var token = await host.MintTokenPlaintextAsync(owner, "disabled");

        using (var disabled = await RunTestHost.DisableProjectAsync(owner, projectId))
        {
            Assert.True((int)disabled.StatusCode is 302 or 303);
        }

        using var api = host.CreateApiClient(token);
        using var response = await ApiV1TestHost.TriggerAsync(api, projectId, new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using (var body = await ApiV1TestHost.ReadJsonAsync(response))
        {
            Assert.Equal("project-disabled", body.RootElement.GetProperty("error").GetString());
        }

        // Nothing was queued; the form says the same word.
        Assert.Empty(await host.ReadProjectRunsAsync(projectId));
        var formResponse = await RunTestHost.StartRunAsync(owner, projectId);
        Assert.Contains("runError=project-disabled", formResponse.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_full_queue_answers_409_queue_full_and_queues_nothing()
    {
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var token = await host.MintTokenPlaintextAsync(owner, "cap");

        var saved = await RunTestHost.SaveLimitsAsync(owner, projectId, maxQueuedRuns: 1);
        Assert.True((int)saved.StatusCode is 302 or 303);
        await host.Inner.AddRunAsync(projectId);

        using var api = host.CreateApiClient(token);
        using var response = await ApiV1TestHost.TriggerAsync(api, projectId, new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using (var body = await ApiV1TestHost.ReadJsonAsync(response))
        {
            Assert.Equal("queue-full", body.RootElement.GetProperty("error").GetString());
        }

        Assert.Single(await host.ReadProjectRunsAsync(projectId));
    }

    [Fact]
    public async Task An_unknown_project_is_a_bare_404_that_creates_nothing()
    {
        using var host = await ApiV1TestHost.CreateAsync();
        var (_, owner) = await host.CreateProjectAsync();
        var token = await host.MintTokenPlaintextAsync(owner, "unknown-project");
        using var api = host.CreateApiClient(token);

        using var response = await ApiV1TestHost.TriggerAsync(api, Guid.NewGuid(), new { });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(CancellationToken));
    }

    [Fact]
    public async Task A_queued_run_is_canceled_and_a_second_cancel_is_a_friendly_no_op()
    {
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var token = await host.MintTokenPlaintextAsync(owner, "cancel");
        var runId = await host.Inner.AddRunAsync(projectId);
        using var api = host.CreateApiClient(token);

        using var canceled = await api.PostAsync($"/api/v1/runs/{runId}/cancel", content: null, CancellationToken);
        Assert.Equal(HttpStatusCode.OK, canceled.StatusCode);
        using (var body = await ApiV1TestHost.ReadJsonAsync(canceled))
        {
            Assert.Equal("canceled", body.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("Canceled", body.RootElement.GetProperty("status").GetString());
        }

        var run = await host.Inner.ReadRunAsync(runId);
        Assert.Equal(BuildRunStatus.Canceled, run.Status);

        // The same word the page's button produces for an already-terminal run.
        using var again = await api.PostAsync($"/api/v1/runs/{runId}/cancel", content: null, CancellationToken);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using var againBody = await ApiV1TestHost.ReadJsonAsync(again);
        Assert.Equal("already-finished", againBody.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task The_idempotency_key_queues_one_run_for_a_retried_post()
    {
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var token = await host.MintTokenPlaintextAsync(owner, "idempotent");
        using var api = host.CreateApiClient(token);

        using var first = await ApiV1TestHost.TriggerAsync(api, projectId, new { }, idempotencyKey: "deploy-47");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var firstBody = await ApiV1TestHost.ReadJsonAsync(first);
        var runId = firstBody.RootElement.GetProperty("id").GetGuid();

        using var retry = await ApiV1TestHost.TriggerAsync(api, projectId, new { }, idempotencyKey: "deploy-47");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("true", Assert.Single(retry.Headers.GetValues("Idempotency-Replayed")));
        using var retryBody = await ApiV1TestHost.ReadJsonAsync(retry);
        Assert.Equal(runId, retryBody.RootElement.GetProperty("id").GetGuid());

        // One key, one run - even though the body was posted twice.
        Assert.Single(await host.ReadProjectRunsAsync(projectId));

        // A blank key is a malformed request, not an anonymous retry.
        using var blank = await ApiV1TestHost.TriggerAsync(api, projectId, new { }, idempotencyKey: " ");
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    [Fact]
    public async Task A_resend_that_differs_only_in_a_refused_tag_is_refused_while_the_same_tags_still_replay()
    {
        // Card Q-017: the fingerprint hashes the tags as *sent*. The list the trigger path accepts is the
        // wrong input, because it silently drops exactly the entries validation refuses - so a resend
        // differing only in such a tag hashed identically and replayed a 200 where a new key is a 400.
        // Only two kinds of entry are dropped: one longer than MaxTagLength, and one that still carries a
        // non-whitespace control character after trimming (MaxRequiredTagsLength is a refusal of its own,
        // not a drop). The whitespace controls tab/vertical-tab/form-feed are trimmed away and are *not*
        // refused, so a tag list padded with those is still the same request and must still replay.
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var token = await host.MintTokenPlaintextAsync(owner, "tagged-key");
        using var api = host.CreateApiClient(token);

        // The organization needs a worker that covers the base tags, or the base trigger is refused for
        // the other reason (tag-unknown) - this fact is about the key, not about claimability.
        await host.Inner.RegisterWorkerAsync(name: "tagged-agent", tags: "linux,gpu");


        Guid firstRunId;
        using (var first = await ApiV1TestHost.TriggerAsync(
            api, projectId, new { branch = "main", requiredTags = "linux, gpu" }, idempotencyKey: "tags-1"))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            using var body = await ApiV1TestHost.ReadJsonAsync(first);
            firstRunId = body.RootElement.GetProperty("id").GetGuid();
        }

        // (a) The same tag set, re-spelled: another order, padded, a repeated entry, and a list whose
        //     padding is made of whitespace controls (trimmed, never refused). Every reader treats tags
        //     as a trimmed set, so this is the request that already ran - it replays.
        foreach (var respelled in new[] { " gpu , linux ", "gpu,linux,gpu", " linux\t, gpu\v", "linux, gpu" })
        {
            using var replay = await ApiV1TestHost.TriggerAsync(
                api, projectId, new { branch = "main", requiredTags = respelled }, idempotencyKey: "tags-1");
            AssertStatus(
                HttpStatusCode.OK,
                replay.StatusCode,
                $"tag dimension: the legal re-spelling '{respelled.Replace("\t", "\\t", StringComparison.Ordinal).Replace("\v", "\\v", StringComparison.Ordinal)}' must still replay the run it repeats");
            Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotency-Replayed")));
            using var body = await ApiV1TestHost.ReadJsonAsync(replay);
            Assert.Equal(firstRunId, body.RootElement.GetProperty("id").GetGuid());
        }

        Assert.Single(await host.ReadProjectRunsAsync(projectId));

        // (b) The differences that are *refusals*: an over-long entry, a control-bearing one, and the
        //     adversarial one - "lin\u0001ux" is dropped as control-bearing, so a fingerprint that
        //     sanitized instead of hashing as sent would collapse it onto the first "linux" and replay.
        var refusedTags = new[]
        {
            $"linux, gpu, {new string('a', RunValidation.MaxTagLength + 1)}",
            "linux, gpu, bad\u0001tag",
            "linux, gpu, lin\u0001ux",
        };
        foreach (var refused in refusedTags)
        {
            var shown = refused.Replace("\u0001", "<U+0001>", StringComparison.Ordinal);
            using (var reused = await ApiV1TestHost.TriggerAsync(
                api, projectId, new { branch = "main", requiredTags = refused }, idempotencyKey: "tags-1"))
            {
                AssertStatus(
                    HttpStatusCode.Conflict,
                    reused.StatusCode,
                    $"tag dimension: a resend differing only in the refused tag '{shown}' is not the request that ran");
                using var body = await ApiV1TestHost.ReadJsonAsync(reused);
                Assert.Equal("idempotency-key-reused", body.RootElement.GetProperty("error").GetString());
                Assert.False(reused.Headers.Contains("Idempotency-Replayed"));
            }

            // The symmetry that makes this a refusal rather than a hiccup: a fresh key sending the very
            // same body is refused on its merits, as tag-invalid. Without this the assertion above could
            // pass while the body was refused for some unrelated reason.
            using var freshKey = await ApiV1TestHost.TriggerAsync(
                api, projectId, new { branch = "main", requiredTags = refused }, idempotencyKey: "tags-2");
            Assert.Equal(HttpStatusCode.BadRequest, freshKey.StatusCode);
            using var freshBody = await ApiV1TestHost.ReadJsonAsync(freshKey);
            Assert.Equal("tag-invalid", freshBody.RootElement.GetProperty("error").GetString());
            Assert.Equal(
                "requiredTags",
                freshBody.RootElement.GetProperty("fieldErrors")[0].GetProperty("field").GetString());
        }

        // Nothing extra was queued by any of the refusals, and the accepted request is still the one the
        // key names (a refusal must not re-point or overwrite the binding).
        var run = Assert.Single(await host.ReadProjectRunsAsync(projectId));
        Assert.Equal(firstRunId, run.Id);
        var record = Assert.Single(await host.ReadIdempotencyRecordsAsync());
        Assert.Equal(firstRunId, record.RunId);

        // The legal retry is still a replay after all of that - the fingerprint did not become an
        // "anything that differs is new" rule, and the refused attempts left no trace behind.
        using var retried = await ApiV1TestHost.TriggerAsync(
            api, projectId, new { branch = "main", requiredTags = "gpu, linux" }, idempotencyKey: "tags-1");
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal("true", Assert.Single(retried.Headers.GetValues("Idempotency-Replayed")));
        Assert.Single(await host.ReadProjectRunsAsync(projectId));
    }

    [Fact]
    public async Task A_reused_key_refuses_a_different_tag_set_before_the_worker_coverage_check()
    {
        // Card Q-017, the tag dimension of AC-2 (branch and timeout already had their assertions). The
        // second tag is *legal* - so the difference is not a validation error - but no worker covers it.
        // The key's decision is taken before field validation and before the coverage check, so the answer
        // is the key's refusal; a route that validated first would answer 400 tag-unknown instead.
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var token = await host.MintTokenPlaintextAsync(owner, "tag-set");
        using var api = host.CreateApiClient(token);
        await host.Inner.RegisterWorkerAsync(name: "linux-agent", tags: "linux");

        Guid firstRunId;
        using (var first = await ApiV1TestHost.TriggerAsync(
            api, projectId, new { branch = "main", requiredTags = "linux" }, idempotencyKey: "tagset-1"))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            using var body = await ApiV1TestHost.ReadJsonAsync(first);
            firstRunId = body.RootElement.GetProperty("id").GetGuid();
        }

        using (var changedTags = await ApiV1TestHost.TriggerAsync(
            api, projectId, new { branch = "main", requiredTags = "linux,docker" }, idempotencyKey: "tagset-1"))
        {
            AssertStatus(
                HttpStatusCode.Conflict,
                changedTags.StatusCode,
                "tag-set dimension: one added tag is a different payload");
            using var body = await ApiV1TestHost.ReadJsonAsync(changedTags);
            Assert.Equal("idempotency-key-reused", body.RootElement.GetProperty("error").GetString());
            Assert.False(changedTags.Headers.Contains("Idempotency-Replayed"));
        }

        // The control, under the contract card F-033 established: a tag nobody carries is no longer a field
        // refusal, it is a queued run waiting for a Worker - so a fresh key sending that body is **accepted**.
        // That is what makes the 409 above purely the key's decision: the body would have been taken.
        Guid queuedRunId;
        using (var freshKey = await ApiV1TestHost.TriggerAsync(
            api, projectId, new { branch = "main", requiredTags = "linux,docker" }, idempotencyKey: "tagset-2"))
        {
            Assert.Equal(HttpStatusCode.Created, freshKey.StatusCode);
            using var body = await ApiV1TestHost.ReadJsonAsync(freshKey);
            queuedRunId = body.RootElement.GetProperty("id").GetGuid();
        }

        // ...and the run waiting for `docker` is there, queued, with the requirement it waits on.
        var waiting = Assert.Single(
            await host.ReadProjectRunsAsync(projectId),
            run => run.Id == queuedRunId);
        Assert.Equal("Queued", waiting.Status);
        Assert.Equal("linux,docker", waiting.RequiredTags);
        Assert.Equal("docker&&linux", waiting.RequiredRequirement);

        var replayedRun = Assert.Single(await host.ReadProjectRunsAsync(projectId), run => run.Id == firstRunId);
        Assert.Equal(firstRunId, replayedRun.Id);
    }

    [Fact]
    public async Task A_reused_key_refuses_a_different_parameter_value()
    {
        // Card Q-017, the parameter dimension of AC-2. A value change is a different payload; the control
        // below shows the changed body is otherwise acceptable, so the 409 is the key's decision and not
        // the parameter validator's.
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        await host.SetParameterDefinitionsAsync(
            projectId,
            """[{"name":"GREETING","type":"string","defaultValue":"hello"}]""");
        var token = await host.MintTokenPlaintextAsync(owner, "param-scope");
        using var api = host.CreateApiClient(token);

        Guid firstRunId;
        using (var first = await ApiV1TestHost.TriggerAsync(
            api,
            projectId,
            new { branch = "main", parameters = new Dictionary<string, string> { ["GREETING"] = "hi" } },
            idempotencyKey: "params-1"))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            using var body = await ApiV1TestHost.ReadJsonAsync(first);
            firstRunId = body.RootElement.GetProperty("id").GetGuid();
        }

        using (var changedValue = await ApiV1TestHost.TriggerAsync(
            api,
            projectId,
            new { branch = "main", parameters = new Dictionary<string, string> { ["GREETING"] = "bye" } },
            idempotencyKey: "params-1"))
        {
            AssertStatus(
                HttpStatusCode.Conflict,
                changedValue.StatusCode,
                "parameters dimension: one changed value is a different payload");
            using var body = await ApiV1TestHost.ReadJsonAsync(changedValue);
            Assert.Equal("idempotency-key-reused", body.RootElement.GetProperty("error").GetString());
            Assert.False(changedValue.Headers.Contains("Idempotency-Replayed"));
        }

        Assert.Single(await host.ReadProjectRunsAsync(projectId));

        // The control: the changed body is accepted under a new key, so the refusal above is about the key.
        using (var freshKey = await ApiV1TestHost.TriggerAsync(
            api,
            projectId,
            new { branch = "main", parameters = new Dictionary<string, string> { ["GREETING"] = "bye" } },
            idempotencyKey: "params-2"))
        {
            Assert.Equal(HttpStatusCode.Created, freshKey.StatusCode);
        }

        // Two runs now (the base and the control), and the first key still names the first run.
        Assert.Equal(2, (await host.ReadProjectRunsAsync(projectId)).Count);
        var record = Assert.Single(await host.ReadIdempotencyRecordsAsync(), row => row.Key == "params-1");
        Assert.Equal(firstRunId, record.RunId);
    }

    [Fact]
    public async Task The_fingerprint_of_a_legal_request_is_the_byte_the_deployed_algorithm_produced()
    {
        // Card Q-017's frozen values. The RequestHash is stored and compared for the key's whole retention
        // window, so a legal request's hash must not move: if it did, every key already in the table would
        // refuse its own retry with 409 - the bug this card fixes, made worse. These two digests are the
        // output of the algorithm as it shipped before the tags input changed, computed from its own
        // framing (branch.trim, the accepted tag list ordered OrdinalIgnoreCase-then-Ordinal, timeout.trim,
        // the parameter count, then name/value pairs by Ordinal - each field written "{length}:{value};"):
        //
        //   4:main;12:dotnet,Linux;2:30;1:1;8:GREETING;2:hi;
        //   4:main;0:;0:;1:0;
        //
        // A fingerprint that sanitized tags, widened the fields or reordered them would break these.
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        await host.SetParameterDefinitionsAsync(
            projectId,
            """[{"name":"GREETING","type":"string","defaultValue":"hello"}]""");
        var token = await host.MintTokenPlaintextAsync(owner, "frozen-digest");
        using var api = host.CreateApiClient(token);
        await host.Inner.RegisterWorkerAsync(name: "digest-agent", tags: "dotnet,Linux");

        using (var full = await ApiV1TestHost.TriggerAsync(
            api,
            projectId,
            new
            {
                branch = "main",
                requiredTags = "dotnet,Linux",
                timeoutMinutes = "30",
                parameters = new Dictionary<string, string> { ["GREETING"] = "hi" },
            },
            idempotencyKey: "digest-1"))
        {
            Assert.Equal(HttpStatusCode.Created, full.StatusCode);
        }

        using (var branchOnly = await ApiV1TestHost.TriggerAsync(
            api, projectId, new { branch = "main" }, idempotencyKey: "digest-2"))
        {
            Assert.Equal(HttpStatusCode.Created, branchOnly.StatusCode);
        }

        var records = await host.ReadIdempotencyRecordsAsync();
        Assert.Equal(
            "a7413c7e579d36a8fe90b723434ca771286c9cd447bbf4be55636d5f5e118c1c",
            Assert.Single(records, row => row.Key == "digest-1").RequestHash);
        Assert.Equal(
            "70982699a4a519ea2d90305a08ed6a489f9ab5d079323c78680112320ba5ace8",
            Assert.Single(records, row => row.Key == "digest-2").RequestHash);
    }

    [Fact]
    public async Task A_reused_key_cannot_straddle_projects_or_payloads_and_an_unknown_project_stays_a_bare_404()
    {
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectA, owner) = await host.CreateProjectAsync("project-a");
        var organizationId = await host.Inner.OrganizationIdAsync();
        var projectB = await host.Inner.Inner.Inner.AddProjectAsync(organizationId, "project-b");
        var token = await host.MintTokenPlaintextAsync(owner, "scoped-key");
        using var api = host.CreateApiClient(token);

        using (var first = await ApiV1TestHost.TriggerAsync(api, projectA, new { branch = "main" }, idempotencyKey: "deploy-1"))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        var firstRun = Assert.Single(await host.ReadProjectRunsAsync(projectA));

        // (a) Same key, another project: a refusal - never project A's run id or Location.
        using (var crossed = await ApiV1TestHost.TriggerAsync(api, projectB, new { branch = "main" }, idempotencyKey: "deploy-1"))
        {
            Assert.Equal(HttpStatusCode.Conflict, crossed.StatusCode);
            using var body = await ApiV1TestHost.ReadJsonAsync(crossed);
            Assert.Equal("idempotency-key-reused", body.RootElement.GetProperty("error").GetString());

            // The 409 carries the field errors a form refusal carries (documented shape).
            var fieldErrors = body.RootElement.GetProperty("fieldErrors");
            Assert.Equal("idempotency-key", fieldErrors[0].GetProperty("field").GetString());
            Assert.Equal("idempotency-key-reused", fieldErrors[0].GetProperty("code").GetString());
            Assert.False(crossed.Headers.Contains("Idempotency-Replayed"));
        }

        // Nothing was queued for B, and A still owns exactly its one run.
        Assert.Empty(await host.ReadProjectRunsAsync(projectB));
        Assert.Single(await host.ReadProjectRunsAsync(projectA));

        // (b) Same key, same project, a different payload: refused rather than replaying the old branch.
        using (var changedBranch = await ApiV1TestHost.TriggerAsync(api, projectA, new { branch = "hotfix" }, idempotencyKey: "deploy-1"))
        {
            Assert.Equal(HttpStatusCode.Conflict, changedBranch.StatusCode);
            using var body = await ApiV1TestHost.ReadJsonAsync(changedBranch);
            Assert.Equal("idempotency-key-reused", body.RootElement.GetProperty("error").GetString());
        }

        using (var changedTimeout = await ApiV1TestHost.TriggerAsync(
            api, projectA, new { branch = "main", timeoutMinutes = "30" }, idempotencyKey: "deploy-1"))
        {
            Assert.Equal(HttpStatusCode.Conflict, changedTimeout.StatusCode);
        }

        Assert.Single(await host.ReadProjectRunsAsync(projectA));

        // The identical retry is still the replay it was (the key names this request; nothing changed).
        using (var retried = await ApiV1TestHost.TriggerAsync(api, projectA, new { branch = "main" }, idempotencyKey: "deploy-1"))
        {
            Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
            Assert.Equal("true", Assert.Single(retried.Headers.GetValues("Idempotency-Replayed")));
            Assert.Equal($"/api/v1/runs/{firstRun.Id}", retried.Headers.Location!.OriginalString);
        }

        // (c) Same key, an unknown project: the bare 404 the page promises, never a 200 replay.
        using (var unknown = await ApiV1TestHost.TriggerAsync(api, Guid.NewGuid(), new { branch = "main" }, idempotencyKey: "deploy-1"))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Empty(await unknown.Content.ReadAsStringAsync(CancellationToken));
        }

        // One record per key, bound to the project and the payload that created it.
        var record = Assert.Single(await host.ReadIdempotencyRecordsAsync());
        Assert.Equal("deploy-1", record.Key);
        Assert.Equal(projectA, record.ProjectId);
        Assert.Equal(firstRun.Id, record.RunId);
        Assert.Equal(64, record.RequestHash.Length);
    }

    [Fact]
    public async Task A_replay_wins_over_the_disabled_gate_and_a_new_key_reads_the_refusal()
    {
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var token = await host.MintTokenPlaintextAsync(owner, "replay-vs-gate");
        using var api = host.CreateApiClient(token);

        using (var first = await ApiV1TestHost.TriggerAsync(api, projectId, new { branch = "main" }, idempotencyKey: "nightly"))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        var firstRun = Assert.Single(await host.ReadProjectRunsAsync(projectId));
        using (var disabled = await RunTestHost.DisableProjectAsync(owner, projectId))
        {
            Assert.True((int)disabled.StatusCode is 302 or 303);
        }

        // The decided precedence (docs/api-v1.md): a replay is the idempotency semantic itself, so the
        // retry answers the run it queued even though the project is disabled now.
        using (var replay = await ApiV1TestHost.TriggerAsync(api, projectId, new { branch = "main" }, idempotencyKey: "nightly"))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotency-Replayed")));
            using var body = await ApiV1TestHost.ReadJsonAsync(replay);
            Assert.Equal(firstRun.Id, body.RootElement.GetProperty("id").GetGuid());
        }

        // A *new* key is a new trigger, and the gate answers it: 409 project-disabled, nothing queued.
        using (var fresh = await ApiV1TestHost.TriggerAsync(api, projectId, new { branch = "main" }, idempotencyKey: "nightly-2"))
        {
            Assert.Equal(HttpStatusCode.Conflict, fresh.StatusCode);
            using var body = await ApiV1TestHost.ReadJsonAsync(fresh);
            Assert.Equal("project-disabled", body.RootElement.GetProperty("error").GetString());
        }

        Assert.Single(await host.ReadProjectRunsAsync(projectId));
    }

    [Fact]
    public async Task A_replay_wins_over_a_full_queue_and_a_new_key_reads_the_refusal()
    {
        // Card Q-017's fact for F-029's documented precedence. Before this, the rule rested on statement
        // order alone - the key was looked up before the cap counted - and no fact filled a queue. Here the
        // queue is genuinely at its cap (MaxQueuedRuns = 1 holding exactly one queued run), so the cap is
        // what a new key hits and the key's own decision is what the retry reads.
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("queue-cap-replay");

        using (var saved = await RunTestHost.SaveLimitsAsync(owner, projectId, maxQueuedRuns: 1))
        {
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
            Assert.Contains("notice=limits-saved", saved.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        var token = await host.MintTokenPlaintextAsync(owner, "replay-vs-cap");
        using var api = host.CreateApiClient(token);

        Guid occupancyRunId;
        using (var first = await ApiV1TestHost.TriggerAsync(
            api, projectId, new { branch = "main" }, idempotencyKey: "nightly"))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            using var body = await ApiV1TestHost.ReadJsonAsync(first);
            occupancyRunId = body.RootElement.GetProperty("id").GetGuid();
        }

        // The placeholder is the key's own run: exactly one row, still queued, the id the 201 returned.
        // (Not simply "a row exists" - the queue is full *because* this request's run occupies it.)
        var occupancy = Assert.Single(await host.ReadProjectRunsAsync(projectId));
        Assert.Equal(occupancyRunId, occupancy.Id);
        Assert.Equal(BuildRunStatus.Queued, occupancy.Status);

        // A *new* key is a new trigger, and the cap answers it: 409 queue-full with the field error the
        // form's vocabulary uses, no replay marker, nothing queued - and no binding for that key either
        // (a refused trigger is not a request the server accepted, so it is decided afresh next time).
        using (var fresh = await ApiV1TestHost.TriggerAsync(
            api, projectId, new { branch = "main" }, idempotencyKey: "nightly-2"))
        {
            Assert.Equal(HttpStatusCode.Conflict, fresh.StatusCode);
            using var body = await ApiV1TestHost.ReadJsonAsync(fresh);
            Assert.Equal("queue-full", body.RootElement.GetProperty("error").GetString());
            Assert.Equal("queue", body.RootElement.GetProperty("fieldErrors")[0].GetProperty("field").GetString());
            Assert.False(fresh.Headers.Contains("Idempotency-Replayed"));
        }

        Assert.Single(await host.ReadProjectRunsAsync(projectId));
        var records = await host.ReadIdempotencyRecordsAsync();
        Assert.Equal("nightly", Assert.Single(records).Key);
        Assert.DoesNotContain(records, row => row.Key == "nightly-2");

        // The cap is still the live state when the retry arrives - the occupancy is re-read, not assumed.
        var stillOccupied = Assert.Single(await host.ReadProjectRunsAsync(projectId));
        Assert.Equal(BuildRunStatus.Queued, stillOccupied.Status);

        // The *same* key is the request that already ran: 200 with the replay marker and the same run id,
        // even though the queue is still at its cap - the cap is not what a retry is asking about.
        using (var replay = await ApiV1TestHost.TriggerAsync(
            api, projectId, new { branch = "main" }, idempotencyKey: "nightly"))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotency-Replayed")));
            using var body = await ApiV1TestHost.ReadJsonAsync(replay);
            Assert.Equal(occupancyRunId, body.RootElement.GetProperty("id").GetGuid());
        }

        var stillOne = Assert.Single(await host.ReadProjectRunsAsync(projectId));
        Assert.Equal(occupancyRunId, stillOne.Id);
        Assert.Single(await host.ReadIdempotencyRecordsAsync());
    }

    [Fact]
    public async Task An_expired_key_queues_a_new_run_instead_of_replaying_forever()
    {
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var token = await host.MintTokenPlaintextAsync(owner, "nightly");
        using var api = host.CreateApiClient(token);

        using (var first = await ApiV1TestHost.TriggerAsync(api, projectId, new { branch = "main" }, idempotencyKey: "nightly"))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        var firstRunId = Assert.Single(await host.ReadProjectRunsAsync(projectId)).Id;

        // A day and a minute later (the deterministic stand-in for waiting out the retention).
        await host.AgeIdempotencyRecordAsync("nightly", ApiIdempotencyRecord.Retention + TimeSpan.FromMinutes(1));

        using (var second = await ApiV1TestHost.TriggerAsync(api, projectId, new { branch = "main" }, idempotencyKey: "nightly"))
        {
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);
            Assert.False(second.Headers.Contains("Idempotency-Replayed"));
            using var body = await ApiV1TestHost.ReadJsonAsync(second);
            Assert.NotEqual(firstRunId, body.RootElement.GetProperty("id").GetGuid());
        }

        // A second run exists, and the binding now names it - one record, re-pointed by a fresh insert.
        Assert.Equal(2, (await host.ReadProjectRunsAsync(projectId)).Count);
        var record = Assert.Single(await host.ReadIdempotencyRecordsAsync());
        Assert.NotEqual(firstRunId, record.RunId);
    }

    [Fact]
    public async Task A_replay_still_spends_the_token_s_budget()
    {
        using var host = await ApiV1TestHost.CreateAsync(builder => builder.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Api:RateLimitPerMinute"] = "2",
            }));

        var (projectId, owner) = await host.CreateProjectAsync();
        var token = await host.MintTokenPlaintextAsync(owner, "replay-budget");
        using var api = host.CreateApiClient(token);

        using (var first = await ApiV1TestHost.TriggerAsync(api, projectId, new { branch = "main" }, idempotencyKey: "budget-1"))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        // The replay is a request like any other: it spends the token's second (last) slot.
        using (var replay = await ApiV1TestHost.TriggerAsync(api, projectId, new { branch = "main" }, idempotencyKey: "budget-1"))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }

        using var limited = await ApiV1TestHost.TriggerAsync(api, projectId, new { branch = "main" }, idempotencyKey: "budget-1");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        using var body = await ApiV1TestHost.ReadJsonAsync(limited);
        Assert.Equal("rate-limited", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Concurrent_posts_with_the_same_new_key_queue_exactly_one_run()
    {
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var token = await host.MintTokenPlaintextAsync(owner, "racing");
        using var api = host.CreateApiClient(token);

        const int posts = 4;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, posts)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task.WaitAsync(CancellationToken);
                using var response = await ApiV1TestHost.TriggerAsync(
                    api, projectId, new { branch = "main" }, idempotencyKey: "race-1");
                using var body = await ApiV1TestHost.ReadJsonAsync(response);
                return (
                    Status: response.StatusCode,
                    Replayed: response.Headers.Contains("Idempotency-Replayed"),
                    RunId: body.RootElement.GetProperty("id").GetGuid());
            }))
            .ToArray();

        start.SetResult();
        var answers = await Task.WhenAll(calls);

        // Exactly one post queued the run; the others answered that same run as a replay. The counting
        // guarantee is the point: a key never queues two runs, not even when the posts overlap.
        Assert.Single(answers, answer => !answer.Replayed);
        Assert.Equal(posts - 1, answers.Count(answer => answer.Replayed));
        Assert.All(answers, answer => Assert.Equal(answers[0].RunId, answer.RunId));
        Assert.Single(await host.ReadProjectRunsAsync(projectId));
        Assert.Single(await host.ReadIdempotencyRecordsAsync());
    }

    [Fact]
    public async Task The_api_lists_projects_and_runs_with_paging()
    {
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var token = await host.MintTokenPlaintextAsync(owner, "listing");
        using var api = host.CreateApiClient(token);

        using var projects = await ApiV1TestHost.GetAsync(api, "/api/v1/projects");
        Assert.Equal(HttpStatusCode.OK, projects.StatusCode);
        using (var body = await ApiV1TestHost.ReadJsonAsync(projects))
        {
            Assert.Equal(1, body.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(projectId, body.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        }

        await host.Inner.AddRunAsync(projectId);
        await host.Inner.AddRunAsync(projectId);

        using var runs = await ApiV1TestHost.GetAsync(api, $"/api/v1/projects/{projectId}/runs?page=2&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, runs.StatusCode);
        using (var body = await ApiV1TestHost.ReadJsonAsync(runs))
        {
            Assert.Equal(2, body.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(1, body.RootElement.GetProperty("pageSize").GetInt32());
            Assert.Equal(1, body.RootElement.GetProperty("items").GetArrayLength());
        }

        using var invalid = await ApiV1TestHost.GetAsync(api, "/api/v1/projects?pageSize=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var invalidBody = await ApiV1TestHost.ReadJsonAsync(invalid);
        Assert.Equal("page-invalid", invalidBody.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_log_tail_is_the_ui_download_and_the_offset_only_returns_new_lines()
    {
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var token = await host.MintTokenPlaintextAsync(owner, "logs");
        var runId = await host.Inner.AddRunAsync(projectId);
        await host.AddLogLinesAsync(runId, "line-1", "line-2", "line-3");
        using var api = host.CreateApiClient(token);

        // The UI's own download (cookie + tenant filter) is the reference.
        using var download = await owner.GetAsync($"/runs/{runId}/log/download", CancellationToken);
        var downloaded = await download.Content.ReadAsStringAsync(CancellationToken);

        using var tail = await ApiV1TestHost.GetAsync(api, $"/api/v1/runs/{runId}/log");
        Assert.Equal(HttpStatusCode.OK, tail.StatusCode);
        Assert.Equal("text/plain", tail.Content.Headers.ContentType!.MediaType);
        Assert.Equal(downloaded, await tail.Content.ReadAsStringAsync(CancellationToken));
        Assert.Equal("3", Assert.Single(tail.Headers.GetValues("X-Zenkins-Last-Seq")));

        // Parity is the whole redaction story: the server stores what the worker already redacted, so
        // the two surfaces stream the same bytes - and neither carries the credential that read them.
        Assert.DoesNotContain(token, downloaded, StringComparison.Ordinal);

        using var fromSecond = await ApiV1TestHost.GetAsync(api, $"/api/v1/runs/{runId}/log?from=2");
        Assert.Equal("line-3\n", await fromSecond.Content.ReadAsStringAsync(CancellationToken));

        using var pastEnd = await ApiV1TestHost.GetAsync(api, $"/api/v1/runs/{runId}/log?from=99");
        Assert.Equal(string.Empty, await pastEnd.Content.ReadAsStringAsync(CancellationToken));

        using var badOffset = await ApiV1TestHost.GetAsync(api, $"/api/v1/runs/{runId}/log?from=yesterday");
        Assert.Equal(HttpStatusCode.BadRequest, badOffset.StatusCode);
        using var badBody = await ApiV1TestHost.ReadJsonAsync(badOffset);
        Assert.Equal("from-invalid", badBody.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_scopeless_route_is_a_403_and_an_unknown_or_revoked_token_is_a_bare_401()
    {
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var readOnly = await host.MintTokenPlaintextAsync(owner, "read-only", trigger: false, read: true, cancel: false);
        var triggerOnly = await host.MintTokenPlaintextAsync(owner, "trigger-only", trigger: true, read: false, cancel: false);

        using (var readClient = host.CreateApiClient(readOnly))
        {
            using var trigger = await ApiV1TestHost.TriggerAsync(readClient, projectId, new { });
            Assert.Equal(HttpStatusCode.Forbidden, trigger.StatusCode);
        }

        using (var triggerClient = host.CreateApiClient(triggerOnly))
        {
            using var read = await ApiV1TestHost.GetAsync(triggerClient, "/api/v1/projects");
            Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);

            var runId = await host.Inner.AddRunAsync(projectId);
            using var cancel = await triggerClient.PostAsync(
                $"/api/v1/runs/{runId}/cancel", content: null, CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);
        }

        // An unknown token and a revoked one are the same bare 401: no body, no tenant detail.
        using (var unknown = host.CreateApiClient("zapi_not-a-real-token"))
        {
            using var response = await ApiV1TestHost.GetAsync(unknown, "/api/v1/projects");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync(CancellationToken));
        }

        var tokens = await host.ReadApiTokensAsync();
        var revocable = Assert.Single(tokens, token => token.Name == "read-only");
        var revoked = await OrganizationTestHost.PostFormAsync(
            owner,
            "/api-tokens",
            $"/api-tokens/{revocable.Id}/revoke",
            new Dictionary<string, string>());
        Assert.True((int)revoked.StatusCode is 302 or 303);

        using (var revokedClient = host.CreateApiClient(readOnly))
        {
            using var response = await ApiV1TestHost.GetAsync(revokedClient, "/api/v1/projects");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync(CancellationToken));
        }
    }

    [Fact]
    public async Task A_session_cookie_cannot_authenticate_the_api()
    {
        using var host = await ApiV1TestHost.CreateAsync();
        var (_, owner) = await host.CreateProjectAsync();

        // The owner client carries a valid, signed-in session cookie for this very organization - and
        // still gets nothing: /api/v1 is bearer-only by policy (the API scheme names no cookie fallback).
        using var response = await ApiV1TestHost.GetAsync(owner, "/api/v1/projects");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(CancellationToken));
    }

    [Fact]
    public async Task A_missing_scope_is_a_403_before_the_budget_is_spent()
    {
        using var host = await ApiV1TestHost.CreateAsync(builder => builder.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Api:RateLimitPerMinute"] = "1",
            }));

        var (projectId, owner) = await host.CreateProjectAsync();

        // One slot per minute: a read-only token spends it on the first read and is limited on the second.
        var readOnly = await host.MintTokenPlaintextAsync(owner, "read-once", trigger: false, read: true, cancel: false);
        using (var readClient = host.CreateApiClient(readOnly))
        {
            using (var first = await ApiV1TestHost.GetAsync(readClient, "/api/v1/projects"))
            {
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            }

            using var second = await ApiV1TestHost.GetAsync(readClient, "/api/v1/projects");
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        }

        // A token without `read` is refused by the policy before the limiter is consulted, so its budget
        // is never spent: three refused reads, then its own scoped call still works (the first slot).
        var triggerOnly = await host.MintTokenPlaintextAsync(owner, "no-read", trigger: true, read: false, cancel: false);
        using (var api = host.CreateApiClient(triggerOnly))
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var refused = await ApiV1TestHost.GetAsync(api, "/api/v1/projects");
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                Assert.Empty(await refused.Content.ReadAsStringAsync(CancellationToken));
            }

            using var trigger = await ApiV1TestHost.TriggerAsync(api, projectId, new { });
            Assert.Equal(HttpStatusCode.Created, trigger.StatusCode);
        }
    }

    [Fact]
    public async Task A_spent_budget_answers_429_before_any_lookup()
    {
        using var host = await ApiV1TestHost.CreateAsync(builder => builder.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Api:RateLimitPerMinute"] = "2",
            }));

        var (_, owner) = await host.CreateProjectAsync();
        var token = await host.MintTokenPlaintextAsync(owner, "limited");
        using var api = host.CreateApiClient(token);

        using (var first = await ApiV1TestHost.GetAsync(api, "/api/v1/projects"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (var second = await ApiV1TestHost.GetAsync(api, "/api/v1/projects"))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        // The third call is refused even though its target does not exist: the budget is spent before
        // any project/run lookup, so a 429 is never an existence oracle.
        using var limited = await ApiV1TestHost.GetAsync(api, $"/api/v1/runs/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.InRange(
            int.Parse(Assert.Single(limited.Headers.GetValues("Retry-After"))),
            1,
            60);
        using var body = await ApiV1TestHost.ReadJsonAsync(limited);
        Assert.Equal("rate-limited", body.RootElement.GetProperty("error").GetString());
    }
}
