using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;
using Zenkins.Server.Workers;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Tenancy;

namespace Zenkins.Tests.Dashboard;

/// <summary>TC-002 at the page: a running run shows live elapsed time that actually moves.</summary>
[Trait("Category", "Dashboard")]
public sealed class DashboardElapsedPageTests : IClassFixture<DashboardPageFixture>
{
    private readonly DashboardPageFixture _host;

    public DashboardElapsedPageTests(DashboardPageFixture host) => _host = host;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_running_run_shows_elapsed_time_that_increases_between_renders()
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var workerId = await DashboardSeed.AddWorkerAsync(
            _host.Data, TwoOrgsFixture.OrgAId, "elapsed-agent", WorkerStatus.Online, DateTimeOffset.UtcNow, CancellationToken);
        await DashboardSeed.AddRunAsync(
            _host.Data, TwoOrgsFixture.OrgAId, TwoOrgsFixture.ProjectAId, BuildRunStatus.Running,
            startedAt: startedAt, workerId: workerId, cancellationToken: CancellationToken);

        using var client = await _host.SignInAsync("alice@example.test");

        var first = await AuthTestHost.GetStringAsync(client, "/");
        var firstElapsed = ReadRunningElapsed(first);

        // A few seconds of wall time, exactly like the card's manual steps.
        await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken);

        var second = await AuthTestHost.GetStringAsync(client, "/");
        var secondElapsed = ReadRunningElapsed(second);

        // Not blank, not zero, marked as running, and older than the two minutes the run already has.
        Assert.EndsWith("(running)", firstElapsed, StringComparison.Ordinal);
        Assert.StartsWith("2m", firstElapsed, StringComparison.Ordinal);
        Assert.True(
            ParseElapsed(firstElapsed) >= TimeSpan.FromMinutes(2),
            $"The running run showed {firstElapsed}; it was started two minutes ago.");

        // And it moved.
        Assert.True(
            ParseElapsed(secondElapsed) > ParseElapsed(firstElapsed),
            $"Elapsed did not increase between renders: '{firstElapsed}' then '{secondElapsed}'.");
    }

    /// <summary>Reads the duration cell of the one run row that is still executing.</summary>
    private static string ReadRunningElapsed(string html)
    {
        var matches = Regex.Matches(html, "id=\"dashboard-run-[0-9a-f]{8}-duration\"[^>]*>([\\s\\S]*?)</span>");
        Assert.True(matches.Count > 0, "The page did not render any run's duration cell.");
        var cells = matches
            .Select(match => Regex.Replace(match.Groups[1].Value, "<[^>]+>", string.Empty).Trim())
            .ToList();
        var running = cells.FirstOrDefault(cell => cell.EndsWith("(running)", StringComparison.Ordinal));
        Assert.True(
            running is not null,
            $"No duration cell reads as still running; the cells were: {string.Join(" | ", cells)}");
        return running!;
    }

    private static TimeSpan ParseElapsed(string text)
    {
        var match = Regex.Match(text, "(\\d+)m (\\d{2})s");
        Assert.True(match.Success, $"Elapsed text '{text}' is not in the m ss shape the formatter promises.");
        return new TimeSpan(0, int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
    }
}

/// <summary>The dashboard's empty states are specific and say what to do next (D-002 AC).</summary>
[Trait("Category", "Dashboard")]
public sealed class DashboardEmptyStatePageTests : IClassFixture<DashboardPageFixture>
{
    private readonly DashboardPageFixture _host;

    public DashboardEmptyStatePageTests(DashboardPageFixture host) => _host = host;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_empty_state_says_what_to_do_next()
    {
        await DashboardSeed.SeedEmptyOrganizationAsync(
            _host.Data,
            new Guid("c0000000-0000-4000-8000-000000000003"),
            "Gamma",
            "dana",
            "dana@example.test",
            CancellationToken);

        using var client = await _host.SignInAsync("dana@example.test");

        // Nothing at all: no projects (with its next step), no workers, no run rows.
        var empty = await AuthTestHost.GetStringAsync(client, "/");
        Assert.Contains("No projects yet", empty, StringComparison.Ordinal);
        Assert.Contains("New project", empty, StringComparison.Ordinal);
        Assert.Contains("No workers connected", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"dashboard-run-", empty, StringComparison.Ordinal);

        // One project in: the runs empty state takes over, workers still report their own state.
        var created = await OrganizationTestHost.PostFormAsync(
            client,
            "/projects/new",
            "/projects/create",
            new Dictionary<string, string>
            {
                ["Name"] = "gamma-first",
                ["RepositoryUrl"] = "https://example.test/gamma-first.git",
                ["DefaultBranch"] = "main",
                ["PipelinePath"] = "pipeline.lua",
            });
        Assert.True((int)created.StatusCode is 302 or 303, $"Creating the project failed with {(int)created.StatusCode}.");

        var withProject = await AuthTestHost.GetStringAsync(client, "/");
        Assert.Contains("No runs yet", withProject, StringComparison.Ordinal);
        Assert.Contains("No workers connected", withProject, StringComparison.Ordinal);
        Assert.DoesNotContain("No projects yet", withProject, StringComparison.Ordinal);
    }
}

