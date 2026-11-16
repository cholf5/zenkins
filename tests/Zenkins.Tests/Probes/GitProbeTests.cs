using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Git;
using Zenkins.Server.Probes;
using Zenkins.Shared.Probes;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Git;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.RunLifecycle;
using Zenkins.Tests.Workers;
using Zenkins.Worker;
using Zenkins.Worker.Git;

namespace Zenkins.Tests.Probes;

/// <summary>
/// Card I-005's acceptance facts over the product's own surfaces: the probe service (the requesting side),
/// the worker claim/report API, the probe-scoped credential route, and the read models the card promises
/// stay untouched (runs, dashboard, history).
/// <para>
/// <b>Nothing here fakes the pull model.</b> Every probe is claimed and reported through the same endpoints
/// the worker binary calls; the git work is either the real in-process worker (TC-001) or the real
/// <see cref="GitRefsProbe"/> against a real local bare repository (the rest). The three typed git failures
/// are covered at the mapping and report boundary - a real 401, a refused connection, a killed budget - with
/// no sshd and no hosted remote (card Q-009 owns the SSH harness).
/// </para>
/// </summary>
public sealed class GitProbeTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_probe_reports_the_remote_heads_and_creates_no_run()
    {
        // TC-001: the whole path, with the product's own worker process model - a real WorkerRunner
        // (registration, heartbeat, probe loop) answering a real bare repository.
        using var fixture = GitFixtureRepository.Create("i005-e2e");
        using var host = await GitProbeTestHost.CreateAsync();
        var projectId = await host.SeedProjectAsync("probe-target", fixture.BarePath);

        Assert.Empty(await host.ReadRunsAsync());
        var before = await host.DashboardAsync();

        await using (var worker = await InProcessWorker.StartAsync(host.Inner, "probe-agent"))
        {
            var result = await host.RequestProbeAsync(projectId, CancellationToken);

            Assert.Equal(GitProbeRequestStatus.Success, result.Status);
            Assert.Equal(GitProbeOutcomes.Refs, result.Outcome);
            Assert.Equal(
                new[] { "refs/heads/feature", "refs/heads/main" },
                result.Refs!.Heads.Select(head => head.Name).ToArray());
            Assert.Equal(fixture.FeatureTip, result.Refs.Heads.Single(head => head.Name == "refs/heads/feature").Sha);
            Assert.Equal(fixture.MainTip, result.Refs.Heads.Single(head => head.Name == "refs/heads/main").Sha);
            Assert.Equal(fixture.MainTip, result.Refs.HeadSha);
            Assert.Equal("refs/heads/main", result.Refs.HeadBranch);
        }

        // A probe is not a run: no BuildRun row, no queue/running count, nothing in the recent-runs list.
        Assert.Empty(await host.ReadRunsAsync());
        var after = await host.DashboardAsync();
        Assert.NotNull(after);
        Assert.Equal(0, after.QueuedCount);
        Assert.Equal(0, after.RunningCount);
        Assert.Empty(after.RecentRuns);
        Assert.Equal(before!.ProjectCount, after.ProjectCount);

        // ...and the outcome is visible on the project (the card's "last outcome on the project").
        var project = await host.ReadProjectRowAsync(projectId);
        Assert.Equal(GitProbeOutcomes.Refs, project.LastProbeOutcome);
        Assert.NotNull(project.LastProbeAt);
        Assert.Null(project.LastProbeMessage);

        using var owner = await host.Inner.SignInOwnerAsync();
        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}");
        Assert.Contains("project-last-probe-outcome", page, StringComparison.Ordinal);
        Assert.Contains("refs", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_probe_completes_while_every_build_slot_is_busy()
    {
        // The card's second criterion: probing never occupies or waits for a build slot. One worker, one
        // slot, and that slot is held by a claimed+accepted run - the probe is still leased and answered.
        using var fixture = GitFixtureRepository.Create("i005-slots");
        using var host = await GitProbeTestHost.CreateAsync();
        var projectId = await host.SeedProjectAsync("busy-target", fixture.BarePath);
        var (workerToken, workerId) = await host.RegisterWorkerAsync("busy-agent", canProbe: true, maxConcurrency: 1);
        _ = workerId;

        var runId = await host.Inner.AddRunAsync(projectId);
        using (var workerClient = host.CreateWorkerClient())
        {
            using var claimed = await WorkerTestHost.SendWorkerJsonAsync(
                workerClient, workerToken, "/api/workers/claim", new { });
            Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
            Assert.Equal(runId, await ReadGuidAsync(claimed, "runId"));

            using var accepted = await WorkerTestHost.SendWorkerJsonAsync(
                workerClient, workerToken, $"/api/workers/runs/{runId}/accepted", new { });
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

            // The build slot is now full: the next build claim gets nothing...
            using var nothing = await WorkerTestHost.SendWorkerJsonAsync(
                workerClient, workerToken, "/api/workers/claim", new { });
            Assert.Equal(HttpStatusCode.NoContent, nothing.StatusCode);
        }

        var requesting = host.RequestProbeAsync(projectId, CancellationToken);
        var pending = await host.WaitForUnsettledProbeAsync(TimeSpan.FromSeconds(10));

        using (var workerClient = host.CreateWorkerClient())
        {
            var claim = await GitProbeTestHost.ClaimOneProbeAsync(workerClient, workerToken);
            Assert.Equal(pending.Id, claim.ProbeId);

            // The probe really runs: the same git layer the worker uses, against the same repository.
            var result = await RunRealProbeAsync(fixture);
            using var reported = await GitProbeTestHost.ReportProbeAsync(
                workerClient,
                workerToken,
                claim.ProbeId,
                result.Outcome,
                result.Heads,
                result.HeadSha,
                result.HeadBranch,
                result.Message);
            Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        }

        var answered = await requesting;
        Assert.Equal(GitProbeOutcomes.Refs, answered.Outcome);
        Assert.Equal(2, answered.Refs!.Heads.Count);

        // The probe appeared in no run-shaped surface: the seeded run is the only row, and the dashboard
        // still shows exactly that one running build.
        var runs = await host.ReadRunsAsync();
        var run = Assert.Single(runs);
        Assert.Equal(runId, run.Id);
        Assert.Equal(BuildRunStatus.Running, run.Status);

        var dashboard = await host.DashboardAsync();
        Assert.Equal(1, dashboard!.RunningCount);
        Assert.Equal(0, dashboard.QueuedCount);
        Assert.Single(dashboard.RecentRuns);
    }

    [Fact]
    public async Task A_reported_failure_is_recorded_on_the_project_and_never_reads_as_no_change()
    {
        // The card's visibility criterion: a probe failure is a recorded, dated outcome on the project -
        // never a silent "no heads", and never anything a caller could mistake for "nothing changed".
        using var fixture = GitFixtureRepository.Create("i005-failure");
        using var host = await GitProbeTestHost.CreateAsync();
        var projectId = await host.SeedProjectAsync("failing-target", fixture.BarePath);
        var (workerToken, _) = await host.RegisterWorkerAsync("failing-agent", canProbe: true);

        var requesting = host.RequestProbeAsync(projectId, CancellationToken);
        var pending = await host.WaitForUnsettledProbeAsync(TimeSpan.FromSeconds(10));

        const string diagnostic = "the remote 'https://zenkins.example.com/team/app.git' rejected the credential (git exit 128).";
        using (var workerClient = host.CreateWorkerClient())
        {
            var claim = await GitProbeTestHost.ClaimOneProbeAsync(workerClient, workerToken);
            Assert.Equal(pending.Id, claim.ProbeId);

            using var reported = await GitProbeTestHost.ReportProbeAsync(
                workerClient,
                workerToken,
                claim.ProbeId,
                GitProbeOutcomes.AuthRejected,
                message: diagnostic);
            Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        }

        var result = await requesting;
        Assert.Equal(GitProbeRequestStatus.Success, result.Status);
        Assert.Equal(GitProbeOutcomes.AuthRejected, result.Outcome);
        Assert.False(GitProbeOutcomes.IsSuccess(result.Outcome));
        Assert.Null(result.Refs);
        Assert.Equal(diagnostic, result.Message);

        var project = await host.ReadProjectRowAsync(projectId);
        Assert.Equal(GitProbeOutcomes.AuthRejected, project.LastProbeOutcome);
        Assert.Equal(diagnostic, project.LastProbeMessage);
        Assert.NotNull(project.LastProbeAt);

        Assert.Empty(await host.ReadRunsAsync());

        using var owner = await host.Inner.SignInOwnerAsync();
        var page = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}");
        Assert.Contains("git-auth-rejected", page, StringComparison.Ordinal);

        // The message is HTML-encoded on the page (it can carry a remote URL), so the assertion is on a
        // fragment without characters the encoder rewrites: the page states the diagnostic, not only the word.
        Assert.Contains("rejected the credential", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_probe_capable_worker_the_probe_is_settled_as_no_worker_after_a_bounded_wait()
    {
        // The card's last criterion: the Server says "nobody is there" instead of waiting forever. The
        // registered worker is what a pre-I-005 binary looks like (it never announces the capability).
        using var fixture = GitFixtureRepository.Create("i005-noworker");
        using var host = await GitProbeTestHost.CreateAsync(builder => builder.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [ProbeOptions.WaitSecondsConfigurationKey] = "0.5",
                [ProbeOptions.PollMillisecondsConfigurationKey] = "25",
            }));
        var projectId = await host.SeedProjectAsync("unattended-target", fixture.BarePath);
        var (oldWorkerToken, _) = await host.RegisterWorkerAsync("pre-probe-agent", canProbe: false);

        // A worker that did not announce probing is never handed one - even with a probe waiting. The
        // legacy claim shape ("nothing for you") is what the old binary would receive if it ever asked.
        var started = DateTimeOffset.UtcNow;
        var result = await host.RequestProbeAsync(projectId, CancellationToken);
        var elapsed = DateTimeOffset.UtcNow - started;

        using (var workerClient = host.CreateWorkerClient())
        using (var claim = await GitProbeTestHost.ClaimProbeAsync(workerClient, oldWorkerToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, claim.StatusCode);
        }

        Assert.Equal(GitProbeStatus.NoWorker, (await host.FindProbeAsync(result.ProbeId!.Value))!.Status);
        Assert.Equal(GitProbeOutcomes.NoWorker, result.Outcome);
        Assert.False(GitProbeOutcomes.IsSuccess(result.Outcome));
        Assert.Contains("No probe-capable Worker", result.Message!, StringComparison.Ordinal);
        Assert.True(elapsed < TimeSpan.FromSeconds(20), $"The bounded wait took {elapsed.TotalSeconds:0.#}s.");

        var project = await host.ReadProjectRowAsync(projectId);
        Assert.Equal(GitProbeOutcomes.NoWorker, project.LastProbeOutcome);
        Assert.NotNull(project.LastProbeAt);
        Assert.Empty(await host.ReadRunsAsync());

        // A settled no-worker probe is never joined: the next caller gets a fresh attempt (a new id), so
        // a worker that appears later is not punished by a cached "nobody was there".
        var second = await host.RequestProbeAsync(projectId, CancellationToken);
        Assert.Equal(GitProbeOutcomes.NoWorker, second.Outcome);
        Assert.NotEqual(result.ProbeId, second.ProbeId);
    }

    [Fact]
    public async Task Three_callers_inside_the_window_share_one_probe_and_one_worker_conversation()
    {
        // The dedupe criterion: per (project, repository), one in-flight probe serves every caller. Three
        // concurrent callers, one probe row, one lease (= one git conversation) and three equal answers.
        using var fixture = GitFixtureRepository.Create("i005-dedupe");
        using var host = await GitProbeTestHost.CreateAsync();
        var projectId = await host.SeedProjectAsync("shared-target", fixture.BarePath);
        var (workerToken, _) = await host.RegisterWorkerAsync("shared-agent", canProbe: true);

        var callers = Enumerable.Range(0, 3)
            .Select(_ => Task.Run(() => host.RequestProbeAsync(projectId, CancellationToken), CancellationToken))
            .ToArray();

        var pending = await host.WaitForUnsettledProbeAsync(TimeSpan.FromSeconds(10));
        var registry = await host.RegistryAsync();
        Assert.Single(registry.Snapshots, probe => !probe.IsSettled);
        Assert.Equal(3, (await host.FindProbeAsync(pending.Id))!.RequestCount);

        using (var workerClient = host.CreateWorkerClient())
        {
            var claim = await GitProbeTestHost.ClaimOneProbeAsync(workerClient, workerToken);
            Assert.Equal(pending.Id, claim.ProbeId);

            var result = await RunRealProbeAsync(fixture);
            using var reported = await GitProbeTestHost.ReportProbeAsync(
                workerClient,
                workerToken,
                claim.ProbeId,
                result.Outcome,
                result.Heads,
                result.HeadSha,
                result.HeadBranch,
                result.Message);
            Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        }

        var answers = await Task.WhenAll(callers);
        Assert.All(answers, answer =>
        {
            Assert.Equal(GitProbeRequestStatus.Success, answer.Status);
            Assert.Equal(GitProbeOutcomes.Refs, answer.Outcome);
            Assert.Equal(pending.Id, answer.ProbeId);
            Assert.Equal(3, answer.RequestCount);
        });
        Assert.Single(answers.Select(answer => answer.Refs!.HeadSha).Distinct());

        // Exactly one lease was handed out, so the worker ran exactly one git conversation for the three
        // callers (the probe loop is one claim → one probe → one report; GitRefsProbeTests pins the
        // "one ls-remote per probe" half of this).
        var settled = await host.FindProbeAsync(pending.Id);
        Assert.Equal(1, settled!.Attempts);
        Assert.Equal(GitProbeStatus.Completed, settled.Status);
    }

    [Fact]
    public async Task Probe_and_run_credentials_do_not_replay_each_other()
    {
        // The card's credential criterion, both directions. The worker owns a run and a probe of the same
        // project - and each id only ever opens its own door.
        using var fixture = GitFixtureRepository.Create("i005-credential");
        using var host = await GitProbeTestHost.CreateAsync();
        var projectId = await host.SeedProjectAsync("credential-target", fixture.BarePath);
        var (workerToken, _) = await host.RegisterWorkerAsync("credential-agent", canProbe: true);

        using var owner = await host.Inner.SignInOwnerAsync();
        var credentialId = await CreateAndAssignCredentialAsync(host, owner, projectId);

        var runId = await host.Inner.AddRunAsync(projectId);
        using var workerClient = host.CreateWorkerClient();

        // The worker holds a run...
        using (var claimed = await WorkerTestHost.SendWorkerJsonAsync(
            workerClient, workerToken, "/api/workers/claim", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
            Assert.Equal(runId, await ReadGuidAsync(claimed, "runId"));
        }

        using (var accepted = await WorkerTestHost.SendWorkerJsonAsync(
            workerClient, workerToken, $"/api/workers/runs/{runId}/accepted", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        // ...and a probe.
        var requesting = host.RequestProbeAsync(projectId, CancellationToken);
        _ = await host.WaitForUnsettledProbeAsync(TimeSpan.FromSeconds(10));
        var claim = await GitProbeTestHost.ClaimOneProbeAsync(workerClient, workerToken);

        // (1) The probe's own route serves the material to the probe's leaseholder.
        using (var probeMaterial = await GitProbeTestHost.FetchProbeCredentialAsync(
            workerClient, workerToken, claim.ProbeId))
        {
            Assert.Equal(HttpStatusCode.OK, probeMaterial.StatusCode);
            using var body = JsonDocument.Parse(await probeMaterial.Content.ReadAsStringAsync(CancellationToken));
            Assert.Equal(GitCredentialProbeToken, body.RootElement.GetProperty("value").GetString());
        }

        // (2) The run's route knows nothing about probe ids: a probe id is a bare 404 there.
        using (var runRouteWithProbeId = await workerClient.SendAsync(
            Bearer(HttpMethod.Get, $"/api/workers/runs/{claim.ProbeId}/git-credential", workerToken),
            CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, runRouteWithProbeId.StatusCode);
            Assert.Equal(string.Empty, await runRouteWithProbeId.Content.ReadAsStringAsync(CancellationToken));
        }

        // (3) The probe's route knows nothing about run ids: a run id is a bare 404 there.
        using (var probeRouteWithRunId = await GitProbeTestHost.FetchProbeCredentialAsync(
            workerClient, workerToken, runId))
        {
            Assert.Equal(HttpStatusCode.NotFound, probeRouteWithRunId.StatusCode);
            Assert.Equal(string.Empty, await probeRouteWithRunId.Content.ReadAsStringAsync(CancellationToken));
        }

        // (4) Once the probe is answered its lease is gone, so its own id opens nothing either.
        var probeResult = await RunRealProbeAsync(fixture);
        using (var reported = await GitProbeTestHost.ReportProbeAsync(
            workerClient,
            workerToken,
            claim.ProbeId,
            probeResult.Outcome,
            probeResult.Heads,
            probeResult.HeadSha,
            probeResult.HeadBranch,
            probeResult.Message))
        {
            Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        }

        Assert.Equal(GitProbeOutcomes.Refs, (await requesting).Outcome);
        using (var afterSettled = await GitProbeTestHost.FetchProbeCredentialAsync(
            workerClient, workerToken, claim.ProbeId))
        {
            Assert.Equal(HttpStatusCode.NotFound, afterSettled.StatusCode);
        }

        // The run's own material still resolves (the probe consumed nothing of it).
        using var runMaterial = await workerClient.SendAsync(
            Bearer(HttpMethod.Get, $"/api/workers/runs/{runId}/git-credential", workerToken),
            CancellationToken);
        Assert.Equal(HttpStatusCode.OK, runMaterial.StatusCode);
        Assert.Equal(credentialId, await ReadProjectCredentialIdAsync(host, projectId));
    }

    [Fact]
    public async Task The_probe_api_never_crosses_the_tenant_boundary()
    {
        // TC-003's shape: organization A's worker cannot claim or report organization B's probe - and the
        // answers are the run API's (nothing claimable / a bare 404), never an existence oracle.
        using var fixture = GitFixtureRepository.Create("i005-tenancy");
        using var host = await GitProbeTestHost.CreateAsync();
        var (foreignOrganizationId, foreignOwner) = await host.CreateForeignOrganizationAsync(
            "Bravo",
            "bravo-owner@zenkins.example");
        using (foreignOwner)
        {
            var foreignProjectId = await host.SeedProjectInOrganizationAsync(
                foreignOrganizationId,
                "bravo-app",
                fixture.BarePath);

            var (alphaToken, _) = await host.RegisterWorkerAsync("alpha-probe-agent", canProbe: true);

            // The foreign probe is in flight (the hosting caller is organization B's own service call).
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var foreignRequest = host.RequestProbeAsync(
                foreignOrganizationId,
                foreignProjectId,
                cancel.Token);
            var pending = await host.WaitForUnsettledProbeAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(foreignProjectId, pending.ProjectId);

            using var alphaClient = host.CreateWorkerClient();
            using (var claim = await GitProbeTestHost.ClaimProbeAsync(alphaClient, alphaToken))
            {
                // Nothing is claimable for A: B's probe is not A's to take, and a claim carries no id to
                // leak one with. (204 is the claim's "nothing for you" answer, exactly like the run claim.)
                Assert.Equal(HttpStatusCode.NoContent, claim.StatusCode);
            }

            var foreignRef = new GitProbeRef("refs/heads/main", fixture.MainTip);
            using (var report = await GitProbeTestHost.ReportProbeAsync(
                alphaClient,
                alphaToken,
                pending.Id,
                GitProbeOutcomes.Refs,
                [foreignRef]))
            {
                // The report does carry an id, so this one is the bare 404 the run report endpoints answer.
                Assert.Equal(HttpStatusCode.NotFound, report.StatusCode);
                Assert.Equal(string.Empty, await report.Content.ReadAsStringAsync(CancellationToken));
            }

            // Nothing was recorded on the foreign probe or its project.
            Assert.False((await host.FindProbeAsync(pending.Id))!.IsSettled);
            var foreignProject = await ReadProjectAcrossTenantsAsync(host, foreignProjectId);
            Assert.Null(foreignProject.LastProbeOutcome);

            // The canceled caller stops waiting; the probe itself stays in flight (a lost caller, not a
            // lost probe - exactly the in-memory queue's documented shape).
            await cancel.CancelAsync();
            var canceled = await foreignRequest;
            Assert.Equal(GitProbeRequestStatus.NotFound, canceled.Status);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private const string GitCredentialProbeToken = "probe-scoped-token-value";

    /// <summary>Runs the real git layer once against the fixture (the probe's answer, honestly produced).</summary>
    private static async Task<GitRefsProbeResult> RunRealProbeAsync(GitFixtureRepository fixture)
    {
        var service = new WorkDirectoryService(fixture.Directory.Combine("probe-work"));
        var work = service.PrepareProbe(Guid.NewGuid());
        try
        {
            var probe = new GitRefsProbe(new GitRefsProbeOptions
            {
                RootPath = work.RootPath,
                TempPath = work.TempPath,
                HardeningPath = work.HardeningPath,
                OperationTimeout = TimeSpan.FromSeconds(60),
                EnvironmentOverrides = fixture.Environment,
            });
            return await probe.ProbeAsync(fixture.BarePath, credential: null, TestContext.Current.CancellationToken);
        }
        finally
        {
            service.CleanupProbe(work);
        }
    }

    private static async Task<Guid> CreateAndAssignCredentialAsync(
        GitProbeTestHost host,
        HttpClient owner,
        Guid projectId)
    {
        using var created = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/settings",
            $"{GitCredentialEndpoints.ManagementPath}/create",
            new Dictionary<string, string>
            {
                ["Name"] = "PROBE_TOKEN",
                ["Kind"] = "HttpsToken",
                ["Value"] = GitCredentialProbeToken,
                ["UserName"] = "x-access-token",
                ["ProjectId"] = projectId.ToString(),
            });
        Assert.True((int)created.StatusCode is 302 or 303, $"The credential create answered {(int)created.StatusCode}.");

        var list = await AuthTestHost.GetStringAsync(owner, $"/projects/{projectId}/git-credentials");
        using var document = JsonDocument.Parse(list);
        var credentialId = Assert.Single(document.RootElement.EnumerateArray()).GetProperty("id").GetGuid();

        using var attached = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/settings",
            $"/projects/{projectId}/git-credential/attach",
            new Dictionary<string, string> { ["GitCredentialId"] = credentialId.ToString() });
        Assert.True((int)attached.StatusCode is 302 or 303, $"The attach answered {(int)attached.StatusCode}.");
        return credentialId;
    }

    private static async Task<Guid> ReadProjectCredentialIdAsync(GitProbeTestHost host, Guid projectId) =>
        (await host.ReadProjectRowAsync(projectId)).GitCredentialId!.Value;

    private static Task<Project> ReadProjectAcrossTenantsAsync(GitProbeTestHost host, Guid projectId) =>
        host.Inner.Inner.InSystemScopeAsync(async context =>
            (await context.SystemQueryAsync<Project>(project => project.Id == projectId)).Single());

    private static async Task<Guid> ReadGuidAsync(HttpResponseMessage response, string property)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken));
        return document.RootElement.GetProperty(property).GetGuid();
    }

    private static HttpRequestMessage Bearer(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
