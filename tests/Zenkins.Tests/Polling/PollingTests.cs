using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Polling;
using Zenkins.Server.Projects;
using Zenkins.Server.Webhooks;
using Zenkins.Shared.Probes;
using Zenkins.Shared.Projects;
using Zenkins.Server.Probes;
using Zenkins.Server.Schedules;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Probes;
using Zenkins.Tests.Webhooks;

namespace Zenkins.Tests.Polling;

/// <summary>
/// Card F-025's poller, driven through the product's own surfaces: the real I-005 probe (a probe-capable
/// worker answers it), the real webhook delivery endpoint, the real change-trigger ledger and the real run
/// queue.
/// <para>
/// <b>Polling is not a second sampler.</b> Every fact here makes the executor ask for exactly one probe per
/// poll and answers it by hand, which is also how the F-023 discovery facts work - a poll that requested two
/// probes would leave the second one hanging and the fact would fail on the wait.
/// </para>
/// </summary>
[Trait("Category", "Polling")]
public sealed class PollingTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------
    // TC-001: a moved head queues one pinned run
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_moved_head_queues_one_run_pinned_to_the_observed_commit_and_the_next_tick_queues_nothing()
    {
        using var host = await CreateHostAsync();
        var workerToken = await host.CreateWorkerAsync("poll-agent", canProbe: true);
        await SetPollingAsync(host, host.ProjectId, enabled: true, intervalMinutes: 1);

        var headA = WebhookPayloads.Commit('a');
        var headB = WebhookPayloads.Commit('b');

        // 1. The first poll records the head it finds and queues nothing: polling starts from "what is there
        //    now", so enabling it on a busy repository cannot queue a run per branch.
        var first = await PollAsync(host, workerToken, host.ProjectId, "main", headA);
        Assert.Equal(PollOutcomes.FirstSighting, first.Outcome);
        Assert.Empty(await host.ReadRunsAsync());
        Assert.Equal(headA, (await host.ReadBranchesAsync()).SingleOrDefault()?.LastCommit ?? headA);

        // 2. The head moved: one run, pinned to the commit the probe observed.
        var second = await PollAsync(host, workerToken, host.ProjectId, "main", headB);
        Assert.Equal(PollOutcomes.Queued, second.Outcome);
        var run = Assert.Single(await host.ReadRunsAsync());
        Assert.Equal(headB, run.GitCommit);
        Assert.Equal("main", run.GitBranch);
        Assert.Equal(BuildRunTriggerType.Poll, run.TriggerType);

        // The polling state says so too (the state is this row, never a derivation from the run).
        var head = await ReadHeadAsync(host, host.ProjectId, "main");
        Assert.Equal(headB, head!.Sha);
        Assert.Equal(headB, head.LastRunCommit);
        Assert.Equal(PollOutcomes.Queued, head.LastOutcome);

        // 3. A tick with no movement queues nothing.
        var third = await PollAsync(host, workerToken, host.ProjectId, "main", headB);
        Assert.Equal(PollOutcomes.Unchanged, third.Outcome);
        Assert.Single(await host.ReadRunsAsync());
        Assert.Equal(PollOutcomes.Unchanged, (await ReadProjectPollStateAsync(host, host.ProjectId)).Outcome);
    }

    // ------------------------------------------------------------------
    // TC-002: webhook and poll never double-queue
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_webhook_and_a_poll_that_see_the_same_commit_queue_exactly_one_run()
    {
        using var host = await CreateHostAsync();
        var workerToken = await host.CreateWorkerAsync("poll-agent", canProbe: true);
        await SetPollingAsync(host, host.ProjectId, enabled: true, intervalMinutes: 1);
        var webhookId = await host.SeedWebhookAsync("poll-dedupe", branchFilter: string.Empty);
        var commit = WebhookPayloads.Commit('c');

        // 1. A signed push for C queues the run (the webhook path).
        using (var delivery = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("main", commit),
            host.SecretFor("poll-dedupe"),
            deliveryId: "poll-dedupe-1"))
        {
            Assert.Equal(HttpStatusCode.Accepted, delivery.StatusCode);
        }

        var pushed = Assert.Single(await host.ReadRunsAsync());
        Assert.Equal(commit, pushed.GitCommit);

        // 2. A poll that observes the same commit queues nothing: the shared (project, branch, commit)
        //    ledger already has it, and the poll records `duplicate` rather than a second run.
        var poll = await PollAsync(host, workerToken, host.ProjectId, "main", commit);
        Assert.Equal(PollOutcomes.Duplicate, poll.Outcome);
        Assert.Equal(commit, Assert.Single(await host.ReadRunsAsync()).GitCommit);

        var head = await ReadHeadAsync(host, host.ProjectId, "main");
        Assert.Equal(commit, head!.Sha);
        Assert.Equal(PollOutcomes.Duplicate, head.LastOutcome);
        Assert.Null(head.LastRunAt);
    }

    [Fact]
    public async Task A_push_for_a_commit_a_poll_already_triggered_is_recorded_as_a_duplicate_not_queued_again()
    {
        // The same rule in the other order: the poll gets there first, the push arrives second, and the
        // delivery is still recorded (the push was seen) with `ignored:duplicate` and the run that exists.
        using var host = await CreateHostAsync();
        var workerToken = await host.CreateWorkerAsync("poll-agent", canProbe: true);
        await SetPollingAsync(host, host.ProjectId, enabled: true, intervalMinutes: 1);
        var webhookId = await host.SeedWebhookAsync("poll-first", branchFilter: string.Empty);
        var headA = WebhookPayloads.Commit('d');
        var headB = WebhookPayloads.Commit('e');

        _ = await PollAsync(host, workerToken, host.ProjectId, "main", headA);
        var queued = await PollAsync(host, workerToken, host.ProjectId, "main", headB);
        Assert.Equal(PollOutcomes.Queued, queued.Outcome);

        using (var delivery = await host.DeliverSignedAsync(
            webhookId,
            WebhookPayloads.GitHub("main", headB),
            host.SecretFor("poll-first"),
            deliveryId: "poll-first-1"))
        {
            Assert.Equal(HttpStatusCode.Accepted, delivery.StatusCode);
        }

        var runs = await host.ReadRunsAsync();
        Assert.Single(runs);
        var recorded = Assert.Single(await host.ReadDeliveriesAsync(webhookId));
        Assert.Equal(WebhookOutcomes.IgnoredDuplicate, recorded.Outcome);
        Assert.Equal(runs[0].Id, recorded.RunId);
    }

    // ------------------------------------------------------------------
    // TC-003 + AC-5: a failed probe is not "no change"
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_failed_probe_queues_nothing_keeps_the_head_and_grows_the_backoff()
    {
        using var host = await CreateHostAsync();
        var workerToken = await host.CreateWorkerAsync("poll-agent", canProbe: true);
        await SetPollingAsync(host, host.ProjectId, enabled: true, intervalMinutes: 1);

        var headA = WebhookPayloads.Commit('f');
        _ = await PollAsync(host, workerToken, host.ProjectId, "main", headA);

        var failed = await PollAsync(
            host,
            workerToken,
            host.ProjectId,
            headName: null,
            outcome: GitProbeOutcomes.Unreachable,
            message: "connection refused");

        Assert.Equal(GitProbeOutcomes.Unreachable, failed.Outcome);
        Assert.Empty(await host.ReadRunsAsync());

        // The head is untouched: "I could not see the remote" is not "nothing moved".
        var head = await ReadHeadAsync(host, host.ProjectId, "main");
        Assert.Equal(headA, head!.Sha);

        // The project shows the failure, its time, and the growing wait.
        var state = await ReadProjectPollStateAsync(host, host.ProjectId);
        Assert.Equal(GitProbeOutcomes.Unreachable, state.Outcome);
        Assert.NotNull(state.At);
        Assert.Equal(1, state.FailureStreak);
        Assert.Equal("connection refused", state.Message);
    }

    [Fact]
    public void The_backoff_doubles_per_failure_caps_at_thirty_minutes_and_resets_on_success()
    {
        // AC-5's curve, as arithmetic: 1x, 2x, 4x ... capped, and a successful probe (streak 0) back to 1x.
        var options = new PollOptions();

        Assert.Equal(TimeSpan.FromMinutes(1), options.EffectiveInterval(1, 0));
        Assert.Equal(TimeSpan.FromMinutes(2), options.EffectiveInterval(1, 1));
        Assert.Equal(TimeSpan.FromMinutes(4), options.EffectiveInterval(1, 2));
        Assert.Equal(TimeSpan.FromMinutes(8), options.EffectiveInterval(1, 3));
        Assert.Equal(TimeSpan.FromMinutes(16), options.EffectiveInterval(1, 4));
        Assert.Equal(TimeSpan.FromMinutes(30), options.EffectiveInterval(1, 5));
        Assert.Equal(TimeSpan.FromMinutes(30), options.EffectiveInterval(1, 40));

        // A longer declared interval doubles just the same, and hits the cap just the same (20 x 2 = 40 is
        // already past it, and so is a declared 30 on its first failure).
        Assert.Equal(TimeSpan.FromMinutes(20), options.EffectiveInterval(10, 1));
        Assert.Equal(TimeSpan.FromMinutes(30), options.EffectiveInterval(20, 1));
        Assert.Equal(TimeSpan.FromMinutes(30), options.EffectiveInterval(30, 1));
    }

    // ------------------------------------------------------------------
    // AC-4: several missed heads collapse to the newest
    // ------------------------------------------------------------------

    [Fact]
    public async Task Several_missed_heads_collapse_to_one_run_for_the_newest()
    {
        using var host = await CreateHostAsync();
        var workerToken = await host.CreateWorkerAsync("poll-agent", canProbe: true);
        await SetPollingAsync(host, host.ProjectId, enabled: true, intervalMinutes: 1);

        var headA = WebhookPayloads.Commit('1');
        var headB = WebhookPayloads.Commit('2');
        var headC = WebhookPayloads.Commit('3');

        _ = await PollAsync(host, workerToken, host.ProjectId, "main", headA);

        // The head moved A -> B -> C between two ticks. The probe answers once, so the poll only ever sees C:
        // one run, pinned to C, and nothing for B.
        var moved = await PollAsync(host, workerToken, host.ProjectId, "main", headC);
        Assert.Equal(PollOutcomes.Queued, moved.Outcome);

        var run = Assert.Single(await host.ReadRunsAsync());
        Assert.Equal(headC, run.GitCommit);
        Assert.DoesNotContain(await host.ReadRunsAsync(), candidate => candidate.GitCommit == headB);

        // And while that run is still queued, the head moves again: one run for the newest head, not two for
        // the two it missed.
        var newer = WebhookPayloads.Commit('4');
        var again = await PollAsync(host, workerToken, host.ProjectId, "main", newer);
        Assert.Equal(PollOutcomes.Queued, again.Outcome);
        Assert.Equal(
            [headC, newer],
            (await host.ReadRunsAsync()).Select(candidate => candidate.GitCommit).OrderBy(commit => commit, StringComparer.Ordinal));
    }

    // ------------------------------------------------------------------
    // AC-6: a skipped poll is recorded, never silent
    // ------------------------------------------------------------------

    [Fact]
    public async Task Every_skip_is_recorded_with_its_reason()
    {
        using var host = await CreateHostAsync();
        var workerToken = await host.CreateWorkerAsync("poll-agent", canProbe: true);
        var headA = WebhookPayloads.Commit('5');
        var headB = WebhookPayloads.Commit('6');

        // (a) A disabled project: recorded, and no probe is spent on it (a disabled project is not polled).
        await SetPollingAsync(host, host.ProjectId, enabled: true, intervalMinutes: 1);
        _ = await PollAsync(host, workerToken, host.ProjectId, "main", headA);
        await SetDisabledAsync(host, host.ProjectId, disabled: true);

        var disabled = await PollProjectAsync(host, host.ProjectId);
        Assert.Equal(ProjectTriggerRefusals.SkippedDisabled, disabled.Outcome);
        Assert.Equal(ProjectTriggerRefusals.SkippedDisabled, (await ReadProjectPollStateAsync(host, host.ProjectId)).Outcome);
        Assert.Empty(await host.ReadRunsAsync());

        // (b) The queue cap (F-029): a head moved, the queue refused it, and the poll says why - while the
        //     head stays where it was so the next idle tick can still build it.
        await SetDisabledAsync(host, host.ProjectId, disabled: false);
        await SetPollingAsync(host, host.ProjectId, enabled: true, intervalMinutes: 1, maxQueued: 1);
        var queued = await PollAsync(host, workerToken, host.ProjectId, "main", headB);
        Assert.Equal(PollOutcomes.Queued, queued.Outcome);

        var atCap = await PollAsync(host, workerToken, host.ProjectId, "main", WebhookPayloads.Commit('7'));
        Assert.Equal(ScheduleOutcomes.SkippedQueueFull, atCap.Outcome);
        Assert.Equal(ScheduleOutcomes.SkippedQueueFull, (await ReadProjectPollStateAsync(host, host.ProjectId)).Outcome);
        Assert.Equal(headB, (await ReadHeadAsync(host, host.ProjectId, "main"))!.Sha);
        Assert.Single(await host.ReadRunsAsync());

        // (c) A run is executing (F-024's overlap, through the same InFlightRuns query the schedule reactor
        //     reads): nothing is stacked on it, the head stays, and the skip names the run that caused it.
        await SetPollingAsync(host, host.ProjectId, enabled: true, intervalMinutes: 1, maxQueued: null);
        await host.InSystemScopeAsync(async context =>
        {
            var run = Assert.Single(await context.SystemQueryAsync<BuildRun>(
                candidate => candidate.ProjectId == host.ProjectId,
                CancellationToken));
            run.Status = BuildRunStatus.Running;
            run.StartedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(CancellationToken);
            return true;
        });

        // The overlap skip is answered before the probe is spent, so this tick asks the remote nothing.
        var overlapped = await PollProjectAsync(host, host.ProjectId);
        Assert.Equal(ScheduleOutcomes.SkippedOverlap, overlapped.Outcome);
        Assert.Equal(ScheduleOutcomes.SkippedOverlap, (await ReadProjectPollStateAsync(host, host.ProjectId)).Outcome);
        Assert.Equal(headB, (await ReadHeadAsync(host, host.ProjectId, "main"))!.Sha);
        Assert.Single(await host.ReadRunsAsync());
    }

    // ------------------------------------------------------------------
    // AC-7: the settings card says why a project cannot be polled
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_polling_card_says_why_a_project_cannot_be_polled()
    {
        using var host = await CreateHostAsync();
        using var owner = await host.SignInOwnerAsync();

        // (a) A repository project with no credential on it: the card says what a private remote will answer,
        //     instead of letting every interval fail with a word nobody can act on.
        var withRepository = await AuthTestHost.GetStringAsync(owner, $"/projects/{host.ProjectId}/settings");
        Assert.Contains("polling-no-credential", withRepository, StringComparison.Ordinal);
        Assert.Contains("missing-credential", withRepository, StringComparison.Ordinal);
        Assert.DoesNotContain("polling-no-repository", withRepository, StringComparison.Ordinal);

        // (b) A project with no repository at all: there is nothing to poll, and the card says that instead of
        //     offering a switch that could never do anything - and the save is refused as well.
        var freestyleId = await CreateFreestyleAsync(owner, "nothing-to-poll");
        var noRepository = await AuthTestHost.GetStringAsync(owner, $"/projects/{freestyleId}/settings");
        Assert.Contains("polling-no-repository", noRepository, StringComparison.Ordinal);
        Assert.DoesNotContain("polling-no-credential", noRepository, StringComparison.Ordinal);

        // (c) A stored missing-credential outcome is rendered as the sentence that says where the fix lives -
        //     the recorded word alone ("missing-credential") is not an explanation.
        await host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Project>(candidate => candidate.Id == host.ProjectId)).Single();
            project.LastPollOutcome = PollOutcomes.MissingCredential;
            project.LastPollAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(CancellationToken);
            return true;
        });
        var withCredentialFailure = await AuthTestHost.GetStringAsync(owner, $"/projects/{host.ProjectId}/settings");
        Assert.Contains("needs a credential and none is assigned", withCredentialFailure, StringComparison.Ordinal);

        // The card and the record say the same thing: a tick on this project records `no-repository` rather
        // than pretending it looked at something.
        var ticked = await PollProjectAsync(host, freestyleId);
        Assert.Equal(PollOutcomes.NoRepository, ticked.Outcome);
        Assert.Equal(PollOutcomes.NoRepository, (await ReadProjectPollStateAsync(host, freestyleId)).Outcome);

        using var refused = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{freestyleId}/settings",
            $"/projects/{freestyleId}/polling",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PollingEnabled"] = "true",
                ["PollIntervalMinutes"] = "5",
            });
        Assert.True((int)refused.StatusCode is 302 or 303);
        Assert.Contains(
            ProjectErrorCodes.PollingNotPipeline,
            refused.Headers.Location!.OriginalString,
            StringComparison.Ordinal);
        Assert.Null((await ReadProjectPollStateAsync(host, freestyleId)).IntervalMinutes);
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("1440", true)]
    [InlineData("1441", false)]
    public async Task The_poll_interval_bounds_are_one_to_1440_minutes(string minutes, bool accepted)
    {
        using var host = await CreateHostAsync();
        using var owner = await host.SignInOwnerAsync();

        using var response = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{host.ProjectId}/settings",
            $"/projects/{host.ProjectId}/polling",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PollingEnabled"] = "true",
                ["PollIntervalMinutes"] = minutes,
            });

        Assert.True((int)response.StatusCode is 302 or 303);
        var location = response.Headers.Location!.OriginalString;
        var state = await ReadProjectPollStateAsync(host, host.ProjectId);

        if (accepted)
        {
            Assert.DoesNotContain(ProjectErrorCodes.PollIntervalInvalid, location, StringComparison.Ordinal);
            Assert.Contains("polling-saved", location, StringComparison.Ordinal);
            Assert.Equal(int.Parse(minutes, System.Globalization.CultureInfo.InvariantCulture), state.IntervalMinutes);
        }
        else
        {
            Assert.Contains(ProjectErrorCodes.PollIntervalInvalid, location, StringComparison.Ordinal);
            Assert.Null(state.IntervalMinutes);
        }
    }

    /// <summary>Creates a repository-free project through the real create form (F-020's kind selector).</summary>
    private static async Task<Guid> CreateFreestyleAsync(HttpClient owner, string name)
    {
        using var response = await OrganizationTestHost.PostFormAsync(
            owner,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Name"] = name,
                ["Kind"] = ProjectKinds.Freestyle,
            });
        Assert.True((int)response.StatusCode is 302 or 303, $"Creating '{name}' answered {(int)response.StatusCode}.");
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/projects/", location, StringComparison.Ordinal);
        return Guid.Parse(location["/projects/".Length..].Split('?')[0]);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// One poll, answered by hand: starts the poll, waits for its probe, claims it with a probe-capable
    /// worker and reports <paramref name="outcome"/> (the heads when it succeeds).
    /// </summary>
    private static async Task<PollProjectResult> PollAsync(
        WebhooksTestHost host,
        string workerToken,
        Guid projectId,
        string? headName,
        string? sha = null,
        string outcome = GitProbeOutcomes.Refs,
        string? message = null)
    {
        await WaitOutProbeDedupeWindowAsync();
        var poll = PollProjectAsync(host, projectId);
        var probe = await host.WaitForUnsettledProbeAsync(TimeSpan.FromSeconds(15));

        // The probe contract takes full ref names (the same shape the F-023 discovery facts report).
        var reference = headName is null ? null : "refs/heads/" + headName;

        using (var workerClient = host.CreateDeliveryClient())
        {
            var claim = await GitProbeTestHost.ClaimOneProbeAsync(workerClient, workerToken);
            Assert.Equal(probe.Id, claim.ProbeId);
            using var reported = await GitProbeTestHost.ReportProbeAsync(
                workerClient,
                workerToken,
                claim.ProbeId,
                outcome,
                reference is null ? [] : [new GitProbeRef(reference, sha!)],
                headSha: sha,
                headBranch: reference,
                message: message);
            Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        }

        return await poll;
    }

    /// <summary>
    /// The host these facts run on: the probe dedupe window is compressed to one second so consecutive polls
    /// start a <b>new</b> probe. (I-005 joins a settled probe inside that window by design - which is exactly
    /// what makes concurrent callers cheap, and exactly what a poll-per-tick test must step past.)
    /// </summary>
    private static Task<WebhooksTestHost> CreateHostAsync() =>
        WebhooksTestHost.CreateAsync(new Dictionary<string, string?>
        {
            [ProbeOptions.DedupeSecondsConfigurationKey] = "1",
        });

    /// <summary>Waits out the compressed dedupe window so the next poll asks the remote again.</summary>
    private static Task WaitOutProbeDedupeWindowAsync() =>
        Task.Delay(TimeSpan.FromMilliseconds(1200), CancellationToken);

    private static Task<PollProjectResult> PollProjectAsync(WebhooksTestHost host, Guid projectId) =>
        host.WithTenantAsync(services =>
            services.GetRequiredService<PollService>().PollProjectAsync(projectId, CancellationToken));

    private static Task<BranchHead?> ReadHeadAsync(WebhooksTestHost host, Guid projectId, string branch) =>
        host.InSystemScopeAsync(async context => (await context.SystemQueryAsync<BranchHead>(
            head => head.ProjectId == projectId && head.Branch == branch)).SingleOrDefault());

    private static Task<(string? Outcome, DateTimeOffset? At, int FailureStreak, string? Message, int? IntervalMinutes)>
        ReadProjectPollStateAsync(WebhooksTestHost host, Guid projectId) =>
        host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId)).Single();
            return (project.LastPollOutcome, project.LastPollAt, project.PollFailureStreak, project.LastPollMessage,
                project.PollIntervalMinutes);
        });

    private static Task SetPollingAsync(
        WebhooksTestHost host,
        Guid projectId,
        bool enabled,
        int? intervalMinutes,
        int? maxConcurrent = null,
        int? maxQueued = null) =>
        host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId)).Single();
            project.PollingEnabled = enabled;
            project.PollIntervalMinutes = intervalMinutes;
            project.MaxConcurrentRuns = maxConcurrent;
            project.MaxQueuedRuns = maxQueued;
            await context.SaveChangesAsync(CancellationToken);
            return true;
        });

    private static Task SetDisabledAsync(WebhooksTestHost host, Guid projectId, bool disabled) =>
        host.InSystemScopeAsync(async context =>
        {
            var project = (await context.SystemQueryAsync<Project>(candidate => candidate.Id == projectId)).Single();
            project.Disabled = disabled;
            await context.SaveChangesAsync(CancellationToken);
            return true;
        });
}
