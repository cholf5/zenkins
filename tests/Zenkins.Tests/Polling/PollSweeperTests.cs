using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Polling;
using Zenkins.Server.Probes;
using Zenkins.Server.Webhooks;
using Zenkins.Shared.Probes;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Probes;
using Zenkins.Tests.Webhooks;

namespace Zenkins.Tests.Polling;

/// <summary>
/// Card F-025's background half. The polls themselves are pinned in <see cref="PollingTests"/>; these facts
/// are about the <b>sweeper</b> - which projects a tick selects, and that a failure's backoff is a wait the
/// next tick honours rather than an arithmetic fact nobody applies.
/// <para>
/// <b>Why this file exists.</b> Before it, <c>PollSweeper.SweepOnceAsync</c> had no fact at all: deleting its
/// <c>PollingEnabled</c> filter, or dropping the due-time comparison, left the whole suite green. "Polling is
/// off by default" and "a backed-off project is not asked again yet" were claims about a method no test ran.
/// </para>
/// </summary>
public sealed class PollSweeperTests
{
    /// <summary>
    /// The sentinel a project that must not be touched carries: distinct from every real
    /// <see cref="PollOutcomes"/> word, so "still there" cannot be confused with "polled and unchanged".
    /// </summary>
    private const string Untouched = "sentinel-not-polled";

    [Fact]
    public async Task Only_the_project_whose_wait_has_passed_is_polled()
    {
        using var host = await WebhooksTestHost.CreateAsync(new Dictionary<string, string?>
        {
            [ProbeOptions.DedupeSecondsConfigurationKey] = "1",
        });
        var workerToken = await host.CreateWorkerAsync("sweep-agent", canProbe: true);

        // The clock the sweeper reads is the container's, not the machine's: a fact that used
        // DateTimeOffset.UtcNow would be comparing two different todays the moment a test host pins time.
        var now = await host.Host.InScopeAsync(services => Task.FromResult(
            services.GetRequiredService<TimeProvider>().GetUtcNow()));

        // Four projects, three of which must not be asked anything:
        //   (a) polling never switched on,
        //   (b) switched on, polled a minute ago with a 30 minute interval,
        //   (c) switched on, in a x4 backoff (2 failures, 1 minute interval -> due in 4 minutes, polled a
        //       minute ago),
        //   (d) switched on, due: one hour past a 5 minute interval.
        var off = await host.AddProjectAsync(host.OrganizationId, "sweep-off", "https://example.test/off.git", "main");
        var tooSoon = await host.AddProjectAsync(host.OrganizationId, "sweep-too-soon", "https://example.test/soon.git", "main");
        var backingOff = await host.AddProjectAsync(host.OrganizationId, "sweep-backing-off", "https://example.test/backoff.git", "main");
        var due = await host.AddProjectAsync(host.OrganizationId, "sweep-due", "https://example.test/due.git", "main");

        await ConfigureAsync(host, off, enabled: false, intervalMinutes: 1, lastPollAt: null, failureStreak: 0);
        await ConfigureAsync(host, tooSoon, enabled: true, intervalMinutes: 30, lastPollAt: now.AddMinutes(-1), failureStreak: 0);
        await ConfigureAsync(host, backingOff, enabled: true, intervalMinutes: 1, lastPollAt: now.AddMinutes(-1), failureStreak: 2);
        await ConfigureAsync(host, due, enabled: true, intervalMinutes: 5, lastPollAt: now.AddHours(-1), failureStreak: 0);

        foreach (var untouched in new[] { off, tooSoon, backingOff })
        {
            await RecordSentinelAsync(host, untouched);
        }

        var head = WebhookPayloads.Commit('1');
        var (result, polled) = await SweepAsync(host, workerToken, head);

        // **First** the per-project evidence, because the two rules this fact pins are independent and must
        // each fail on their own line: a swithed-off project betrays itself by losing its sentinel (the
        // first block below), a not-yet-due or backed-off one by losing its timestamp (the second and third
        // entries). A count or a set assertion first would collapse all three into one failure point.
        // Written out one project at a time on purpose: a loop would put all three failures on one source
        // line, and "switched off" and "not due yet" must be able to fail apart.
        var offState = await ReadPollingAsync(host, off);
        Assert.Equal(Untouched, offState.Outcome);
        Assert.Null(offState.LastPollAt);

        var tooSoonState = await ReadPollingAsync(host, tooSoon);
        Assert.Equal(Untouched, tooSoonState.Outcome);
        Assert.Equal(now.AddMinutes(-1), tooSoonState.LastPollAt);

        var backingOffState = await ReadPollingAsync(host, backingOff);
        Assert.Equal(Untouched, backingOffState.Outcome);
        Assert.Equal(now.AddMinutes(-1), backingOffState.LastPollAt);

        // Then the shape of the tick: exactly the due project, once.
        Assert.Equal([due], polled);
        Assert.Equal(1, result.ProjectsPolled);
        Assert.Empty(await ReadRunsForAsync(host, due));

        var state = await ReadPollingAsync(host, due);
        Assert.Equal(PollOutcomes.FirstSighting, state.Outcome);
        Assert.True(state.LastPollAt > now.AddMinutes(-1), $"the due project was not polled (LastPollAt {state.LastPollAt}).");

        // The due project recorded the head it saw - the probe's answer reached the row, so the tick did the
        // whole job rather than only starting it.
        var recorded = await ReadHeadAsync(host, due, "main");
        Assert.Equal(head, recorded!.Sha);
    }

