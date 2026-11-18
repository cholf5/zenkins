using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Zenkins.Server.Branches;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Probes;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Probes;
using Zenkins.Tests.Runs;
using Zenkins.Tests.Webhooks;

namespace Zenkins.Tests.Branches;

/// <summary>
/// Card Q-022's case-sensitivity debt. F-023's review found the service-side Ordinal behaviour pinned only
/// at the database (a PostgreSQL fact proves <c>Main</c> and <c>main</c> coexist as rows), while the
/// mutation that lower-cased branch names in the service stayed green: git treats the two refs as different
/// branches, and every in-memory structure between the probe and the page has to agree.
/// <para>
/// Three surfaces, three facts - the discovery dictionaries, the branch list's ordering and the runs filter.
/// Each one fails when a comparer is case-insensitive or a branch name is folded to lower case, which is
/// exactly what the review's mutation did.
/// </para>
/// </summary>
[Trait("Category", "Branches")]
public sealed class BranchCaseSensitivityTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Discovery_keeps_heads_that_differ_only_in_case_apart()
    {
        // The in-memory dictionaries of one discovery pass: the head set, the head->commit map and the
        // registry rows keyed by branch. All three are Ordinal, because git's refs are byte strings.
        using var host = await WebhooksTestHost.CreateAsync(new Dictionary<string, string?>
        {
            [BranchDiscoveryOptions.SweepIntervalSecondsKey] = "3600",
        });
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);
        var workerToken = await host.CreateWorkerAsync("case-discovery-agent", canProbe: true);

        var upperSha = WebhookPayloads.Commit('a');
        var lowerSha = WebhookPayloads.Commit('b');

        var discovery = host.WithTenantAsync(services =>
            services.GetRequiredService<BranchDiscoveryService>()
                .DiscoverAsync(host.ProjectId, CancellationToken));
        var probe = await host.WaitForUnsettledProbeAsync(TimeSpan.FromSeconds(10));

        using (var workerClient = host.CreateDeliveryClient())
        {
            var claim = await GitProbeTestHost.ClaimOneProbeAsync(workerClient, workerToken);
            Assert.Equal(probe.Id, claim.ProbeId);
            using var reported = await GitProbeTestHost.ReportProbeAsync(
                workerClient,
                workerToken,
                claim.ProbeId,
                GitProbeOutcomes.Refs,
                [
                    new GitProbeRef("refs/heads/Main", upperSha),
                    new GitProbeRef("refs/heads/main", lowerSha),
                ],
                headSha: upperSha,
                headBranch: "refs/heads/Main");
            Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        }

        var result = await discovery;
        Assert.True(result.Succeeded);

        // Two heads, two rows, two reads - not one collapsed branch and a lost commit. A dictionary keyed
        // case-insensitively (or a branch name folded to lower case) answers 1/1/1 here.
        Assert.Equal(2, result.Heads);
        Assert.Equal(2, result.Registered);
        Assert.Equal(2, result.Awaiting);

        var rows = await host.ReadBranchesAsync();
        Assert.Equal(
            new[] { "Main", "main" },
            rows.Select(row => row.Branch).OrderBy(branch => branch, StringComparer.Ordinal));
        Assert.All(rows, row => Assert.Equal(BranchPipelineStates.Unknown, row.PipelineState));

        // Each row keeps its own head: the two commits are not shared between the two branches.
        Assert.Equal(upperSha, rows.Single(row => row.Branch == "Main").PendingCommit);
        Assert.Equal(lowerSha, rows.Single(row => row.Branch == "main").PendingCommit);

        var reads = (await host.ObservationsAsync())
            .Where(entry => entry.Kind == DefinitionValidationKinds.RepositoryBranch && !entry.Settled)
            .ToArray();
        Assert.Equal(2, reads.Length);
        Assert.Equal(
            new[] { "Main", "main" },
            reads.Select(entry => entry.Branch!).OrderBy(branch => branch, StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_branch_list_orders_names_ordinally_so_Main_and_main_sit_apart()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        await host.SetBranchDiscoveryAsync(host.ProjectId, BranchDiscoveryModes.Branches);

        // Names chosen so that an ordinal sort and a culture/case-insensitive sort disagree: uppercase
        // sorts before lowercase in Ordinal ('B' 0x42, 'M' 0x4D vs 'a' 0x61, 'm' 0x6D).
        foreach (var branch in new[] { "Main", "main", "a", "B" })
        {
            await host.SeedBranchAsync(branch, BranchPipelineStates.Unknown, WebhookPayloads.Commit('c'));
        }

        var listed = await host.WithTenantAsync(services =>
            services.GetRequiredService<BranchService>().GetBranchesAsync(host.ProjectId, CancellationToken));

        // Both case-differing branches are listed (a folded dictionary collapses them into one), in git's
        // own ref order.
        Assert.Equal(new[] { "B", "Main", "a", "main" }, listed.Select(branch => branch.Branch));
    }

    [Fact]
    public async Task The_runs_filter_distinguishes_branches_that_differ_only_in_case()
    {
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();

        // Two runs whose branch snapshots differ only in case - the two refs a repository can really have.
        var upperRunId = await RunTestHost.TriggerRunAsync(owner, host.ProjectId, branch: "Main");
        var lowerRunId = await RunTestHost.TriggerRunAsync(owner, host.ProjectId, branch: "main");

        // Each filter answers exactly its own run: a case-insensitive filter (or a run whose branch was
        // folded to lower case on the way in) returns both, or the wrong one.
        var upper = await RunsAsync(host, "Main");
        Assert.Equal(new[] { upperRunId }, upper.Select(run => run.Id));
        Assert.Equal("Main", Assert.Single(upper).GitBranch);

        var lower = await RunsAsync(host, "main");
        Assert.Equal(new[] { lowerRunId }, lower.Select(run => run.Id));
        Assert.Equal("main", Assert.Single(lower).GitBranch);

        // And both are still on the project's unfiltered list.
        Assert.Equal(2, (await RunsAsync(host, branch: null)).Count);
    }

    [Fact]
    public async Task The_branch_list_gives_branches_that_differ_only_in_case_their_own_last_run()
    {
        // The registry read folds two more Ordinal dictionaries: the runs by branch (`lastRunByBranch`) and
        // the views by branch (`byBranch`). A case-insensitive fold here does not lose a branch - the list
        // still shows `Main` and `main` - it silently gives both of them the same LastRun (the higher
        // numbered one). That is the quiet half of the case-sensitivity hole: no row disappears, so nothing
        // looks wrong, and the Branches tab renders one branch's status under the other's name.
        using var host = await WebhooksTestHost.CreateAsync();
        using var owner = await host.SignInOwnerAsync();

        // Both branches are real registry rows *and* carry a run, so the assertion is about the fold that
        // joins the two (a run-only branch would take the history path instead).
        await host.SeedBranchAsync("Main", BranchPipelineStates.HasPipeline, WebhookPayloads.Commit('d'));
        await host.SeedBranchAsync("main", BranchPipelineStates.HasPipeline, WebhookPayloads.Commit('e'));
        var upperRunId = await RunTestHost.TriggerRunAsync(owner, host.ProjectId, branch: "Main");
        var lowerRunId = await RunTestHost.TriggerRunAsync(owner, host.ProjectId, branch: "main");

        var listed = await host.WithTenantAsync(services =>
            services.GetRequiredService<BranchService>().GetBranchesAsync(host.ProjectId, CancellationToken));

        Assert.Equal(new[] { "Main", "main" }, listed.Select(branch => branch.Branch));
        var upper = listed.Single(branch => branch.Branch == "Main");
        var lower = listed.Single(branch => branch.Branch == "main");

        // The load-bearing assertion first: the two views must not share a LastRun. A case-insensitive
        // dictionary hands both branches the higher-numbered run, which makes this fail before anything else.
        Assert.NotEqual(upper.LastRun?.RunId, lower.LastRun?.RunId);
        Assert.Equal(upperRunId, upper.LastRun?.RunId);
        Assert.Equal(lowerRunId, lower.LastRun?.RunId);
    }

    private static Task<IReadOnlyList<RunView>> RunsAsync(WebhooksTestHost host, string? branch) =>
        host.WithTenantAsync(services =>
            services.GetRequiredService<RunService>()
                .GetProjectRunsAsync(host.ProjectId, branch, CancellationToken));
}
