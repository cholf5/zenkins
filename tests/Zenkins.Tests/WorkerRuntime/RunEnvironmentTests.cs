using Xunit;
using Zenkins.Worker;
using Zenkins.Worker.Pipelines;

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
        Assert.Equal("7", environment.Metadata["ZENKINS_BUILD_NUMBER"]);
        Assert.Equal(RunId.ToString(), environment.Metadata["ZENKINS_BUILD_ID"]);
        Assert.Equal(RunId.ToString(), environment.Metadata["ZENKINS_RUN_ID"]);
        Assert.Equal(ProjectId.ToString(), environment.Metadata["ZENKINS_PROJECT_ID"]);
        Assert.Equal("linux-agent-1", environment.Metadata["ZENKINS_WORKER_NAME"]);
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

            Assert.False(environment.Metadata.ContainsKey("ZENKINS_BUILD_NUMBER"));
            Assert.Equal(RunId.ToString(), environment.Metadata["ZENKINS_BUILD_ID"]);
        }
    }

    [Fact]
    public void A_layer_that_declares_a_reserved_name_is_dropped_and_reported()
    {
        // F-036's intent, kept after F-034's reorder. The MECHANISM changed and this is the record of it:
        // before the reorder the metadata was simply written last, so a spoofing value lost by sort order;
        // now the metadata is the *lowest* layer (PRD §3.11.3), so a reserved name arriving from any
        // declared layer is dropped - and reported - instead of being allowed to win.
        //
        // The intent is unchanged: a parameter (or a server-resolved value, or a pipeline/step env entry)
        // named like the metadata cannot make a step read a run identity the server never wrote.
        var claim = Claim(number: 3) with
        {
            Parameters = new Dictionary<string, string>
            {
                ["ZENKINS_BUILD_NUMBER"] = "999",
                ["ZENKINS_BUILD_ID"] = "spoofed",
            },
        };

        var reported = new List<string>();
        var environment = StepEnvironment.Compose(
            new Dictionary<string, string?> { ["ZENKINS_RUN_ID"] = "from-inherited" },
            pipelineEnvironment: new Dictionary<string, string> { ["ZENKINS_RUN_ID"] = "from-pipeline" },
            stepEnvironment: new Dictionary<string, string> { ["ZENKINS_BUILD_ID"] = "from-step" },
            WorkerRunLoop.BuildRunEnvironment(claim, "linux-agent-1"),
            reported.Add);

        Assert.Equal("3", environment["ZENKINS_BUILD_NUMBER"]);
        Assert.Equal(RunId.ToString(), environment["ZENKINS_BUILD_ID"]);
        Assert.Equal(RunId.ToString(), environment["ZENKINS_RUN_ID"]);

        // Every dropped name is named exactly once - the server-resolved map's two, the pipeline's one and
        // the step's one. Silence would leave a script wondering why its value never arrived.
        Assert.Equal(4, reported.Count);
        Assert.Contains(reported, message => message.Contains("ZENKINS_BUILD_NUMBER", StringComparison.Ordinal));
        Assert.Contains(reported, message => message.Contains("ZENKINS_BUILD_ID", StringComparison.Ordinal));
        Assert.Contains(reported, message => message.Contains("ZENKINS_RUN_ID", StringComparison.Ordinal));
        Assert.All(reported, message => Assert.Contains("reserved", message, StringComparison.Ordinal));
    }

    [Fact]
    public void The_inherited_environment_is_the_lowest_layer_and_the_server_map_is_next()
    {
        // The bottom two edges of the documented chain, in isolation: the worker's own process environment
        // is below everything the server wrote, and the server-resolved map is above the metadata.
        var claim = Claim(number: 1) with
        {
            Parameters = new Dictionary<string, string> { ["TARGET"] = "server" },
        };

        var environment = StepEnvironment.Compose(
            new Dictionary<string, string?>
            {
                ["TARGET"] = "inherited",
                ["ZENKINS_RUN_ID"] = "from-inherited",
                ["WORKER_ONLY"] = "kept",
            },
            pipelineEnvironment: null,
            stepEnvironment: null,
            WorkerRunLoop.BuildRunEnvironment(claim, "linux-agent-1"));

        Assert.Equal("server", environment["TARGET"]);
        Assert.Equal(RunId.ToString(), environment["ZENKINS_RUN_ID"]);
        Assert.Equal("kept", environment["WORKER_ONLY"]);
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
