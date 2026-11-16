using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Shared.RunCancellation;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Runs;

/// <summary>
/// Card F-028: a project can be disabled so that no trigger queues anything for it, while its history,
/// artifacts and settings stay readable, and enabling restores the triggers without replaying anything.
/// <para>
/// Everything here goes through the product's own surfaces - the settings form for the state change, the
/// real start-run form for the trigger, the real claim/accept/complete endpoints for the run that is in
/// flight - and the verdicts are read from the rows the server wrote, never from the responses alone.
/// </para>
/// </summary>
[Trait("Category", "Runs")]
public sealed class ProjectEnableDisableTests
{
    [Fact]
    public async Task A_disabled_project_refuses_the_trigger_form_and_queues_nothing()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("disable-refuses");

        var disable = await RunTestHost.DisableProjectAsync(owner, projectId, "repository is mid-migration");
        Assert.Equal(HttpStatusCode.Redirect, disable.StatusCode);
        Assert.Contains("notice=project-disabled", disable.Headers.Location!.OriginalString, StringComparison.Ordinal);

        var refused = await RunTestHost.StartRunAsync(owner, projectId);
        Assert.True((int)refused.StatusCode is 302 or 303);
        var location = refused.Headers.Location!.OriginalString;
        Assert.Equal($"/projects/{projectId}?runError=project-disabled", location);

        // Nothing was queued: the refusal happens before any insert.
        var runs = await host.ReadRunsAsync(await host.OrganizationIdAsync());
        Assert.Empty(runs);

        // The project page says it is disabled and why (the form is not offered at all).
        var page = await AuthTestHost.GetStringAsync(owner, location);
        Assert.Contains("project-disabled-notice", page, StringComparison.Ordinal);
        Assert.Contains("repository is mid-migration", page, StringComparison.Ordinal);
        Assert.Contains("run-form-disabled", page, StringComparison.Ordinal);
        Assert.DoesNotContain($"action=\"/projects/{projectId}/runs/start\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disabled_project_answers_its_reason_before_the_triggers_field_errors()
    {
        // Card F-028's precedence contract, locked: a disabled project refuses *before* the trigger's own
        // field validation runs. The card's reason is the actionable answer, and a form the project will
        // refuse must not spend the caller's attention on a branch name. (The queue cap is the other half
        // of the gate and is answered last, under the project lock - see the card's Notes.)
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("disable-precedence");
        Assert.Equal(
            HttpStatusCode.Redirect,
            (await RunTestHost.DisableProjectAsync(owner, projectId, "brake first")).StatusCode);

        // Three invalid fields in one request: without the gate-first order the redirect would carry
        // branch-invalid (the first validation failure), not the project's own refusal.
        var refused = await RunTestHost.StartRunAsync(
            owner,
            projectId,
            branch: "bad..branch",
            requiredTags: new string('x', 100),
            timeoutMinutes: "99999");

        Assert.True((int)refused.StatusCode is 302 or 303);
        Assert.Equal($"/projects/{projectId}?runError=project-disabled", refused.Headers.Location!.OriginalString);
        Assert.Empty(await host.ReadRunsAsync(await host.OrganizationIdAsync()));
    }

    [Fact]
    public async Task A_disable_without_a_reason_is_refused_and_changes_nothing()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("disable-no-reason");

        var refused = await RunTestHost.DisableProjectAsync(owner, projectId, reason: "   ");
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Contains(
            "error=disabled-reason-required",
            refused.Headers.Location!.OriginalString,
            StringComparison.Ordinal);

        var project = await host.ReadProjectAsync(projectId);
        Assert.False(project.Disabled);
        Assert.Null(project.DisabledReason);
        Assert.Null(project.DisabledAt);
        Assert.Null(project.DisabledByUserId);

        // ...and a valid one records the reason, the moment and the actor.
        var accepted = await RunTestHost.DisableProjectAsync(owner, projectId, "branch is broken");
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);