    [Fact]
    public async Task A_successful_probe_resets_the_stored_failure_streak()
    {
        using var host = await WebhooksTestHost.CreateAsync(new Dictionary<string, string?>
        {
            [ProbeOptions.DedupeSecondsConfigurationKey] = "1",
        });
        var workerToken = await host.CreateWorkerAsync("sweep-agent", canProbe: true);
        var projectId = await host.AddProjectAsync(
            host.OrganizationId,
            "streak-reset",
            "https://example.test/streak.git",
            "main");

        // A project that has failed once: the row says so, and the next wait is doubled.
        await ConfigureAsync(host, projectId, enabled: true, intervalMinutes: 1, lastPollAt: DateTimeOffset.UtcNow.AddMinutes(-10), failureStreak: 1);
        var afterFailure = await PollAsync(host, workerToken, projectId, GitProbeOutcomes.Unreachable);
        Assert.Equal(GitProbeOutcomes.Unreachable, afterFailure.Outcome);
        Assert.Equal(2, (await ReadPollingAsync(host, projectId)).FailureStreak);

        // ...and one good probe stores zero: the *stored* streak is what the next tick's wait reads, so the
        // arithmetic being right is not enough - this is the row.
        var afterSuccess = await PollAsync(host, workerToken, projectId, GitProbeOutcomes.Refs);
        Assert.Equal(PollOutcomes.FirstSighting, afterSuccess.Outcome);
        Assert.Equal(0, (await ReadPollingAsync(host, projectId)).FailureStreak);
    }

    [Fact]
    public async Task A_multibranch_project_records_every_head_and_queues_one_run_for_the_head_that_moved()
    {
        using var host = await WebhooksTestHost.CreateAsync(new Dictionary<string, string?>
        {
            [ProbeOptions.DedupeSecondsConfigurationKey] = "1",
        });
        var workerToken = await host.CreateWorkerAsync("sweep-agent", canProbe: true);
        var projectId = await host.AddProjectAsync(
            host.OrganizationId,
            "poll-multibranch",
            "https://example.test/multi.git",
            "main",
            BranchDiscoveryModes.Branches);
        await ConfigureAsync(host, projectId, enabled: true, intervalMinutes: 1, lastPollAt: null, failureStreak: 0);

        // F-023's verdict gate: a poll of a multibranch project queues a branch only when the branch's own
        // verdict says it has a pipeline. These rows are that evidence - without them every head answers
        // "awaiting observation" and nothing is ever queued here (the sibling fact below pins the other
        // side of the same rule).
        await SeedBranchJobsAsync(host, projectId, "main", "release/1.0", "feature/one");

        var featureSha = WebhookPayloads.Commit('2');
        var mainSha = WebhookPayloads.Commit('3');
        var releaseSha = WebhookPayloads.Commit('4');
        GitProbeRef[] Heads() =>
        [
            new("refs/heads/main", mainSha),
            new("refs/heads/release/1.0", releaseSha),
            new("refs/heads/feature/one", featureSha),
        ];

        // First tick: three heads, three recorded branch heads, nothing queued (a first sighting is a
        // baseline, not a change).
        _ = await PollRefsAsync(host, workerToken, projectId, Heads());
        var recorded = await ReadHeadsAsync(host, projectId);
        Assert.Equal(
            ["feature/one", "main", "release/1.0"],
            recorded.Select(head => head.Branch).OrderBy(branch => branch, StringComparer.Ordinal));
        Assert.Equal(mainSha, recorded.Single(head => head.Branch == "main").Sha);
        Assert.Empty(await ReadRunsForAsync(host, projectId));

        // Second tick: exactly one head moved, and exactly one run is queued - for that branch, pinned to the
        // commit that was observed.
        var movedFeature = WebhookPayloads.Commit('5');
        _ = await PollRefsAsync(
            host,
            workerToken,
            projectId,
            [new("refs/heads/main", mainSha), new("refs/heads/release/1.0", releaseSha), new("refs/heads/feature/one", movedFeature)]);

        var run = Assert.Single(await ReadRunsForAsync(host, projectId));
        Assert.Equal("feature/one", run.GitBranch);
        Assert.Equal(movedFeature, run.GitCommit);
    }

