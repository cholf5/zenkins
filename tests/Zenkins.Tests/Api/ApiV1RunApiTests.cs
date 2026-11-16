using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;
using Zenkins.Server.Data.Entities;
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