        project = await host.ReadProjectAsync(projectId);
        Assert.True(project.Disabled);
        Assert.Equal("branch is broken", project.DisabledReason);
        Assert.NotNull(project.DisabledAt);
        Assert.False(string.IsNullOrEmpty(project.DisabledByUserId));
    }

    [Fact]
    public async Task Disabling_cancels_the_queue_and_leaves_a_running_run_alone()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("disable-mid-flight");
        var organizationId = await host.OrganizationIdAsync();

        // Two runs already waiting, then one of them claimed and accepted (Running) by a real worker. The
        // claim is FIFO, so the run it returns is the oldest - the test works from that id, not from a
        // guessed one.
        await host.AddRunAsync(projectId, organizationId: organizationId);
        await host.AddRunAsync(projectId, organizationId: organizationId);

        var (workerToken, _) = await host.RegisterWorkerAsync(name: "disable-agent");
        using var workerClient = host.Host.CreateClient();
        var runningId = await RunTestHost.ReadClaimedRunIdAsync(
            await RunTestHost.ClaimAsync(workerClient, workerToken));
        Assert.Equal(HttpStatusCode.OK, (await RunTestHost.AcceptAsync(workerClient, workerToken, runningId)).StatusCode);

        // Whatever the claim left queued is exactly the second seeded run; assert the shape first, so a
        // FIFO surprise fails here instead of silently weakening the test.
        var beforeDisable = await host.ReadRunsAsync(organizationId);
        Assert.Equal(BuildRunStatus.Running, beforeDisable.Single(run => run.Id == runningId).Status);
        Assert.Single(beforeDisable, run =>
            run.Status == BuildRunStatus.Queued && run.ProjectId == projectId);

        var disable = await RunTestHost.DisableProjectAsync(owner, projectId, "stop the bleeding");
        Assert.Equal(HttpStatusCode.Redirect, disable.StatusCode);

        var afterDisable = await host.ReadRunsAsync(organizationId);

        // Every Queued run of the project is canceled, with the reason the card fixes - including the one
        // the claim had leased first (the lease holder's accept is the documented 409).
        foreach (var canceled in afterDisable.Where(run => run.ProjectId == projectId && run.Id != runningId))
        {
            Assert.Equal(BuildRunStatus.Canceled, canceled.Status);
            Assert.Equal(RunCancelReasons.ProjectDisabled, canceled.FailureReason);
            Assert.Null(canceled.LeaseExpiresAt);
            Assert.Null(canceled.WorkerId);
            Assert.NotNull(canceled.EndedAt);
        }

        Assert.Single(afterDisable, run => run.ProjectId == projectId && run.Status == BuildRunStatus.Canceled);
        Assert.Single(afterDisable, run => run.ProjectId == projectId && run.Status == BuildRunStatus.Running);

        // The running run is untouched: still Running, still this worker's, no cancel request recorded.
        var running = afterDisable.Single(run => run.Id == runningId);
        Assert.Equal(BuildRunStatus.Running, running.Status);
        Assert.Null(running.CancelRequestedAt);
        Assert.Null(running.FailureReason);

        // It finishes on its own, and its page notes the project was disabled while it was in flight.
        Assert.Equal(HttpStatusCode.OK, (await RunTestHost.CompleteAsync(workerClient, workerToken, runningId, 0)).StatusCode);
        Assert.Equal(
            BuildRunStatus.Success,
            (await host.ReadRunAsync(runningId)).Status);

        var runPage = await AuthTestHost.GetStringAsync(owner, $"/runs/{runningId}");
        Assert.Contains("Project disabled", runPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Re_enabling_restores_the_trigger_and_replays_nothing()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("disable-then-enable");
        var organizationId = await host.OrganizationIdAsync();

        // A run canceled by an earlier disable: enabling must never resurrect it.
        var queuedBefore = await host.AddRunAsync(projectId, organizationId: organizationId);
        Assert.Equal(HttpStatusCode.Redirect, (await RunTestHost.DisableProjectAsync(owner, projectId)).StatusCode);
        Assert.Equal(BuildRunStatus.Canceled, (await host.ReadRunAsync(queuedBefore)).Status);

        // Two refused triggers while disabled: neither may leave a trace.
        var firstRefusal = await RunTestHost.StartRunAsync(owner, projectId);
        var secondRefusal = await RunTestHost.StartRunAsync(owner, projectId);
        Assert.Contains("runError=project-disabled", firstRefusal.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Contains("runError=project-disabled", secondRefusal.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Single(await host.ReadRunsAsync(organizationId));

        var enable = await RunTestHost.EnableProjectAsync(owner, projectId);
        Assert.Equal(HttpStatusCode.Redirect, enable.StatusCode);
        Assert.Contains("notice=project-enabled", enable.Headers.Location!.OriginalString, StringComparison.Ordinal);

        var enabled = await host.ReadProjectAsync(projectId);
        Assert.False(enabled.Disabled);
        Assert.Null(enabled.DisabledReason);
        Assert.Null(enabled.DisabledAt);
        Assert.Null(enabled.DisabledByUserId);

        // Nothing was replayed: the refused triggers produced no run, and the canceled one stays canceled.
        Assert.Single(await host.ReadRunsAsync(organizationId));
        Assert.Equal(BuildRunStatus.Canceled, (await host.ReadRunAsync(queuedBefore)).Status);

        // The next trigger queues exactly one run.
        var thirdRunId = await RunTestHost.TriggerRunAsync(owner, projectId);
        var runs = await host.ReadRunsAsync(organizationId);
        Assert.Equal(2, runs.Count);
        Assert.Equal(BuildRunStatus.Queued, runs.Single(run => run.Id == thirdRunId).Status);
    }

    [Fact]
    public async Task The_disabled_state_is_visible_on_every_page_and_history_stays_readable()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("disable-visible");
        var organizationId = await host.OrganizationIdAsync();
        var runId = await host.AddRunAsync(projectId, organizationId: organizationId);

        Assert.Equal(HttpStatusCode.Redirect, (await RunTestHost.DisableProjectAsync(owner, projectId, "visible reason")).StatusCode);

        var projectsList = await AuthTestHost.GetStringAsync(owner, "/projects");
        Assert.Contains("Disabled", projectsList, StringComparison.Ordinal);

        var overview = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}");
        Assert.Contains("This project is disabled", overview, StringComparison.Ordinal);
        Assert.Contains("visible reason", overview, StringComparison.Ordinal);

        var runsPage = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/runs");
        Assert.Contains("Disabled", runsPage, StringComparison.Ordinal);
        Assert.Contains($"Run {runId:N}"[..12], runsPage, StringComparison.Ordinal);

        var settings = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/settings");
        Assert.Contains("trigger-state-disabled", settings, StringComparison.Ordinal);
        Assert.Contains($"action=\"/projects/{projectId}/enable\"", settings, StringComparison.Ordinal);
        Assert.DoesNotContain($"action=\"/projects/{projectId}/disable\"", settings, StringComparison.Ordinal);

        // AC 4's "the project's tabs stay readable": the remaining two routed tabs answer 200 as well
        // (GetStringAsync fails the test on any non-2xx, so reaching the assertion IS the status check)
        // and still render the shared tab strip. Artifacts are per run rather than a project tab, so the
        // run page asserted below is where they are covered.
        foreach (var tab in new[] { "pipelines", "parameters" })
        {
            var tabPage = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/{tab}");
            Assert.Contains("Project sections", tabPage, StringComparison.Ordinal);
        }

        var dashboard = await AuthTestHost.GetStringAsync(owner, "/");
        Assert.Contains("dashboard-disabled-projects", dashboard, StringComparison.Ordinal);

        // History stays readable: the run page and the artifacts tab are not gated by the flag.
        var runPage = await AuthTestHost.GetStringAsync(owner, $"/runs/{runId}");
        Assert.Contains("Project disabled", runPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_a_disabled_project_is_still_a_separate_owner_action()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("disable-not-delete");
        Assert.Equal(HttpStatusCode.Redirect, (await RunTestHost.DisableProjectAsync(owner, projectId)).StatusCode);

        // Disabling stored no deleted marker: the row is still there, with its history.
        var project = await host.ReadProjectAsync(projectId);
        Assert.True(project.Disabled);

        // The delete still needs its own confirmation route, and still removes the project.
        var confirmPage = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/delete/confirm");
        Assert.Contains($"action=\"/projects/{projectId}/delete\"", confirmPage, StringComparison.Ordinal);

        var deleted = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/delete/confirm",
            $"/projects/{projectId}/delete",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => host.ReadProjectAsync(projectId));
    }
}
