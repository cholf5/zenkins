using Xunit;
using Zenkins.Tests.Milestone;
using Zenkins.Tests.Recovery;

namespace Zenkins.Tests.Concurrency;

/// <summary>
/// Q-010's pin: the trigger/claim tag asymmetry is <b>documented semantics</b>, not an accident
/// (the card's written decision - union-accept + honest starvation, kept after weighing the
/// tighten alternative). The trigger form checks the workers' <b>union</b> (every required tag is
/// declared by some worker row) while claiming requires <b>one worker</b> whose own tags cover the
/// whole set, so a run whose required set splits across workers is accepted by the real form and
/// then starves honestly: it stays <c>Queued</c> (never <c>Lost</c>, never <c>Failed</c>) while
/// only mismatched workers poll - and then executes exactly once when a covering worker registers.
/// That "queue first, a covering worker arrives later" workflow is what the semantics exists to
/// serve, so the test proves the execution, not just the starvation. The matched case - one worker
/// declaring the full set - is unchanged from Q-005 TC-002: accepted by the same form and placed.
/// </summary>
[Trait("Category", "Concurrency")]
public sealed class ConcurrencyTagValidationTests
{
    [Fact(Timeout = 7 * 60 * 1000)]
    public async Task Split_required_tags_are_accepted_starve_honestly_and_run_when_a_covering_worker_registers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var host = await ConcurrencyHost.StartAsync();

        var project = await host.CreateProjectAsync("q010-split-tags");
        using var repository = MilestoneRepository.Create(ConcurrencyPipelines.Identity);
        await host.PointProjectAtAsync(project, repository.FileUrl);

        // Two real workers whose tags cover the required set only in union: no single worker's own
        // tags can claim a run requiring both, exactly the shape the card records.
        var linux = await host.StartWorkerAsync("q010-linux", "linux", 1);
        var cuda = await host.StartWorkerAsync("q010-cuda", "cuda", 1);
        Assert.Equal(2, await host.CountOnlineWorkersAsync());
        var linuxProbeToken = await host.RegisterTaggedProbeWorkerAsync("q010-probe-linux", "linux");

        // ----- TC-001, the form response: the union rule accepts the split set. -----
        // The raw trigger-form POST, inspected directly: a redirect to the new run with no
        // runError - the same acceptance a single-tag run gets (a tightening to single-worker
        // coverage would refuse here; the decision keeps it accepting).
        var response = await host.PostTriggerFormAsync(project, requiredTags: "linux,cuda");
        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"the trigger form answered {(int)response.StatusCode} for a union-coverable tag set.");
        var location = response.Headers.Location!.OriginalString;
        Assert.DoesNotContain("runError=", location, StringComparison.Ordinal);
        Assert.StartsWith("/runs/", location, StringComparison.Ordinal);
        var splitRunId = Guid.Parse(location["/runs/".Length..].Split('?')[0]);

        // ----- TC-001, the queue outcome: honest starvation while uncovered. -----
        // Both mismatched workers keep polling the whole window and neither receives the run; a
        // linux-tagged probe's direct claim - a worker that matches one tag but not both, the
        // asymmetry's own shape - reads the 204 no eligible run produces; the run stays Queued
        // through sampling, never Lost, never Failed.
        await host.AssertHoldsAsync(async () =>
        {
            Assert.Equal("Queued", (await host.ReadRunAsync(splitRunId)).Status);
            using var claim = await host.ProbeClaimAsync(linuxProbeToken);
            RecoveryHost.AssertClaimGotNothing(claim);
        }, TimeSpan.FromSeconds(8), cancellationToken);
        Assert.DoesNotContain(
            linux.Output,
            line => line.Contains($"run {splitRunId:D} executing in", StringComparison.Ordinal));
        Assert.DoesNotContain(
            cuda.Output,
            line => line.Contains($"run {splitRunId:D} executing in", StringComparison.Ordinal));
        await host.AssertRunSettlesInAsync(splitRunId, cancellationToken, "Queued");

        // ----- TC-001, the workflow the semantics serves: a covering worker registers. -----
        var cover = await host.StartWorkerAsync("q010-cover", "linux,cuda", 1);
        var splitDone = await host.WaitForTerminalAsync(splitRunId);
        Assert.Equal("Success", splitDone.Status);
        Assert.Equal(0, splitDone.ExitCode);
        await host.AssertRunExecutedByExactlyOneWorkerAsync(splitDone, cover, [linux, cuda]);
        await host.AssertRunStepsAreOneUncorruptedSequenceAsync(splitRunId, ConcurrencyPipelines.IdentitySteps);

        // ----- TC-002: the matched case is unchanged. -----
        // The same required set, now declared in full by one live worker: accepted by the same form
        // and placed exactly as Q-005 TC-002 proved - claimed by the covering worker and executed
        // once, with the same attribution standard.
        var matchedRunId = await host.TriggerTaggedRunAsync(project, requiredTags: "linux,cuda");
        // No intermediate Running observation here: the identity pipeline completes inside one
        // poll gap (Queued → Running → Success), so a sampled wait either starves past it or fails
        // on a run that is already terminal - and the claim-by-the-covering-worker fact is proven
        // exactly by the terminal row's WorkerId below.
        var coverRow = await host.ReadConcurrencyWorkerRowAsync(cover.Name);
        var matchedDone = await host.WaitForTerminalAsync(matchedRunId);
        Assert.Equal("Success", matchedDone.Status);
        Assert.Equal(0, matchedDone.ExitCode);
        Assert.Equal(coverRow.Id, matchedDone.WorkerId);
        await host.AssertRunExecutedByExactlyOneWorkerAsync(matchedDone, cover, [linux, cuda]);
        await host.AssertRunStepsAreOneUncorruptedSequenceAsync(matchedRunId, ConcurrencyPipelines.IdentitySteps);
    }
}
