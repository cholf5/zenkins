using System.Globalization;

namespace Zenkins.Server.Schedules;

/// <summary>
/// The schedule sweeper's knobs (card F-024), read from configuration once at startup - the same shape as
/// <see cref="Runs.RunOptions"/>, <see cref="Webhooks.WebhookOptions"/> and <see cref="Artifacts.ArtifactOptions"/>.
/// Every default is the documented policy, so a deployment that configures nothing still behaves as the PRD
/// describes.
/// </summary>
/// <param name="TickSeconds">
/// How often the sweeper evaluates the schedules. A due time is queued on the first tick at or after it, so this
/// is also the worst-case lateness of a normal run; it must stay below <paramref name="MaxDueLateness"/> or every
/// due time would read as late.
/// </param>
/// <param name="MaxDueLateness">
/// How late a due time may still be queued. A tick that arrives later than this for a due time records it as
/// missed instead - the boundary between "the sweeper was a moment late" and "nothing was evaluating this schedule"
/// (a restart, a hang, a system sleep). It is the no-backfill rule in one number.
/// </param>
/// <param name="MissWindow">
/// How far back a single evaluation looks for missed due times. Everything older than this is folded into one
/// "earlier misses are not listed" note instead of being enumerated, so a Server that was down for a month costs
/// one bounded pass, not a million rows.
/// </param>
/// <param name="MaxRecordedMisses">
/// The most individual <c>missed</c> rows one evaluation writes. The count is never lost: the oldest row that is
/// written carries the folded number, so the page's total stays exact while the table stays small.
/// </param>
/// <param name="RetainedOutcomes">
/// How many occurrence rows one schedule keeps. Older rows are deleted by the same tick that writes new ones, so a
/// per-minute schedule cannot grow the table without limit.
/// </param>
public sealed record ScheduleOptions(
    int TickSeconds,
    TimeSpan MaxDueLateness,
    TimeSpan MissWindow,
    int MaxRecordedMisses,
    int RetainedOutcomes)
{
    public const string TickSecondsConfigurationKey = "Schedules:TickSeconds";
    public const string MaxDueLatenessSecondsConfigurationKey = "Schedules:MaxDueLatenessSeconds";
    public const string MissWindowHoursConfigurationKey = "Schedules:MissWindowHours";
    public const string MaxRecordedMissesConfigurationKey = "Schedules:MaxRecordedMisses";
    public const string RetainedOutcomesConfigurationKey = "Schedules:RetainedOutcomes";

    /// <summary>How many due times the settings page previews before a save (the card's "next three").</summary>
    public const int PreviewCount = 3;

    /// <summary>Every half minute: a due time is queued at most 30 seconds after it was due.</summary>
    public const int DefaultTickSeconds = 30;

    /// <summary>Five minutes of lateness still queue; more is a miss. Well above one tick, far below a restart.</summary>
    public static readonly TimeSpan DefaultMaxDueLateness = TimeSpan.FromMinutes(5);

    /// <summary>One day of missed due times is listed (a per-minute schedule fills it); older is summarised.</summary>
    public static readonly TimeSpan DefaultMissWindow = TimeSpan.FromHours(24);

    /// <summary>Twenty rows is a readable backlog; the count beyond it is folded, not dropped.</summary>
    public const int DefaultMaxRecordedMisses = 20;

    /// <summary>Twenty outcomes is the "last N" the card asks for; the page shows ten of them.</summary>
    public const int DefaultRetainedOutcomes = 20;

    public static ScheduleOptions Defaults { get; } = new(
        TickSeconds: DefaultTickSeconds,
        MaxDueLateness: DefaultMaxDueLateness,
        MissWindow: DefaultMissWindow,
        MaxRecordedMisses: DefaultMaxRecordedMisses,
        RetainedOutcomes: DefaultRetainedOutcomes);

    public static ScheduleOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var tickSeconds = ReadCount(configuration, TickSecondsConfigurationKey, Defaults.TickSeconds, minimum: 1);

        // The lateness can never be below one tick, or a punctual sweeper would report its own runs as missed.
        var latenessSeconds = ReadCount(
            configuration,
            MaxDueLatenessSecondsConfigurationKey,
            (int)Defaults.MaxDueLateness.TotalSeconds,
            minimum: tickSeconds);

        return new ScheduleOptions(
            tickSeconds,
            TimeSpan.FromSeconds(latenessSeconds),
            TimeSpan.FromHours(ReadCount(
                configuration,
                MissWindowHoursConfigurationKey,
                (int)Defaults.MissWindow.TotalHours,
                minimum: 1)),
            ReadCount(configuration, MaxRecordedMissesConfigurationKey, Defaults.MaxRecordedMisses, minimum: 1),
            ReadCount(configuration, RetainedOutcomesConfigurationKey, Defaults.RetainedOutcomes, minimum: 1));
    }

    private static int ReadCount(IConfiguration configuration, string key, int fallback, int minimum)
    {
        var text = configuration[key];
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= minimum
                ? parsed
                : fallback;
    }
}
