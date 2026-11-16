using System.Collections.Frozen;
using Zenkins.Server.Data.Entities;

namespace Zenkins.Server.Runs;

/// <summary>
/// Error codes the run surface (trigger form, run pages, worker run API) renders.
/// </summary>
public static class RunErrorCodes
{
    // Trigger-form validation (field-level, PRG ?error=).

    /// <summary>F-014: the trigger form's optional branch is not a valid git ref.</summary>
    public const string BranchInvalid = "branch-invalid";
    public const string TagUnknown = "tag-unknown";
    public const string TagInvalid = "tag-invalid";
    public const string TimeoutInvalid = "timeout-invalid";

    /// <summary>F-020: a Freestyle project's steps were never saved, so the trigger has nothing to run.</summary>
    public const string StepsMissing = "steps-missing";

    /// <summary>F-020: the target project is not a Pipeline project, and this operation needs a
    /// repository (defence in depth - the webhook create path already refuses Freestyle projects).</summary>
    public const string NotPipeline = "not-pipeline";

    /// <summary>F-021: the trigger supplied a parameter name the project does not define.</summary>
    public const string ParameterUnknown = "parameter-unknown";

    /// <summary>F-021: a supplied parameter value does not fit its type (boolean, choice, length).</summary>
    public const string ParameterInvalid = "parameter-invalid";

    /// <summary>
    /// Card B-008: the caller's <c>Idempotency-Key</c> already names a different request - another
    /// project, or another payload - so it is refused instead of replaying that request's run.
    /// </summary>
    public const string IdempotencyKeyReused = "idempotency-key-reused";

    // Card F-037: a re-run the original's own state refused (nothing was written).

    /// <summary>The run has not finished, so it is not a re-run input yet.</summary>
    public const string RebuildNotTerminal = "rebuild-not-terminal";

    /// <summary>
    /// A Pipeline run recorded no commit, so a rebuild could not pin one - and a rebuild that took the
    /// branch head would build different code under the same name.
    /// </summary>
    public const string RebuildNoCommit = "rebuild-no-commit";

    /// <summary>The project's kind no longer matches the shape the run executed (a row written out of band).</summary>
    public const string RebuildKindChanged = "rebuild-kind-changed";

    /// <summary>A Freestyle run's frozen definition is gone or unreadable, so there is nothing to re-execute.</summary>
    public const string RebuildSpecUnavailable = "rebuild-spec-unavailable";

    /// <summary>The Git credential the project has assigned no longer resolves, so a clone could not authenticate.</summary>
    public const string RebuildCredentialMissing = "rebuild-credential-missing";

    /// <summary>
    /// Card F-037: Replay is offered only where a definition <em>text</em> exists (today a Freestyle run).
    /// A repository pipeline's run stores the spec its worker produced, not the source behind it, so
    /// replaying it would erase the definition phase - refused, and the page's absent button says so.
    /// </summary>
    public const string ReplayUnavailable = "replay-unavailable";

    // Worker run-API conflicts (409).
    public const string RunNotAssignedToWorker = "run-not-assigned-to-worker";
    public const string LeaseExpired = "lease-expired";
    public const string InvalidTransition = "invalid-transition";

    /// <summary>
    /// F-017 (review M1): a report named a commit that is not the one this run is pinned to. A webhook run
    /// records the pushed commit at creation and the worker is told to check out exactly it, so a
    /// disagreement means the worker built something else - refused rather than absorbed, which is what
    /// keeps "what the run says" and "what the worker built" the same fact.
    /// </summary>
    public const string GitCommitMismatch = "git-commit-mismatch";
    public const string CancelNotRequested = "cancel-not-requested";

    // Reporting payload validation (400).
    public const string FieldInvalid = "field-invalid";

    /// <summary>F-014: the definition report's spec was refused (bad version, empty/too-long step
    /// list, over-long JSON).</summary>
    public const string PipelineSpecInvalid = "pipeline-spec-invalid";

    /// <summary>F-014: the run already has a frozen spec and the report carried a different one. The
    /// spec is immutable, so a second, different definition is refused rather than silently ignored.</summary>
    public const string PipelineSpecFrozen = "pipeline-spec-frozen";

    /// <summary>F-015: the reported step does not exist in the run's frozen spec (index out of range, or
    /// a name that disagrees with the spec at that position).</summary>
    public const string StepUnknown = "step-unknown";

    /// <summary>F-015: the step reports arrived out of order (a position above one that already has a row).</summary>
    public const string StepOutOfOrder = "step-out-of-order";

    /// <summary>F-015: the step is already terminal, so a further transition is refused (a re-delivery of
    /// the same report is idempotent instead).</summary>
    public const string StepAlreadyFinished = "step-already-finished";
}

/// <summary>
/// The one authority for build-run state transitions (PRD §3.5, card F-006).
/// <para>
/// <c>Queued → Running → Success | Failed | Canceled | Lost</c>, with <c>Queued → Canceled</c>
/// reserved for F-009 (queued cancellation) and <c>Running → Lost</c> for F-010 (offline
/// detection). Both edges are already legal here so those cards only add callers, never new
/// rules; terminal states have no outgoing edges, so nothing can requeue, retry or resurrect a
/// finished run. Every transition in the product goes through <see cref="RunService"/>, which
/// answers an illegal transition with a conflict (409).
/// </para>
/// </summary>
public static class BuildRunStateMachine
{
    private static readonly FrozenDictionary<string, FrozenSet<string>> AllowedTargets =
        new Dictionary<string, HashSet<string>>
        {
            [BuildRunStatus.Queued] = new HashSet<string>
            {
                BuildRunStatus.Running,

                // F-009 (queued cancel) and F-010 (lost) own these callers; the state machine
                // already permits the edges so a future transition request is never rejected here.
                BuildRunStatus.Canceled,
                BuildRunStatus.Lost,
            },
            [BuildRunStatus.Running] = new HashSet<string>
            {
                BuildRunStatus.Success,
                BuildRunStatus.Failed,
                BuildRunStatus.Canceled, // F-009 (running cancel / timeout)
                BuildRunStatus.Lost, // F-010 (worker offline)
            },

            // Terminal: no outgoing edges. No code path may requeue or retry a finished run.
            [BuildRunStatus.Success] = [],
            [BuildRunStatus.Failed] = [],
            [BuildRunStatus.Canceled] = [],
            [BuildRunStatus.Lost] = [],
        }.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToFrozenSet(StringComparer.Ordinal));

    /// <summary>Whether <paramref name="from"/> → <paramref name="to"/> is a legal transition.</summary>
    public static bool CanTransition(string from, string to) =>
        AllowedTargets.TryGetValue(from, out var targets) && targets.Contains(to);
}
