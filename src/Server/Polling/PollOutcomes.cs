namespace Zenkins.Server.Polling;

using Zenkins.Server.Projects;
using Zenkins.Server.Schedules;
using Zenkins.Server.Webhooks;

/// <summary>
/// The closed vocabulary of what one poll decided (card F-025) - stored on the project
/// (<c>Project.LastPollOutcome</c>) and on each branch's head row, and rendered on the settings card.
/// <para>
/// <b>Every tick records one of these.</b> A poll that queues nothing because the project is disabled, the
/// queue is full or the project is at its concurrency cap is a <b>recorded skip</b>, not silence: an
/// operator asking "why did nobody build my push?" has to be able to see the answer on the page.
/// </para>
/// <para>
/// <b>A failure is not "no change".</b> The probe's own outcome (<c>git-unreachable</c>, <c>no-worker</c>,
/// ...) is stored verbatim when the probe fails, so "I could not see the remote" can never be mistaken for
/// "the head did not move" - the same rule F-023's discovery follows.
/// </para>
/// </summary>
public static class PollOutcomes
{
    /// <summary>A run was queued for the observed commit.</summary>
    public const string Queued = "queued";

    /// <summary>The observed head is the head the last poll already knew: nothing to do.</summary>
    public const string Unchanged = "unchanged";

    /// <summary>
    /// The branch was never polled before, so its head was <b>recorded</b> and no run was queued. Polling
    /// starts from "what is there now": enabling it on a repository with fifty branches must not queue fifty
    /// runs, and there is no earlier head a change could be measured against.
    /// </summary>
    public const string FirstSighting = "first-sighting";

    /// <summary>
    /// F-023's branch verdict, shared with the delivery path: the branch has no pipeline in it, so there is
    /// nothing a run could execute (<see cref="WebhookOutcomes.NoPipeline"/>).
    /// </summary>
    public const string NoPipeline = WebhookOutcomes.NoPipeline;

    /// <summary>
    /// F-023's branch verdict, shared with the delivery path: no verdict about <em>this</em> commit yet, so
    /// nothing was queued and an observation was asked for
    /// (<see cref="WebhookOutcomes.AwaitingObservation"/>).
    /// </summary>
    public const string AwaitingObservation = WebhookOutcomes.AwaitingObservation;

    /// <summary>
    /// The probe was refused because the repository needs a credential (Freestyle, or an inline Pipeline), so there is
    /// nothing to poll. The settings card says so instead of failing every interval.</summary>
    public const string NoRepository = "no-repository";

    /// <summary>
    /// The probe was refused because the repository needs a credential the project does not have assigned
    /// (card F-018's material, absent). Distinct from a plain failure because the fix is on the settings
    /// card, not on the remote - and the card says exactly that.
    /// </summary>
    public const string MissingCredential = "missing-credential";

    /// <summary>
    /// The change was already triggered for this (branch, commit) - by a webhook, or by an earlier poll - so
    /// nothing was queued twice. It is <see cref="WebhookOutcomes.IgnoredDuplicate"/>: the shared ledger's one
    /// spelling for "this commit already has its run", used by both paths that read it.
    /// </summary>
    public const string Duplicate = WebhookOutcomes.IgnoredDuplicate;

    /// <summary>
    /// The three skips the trigger gate already names - disabled (F-028), queue full (F-029) and a run already
    /// in flight (F-024) - are <b>not</b> redeclared here: a poll stores the same words the schedule log stores,
    /// so one cause reads the same on both pages. See <see cref="ProjectTriggerRefusals.SkippedDisabled"/>,
    /// <see cref="ScheduleOutcomes.SkippedQueueFull"/> and <see cref="ScheduleOutcomes.SkippedOverlap"/>.
    /// </summary>
    public const string SkippedDisabled = ProjectTriggerRefusals.SkippedDisabled;

    /// <inheritdoc cref="SkippedDisabled"/>
    public const string SkippedQueueFull = ScheduleOutcomes.SkippedQueueFull;

    /// <inheritdoc cref="SkippedDisabled"/>
    public const string SkippedOverlap = ScheduleOutcomes.SkippedOverlap;

    /// <summary>Every fixed outcome this vocabulary declares (a probe's failure words come from
    /// <c>GitProbeOutcomes</c> and are stored verbatim, so they are not listed here).</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Queued,
        Duplicate,
        Unchanged,
        FirstSighting,
        SkippedDisabled,
        SkippedQueueFull,
        SkippedOverlap,
        NoRepository,
        MissingCredential,
        NoPipeline,
        AwaitingObservation,
    };

    /// <summary>
    /// The order in which one tick's per-branch decisions collapse into the project's single
    /// <c>LastPollOutcome</c>: something queued is the most interesting thing that happened, then a
    /// duplicate, then a skip (which says why nothing was built), and "nothing moved" is the quiet case.
    /// </summary>
    private static readonly string[] Precedence =
    [
        Queued,
        Duplicate,
        SkippedQueueFull,
        SkippedOverlap,
        SkippedDisabled,
        NoPipeline,
        AwaitingObservation,
        MissingCredential,
        NoRepository,
        NoPipeline,
        AwaitingObservation,
        FirstSighting,
        Unchanged,
    ];

    /// <summary>The most significant of <paramref name="outcomes"/> (see <see cref="Precedence"/>).</summary>
    public static string? Aggregate(IEnumerable<string?> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var outcome in outcomes)
        {
            if (outcome is not null)
            {
                seen.Add(outcome);
            }
        }

        foreach (var candidate in Precedence)
        {
            if (seen.Contains(candidate))
            {
                return candidate;
            }
        }

        // A probe failure (a GitProbeOutcomes word) is what is left: keep the first one, it is the reason.
        return seen.FirstOrDefault();
    }
}
