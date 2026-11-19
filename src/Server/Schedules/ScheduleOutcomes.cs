using Zenkins.Server.Localization;

using Zenkins.Server.Projects;

namespace Zenkins.Server.Schedules;

/// <summary>
/// The closed vocabulary of what happened at one due time (card F-024), and the sentences the settings page
/// says it with. One spelling per cause, shared by the occurrence row, the log line and the page.
/// </summary>
public static class ScheduleOutcomes
{
    /// <summary>A run was queued for this due time.</summary>
    public const string Queued = "queued";

    /// <summary>
    /// A run of the project was already queued or running, and the schedule does not queue anyway: the due
    /// time is skipped, never stacked. The row carries the in-flight run's number.
    /// </summary>
    public const string SkippedOverlap = "skipped-overlap";

    /// <summary>
    /// Nothing was queued because the project is disabled (F-028) or the schedule itself is disabled. The
    /// spelling is <see cref="ProjectTriggerRefusals.SkippedDisabled"/> - one cause, one word, across the
    /// trigger sources that keep a log instead of a run.
    /// </summary>
    public const string SkippedDisabled = ProjectTriggerRefusals.SkippedDisabled;

    /// <summary>
    /// "Queue anyway" was on, but the project's queue is at its cap (F-029): the same gate refused it before any
    /// run row existed.
    /// </summary>
    public const string SkippedQueueFull = "skipped-queue-full";

    /// <summary>
    /// The trigger was refused for a reason that is neither the project gate nor the cap - today the one case is
    /// a Freestyle project whose steps were never saved, which nothing can execute. <c>Detail</c> carries the
    /// stable refusal code.
    /// </summary>
    public const string SkippedRefused = "skipped-refused";

    /// <summary>
    /// The due time passed while the schedule was not being evaluated: the Server was down, or the schedule was
    /// created after it. Nothing is queued for it - no backfill - and this row is the honest record, so the page
    /// can say "3 missed while the Server was down".
    /// </summary>
    public const string Missed = "missed";

    /// <summary>
    /// The local wall clock this due time named does not exist (the spring-forward gap). Nothing was queued; the
    /// row sits at the transition instant. See <see cref="ScheduleEvaluator"/>.
    /// </summary>
    public const string SkippedNonexistent = "skipped-nonexistent";

    /// <summary>
    /// The <c>Detail</c> of the oldest kept <c>missed</c> row when the retention window could not list every
    /// earlier miss: the count is exact, the list is not.
    /// </summary>
    public const string TruncatedDetail = "window-truncated";

    /// <summary>The longest value the outcome column holds.</summary>
    public const int MaxLength = 32;

    /// <summary>The longest <c>Detail</c> value (a refusal code, or <see cref="TruncatedDetail"/>).</summary>
    public const int MaxDetailLength = 200;

    /// <summary>
    /// The sentence the settings page shows for one outcome. <paramref name="runNumber"/> is the run's number
    /// where the row has one (the queued run, or the run that was in flight for an overlap skip).
    /// </summary>
    public static string Describe(
        string outcome,
        int? runNumber,
        int collapsedDueTimes,
        string? detail,
        Localizer L10n)
    {
        var sentence = outcome switch
        {
            Queued => runNumber is { } number
                ? L10n["Queued run #{0}.", number]
                : L10n["Queued a run."],
            SkippedOverlap => runNumber is { } number
                ? L10n["Skipped: run #{0} was still queued or running.", number]
                : L10n["Skipped: a run of this project was still queued or running."],
            SkippedDisabled => L10n["Skipped: the project or the schedule is disabled."],
            SkippedQueueFull => L10n["Skipped: the project's queue is full."],
            SkippedRefused => detail switch
            {
                "steps-missing" => L10n["Skipped: this Freestyle project has no steps to run."],
                null or "" => L10n["Skipped: the trigger was refused."],
                _ => L10n["Skipped: the trigger was refused ({0}).", detail],
            },
            Missed => L10n["Missed: the due time passed while nothing was evaluating this schedule."],
            SkippedNonexistent => L10n["Skipped: that clock time does not exist in the schedule's time zone (DST)."],
            _ => outcome,
        };

        if (collapsedDueTimes > 0)
        {
            sentence += " " + L10n["(+{0} earlier due time(s) before this one)", collapsedDueTimes];
        }

        if (string.Equals(detail, TruncatedDetail, StringComparison.Ordinal))
        {
            sentence += " " + L10n["Earlier misses are not listed (the list keeps the newest entries)."];
        }

        return sentence;
    }
}

