using Xunit;
using Zenkins.Worker;

namespace Zenkins.Tests.WorkerRuntime;

/// <summary>
/// Card F-036's worker half: the run's identity reaches every step's environment. The server side of the
/// fact - the claim payload carrying the number, the page printing it - is pinned in
/// <c>Zenkins.Tests.Runs.BuildNumberTests</c>; this file pins the composition the worker performs on top of
/// it, so neither layer can silently stop feeding the other.
/// </summary>
[Trait("Category", "WorkerRuntime")]
public sealed class RunEnvironmentTests
{
    private static readonly Guid RunId = Guid.Parse("0197c0de-0000-0000-0000-000000000042");
    private static readonly Guid ProjectId = Guid.Parse("0197c0de-0000-0000-0000-0000000000aa");

    [Fact]
    public void A_claim_with_a_number_names_the_build_by_number_and_by_id()
    {
        var environment = WorkerRunLoop.BuildRunEnvironment(Claim(number: 7), "linux-agent-1");

        // The number is the name every page prints; the id is the guid the run has always had, kept
        // beside ZENKINS_RUN_ID so either spelling works in a script.
        Assert.Equal("7", environment["ZENKINS_BUILD_NUMBER"]);
        Assert.Equal(RunId.ToString(), environment["ZENKINS_BUILD_ID"]);
        Assert.Equal(RunId.ToString(), environment["ZENKINS_RUN_ID"]);
        Assert.Equal(ProjectId.ToString(), environment["ZENKINS_PROJECT_ID"]);
        Assert.Equal("linux-agent-1", environment["ZENKINS_WORKER_NAME"]);
    }

    [Fact]
    public void A_claim_without_a_number_leaves_the_variable_unset_rather_than_writing_zero()
    {
        // An older server's claim carries no number, and a request for a run the product never numbered
        // answers 0 rather than null. Writing either as ZENKINS_BUILD_NUMBER would look like a real build
        // number to a step that tags an artifact with it, so the honest answer is "not set" - and that
        // defence has to be reachable, not dead: both spellings of "no number" are exercised here.
        foreach (var claim in new[] { Claim(number: null), Claim(number: 0) })
        {
            var environment = WorkerRunLoop.BuildRunEnvironment(claim, "linux-agent-1");

            Assert.False(environment.ContainsKey("ZENKINS_BUILD_NUMBER"));
            Assert.Equal(RunId.ToString(), environment["ZENKINS_BUILD_ID"]);
        }
    }

    [Fact]
    public void A_parameter_cannot_overwrite_the_build_metadata()
    {
        // F-021's precedence: the trigger's parameter values join the same layer, but the ZENKINS_*
        // metadata is written last - a parameter named like one of them cannot spoof it (F-036 adds two
        // more reserved names, and they obey the same rule).
        var claim = Claim(number: 3) with
        {
            Parameters = new Dictionary<string, string>
            {
                ["ZENKINS_BUILD_NUMBER"] = "999",
                ["ZENKINS_BUILD_ID"] = "spoofed",
            },
        };

        var environment = WorkerRunLoop.BuildRunEnvironment(claim, "linux-agent-1");

        Assert.Equal("3", environment["ZENKINS_BUILD_NUMBER"]);
        Assert.Equal(RunId.ToString(), environment["ZENKINS_BUILD_ID"]);
    }

    private static WorkerClaimResponse Claim(int? number) => new(
        RunId,
        ProjectId,
        RepositoryUrl: null,
        Branch: string.Empty,
        PipelinePath: string.Empty,
        LeaseExpiresAt: DateTimeOffset.UnixEpoch.AddHours(1),
        Number: number);
}
