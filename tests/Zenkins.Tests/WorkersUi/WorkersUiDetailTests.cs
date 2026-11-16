using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.WorkersUi;

/// <summary>
/// Card D-005's detail page: a worker's current and recent runs with status and links (through
/// D-002's shared badge, so no second status map), and the tenant boundary - another
/// organization's worker id is a 404 on the detail page and on every action, never a 403
/// existence oracle.
/// </summary>
[Trait("Category", "WorkersUi")]
public sealed class WorkersUiDetailTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_detail_page_lists_current_and_recent_runs_with_status_and_links()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await WorkersUiHost.CreateAsync();
        var (projectId, owner) = await host.Runs.CreateProjectAsync();
        var (token, workerId) = await host.Runs.RegisterWorkerAsync(name: "detail-agent");
        var client = host.Host.CreateClient();

        // One finished run first (Success), then one still executing (Running): the detail page
        // separates the two - current runs from recent runs - with a link and a badge per run.
        var finishedRunId = await host.Runs.AddRunAsync(projectId);
        using (var claim = await RunTestHost.ClaimAsync(client, token))
        {
            Assert.Equal(finishedRunId, await RunTestHost.ReadClaimedRunIdAsync(claim));
        }

        using (var accept = await RunTestHost.AcceptAsync(client, token, finishedRunId))
        {
            Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        }

        using (var complete = await RunTestHost.CompleteAsync(client, token, finishedRunId, 0))
        {
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        }

        var currentRunId = await host.Runs.AddRunAsync(projectId);
        using (var claim = await RunTestHost.ClaimAsync(client, token))
        {
            Assert.Equal(currentRunId, await RunTestHost.ReadClaimedRunIdAsync(claim));
        }

        using (var accept = await RunTestHost.AcceptAsync(client, token, currentRunId))
        {
            Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        }

        var page = await host.ReadWorkerDetailAsync(owner, workerId);

        // The worker's identity fields are on the page.
        Assert.Contains("detail-agent", page, StringComparison.Ordinal);
        Assert.Contains("Linux", page, StringComparison.Ordinal);
        Assert.Contains("X64", page, StringComparison.Ordinal);
        Assert.Contains("v1.0.0", page, StringComparison.Ordinal);
        Assert.Contains("CPU count", page, StringComparison.Ordinal);
        Assert.Contains("id=\"worker-cpus\"", page, StringComparison.Ordinal);

        // The current-runs section lists the executing run, linked, with its status badge. Card F-036:
        // the link is the run's permalink (its project and build number), because that is the name a
        // person reads and shares - the guid route still resolves, it is just no longer the address the
        // pages advertise.
        Assert.Contains("id=\"worker-current-runs\"", page, StringComparison.Ordinal);
        Assert.Contains($"href=\"/projects/{projectId}/runs/2\"", page, StringComparison.Ordinal);
        Assert.Contains("Run #2", page, StringComparison.Ordinal);
        Assert.Contains("Running", page, StringComparison.Ordinal);

        // The recent-runs section lists the finished run, linked, with its Success badge.
        Assert.Contains("id=\"worker-recent-runs\"", page, StringComparison.Ordinal);
        Assert.Contains($"href=\"/projects/{projectId}/runs/1\"", page, StringComparison.Ordinal);
        Assert.Contains("Run #1", page, StringComparison.Ordinal);
        Assert.Contains("Success", page, StringComparison.Ordinal);

        // Current before recent - the honest reading order of "what is it doing right now, and
        // what has it done".
        Assert.True(
            page.IndexOf("id=\"worker-current-runs\"", StringComparison.Ordinal)
            < page.IndexOf("id=\"worker-recent-runs\"", StringComparison.Ordinal),
            "the current-runs card is not before the recent-runs card.");
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_detail_page_of_an_unknown_worker_is_a_real_404()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await WorkersUiHost.CreateAsync();
        var owner = await host.Runs.Inner.SignInOwnerAsync();

        using var response = await owner.GetAsync($"/workers/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task Another_organizations_worker_is_404_on_the_detail_and_every_action()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = await WorkersUiHost.CreateAsync();
        var owner = await host.Runs.Inner.SignInOwnerAsync();

        // A second organization with its own worker, created directly (no Owner session exists
        // for it, and none is needed - the row exercises the tenant filter).
        var foreignOrganizationId = await host.Runs.Inner.Inner.AddOrganizationAsync("Foreign Co");
        await host.Runs.CreateWorkerTokenDirectlyAsync(foreignOrganizationId, name: "foreign-agent");
        var foreignWorker = (await host.Runs.Inner.ReadWorkersAsync(foreignOrganizationId)).Single();

        // The detail: the same 404 as an unknown id, no existence oracle.
        using (var detail = await owner.GetAsync($"/workers/{foreignWorker.Id}", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        }

        // Every action: the same 404 - and afterwards the foreign row is exactly as it was.
        foreach (var action in new[] { "disable", "enable", "delete" })
        {
            using var attempt = await host.PostWorkerActionAsync(owner, foreignWorker.Id, action);
            Assert.Equal(HttpStatusCode.NotFound, attempt.StatusCode);
        }

        var row = (await host.Runs.Inner.ReadWorkersAsync(foreignOrganizationId)).Single();
        Assert.Equal(foreignWorker.Id, row.Id);
        Assert.False(row.IsDisabled);
    }
}
