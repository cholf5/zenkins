using System.Net;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;
using Zenkins.Shared.Workers;

namespace Zenkins.Tests.Runs;

/// <summary>
/// Card F-033's one-predicate rule, as a matrix over the two questions a run's requirement answers.
/// <para>
/// <b>What this pins.</b> A run is created by one shared path (<c>RunService.QueueWebhookRunAsync</c>) no
/// matter which trigger asked - a manual start, a webhook delivery, a schedule, a poll, or the branch
/// observation that queues a run after a tree read - so "which requirement does the run carry" has exactly
/// one answer per tag list. The claim loop then decides with <c>WorkerRequirement.SatisfiedBy</c> on that
/// stored text. If either half drifts (the queue writes a different normal form, or the claim re-derives a
/// rule of its own), a run is claimed by the wrong Worker or sits queued for none.
/// </para>
/// </summary>
public sealed class WorkerRequirementClaimTests
{
    [Theory]
    [InlineData("linux", "linux", true)]
    [InlineData("linux", "linux,arm64", true)]
    [InlineData("linux,arm64", "linux", false)]
    [InlineData("linux,arm64", "arm64,linux,extra", true)]
    [InlineData("windows", "linux", false)]
    [InlineData("", "linux", true)]
    [InlineData("Linux", "linux", true)]
    public async Task A_run_is_claimed_exactly_when_its_worker_satisfies_the_stored_requirement(
        string requiredTags,
        string workerTags,
        bool claimable)
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "agent", tags: workerTags);

        var response = await RunTestHost.StartRunAsync(owner, projectId, requiredTags: requiredTags);
        Assert.True((int)response.StatusCode is 302 or 303);

        var run = Assert.Single(await host.ReadRunsAsync(organizationId));

        // 1. The queue stored the canonical form of exactly what the trigger said.
        var requirement = WorkerRequirement.FromTags(requiredTags);
        Assert.Equal(requirement.StorageForm, run.RequiredRequirement);
        Assert.Equal(requiredTags, run.RequiredTags);

        // 2. The claim answered the same question the stored text answers.
        using var workerClient = host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(requirement.SatisfiedBy(workerTags.Split(',')), claimable);
        Assert.Equal(claimable ? HttpStatusCode.OK : HttpStatusCode.NoContent, claimed.StatusCode);
    }

    [Fact]
    public async Task A_disjunctive_requirement_is_claimed_by_the_stored_form_not_by_a_fallback_to_its_tags()
    {
        // Conjunctions cannot tell the two apart: a comma list of the same atoms is equivalent, which is why
        // every other claim fact passes even if the normalized column is ignored. A disjunction is where they
        // differ - `linux && (arm64 || x64)` accepts a machine with linux+arm64, while the tag list an old row
        // would carry (`linux,arm64,x64`, all three) demands a third label.
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        var (workerToken, _) = await host.RegisterWorkerAsync(name: "arm", tags: "linux,arm64");

        var response = await RunTestHost.StartRunAsync(owner, projectId, requiredTags: "linux,arm64,x64");
        Assert.True((int)response.StatusCode is 302 or 303);

        var run = Assert.Single(await host.ReadRunsAsync(organizationId));
        var disjunction = WorkerRequirement.FromTags("").And(Parse("linux && (arm64 || x64)"))!;
        await host.InSystemScopeAsync(async context =>
        {
            var row = (await context.SystemQueryAsync<BuildRun>(candidate => candidate.Id == run.Id)).Single();
            row.RequiredRequirement = disjunction.StorageForm;
            await context.SaveChangesAsync(CancellationToken.None);
            return true;
        });

        // (1) The row now carries a disjunction the worker satisfies - and the claim must therefore succeed.
        Assert.True(disjunction.SatisfiedBy(["linux", "arm64"]));
        using var workerClient = host.Host.CreateClient();
        using var claimed = await Zenkins.Tests.Runs.RunTestHost.ClaimAsync(workerClient, workerToken);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

        // (2) ...and the same worker is refused by the legacy pair alone: a fallback to `RequiredTags` (or to a
        // comma list) would have turned this disjunction into "all three labels" and rejected the claim above.
        Assert.False(RunValidation.RequirementFor(null, run.RequiredTags).SatisfiedBy(["linux", "arm64"]));
        Assert.False(WorkerRequirement.FromTags(run.RequiredTags).SatisfiedBy(["linux", "arm64"]));
    }

    private static WorkerRequirement Parse(string expression)
    {
        Assert.True(WorkerRequirement.TryParse(expression, out var requirement, out var error), error);
        return requirement;
    }

    [Fact]
    public async Task A_manual_trigger_stores_the_canonical_form_of_its_tag_list()
    {
        // One path, one assertion: the run a manual start creates carries the canonical form of the tags it
        // was given, not the text as typed. (The three callers of the queue routine are driven end to end in
        // WorkerRequirementObservationTests; this fact does not speak for them.)
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync();
        var organizationId = await host.OrganizationIdAsync();
        await host.RegisterWorkerAsync(name: "agent", tags: "ci");

        await RunTestHost.StartRunAsync(owner, projectId, requiredTags: "ci,linux");
        var manual = Assert.Single(await host.ReadRunsAsync(organizationId));
        Assert.Equal(WorkerRequirement.FromTags("ci,linux").StorageForm, manual.RequiredRequirement);

        // Canonical means order-independent and deterministic: the same tags in another order store the same
        // text, which is what makes two runs comparable at all.
        Assert.Equal(WorkerRequirement.FromTags("linux,ci").StorageForm, manual.RequiredRequirement);
        Assert.Equal(BuildRunTriggerType.Manual, manual.TriggerType);
    }
}
