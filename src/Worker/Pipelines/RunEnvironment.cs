using Zenkins.Shared.Pipelines;

namespace Zenkins.Worker.Pipelines;

/// <summary>
/// One run's server-provided environment, as the executor composes it (cards F-007, F-015, F-021,
/// F-034): the <b>reserved metadata</b> the server writes about the run, and <b>one flat map</b> of the
/// values the server resolved for it (the organization layer with the run's parameter values on top).
/// <para>
/// <b>One map, not two layers.</b> The organization → parameter order is resolved on the server when the
/// claim is composed; the worker injects the result and never compares the two server layers itself. A
/// second copy of that order on this side of the wire would be a second thing that can drift, and the
/// symptom - one name resolving differently in two places - would be invisible until a step read it.
/// </para>
/// <para>
/// <b>The documented order (PRD §3.11.3, lowest first)</b> is applied in exactly one place,
/// <see cref="StepEnvironment.Compose"/>: the worker's inherited process environment → <c>ZENKINS_*</c>
/// metadata → the server-resolved values → the pipeline's <c>env</c> → the step's <c>env</c>.
/// </para>
/// <para>
/// <b>The reserved names stay the server's - by refusal and filtering, not by ordering.</b> Because the
/// metadata is the <em>lowest</em> layer, nothing else may claim its namespace: an organization variable
/// inside it is refused at save time, a parameter inside it is refused at save time, and a pipeline or
/// step <c>env</c> entry inside it (Lua has no save-time validation) is dropped by
/// <see cref="StepEnvironment.Compose"/> with a line in the run log. Silently letting the declared value
/// win would let a script spoof <c>ZENKINS_RUN_ID</c>; silently dropping it without saying so would hide
/// the mistake.
/// </para>
/// </summary>
public sealed class RunEnvironment
{
    /// <summary>The per-step metadata every step sees (it is the run's environment plus the step's name).</summary>
    public const string StepNameVariable = "ZENKINS_STEP_NAME";

    /// <summary>
    /// The server's own record of this run: <c>ZENKINS_RUN_ID</c>, <c>ZENKINS_PROJECT_ID</c>,
    /// <c>ZENKINS_WORKER_NAME</c>, <c>ZENKINS_BUILD_ID</c>, <c>ZENKINS_BUILD_NUMBER</c> (when the run has
    /// a number) and <c>ZENKINS_STEP_NAME</c> once a step is known. A <see langword="null"/> value means
    /// "do not set".
    /// </summary>
    public IReadOnlyDictionary<string, string?> Metadata { get; init; } =
        new Dictionary<string, string?>();

    /// <summary>
    /// The values the server resolved for this run: the organization environment layer frozen at trigger
    /// time, with the parameter values on top. Empty when the run has neither - never read from the
    /// organization's <em>current</em> rows, so editing a variable cannot move a queued run.
    /// </summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Nothing at all - the shape tests and the Worker's "no claim yet" default use it.</summary>
    public static RunEnvironment Empty { get; } = new();

    /// <summary>The same layers with <c>ZENKINS_STEP_NAME</c> stamped into the metadata (per step).</summary>
    public RunEnvironment WithStepName(string stepName)
    {
        var metadata = new Dictionary<string, string?>(Metadata, StringComparer.Ordinal)
        {
            [StepNameVariable] = stepName,
        };

        return new RunEnvironment { Metadata = metadata, Values = Values };
    }

    /// <summary>
    /// Whether <paramref name="name"/> is inside the server's reserved namespace. The rule itself is
    /// <see cref="ServerEnvironmentNames.IsReserved"/> - the server's, shared rather than twinned: two
    /// copies of this predicate disagreed about casing once, and on Windows that was a reachable spoof
    /// (<c>zenkins_run_id</c> overwriting <c>ZENKINS_RUN_ID</c> in the child's case-insensitive block).
    /// </summary>
    public static bool IsReserved(string name) => ServerEnvironmentNames.IsReserved(name);

    /// <summary>
    /// The diagnostic for a declared value that was dropped because its name belongs to the server's
    /// metadata. One sentence, naming the variable and saying what happened - never a silent drop.
    /// <para>
    /// It carries no <c>zenkins: </c> prefix: the caller knows where in the run it belongs and writes the
    /// line's own location prefix (<c>zenkins: step 1/2 "Build": …</c>), so a prefix here would render
    /// twice in the run log.
    /// </para>
    /// </summary>
    public static string CollisionDiagnostic(string name, string layer) =>
        $"the {layer} sets '{name}', which is reserved for the run's own metadata; "
        + "the declared value was ignored (ZENKINS_* is written by the server, not by a pipeline).";
}