/// <summary>
/// The stable codes a refused schedule save answers with (card F-024's field errors). Short values, so the
/// settings page's <c>?error=</c> redirect stays readable and the message lookup stays one switch.
/// </summary>
public static class ScheduleErrorCodes
{
    /// <summary>No expression was entered.</summary>
    public const string CronRequired = "cron-required";

    /// <summary>The expression is not a 5-field cron expression this product reads.</summary>
    public const string CronInvalid = "cron-invalid";

    /// <summary>The expression parses but never fires (for example 30 February).</summary>
    public const string CronNeverFires = "cron-never-fires";

    /// <summary>No time zone was entered.</summary>
    public const string TimeZoneRequired = "timezone-required";

    /// <summary>The time zone is not an id <see cref="TimeZoneInfo"/> resolves.</summary>
    public const string TimeZoneUnknown = "timezone-unknown";

    /// <summary>The project already has the most schedules one project may carry.</summary>
    public const string ScheduleLimit = "schedule-limit";

    /// <summary>An identical schedule (same expression, same zone) already exists on this project.</summary>
    public const string ScheduleDuplicate = "schedule-duplicate";

    /// <summary>An unknown schedule id for this project (a foreign id is the same answer).</summary>
    public const string ScheduleNotFound = "schedule-not-found";

    /// <summary>One request carried more than one schedule mutation field.</summary>
    public const string FieldInvalid = "field-invalid";

    /// <summary>The longest time zone id accepted (an IANA id, never a display name).</summary>
    public const int MaxTimeZoneLength = 100;

    /// <summary>The most schedules one project may carry - enough for a real deployment, bounded for a page.</summary>
    public const int MaxSchedulesPerProject = 20;

    /// <summary>The form field the code belongs to, or <see langword="null"/> for an unknown code.</summary>
    public static string? FieldFor(string? code) => code switch
    {
        CronRequired or CronInvalid or CronNeverFires => ScheduleFields.Cron,
        TimeZoneRequired or TimeZoneUnknown => ScheduleFields.TimeZone,
        _ => null,
    };

    /// <summary>The message rendered next to the field (and in the form-level alert).</summary>
    /// <summary>The message rendered next to the field (and in the form-level alert), in the UI's language.</summary>
    public static string? MessageFor(string? code, Localizer L10n) => code switch
    {
        CronRequired => L10n["Enter a cron expression."],
        CronInvalid =>
            L10n["That is not a 5-field cron expression. Use minute hour day-of-month month day-of-week, for example 0 3 * * * (every day at 03:00) or */15 9-17 * * MON-FRI."],
        CronNeverFires =>
            L10n["That expression never fires - no date in the next eight years matches it (30 February, for example)."],
        TimeZoneRequired => L10n["Enter a time zone."],
        TimeZoneUnknown =>
            L10n["That time zone is not one the server knows. Use an IANA id such as Europe/Berlin, America/New_York or UTC."],
        ScheduleLimit => L10n["A project can carry at most {0} schedules.", MaxSchedulesPerProject],
        ScheduleDuplicate => L10n["This project already has a schedule with that expression and time zone."],
        ScheduleNotFound => L10n["That schedule no longer exists on this project."],
        FieldInvalid => L10n["The request did not name one of the schedule's settings."],
        _ => null,
    };
}

/// <summary>The form field names the schedule editor posts and the error lookup keys on.</summary>
public static class ScheduleFields
{
    public const string Cron = "cron";
    public const string TimeZone = "timeZone";
}
