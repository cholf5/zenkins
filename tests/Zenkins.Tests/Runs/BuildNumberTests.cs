using System.Net;
using System.Text.Json;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Api;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Runs;

/// <summary>
/// Card F-036: every run gets a per-project build number, shown as <c>#N</c>, with stable permalinks.
/// <para>
/// The facts here are the card's test cases, driven through the product's own surfaces - the trigger form,
/// the pages and the log download - and read back from the rows the server wrote:
/// </para>
/// <list type="bullet">
/// <item><b>TC-001:</b> eight concurrent triggers of one project earn exactly the numbers 1..8, once each
/// (the project row lock assigns them, and the unique index refuses a duplicate).</item>
/// <item><b>TC-002:</b> <c>/projects/{id}/runs/{number}</c>, <c>/runs/last</c> and <c>/lastSuccessful</c>
/// resolve, redirect and answer 404 honestly; the guid route keeps working beside them.</item>
/// <item><b>TC-003:</b> the number reaches the log download's filename and the claim the worker turns into
/// <c>ZENKINS_BUILD_NUMBER</c> (the worker-side composition is pinned in
/// <c>Zenkins.Tests.WorkerRuntime.RunEnvironmentTests</c> - one layer here, one there, so neither can
/// silently stop feeding the other).</item>
/// </list>
/// </summary>
[Trait("Category", "Runs")]
public sealed class BuildNumberTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact(Timeout = 3 * 60 * 1000)]
    public async Task Concurrent_triggers_get_distinct_numbers_one_to_n()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const int triggers = 8;

        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("build-numbers");
        var organizationId = await host.OrganizationIdAsync();

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, triggers)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task.WaitAsync(cancellationToken);
                using var response = await RunTestHost.StartRunAsync(owner, projectId);
                return response.StatusCode;
            }))
            .ToArray();
        start.SetResult();
        var statuses = await Task.WhenAll(calls);

        Assert.All(statuses, status => Assert.True((int)status is 302 or 303, $"A trigger answered {status}."));

        // The verdict comes from the rows: exactly 1..N as a set, no duplicate and no gap. A duplicate
        // would also have been refused by the unique (ProjectId, Number) index, so a green read here is
        // the compound fact - assigned once each, and assigned at all.
        var runs = await host.ReadRunsAsync(organizationId);
        Assert.Equal(triggers, runs.Count);
        Assert.Equal(
            Enumerable.Range(1, triggers).ToArray(),
            runs.Select(run => run.Number).OrderBy(number => number).ToArray());
        Assert.All(runs, run => Assert.Equal(projectId, run.ProjectId));
        Assert.All(runs, run => Assert.Equal(BuildRunStatus.Queued, run.Status));
    }

    [Fact]
    public async Task The_three_permalinks_resolve_and_answer_404_honestly()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("permalinks");
        var organizationId = await host.OrganizationIdAsync();

        // A project with no runs refuses all three: 404, not an empty page and not a guess.
        foreach (var path in new[]
                 {
                     $"/projects/{projectId}/runs/1",
                     $"/projects/{projectId}/runs/last",
                     $"/projects/{projectId}/lastSuccessful",
                 })
        {
            using var empty = await owner.GetAsync(path, CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
        }

        var firstRunId = await RunTestHost.TriggerRunAsync(owner, projectId);
        var secondRunId = await RunTestHost.TriggerRunAsync(owner, projectId);

        // #1 succeeds, #2 stays queued: the two aliases must then point at different runs - "last" is
        // the newest run whatever its state, "last successful" is the newest success.
        await host.InSystemScopeAsync(async context =>
        {
            var run = (await context.SystemQueryAsync<BuildRun>(candidate => candidate.Id == firstRunId)).Single();
            run.Status = BuildRunStatus.Success;
            run.ExitCode = 0;
            await context.SaveChangesAsync();
            return true;
        });

        // #1 renders the run itself (the number route is not a redirect: it is the run's address).
        var first = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/runs/1");
        Assert.Contains("Run #1", first, StringComparison.Ordinal);
        Assert.Contains(firstRunId.ToString(), first, StringComparison.OrdinalIgnoreCase);

        // /runs/last and /lastSuccessful redirect to the run's own permalink.
        var last = await GetNoRedirectAsync(owner, $"/projects/{projectId}/runs/last");
        Assert.Equal(HttpStatusCode.Redirect, last.StatusCode);
        Assert.Equal($"/projects/{projectId}/runs/2", last.Headers.Location!.OriginalString);

        var lastSuccessful = await GetNoRedirectAsync(owner, $"/projects/{projectId}/lastSuccessful");
        Assert.Equal(HttpStatusCode.Redirect, lastSuccessful.StatusCode);
        Assert.Equal($"/projects/{projectId}/runs/1", lastSuccessful.Headers.Location!.OriginalString);

        // Failing the newest run does not move "last successful" back to it: a failure is not a success.
        await host.InSystemScopeAsync(async context =>
        {
            var run = (await context.SystemQueryAsync<BuildRun>(candidate => candidate.Id == secondRunId)).Single();
            run.Status = BuildRunStatus.Failed;
            run.ExitCode = 1;
            await context.SaveChangesAsync();
            return true;
        });

        var afterFailure = await GetNoRedirectAsync(owner, $"/projects/{projectId}/lastSuccessful");
        Assert.Equal(HttpStatusCode.Redirect, afterFailure.StatusCode);
        Assert.Equal($"/projects/{projectId}/runs/1", afterFailure.Headers.Location!.OriginalString);

        var otherProject = await host.CreateProjectAsync("no-success");
        var failingRunId = await RunTestHost.TriggerRunAsync(otherProject.Owner, otherProject.ProjectId);
        await host.InSystemScopeAsync(async context =>
        {
            var run = (await context.SystemQueryAsync<BuildRun>(candidate => candidate.Id == failingRunId)).Single();
            run.Status = BuildRunStatus.Failed;
            run.ExitCode = 2;
            await context.SaveChangesAsync();
            return true;
        });

        // A project whose only run failed has no last successful build: 404, never the failed row.
        using var noSuccess = await otherProject.Owner.GetAsync(
            $"/projects/{otherProject.ProjectId}/lastSuccessful", CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, noSuccess.StatusCode);

        // A number the project never issued is a 404 too (not "the nearest one").
        using var missingNumber = await owner.GetAsync($"/projects/{projectId}/runs/99", CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missingNumber.StatusCode);
    }

    [Fact]
    public async Task The_dashboard_and_the_project_pages_name_a_run_by_its_number()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("number-display");
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);

        // Card F-036's display sweep, on the two pages nobody reaches through a run link: the dashboard's
        // recent runs (and its failures section) and the project overview's "Latest run" card. Both print
        // the number and link to the run's permalink.
        await host.InSystemScopeAsync(async context =>
        {
            var run = (await context.SystemQueryAsync<BuildRun>(candidate => candidate.Id == runId)).Single();
            run.Status = BuildRunStatus.Failed;
            run.ExitCode = 3;
            run.FailureReason = "step-failed";
            run.EndedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
            return true;
        });

        var dashboard = await AuthTestHost.GetStringAsync(owner, "/");
        Assert.Contains("Run #1", dashboard, StringComparison.Ordinal);
        Assert.Contains($"href=\"/projects/{projectId}/runs/1\"", dashboard, StringComparison.Ordinal);

        var overview = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}");
        Assert.Contains("Run #1", overview, StringComparison.Ordinal);
        Assert.Contains($"href=\"/projects/{projectId}/runs/1\"", overview, StringComparison.Ordinal);

        var runsPage = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/runs");
        Assert.Contains("Run #1", runsPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_guid_route_keeps_working_beside_the_number_route()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("guid-route");
        var runId = await RunTestHost.TriggerRunAsync(owner, projectId);

        // F-036's "no existing link breaks": the old address renders the same run, and every action on
        // the page still targets the id.
        var byGuid = await AuthTestHost.GetStringAsync(owner, $"/runs/{runId}");
        Assert.Contains("Run #1", byGuid, StringComparison.Ordinal);
        Assert.Contains($"action=\"/runs/{runId}/cancel\"", byGuid, StringComparison.Ordinal);

        // The permalink the page advertises is the number form.
        Assert.Contains($"/projects/{projectId}/runs/1", byGuid, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_log_download_name_carries_the_build_number()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("log-name");
        var firstRunId = await RunTestHost.TriggerRunAsync(owner, projectId);
        var secondRunId = await RunTestHost.TriggerRunAsync(owner, projectId);

        using var download = await owner.GetAsync($"/runs/{secondRunId}/log/download", CancellationToken);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);

        var fileName = download.Content.Headers.ContentDisposition?.FileNameStar
            ?? download.Content.Headers.ContentDisposition?.FileName;
        Assert.NotNull(fileName);
        Assert.Contains("run-2-", fileName!, StringComparison.Ordinal);

        // The short id stays beside it, so two projects' #2 files cannot collide in a downloads folder.
        Assert.Contains(firstRunId.ToString("N")[..8], await DownloadNameAsync(owner, firstRunId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_api_run_resource_carries_the_build_number()
    {
        using var host = await ApiV1TestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("api-numbers");
        var (token, mintResponse) = await host.MintTokenAsync(owner);
        Assert.Equal(HttpStatusCode.OK, mintResponse.StatusCode);

        using var api = host.CreateApiClient(token);

        // Triggered through the API, the 201's resource carries the number the run was queued with.
        using var triggered = await api.PostAsync(
            $"/api/v1/projects/{projectId}/runs",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            CancellationToken);
        Assert.Equal(HttpStatusCode.Created, triggered.StatusCode);

        using var document = JsonDocument.Parse(await triggered.Content.ReadAsStringAsync(CancellationToken));
        var runId = document.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(1, document.RootElement.GetProperty("number").GetInt32());

        // ...and the read route reports the same number for the same run (the id stays the reference).
        using var read = await api.GetAsync($"/api/v1/runs/{runId}", CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readDocument = JsonDocument.Parse(await read.Content.ReadAsStringAsync(CancellationToken));
        Assert.Equal(1, readDocument.RootElement.GetProperty("number").GetInt32());
        Assert.Equal(runId, readDocument.RootElement.GetProperty("id").GetGuid());

        // A second trigger is #2, not #1.
        using var second = await api.PostAsync(
            $"/api/v1/projects/{projectId}/runs",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            CancellationToken);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        using var secondDocument = JsonDocument.Parse(await second.Content.ReadAsStringAsync(CancellationToken));
        Assert.Equal(2, secondDocument.RootElement.GetProperty("number").GetInt32());
    }

    private static async Task<HttpResponseMessage> GetNoRedirectAsync(HttpClient client, string path) =>
        await client.GetAsync(path, CancellationToken);

    private static async Task<string> DownloadNameAsync(HttpClient client, Guid runId)
    {
        using var download = await client.GetAsync($"/runs/{runId}/log/download", CancellationToken);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        return download.Content.Headers.ContentDisposition?.FileNameStar
            ?? download.Content.Headers.ContentDisposition?.FileName
            ?? string.Empty;
    }
}
