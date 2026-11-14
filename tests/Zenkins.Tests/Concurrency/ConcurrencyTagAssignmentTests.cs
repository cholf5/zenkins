using Xunit;
using Zenkins.Tests.Milestone;
using Zenkins.Tests.Recovery;

namespace Zenkins.Tests.Concurrency;

/// <summary>
/// Q-005 TC-002 (ACs: tag placement, honest starvation, trigger/claim consistency): workers tagged
/// <c>linux</c> and <c>windows</c> share one queue.
/// <list type="number">
/// <item>A run requiring <c>linux</c> is claimed only by the linux worker; the windows worker polls
/// the same queue the whole time and never receives it, and a windows-tagged probe's direct claim
/// reads the endpoint's 204 while the run is held.</item>
/// <item>Trigger-side validation matches claim-side filtering: a tag <b>no worker row declares at
/// all</b> is refused by the real trigger form (<c>runError=tag-unknown</c>), so an unmatchable run
/// cannot even be queued by hand.</item>
/// <item>A run whose tags match no <b>live</b> worker (they were declared by a worker that has since
/// gone offline - the honest way such a run exists) stays <c>Queued</c> - never Lost, never Failed -
/// while the mismatched workers keep polling, and executes exactly once when a matching worker
/// registers.</item>
/// </list>
/// </summary>
[Trait("Category", "Concurrency")]
public sealed class ConcurrencyTagAssignmentTests
{
    [Fact(Timeout = 7 * 60 * 1000)]
    public async Task Required_tags_place_a_run_and_starve_unmatched_workers_honestly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await ConcurrencyHost.StartAsync();

        var heldProject = await host.CreateProjectAsync("concurrency-tags-held");
        using var heldRepository = MilestoneRepository.Create(ConcurrencyPipelines.IdentityWithHold(20_000));
        await host.PointProjectAtAsync(heldProject, heldRepository.FileUrl);

        var starvedProject = await host.CreateProjectAsync("concurrency-tags-starved");
        using var starvedRepository = MilestoneRepository.Create(ConcurrencyPipelines.Identity);
        await host.PointProjectAtAsync(starvedProject, starvedRepository.FileUrl);

        var linux = await host.StartWorkerAsync("tags-linux", "linux,ci", 1);
        var windows = await host.StartWorkerAsync("tags-windows", "windows,ci", 1);
        Assert.Equal(2, await host.CountOnlineWorkersAsync());
        var windowsProbeToken = await host.RegisterTaggedProbeWorkerAsync("tags-probe-windows", "windows,probe");

        // ----- A linux-tagged run is claimed only by the linux worker. -----
        var heldRunId = await host.TriggerTaggedRunAsync(heldProject, requiredTags: "linux");
        await host.WaitForRunStatusAsync(heldRunId, "Running");
        var heldRun = await host.ReadRunAsync(heldRunId);
        var linuxRow = await host.ReadConcurrencyWorkerRowAsync(linux.Name);
        Assert.Equal(linuxRow.Id, heldRun.WorkerId);

        // The windows worker polls the same queue for the whole hold and never receives the run;
        // the windows probe's direct claim reads the 204 no eligible run produces.
        await host.AssertHoldsAsync(async () =>
        {
            Assert.Equal("Running", (await host.ReadRunAsync(heldRunId)).Status);
            Assert.DoesNotContain(
                windows.Output,
                line => line.Contains("executing in", StringComparison.Ordinal));
            using var claim = await host.ProbeClaimAsync(windowsProbeToken);
            RecoveryHost.AssertClaimGotNothing(claim);
        }, TimeSpan.FromSeconds(3), cancellationToken);

        var heldDone = await host.WaitForTerminalAsync(heldRunId);
        Assert.Equal("Success", heldDone.Status);
        Assert.Equal(0, heldDone.ExitCode);
        await host.AssertRunExecutedByExactlyOneWorkerAsync(heldDone, linux, [windows]);
        await host.AssertRunStepsAreOneUncorruptedSequenceAsync(heldRunId, ConcurrencyPipelines.HeldSteps);

        // ----- Trigger-side validation matches claim-side filtering: -----
        // a tag no worker row at all declares is refused at the real trigger form, and nothing is queued.
        var refused = await host.PostTriggerFormAsync(starvedProject, requiredTags: "gpu");
        Assert.True(
            (int)refused.StatusCode is 302 or 303,
            $"the trigger form answered {(int)refused.StatusCode} for an unknown tag.");
        Assert.Contains(
            "runError=tag-unknown",
            refused.Headers.Location!.OriginalString,
            StringComparison.Ordinal);
        Assert.Empty(await host.ReadProjectRunsAsync(starvedProject));

        // ----- A run whose tags match no live worker is starved honestly. -----
        // The gpu tag becomes declarable only by registering a gpu worker, which is then killed and
        // really flipped Offline by the server's own monitor - so the tag exists in an org worker row
        // (the trigger accepts it) while no live worker declares it.
        var gone = await host.StartWorkerAsync("tags-gpu-old", "gpu,ci", 1);
        host.KillWorker(gone);
        await RecoveryHost.WaitForProcessGoneAsync(gone.ProcessId);
        await host.WaitForWorkerStatusAsync("tags-gpu-old", "Offline", cancellationToken);

        var starvedRunId = await host.TriggerTaggedRunAsync(starvedProject, requiredTags: "gpu");

        // The mismatched workers keep polling; the run stays Queued - not Lost, not Failed.
        await host.AssertHoldsAsync(async () =>
        {
            Assert.Equal("Queued", (await host.ReadRunAsync(starvedRunId)).Status);
            using var claim = await host.ProbeClaimAsync(windowsProbeToken);
            RecoveryHost.AssertClaimGotNothing(claim);
        }, TimeSpan.FromSeconds(8), cancellationToken);
        Assert.DoesNotContain(
            linux.Output,
            line => line.Contains($"run {starvedRunId:D} executing in", StringComparison.Ordinal));
        Assert.DoesNotContain(
            windows.Output,
            line => line.Contains($"run {starvedRunId:D} executing in", StringComparison.Ordinal));
        await host.AssertRunSettlesInAsync(starvedRunId, cancellationToken, "Queued");

        // ----- A matching worker registers: the run executes, exactly once. -----
        var fresh = await host.StartWorkerAsync("tags-gpu-new", "gpu,ci", 1);
        var starvedDone = await host.WaitForTerminalAsync(starvedRunId);
        Assert.Equal("Success", starvedDone.Status);
        Assert.Equal(0, starvedDone.ExitCode);
        await host.AssertRunExecutedByExactlyOneWorkerAsync(starvedDone, fresh, [linux, windows, gone]);
        await host.AssertRunStepsAreOneUncorruptedSequenceAsync(starvedRunId, ConcurrencyPipelines.IdentitySteps);
    }
}
