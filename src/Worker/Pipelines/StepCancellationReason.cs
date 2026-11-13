using Zenkins.Shared.Pipelines;
using Zenkins.Shared.RunCancellation;

namespace Zenkins.Worker.Pipelines;

/// <summary>
/// Why a step was killed by a cancellation (card F-015): <b>one</b> predicate, used by the worker loop (which
/// reads the reason off the server's cancel request) and by the tests that simulate one.
/// <para>
/// <b>Why it is a type and not a ternary in the loop.</b> The distinction it draws - a user cancel versus the
/// run's deadline - is the whole point of the separate <c>timed-out</c> value on the run page. A test that
/// re-implemented the same ternary would keep passing after the loop's version drifted, which is exactly the
/// bug class this helper exists to prevent.
/// </para>
/// </summary>
public static class StepCancellationReason
{
    /// <summary>The step's failure reason for a cancel whose reason is <paramref name="cancelReason"/>.</summary>
    public static string For(string? cancelReason) =>
        RunCancelReasons.IsTimeout(cancelReason)
            ? PipelineStepFailureReasons.TimedOut
            : PipelineStepFailureReasons.Canceled;
}