/// <summary>Owner and Member both see the dashboard, and it offers no write affordance (D-002 AC).</summary>
[Trait("Category", "Dashboard")]
public sealed class DashboardAccessPageTests : IClassFixture<DashboardPageFixture>
{
    private readonly DashboardPageFixture _host;

    public DashboardAccessPageTests(DashboardPageFixture host) => _host = host;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Owners_and_members_see_the_dashboard_and_it_has_no_write_affordance()
    {
        using var owner = await _host.SignInAsync("alice@example.test");
        var ownerHtml = await AuthTestHost.GetStringAsync(owner, "/");
        AssertReadonlyDashboard(ownerHtml);
        Assert.Equal("1", DashboardPageReader.TextById(ownerHtml, "dashboard-queued-count"));

        using var member = await _host.SignInAsync("bob@example.test");
        var memberHtml = await AuthTestHost.GetStringAsync(member, "/");
        AssertReadonlyDashboard(memberHtml);
        Assert.Equal("1", DashboardPageReader.TextById(memberHtml, "dashboard-queued-count"));

        // Not one form is rendered anywhere in the static page: the shell's organization switcher
        // only exists while its menu is open, so what the operator sees on "/" is links and
        // numbers - nothing that can POST.
        static void AssertReadonlyDashboard(string html) =>
            Assert.DoesNotContain("<form", html, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Worker liveness: the dashboard reads the status the offline sweeper (F-010) wrote - the test
/// drives the real sweeper and the offline count agrees with it, with no heartbeat arithmetic
/// anywhere on the page.
/// </summary>
[Trait("Category", "Dashboard")]
public sealed class DashboardWorkerLivenessTests : IClassFixture<DashboardPageFixture>
{
    private readonly DashboardPageFixture _host;

    public DashboardWorkerLivenessTests(DashboardPageFixture host) => _host = host;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_offline_count_is_what_the_real_sweeper_produces()
    {
        // Sign in first: creating the first client boots the host, and the monitor's startup tick
        // sweeps once immediately. Seeding after that boot means the sweep below is the one that
        // owns this worker's Online -> Offline transition (the fixture's own workers are either
        // Offline or have no heartbeat, so the startup sweep does nothing).
        using var client = await _host.SignInAsync("alice@example.test");

        // An Online worker whose heartbeat went stale (well past the sweeper's threshold) with a
        // run still executing on it.
        var workerId = await DashboardSeed.AddWorkerAsync(
            _host.Data, TwoOrgsFixture.OrgAId, "liveness-agent", WorkerStatus.Online,
            DateTimeOffset.UtcNow.AddSeconds(-30), CancellationToken);
        var runningRunId = await DashboardSeed.AddRunAsync(
            _host.Data, TwoOrgsFixture.OrgAId, TwoOrgsFixture.ProjectAId, BuildRunStatus.Running,
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-5), workerId: workerId, cancellationToken: CancellationToken);

        var monitor = _host.Factory.Services
            .GetServices<IHostedService>()
            .OfType<WorkerOfflineMonitor>()
            .Single();
        var sweep = await monitor.SweepOnceAsync(CancellationToken);

        Assert.Equal(1, sweep.WorkersOfflined);
        Assert.Equal(1, sweep.RunsLost);

        _host.Data.UseOrganizationA();
        var summary = await DashboardQueryFactory.Create(_host.Data).GetSummaryAsync(CancellationToken);

        Assert.NotNull(summary);
        // The dashboard's offline count is the row count the sweeper left behind - compared with
        // direct SQL, and equal to the sweep's own report.
        Assert.Equal(
            await DashboardSql.CountWorkersAsync(_host.Data, TwoOrgsFixture.OrgAId, WorkerStatus.Offline, CancellationToken),
            summary.WorkersOffline);
        Assert.Equal(2, summary.WorkersOffline);
        Assert.Equal(0, summary.WorkersOnline);
        Assert.Equal(0, summary.WorkersBusy);
        var lost = summary.RecentRuns.Single(run => run.RunId == runningRunId);
        Assert.Equal(BuildRunStatus.Lost, lost.Status);
        Assert.Equal(RunFailureReasons.WorkerOffline, lost.FailureReason);
        Assert.Contains(lost.RunId, summary.RecentFailures.Select(run => run.RunId));
    }
}

/// <summary>
/// Switching organization through the real switch endpoint replaces every figure on the page, in
/// both directions (D-002 AC); the other organization's rows never appear.
/// </summary>
[Trait("Category", "Dashboard")]
public sealed class DashboardOrganizationSwitchPageTests : IClassFixture<DashboardPageFixture>
{
    private readonly DashboardPageFixture _host;

    public DashboardOrganizationSwitchPageTests(DashboardPageFixture host) => _host = host;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Switching_organization_replaces_every_figure_in_both_directions()
    {
        var now = DateTimeOffset.UtcNow;
        // A executes and fails; B only queues.
        var busyWorkerId = await DashboardSeed.AddWorkerAsync(
            _host.Data, TwoOrgsFixture.OrgAId, "switch-agent", WorkerStatus.Online, now, CancellationToken);
        await DashboardSeed.AddRunAsync(
            _host.Data, TwoOrgsFixture.OrgAId, TwoOrgsFixture.ProjectAId, BuildRunStatus.Running,
            startedAt: now.AddMinutes(-1), workerId: busyWorkerId, cancellationToken: CancellationToken);
        await DashboardSeed.AddRunAsync(
            _host.Data, TwoOrgsFixture.OrgAId, TwoOrgsFixture.ProjectAId, BuildRunStatus.Failed,
            startedAt: now.AddMinutes(-5), endedAt: now.AddMinutes(-4), exitCode: 2, cancellationToken: CancellationToken);
        await DashboardSeed.AddRunAsync(
            _host.Data, TwoOrgsFixture.OrgBId, TwoOrgsFixture.ProjectBId, BuildRunStatus.Queued,
            cancellationToken: CancellationToken);

        // One account on both sides, so the real switch endpoint can move the dashboard.
        await DashboardSeed.AddMembershipAsync(
            _host.Data, TwoOrgsFixture.OrgBId, TwoOrgsFixture.OrgAOwnerUserId, CancellationToken);

        using var client = await _host.SignInAsync("alice@example.test");

        var alpha = await AuthTestHost.GetStringAsync(client, "/");
        Assert.Equal("1", DashboardPageReader.TextById(alpha, "dashboard-queued-count"));
        Assert.Equal("1", DashboardPageReader.TextById(alpha, "dashboard-running-count"));
        Assert.Equal("1", DashboardPageReader.TextById(alpha, "dashboard-failure-count"));
        Assert.Equal("1", DashboardPageReader.TextById(alpha, "dashboard-workers-online"));
        Assert.Equal("1", DashboardPageReader.TextById(alpha, "dashboard-workers-busy"));
        Assert.Equal("1", DashboardPageReader.TextById(alpha, "dashboard-workers-offline"));
        Assert.Contains(TwoOrgsFixture.ProjectAName, alpha, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.ProjectBName, alpha, StringComparison.Ordinal);

        var switchToBeta = await OrganizationTestHost.PostFormAsync(
            client,
            "/",
            "/organizations/switch",
            new Dictionary<string, string>
            {
                ["OrganizationId"] = TwoOrgsFixture.OrgBId.ToString(),
                ["ReturnUrl"] = "/",
            });
        Assert.True((int)switchToBeta.StatusCode is 302 or 303);

        var beta = await AuthTestHost.GetStringAsync(client, "/");
        Assert.Equal("2", DashboardPageReader.TextById(beta, "dashboard-queued-count"));
        Assert.Equal("0", DashboardPageReader.TextById(beta, "dashboard-running-count"));
        Assert.Equal("0", DashboardPageReader.TextById(beta, "dashboard-failure-count"));
        Assert.Equal("1", DashboardPageReader.TextById(beta, "dashboard-workers-online"));
        Assert.Equal("0", DashboardPageReader.TextById(beta, "dashboard-workers-busy"));
        Assert.Equal("0", DashboardPageReader.TextById(beta, "dashboard-workers-offline"));
        Assert.Contains(TwoOrgsFixture.ProjectBName, beta, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.ProjectAName, beta, StringComparison.Ordinal);

        var switchBack = await OrganizationTestHost.PostFormAsync(
            client,
            "/",
            "/organizations/switch",
            new Dictionary<string, string>
            {
                ["OrganizationId"] = TwoOrgsFixture.OrgAId.ToString(),
                ["ReturnUrl"] = "/",
            });
        Assert.True((int)switchBack.StatusCode is 302 or 303);

        var alphaAgain = await AuthTestHost.GetStringAsync(client, "/");
        Assert.Equal("1", DashboardPageReader.TextById(alphaAgain, "dashboard-running-count"));
        Assert.Contains(TwoOrgsFixture.ProjectAName, alphaAgain, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.ProjectBName, alphaAgain, StringComparison.Ordinal);
    }
}
