using System.Globalization;
using Zenkins.Tests.Milestone;
using Zenkins.Tests.Pipelines;

namespace Zenkins.Tests.Recovery;

/// <summary>
/// The <c>pipeline.lua</c> shapes Q-004 drives, built on Q-002's fixture pieces (real child processes
/// through the ArgvEcho asset - nothing here fakes an execution, so "the step was running" is a
/// process that really was, and "nothing ran past the kill" is a marker file that really is absent).
/// </summary>
internal static class RecoveryPipelines
{
    /// <summary>One step whose argv is the dotnet host running the ArgvEcho asset with <paramref name="modeAndArgs"/>.</summary>
    public static string EchoStep(string name, params string[] modeAndArgs) =>
        MilestonePipelines.Step(name, [PipelineTestData.DotNet, PipelineTestData.ArgvEcho, .. modeAndArgs]);

    /// <summary>
    /// A fast, two-step pipeline that always succeeds: the queue-restart and lease-control runs finish
    /// in seconds, so "exactly once" and "the restarted worker claimed it" are asserted quickly.
    /// </summary>
    public static string Fast =>
        MilestonePipelines.Pipeline(
            "recovery-fast",
            env: null,
            EchoStep("Step1", "echo", "recovery-fast-1"),
            EchoStep("Step2", "echo", "recovery-fast-2"));

    /// <summary>
    /// A pipeline whose first step streams for <paramref name="milliseconds"/> (a process that really
    /// holds the step open) while steps 2 and 3 would append their own lines to
    /// <paramref name="markerFile"/> - so after a mid-step kill, "no retry, nothing resumed" is also a
    /// file-system fact, not only a row count.
    /// </summary>
    public static string HoldsInFirstStep(string markerFile, int milliseconds) =>
        MilestonePipelines.Pipeline(
            "recovery-hold",
            env: null,
            EchoStep("Step1", "stream", milliseconds.ToString(CultureInfo.InvariantCulture)),
            EchoStep("Step2", "append", markerFile, "Step2"),
            EchoStep("Step3", "append", markerFile, "Step3"));
}
