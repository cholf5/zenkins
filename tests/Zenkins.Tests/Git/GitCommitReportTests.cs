using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Git;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.Git;

/// <summary>
/// <c>BuildRun.GitCommit</c> (card F-013, AC-5): written from the worker's accepted report, never
/// derived from a branch, and immutable afterwards - a second report cannot rewrite it.
/// </summary>
[Trait("Category", "Git")]
public sealed class GitCommitReportTests
{
    private const string FirstCommit = "0123456789abcdef0123456789abcdef01234567";
    private const string SecondCommit = "fedcba9876543210fedcba9876543210fedcba98";

    [Fact]
    public async Task The_accepted_report_records_the_commit()
    {
        using var setup = await CommitFixture.CreateAsync();

        using var accepted = await AcceptAsync(setup, setup.RunId, FirstCommit);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var run = await setup.Host.ReadRunAsync(setup.RunId);
        Assert.Equal(BuildRunStatus.Running, run.Status);
        Assert.Equal(FirstCommit, run.GitCommit);
    }

    [Fact]
    public async Task A_second_accepted_report_cannot_rewrite_the_commit()
    {
        using var setup = await CommitFixture.CreateAsync();

        using var first = await AcceptAsync(setup, setup.RunId, FirstCommit);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // The run already left Queued, so the second report is an illegal transition (409) - the
        // commit it carries is never even considered.
        using var second = await AcceptAsync(setup, setup.RunId, SecondCommit);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var run = await setup.Host.ReadRunAsync(setup.RunId);
        Assert.Equal(FirstCommit, run.GitCommit);
        Assert.NotEqual(SecondCommit, run.GitCommit);
    }

    [Fact]
    public async Task The_commit_column_is_write_once_at_the_database_layer_too()
    {
        using var setup = await CommitFixture.CreateAsync();
        using var accepted = await AcceptAsync(setup, setup.RunId, FirstCommit);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        // The guarded compare-and-set is the real authority: even a direct second stamp updates zero
        // rows, so no future code path can overwrite what the run built.
        var secondStamp = await StampAsync(setup, SecondCommit);
        Assert.Equal(0, secondStamp);

        var run = await setup.Host.ReadRunAsync(setup.RunId);
        Assert.Equal(FirstCommit, run.GitCommit);

        // ...while the first stamp on a run without a commit does write (the guard is the column, not
        // the caller).
        using var other = await CommitFixture.CreateAsync();
        Assert.Equal(1, await StampAsync(other, FirstCommit));
        Assert.Equal(FirstCommit, (await other.Host.ReadRunAsync(other.RunId)).GitCommit);
    }

    [Fact]
    public async Task An_interruption_between_the_transition_and_the_commit_stamp_leaves_neither()
    {
        using var setup = await CommitFixture.CreateAsync();

        // Simulate the interruption the review named at the exact boundary between the two statements:
        // a SQLite trigger aborts the commit write after the status update has been issued. Without a
        // transaction the run would be left Running with GitCommit NULL - and Running -> Running is not
        // a legal transition, so every retry would be a 409 and the commit could never be filled in.
        await ExecuteSqlAsync(
            setup,
            "CREATE TRIGGER ZkFailGitCommit BEFORE UPDATE OF GitCommit ON BuildRuns "
            + "WHEN NEW.GitCommit IS NOT NULL "
            + "BEGIN SELECT RAISE(ABORT, 'zenkins-test-interruption'); END;");

        var failed = false;
        try
        {
            using var interrupted = await AcceptAsync(setup, setup.RunId, FirstCommit);
            failed = interrupted.StatusCode != HttpStatusCode.OK;
        }
        catch (Exception)
        {
            // The aborted statement surfacing through the pipeline is the interruption itself.
            failed = true;
        }

        Assert.True(failed, "the interrupted accept was expected to fail");

        // Both halves died together: no Running status, no half-written commit, and the lease is intact
        // so the retried report is still legal.
        var interruptedRun = await setup.Host.ReadRunAsync(setup.RunId);
        Assert.Equal(BuildRunStatus.Queued, interruptedRun.Status);
        Assert.Null(interruptedRun.StartedAt);
        Assert.Null(interruptedRun.GitCommit);
        Assert.NotNull(interruptedRun.LeaseExpiresAt);

        // ...and once the fault is gone the retry completes both halves, exactly as the worker's
        // re-delivered accepted report would.
        await ExecuteSqlAsync(setup, "DROP TRIGGER ZkFailGitCommit;");
        using var retried = await AcceptAsync(setup, setup.RunId, FirstCommit);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);

