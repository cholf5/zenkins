using Zenkins.Tests.Milestone;
using Zenkins.Tests.Pipelines;
using Zenkins.Tests.Recovery;

namespace Zenkins.Tests.Concurrency;

/// <summary>
/// The <c>pipeline.lua</c> shapes Q-005 drives, built on the fixture pieces Q-002/Q-004 already use
/// (real child processes through the ArgvEcho asset). The identity step is the attribution witness:
/// ArgvEcho's <c>env</c> mode prints the worker-injected <c>ZENKINS_WORKER_NAME</c>, so each run's
/// persisted log names the exact worker process that executed it.
/// </summary>
internal static class ConcurrencyPipelines
{
    /// <summary>One step whose argv is the dotnet host running the ArgvEcho asset with <paramref name="modeAndArgs"/>.</summary>
    public static string EchoStep(string name, params string[] modeAndArgs) =>
        MilestonePipelines.Step(name, [PipelineTestData.DotNet, PipelineTestData.ArgvEcho, .. modeAndArgs]);

    /// <summary>
    /// A fast two-step pipeline: step 1 prints the executing worker's identity into the run's log,
    /// step 2 proves the run ran to its declared end. The declared names of
    /// <see cref="IdentitySteps"/> belong with this definition.
    /// </summary>
    public static string Identity =>
        MilestonePipelines.Pipeline(
            "concurrency-identity",
            env: null,
            EchoStep("Who", "env", "ZENKINS_WORKER_NAME"),
            EchoStep("Done", "echo", "concurrency-done"));

    /// <summary>The declared step names of <see cref="Identity"/>, in order.</summary>
    public static string[] IdentitySteps => ["Who", "Done"];

    /// <summary>
    /// The identity pipeline with a hold inside: step 2 streams for <paramref name="milliseconds"/> -
    /// a process that really keeps the run <c>Running</c> - so a scenario has a window in which the
    /// run is held while other workers poll the same queue.
    /// </summary>
    public static string IdentityWithHold(int milliseconds) =>
        MilestonePipelines.Pipeline(
            "concurrency-held",
            env: null,
            EchoStep("Who", "env", "ZENKINS_WORKER_NAME"),
            EchoStep("Hold", "stream", milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            EchoStep("Done", "echo", "concurrency-held-done"));

    /// <summary>The declared step names of every <see cref="IdentityWithHold"/> definition, in order.</summary>
    public static string[] HeldSteps => ["Who", "Hold", "Done"];

    /// <summary>
    /// A hold-only pipeline for the concurrency-ceiling scenario: each run occupies its worker for
    /// roughly <paramref name="milliseconds"/> plus process startup, so several runs are in flight at
    /// once and the ceiling can be sampled under real load.
    /// </summary>
    public static string HoldsFor(int milliseconds) =>
        MilestonePipelines.Pipeline(
            "concurrency-load",
            env: null,
            EchoStep("Hold", "stream", milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            EchoStep("Done", "echo", "concurrency-load-done"));
}
