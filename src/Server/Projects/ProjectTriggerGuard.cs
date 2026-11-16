using Zenkins.Shared.RunCancellation;

namespace Zenkins.Server.Projects;

/// <summary>
/// The stable vocabulary of a refused trigger (cards F-028 and F-029). One set of codes, used by the
/// form's <c>?runError=</c>, the run service's operation status, and a webhook delivery's recorded
/// outcome - so the page, the API and the delivery history never describe the same refusal with
/// different words. Values are short on purpose: a delivery outcome column holds at most 32 characters.
/// </summary>
public static class ProjectTriggerRefusals
{
    /// <summary>F-028: the project is disabled, so nothing is queued for it. The same word is the
    /// reason a run canceled by the disable carries (<c>RunCancelReasons.ProjectDisabled</c>) - one
    /// cause, one spelling.</summary>
    public const string ProjectDisabled = RunCancelReasons.ProjectDisabled;

    /// <summary>F-029: the project already holds <c>MaxQueuedRuns</c> queued runs; nothing was inserted.</summary>
    public const string QueueFull = "queue-full";

    /// <summary>
    /// F-028, for the trigger sources that keep their own log line rather than a delivery row
    /// (schedules, polls and the reactor of F-024/F-025/F-026 record this instead of a run): a tick
    /// that would have queued a run skipped a disabled project. Declared here - with the two codes
    /// above and the same predicate - so those sources read the gate instead of re-inventing "is this
    /// project disabled?".
    /// </summary>
    public const string SkippedDisabled = "skipped-disabled";
}

/// <summary>
/// The one trigger gate (cards F-028 and F-029). Every trigger source - the manual form, a webhook
/// delivery, and the schedules/polls/reactor that will follow - asks this type, and
/// <c>RunService</c>'s staging routine applies it under the project row lock; no endpoint decides
/// these questions for itself.
/// <para>
/// <b>Two rules, one gate.</b> A disabled project refuses every trigger (F-028), and a project at its
/// queue cap refuses the trigger before any row exists (F-029). Both facts are read from the project row
/// while the caller's transaction holds the project's write lock
/// (<c>ZenkinsDbContext.LockProjectForTriggerAsync</c>), which is what makes them decisions rather than
/// check-then-act.
/// </para>
/// <para>
/// <b>Precedence, and why the two are answered at different points.</b> Disabled beats everything: the
/// service answers it as soon as it has the project row, <em>before</em> the trigger's own field
/// validation, because a form the project will refuse must not spend the caller's attention on a branch
/// name. The queue cap is answered last - under the project lock, immediately before the insert - because
/// that is the only place it can be atomic; asking it earlier would mean counting without the lock, i.e.
/// the check-then-act F-029 exists to avoid. So a malformed trigger on a full queue reads its field error,
/// and any trigger on a disabled project reads the reason.
/// </para>
/// <para>
/// The methods take plain values rather than a <c>Project</c> entity so both the service (a row) and a
/// page (a <c>ProjectView</c>) can ask the gate the same questions.
/// </para>
/// </summary>
public static class ProjectTriggerGuard
{
    /// <summary>
    /// The refusal code for a disabled project, or <see langword="null"/> when it accepts triggers. This
    /// is the single place the question is answered; callers map the code into their own vocabulary (a
    /// form error, a delivery outcome, a log line).
    /// </summary>
    public static string? DisabledRefusal(bool disabled) =>
        disabled ? ProjectTriggerRefusals.ProjectDisabled : null;

    /// <summary>
    /// Whether the project's queue is already at its cap - a trigger must then be refused before it
    /// inserts anything. A project without a cap (<see langword="null"/>) is never full.
    /// </summary>
    /// <param name="maxQueuedRuns">The project's <c>MaxQueuedRuns</c>.</param>
    /// <param name="queuedRuns">The project's queued runs, counted under the project row lock.</param>
    public static bool IsQueueFull(int? maxQueuedRuns, int queuedRuns) =>
        maxQueuedRuns is { } cap && queuedRuns >= cap;

    /// <summary>
    /// The message a refused trigger shows (F-028): the operator's own reason, phrased so the person
    /// who pushed or clicked Start knows the run was not lost - nothing was queued, and enabling
    /// restores the trigger. A project disabled without a reason (a row written out of band, before
    /// this card's form existed) still explains itself instead of rendering an empty quote.
    /// </summary>
    public static string DisabledMessage(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "This project is disabled, so nothing is queued for it. Enable it to start runs again.";
        }

        // The reason is the operator's own sentence, so it is quoted as one: a trailing period is
        // dropped before the terminator below, otherwise the message reads "mid-migration. Nothing…"
        // for one author and "mid-migration.. Nothing…" for another.
        var quoted = reason.Trim().TrimEnd('.');
        return $"This project is disabled: {quoted}. Nothing is queued for it until it is enabled.";
    }

    /// <summary>
    /// The message a trigger at the cap shows (F-029), with the observed numbers - "queue full: 20
    /// queued runs, cap 20" - because a refusal without the numbers leaves the operator guessing
    /// whether the cap or the queue is the problem.
    /// </summary>
    public static string QueueFullMessage(int queuedRuns, int cap) =>
        $"Queue full: {queuedRuns} queued runs, cap {cap}. Wait for the queue to drain or raise the cap on the project's settings tab.";
}