        var accepted = await setup.Host.ReadRunAsync(setup.RunId);
        Assert.Equal(BuildRunStatus.Running, accepted.Status);
        Assert.Equal(FirstCommit, accepted.GitCommit);
    }

    [Fact]
    public async Task A_malformed_commit_is_rejected_and_the_run_stays_queued()
    {
        using var setup = await CommitFixture.CreateAsync();

        foreach (var malformed in new[] { "main", "HEAD~1", "0123456789ABCDEF0123456789ABCDEF01234567", "0123" })
        {
            using var rejected = await AcceptAsync(setup, setup.RunId, malformed);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

            var body = await rejected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains(GitCommitValidation.InvalidCode, body, StringComparison.Ordinal);
            Assert.DoesNotContain(malformed, body, StringComparison.Ordinal);
        }

        var run = await setup.Host.ReadRunAsync(setup.RunId);
        Assert.Equal(BuildRunStatus.Queued, run.Status);
        Assert.Null(run.GitCommit);
    }

    [Fact]
    public async Task The_accepted_report_is_still_optional_for_a_worker_that_sends_no_body()
    {
        using var setup = await CommitFixture.CreateAsync();

        // The pre-F-013 shape: a bodyless POST. It must keep working, because a worker that has not
        // been upgraded (or a run with no repository) legitimately has nothing to report.
        using var client = setup.Host.Host.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/workers/runs/{setup.RunId}/accepted");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            setup.WorkerToken);
        using var accepted = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var run = await setup.Host.ReadRunAsync(setup.RunId);
        Assert.Equal(BuildRunStatus.Running, run.Status);
        Assert.Null(run.GitCommit);
    }

    private static async Task<HttpResponseMessage> AcceptAsync(CommitFixture setup, Guid runId, string? commit)
    {
        using var client = setup.Host.Host.CreateClient();
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/workers/runs/{runId}/accepted")
        {
            Content = JsonContent.Create(new { gitCommit = commit }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            setup.WorkerToken);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<bool> ExecuteSqlAsync(CommitFixture setup, string sql) =>
        setup.Host.InSystemScopeAsync(async context =>
        {
            await context.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);
            return true;
        });

    private static Task<int> StampAsync(CommitFixture setup, string commit) =>
        setup.Host.Host.InScopeAsync(async services =>
        {
            services.GetRequiredService<TenantContext>().OrganizationId = setup.OrganizationId;
            await using var context = await services
                .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync();
            return await context.TryStampGitCommitAsync(
                setup.RunId,
                commit,
                DateTimeOffset.UtcNow);
        });

    /// <summary>A queued run leased by a registered worker: the state an accepted report needs.</summary>
    private sealed class CommitFixture : IDisposable
    {
        private CommitFixture(
            RunTestHost host,
            Guid organizationId,
            Guid runId,
            string workerToken)
        {
            Host = host;
            OrganizationId = organizationId;
            RunId = runId;
            WorkerToken = workerToken;
        }

        public RunTestHost Host { get; }

        public Guid OrganizationId { get; }

        public Guid RunId { get; }

        public string WorkerToken { get; }

        public static async Task<CommitFixture> CreateAsync()
        {
            var host = await RunTestHost.CreateAsync();
            var (projectId, owner) = await host.CreateProjectAsync();
            owner.Dispose();
            var organizationId = await host.OrganizationIdAsync();
            var runId = await host.AddRunAsync(projectId);
            var (workerToken, _) = await host.RegisterWorkerAsync(name: "commit-agent");

            using var client = host.Host.CreateClient();
            using var claimed = await RunTestHost.ClaimAsync(client, workerToken);
            Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
            Assert.Equal(runId, await RunTestHost.ReadClaimedRunIdAsync(claimed));

            return new CommitFixture(host, organizationId, runId, workerToken);
        }

        public void Dispose() => Host.Dispose();
    }
}
