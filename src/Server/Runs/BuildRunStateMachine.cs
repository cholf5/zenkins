using System.Collections.Frozen;
using Zenkins.Server.Data.Entities;

namespace Zenkins.Server.Runs;

/// <summary>
/// Error codes the run surface (trigger form, run pages, worker run API) renders.
/// </summary>
public static class RunErrorCodes
{
    // Trigger-form validation (field-level, PRG ?error=).
    public const string ArgvEmpty = "argv-empty";
    public const string ArgvTooManyArguments = "argv-too-many-arguments";
    public const string ArgvArgumentTooLong = "argv-argument-too-long";
    public const string TagUnknown = "tag-unknown";
    public const string TagInvalid = "tag-invalid";

    // Worker run-API conflicts (409).
    public const string RunNotAssignedToWorker = "run-not-assigned-to-worker";
    public const string LeaseExpired = "lease-expired";
    public const string InvalidTransition = "invalid-transition";

    // Reporting payload validation (400).
    public const string FieldInvalid = "field-invalid";
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