    [Fact]
    public async Task A_branch_without_a_pipeline_is_not_queued_and_the_poll_says_which_answer_it_gave()
    {
        using var host = await WebhooksTestHost.CreateAsync(new Dictionary<string, string?>
        {
            [ProbeOptions.DedupeSecondsConfigurationKey] = "1",
        });
        var workerToken = await host.CreateWorkerAsync("sweep-agent", canProbe: true);
        var projectId = await host.AddProjectAsync(
            host.OrganizationId,
            "poll-docs-branch",
            "https://example.test/docs.git",
            "main",
            BranchDiscoveryModes.Branches);
        await ConfigureAsync(host, projectId, enabled: true, intervalMinutes: 1, lastPollAt: null, failureStreak: 0);

        // `main` has a pipeline; `docs` does not, and its verdict speaks about one specific commit.
        var mainSha = WebhookPayloads.Commit('7');
        var docsFirst = WebhookPayloads.Commit('8');
        var docsSecond = WebhookPayloads.Commit('9');
        var docsFresh = WebhookPayloads.Commit('a');
        await SeedBranchJobAsync(host, projectId, "main", BranchPipelineStates.HasPipeline, observedCommit: null);
        await SeedBranchJobAsync(host, projectId, "docs", BranchPipelineStates.NoPipeline, docsSecond);

        // 1. First sighting: both heads recorded, nothing queued.
        _ = await PollRefsAsync(
            host,
            workerToken,
            projectId,
            [new("refs/heads/main", mainSha), new("refs/heads/docs", docsFirst)]);
        Assert.Empty(await ReadRunsForAsync(host, projectId));

        // 2. `docs` moved to exactly the commit its no-pipeline verdict speaks about: F-023's gate says
        //    nothing is queued and the poll records the same word the delivery path records.
        _ = await PollRefsAsync(
            host,
            workerToken,
            projectId,
            [new("refs/heads/main", mainSha), new("refs/heads/docs", docsSecond)]);
        Assert.Empty(await ReadRunsForAsync(host, projectId));
        Assert.Equal(
            WebhookOutcomes.NoPipeline,
            (await ReadHeadAsync(host, projectId, "docs"))!.LastOutcome);

        // Timeliness rule 1: the verdict is about *this* commit, so there is nothing to ask - the delivery
        // path does not request a tree read for a settled `no-pipeline` answer either.
        Assert.Empty(await ObservationTasksAsync(host, projectId));

        // 3. A newer head that the verdict does not speak about: unknown is never queued, so the answer is
        //    "awaiting observation" - and the branch head stays where it was, so the next tick asks again.
        _ = await PollRefsAsync(
            host,
            workerToken,
            projectId,
            [new("refs/heads/main", mainSha), new("refs/heads/docs", docsFresh)]);
        Assert.Empty(await ReadRunsForAsync(host, projectId));
        var docsHead = await ReadHeadAsync(host, projectId, "docs");
        Assert.Equal(WebhookOutcomes.AwaitingObservation, docsHead!.LastOutcome);
        Assert.Equal(docsFirst, docsHead.Sha);

        // Timeliness rule 2 - and the loop is closed, not merely reported: the stale `no-pipeline` answer
        // named an older commit, so it was not reused, and a real RepositoryBranch task now exists for the
        // observed commit. Recording the word without asking for the read would leave the branch unknown
        // forever on a project whose whole reason for polling is that no push will ever arrive.
        var task = Assert.Single(await ObservationTasksAsync(host, projectId));
        Assert.Equal("docs", task.Branch);
        Assert.Equal(docsFresh, task.Commit);

        // ...and the branch that does have a pipeline was never held back by its neighbour's verdict: the
        // `main` head is the first sighting from step 1, recorded and current.
        Assert.Equal(mainSha, (await ReadHeadAsync(host, projectId, "main"))!.Sha);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Task ConfigureAsync(
        WebhooksTestHost host,
        Guid projectId,
        bool enabled,
        int? intervalMinutes,
        DateTimeOffset? lastPollAt,
        int failureStreak) =>
        host.WithTenantAsync(async services =>
        {
            var context = services.GetRequiredService<Zenkins.Server.Data.ZenkinsDbContext>();
            var project = context.Projects.Single(candidate => candidate.Id == projectId);
            project.PollingEnabled = enabled;
            project.PollIntervalMinutes = intervalMinutes;
            project.LastPollAt = lastPollAt;
            project.PollFailureStreak = failureStreak;
            await context.SaveChangesAsync(CancellationToken.None);
            return true;
        });

    private static Task RecordSentinelAsync(WebhooksTestHost host, Guid projectId) =>
        host.WithTenantAsync(async services =>
        {
            var context = services.GetRequiredService<Zenkins.Server.Data.ZenkinsDbContext>();
            var project = context.Projects.Single(candidate => candidate.Id == projectId);
            project.LastPollOutcome = Untouched;
            await context.SaveChangesAsync(CancellationToken.None);
            return true;
        });

    /// <summary>
    /// One real sweeper tick, with <b>every</b> probe it asks for answered from the worker side. The tick's
    /// own report of which projects it polled is the fact's subject, so a project the sweeper wrongly
    /// selects must not be able to hide behind an unanswered probe - it is answered, and then named.
    /// </summary>
    private static async Task<(PollSweepResult Result, Guid[] Polled)> SweepAsync(
        WebhooksTestHost host,
        string workerToken,
        string sha)
    {
        var sweeper = await host.Host.InScopeAsync(services => Task.FromResult(
            services.GetServices<IHostedService>().OfType<PollSweeper>().Single()));

        var sweep = sweeper.SweepOnceAsync(CancellationToken.None);
        var polled = new List<Guid>();

        // Four is the most projects this fixture can have polling; the loop ends earlier when the tick does -
        // a tick that asks for nothing more finishes, and waiting for a probe it will never raise is not a
        // failure but the end of this loop.
        for (var answer = 0; answer < 4 && !sweep.IsCompleted; answer++)
        {
            Zenkins.Server.Probes.GitProbeSnapshot probe;
            try
            {
                probe = await host.WaitForUnsettledProbeAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                break;
            }

            polled.Add(probe.ProjectId);

            using var workerClient = host.CreateDeliveryClient();
            var claim = await GitProbeTestHost.ClaimOneProbeAsync(workerClient, workerToken);
            Assert.Equal(probe.Id, claim.ProbeId);
            using var reported = await GitProbeTestHost.ReportProbeAsync(
                workerClient,
                workerToken,
                claim.ProbeId,
                GitProbeOutcomes.Refs,
                [new GitProbeRef("refs/heads/main", sha)],
                headSha: sha,
                headBranch: "refs/heads/main");
            Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        }

        return (await sweep, polled.ToArray());
    }

    /// <summary>One poll of one project with the given refs (or failure) answered from the worker side.</summary>
    private static async Task<PollProjectResult> PollAsync(
        WebhooksTestHost host,
        string workerToken,
        Guid projectId,
        string outcome)
    {
        var sha = WebhookPayloads.Commit('6');
        await Task.Delay(TimeSpan.FromMilliseconds(1200), CancellationToken.None);
        using var workerClient = host.CreateDeliveryClient();
        var poll = PollProjectAsync(host, projectId);
        var probe = await host.WaitForUnsettledProbeAsync(TimeSpan.FromSeconds(15));
        var claim = await GitProbeTestHost.ClaimOneProbeAsync(workerClient, workerToken);
        Assert.Equal(probe.Id, claim.ProbeId);
        var succeeded = string.Equals(outcome, GitProbeOutcomes.Refs, StringComparison.Ordinal);
        using var reported = await GitProbeTestHost.ReportProbeAsync(
            workerClient,
            workerToken,
            claim.ProbeId,
            outcome,
            succeeded ? [new GitProbeRef("refs/heads/main", sha)] : [],
            headSha: succeeded ? sha : null,
            headBranch: succeeded ? "refs/heads/main" : null);
        Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        return await poll;
    }

    private static async Task<PollProjectResult> PollRefsAsync(
        WebhooksTestHost host,
        string workerToken,
        Guid projectId,
        GitProbeRef[] refs)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1200), CancellationToken.None);
        using var workerClient = host.CreateDeliveryClient();
        var poll = PollProjectAsync(host, projectId);
        var probe = await host.WaitForUnsettledProbeAsync(TimeSpan.FromSeconds(15));
        var claim = await GitProbeTestHost.ClaimOneProbeAsync(workerClient, workerToken);
        Assert.Equal(probe.Id, claim.ProbeId);
        using var reported = await GitProbeTestHost.ReportProbeAsync(
            workerClient,
            workerToken,
            claim.ProbeId,
            GitProbeOutcomes.Refs,
            refs,
            headSha: refs[0].Sha,
            headBranch: refs[0].Name);
        Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        return await poll;
    }

    private static Task<PollProjectResult> PollProjectAsync(WebhooksTestHost host, Guid projectId) =>
        host.WithTenantAsync(services =>
            services.GetRequiredService<PollService>().PollProjectAsync(projectId, CancellationToken.None));

    private static Task<(string? Outcome, DateTimeOffset? LastPollAt, int FailureStreak)> ReadPollingAsync(
        WebhooksTestHost host,
        Guid projectId) =>
        host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId)).Single();
            return (project.LastPollOutcome, project.LastPollAt, project.PollFailureStreak);
        });

    private static Task SeedBranchJobsAsync(WebhooksTestHost host, Guid projectId, params string[] branches) =>
        host.InSystemScopeAsync(async context =>
        {
            foreach (var branch in branches)
            {
                context.BranchJobs.Add(new BranchJob
                {
                    OrganizationId = host.OrganizationId,
                    ProjectId = projectId,
                    Branch = branch,
                    PipelineState = BranchPipelineStates.HasPipeline,
                });
            }

            await context.SaveChangesAsync(CancellationToken.None);
            return true;
        });

    private static Task SeedBranchJobAsync(
        WebhooksTestHost host,
        Guid projectId,
        string branch,
        string state,
        string? observedCommit) =>
        host.InSystemScopeAsync(async context =>
        {
            context.BranchJobs.Add(new BranchJob
            {
                OrganizationId = host.OrganizationId,
                ProjectId = projectId,
                Branch = branch,
                PipelineState = state,
                ObservedCommit = observedCommit,
            });
            await context.SaveChangesAsync(CancellationToken.None);
            return true;
        });

    /// <summary>The definition-registry tasks this project has open (the in-memory queue the worker reads).</summary>
    private static async Task<List<Zenkins.Server.Probes.DefinitionValidationEntry>> ObservationTasksAsync(
        WebhooksTestHost host,
        Guid projectId) =>
        (await host.ObservationsAsync())
            .Where(entry => entry.ProjectId == projectId)
            .ToList();

    private static Task<List<BuildRun>> ReadRunsForAsync(WebhooksTestHost host, Guid projectId) =>
        host.InSystemScopeAsync(context => context.SystemQueryAsync<BuildRun>(run => run.ProjectId == projectId));

    private static Task<BranchHead?> ReadHeadAsync(WebhooksTestHost host, Guid projectId, string branch) =>
        host.InSystemScopeAsync(async context => (await context.SystemQueryAsync<BranchHead>(
            head => head.ProjectId == projectId && head.Branch == branch)).SingleOrDefault());

    private static Task<List<BranchHead>> ReadHeadsAsync(WebhooksTestHost host, Guid projectId) =>
        host.InSystemScopeAsync(context => context.SystemQueryAsync<BranchHead>(head => head.ProjectId == projectId));
}
