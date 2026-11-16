using System.Globalization;

namespace Zenkins.Server.Schedules;

/// <summary>
/// A parsed 5-field cron expression (<c>minute hour day-of-month month day-of-week</c>), evaluated with
/// Vixie/POSIX semantics (card F-024).
/// <para>
/// <b>Grammar.</b> Each field accepts <c>*</c>, a number, a range (<c>1-5</c>), a step over either
/// (<c>*/15</c>, <c>1-30/2</c>, <c>5/10</c> = from 5 to the field's maximum) and comma-separated lists of
/// those. Months accept <c>JAN</c>-<c>DEC</c> and weekdays <c>SUN</c>-<c>SAT</c> (case-insensitive) wherever a
/// number is accepted, in single values and in range bounds. Day-of-week is <c>0-7</c> with both 0 and 7 meaning
/// Sunday. Nothing else is accepted: a six-field expression (seconds), <c>@daily</c> macros, <c>?</c>, <c>L</c>
/// and <c>W</c> are refused at save time with a field error rather than half-supported.
/// </para>
/// <para>
/// <b>The day rule is Vixie's, not an OR of convenience.</b> When neither day field is starred, a day matches if
/// the day-of-month <em>or</em> the day-of-week matches (<c>0 0 1 * MON</c> fires on the first and on every
/// Monday). When either field is starred - a field whose text starts with <c>*</c>, so <c>*/2</c> counts as
/// starred, exactly as Vixie's <c>DOM_STAR</c>/<c>DOW_STAR</c> flags do - both must match. Deployments migrating
/// from Jenkins or cron therefore get the schedule they wrote, not an approximation of it.
/// </para>
/// <para>
/// <b>Deliberately absent: <c>H</c>.</b> Jenkins hashes a schedule across the fleet to spread load; this product's
/// queue is cheap and a deterministic, explainable time is worth more (see the card's Notes). Revisit if a
/// deployment ever reports a thundering herd.
/// </para>
/// </summary>
public sealed class ScheduleCron
{
    /// <summary>The longest expression accepted, so a typo cannot become a stored wall of text.</summary>
    public const int MaxLength = 120;

    private static readonly string[] MonthNames =
        ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private static readonly string[] DayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _daysOfMonth = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _daysOfWeek = new bool[7];
    private readonly bool _dayOfMonthStarred;
    private readonly bool _dayOfWeekStarred;

    private ScheduleCron(
        string text,
        int[] minutes,
        int[] hours,
        int[] daysOfMonth,
        int[] months,
        int[] daysOfWeek,
        bool dayOfMonthStarred,
        bool dayOfWeekStarred)
    {
        Text = text;
        Minutes = minutes;
        Hours = hours;
        DaysOfMonth = daysOfMonth;
        Months = months;
        DaysOfWeek = daysOfWeek;
        _dayOfMonthStarred = dayOfMonthStarred;
        _dayOfWeekStarred = dayOfWeekStarred;

        foreach (var value in minutes)
        {
            _minutes[value] = true;
        }

        foreach (var value in hours)
        {
            _hours[value] = true;
        }

        foreach (var value in daysOfMonth)
        {
            _daysOfMonth[value] = true;
        }

        foreach (var value in months)
        {
            _months[value] = true;
        }

        foreach (var value in daysOfWeek)
        {
            _daysOfWeek[value] = true;
        }
    }

    /// <summary>The expression as the operator wrote it, trimmed.</summary>
    public string Text { get; }

    /// <summary>The matching minutes of the hour, ascending.</summary>
    public IReadOnlyList<int> Minutes { get; }

    /// <summary>The matching hours of the day, ascending.</summary>
    public IReadOnlyList<int> Hours { get; }

    public IReadOnlyList<int> DaysOfMonth { get; }

    public IReadOnlyList<int> Months { get; }

    /// <summary>The matching weekdays with Sunday as both 0 and 7 (both values are kept as 0).</summary>
    public IReadOnlyList<int> DaysOfWeek { get; }

    /// <summary>
    /// Parses one expression. <paramref name="error"/> is a <see cref="ScheduleErrorCodes"/> value on failure,
    /// naming what is wrong with the text so the form can show a field-level message.
    /// </summary>
    public static bool TryParse(string? text, out ScheduleCron? cron, out string? error)
    {
        cron = null;
        error = null;

        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            error = ScheduleErrorCodes.CronRequired;
            return false;
        }

        if (trimmed.Length > MaxLength)
        {
            error = ScheduleErrorCodes.CronInvalid;
            return false;
        }

        var fields = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            // Five fields is the whole grammar (minutes, no seconds); saying so beats a per-field guess.
            error = ScheduleErrorCodes.CronInvalid;
            return false;
        }

        if (!TryParseField(fields[0], 0, 59, nameLookup: null, out var minutes)
            || !TryParseField(fields[1], 0, 23, nameLookup: null, out var hours)
            || !TryParseField(fields[2], 1, 31, nameLookup: null, out var daysOfMonth)
            || !TryParseField(fields[3], 1, 12, MonthValue, out var months)
            || !TryParseField(fields[4], 0, 7, DayValue, out var daysOfWeek))
        {
            error = ScheduleErrorCodes.CronInvalid;
            return false;
        }

        if (minutes.Count == 0 || hours.Count == 0 || daysOfMonth.Count == 0 || months.Count == 0)
        {
            // A field that matches nothing is a schedule that never fires; refuse it at save time.
            error = ScheduleErrorCodes.CronInvalid;
            return false;
        }

        // 0 and 7 are both Sunday: fold 7 into 0 after parsing, so the matcher has one value per weekday.
        var weekdays = daysOfWeek.Select(value => value == 7 ? 0 : value).Distinct().Order().ToArray();
        if (weekdays.Length == 0)
        {
            error = ScheduleErrorCodes.CronInvalid;
            return false;
        }

        cron = new ScheduleCron(
            trimmed,
            [.. minutes.Order()],
            [.. hours.Order()],
            [.. daysOfMonth.Order()],
            [.. months.Order()],
            weekdays,
            dayOfMonthStarred: fields[2].StartsWith('*'),
            dayOfWeekStarred: fields[4].StartsWith('*'));
        return true;
    }

    /// <summary>Whether this expression fires on <paramref name="date"/> (month, then the Vixie day rule).</summary>
    public bool Matches(DateOnly date)
    {
        if (!_months[date.Month])
        {
            return false;
        }

        var dayOfMonthMatches = _daysOfMonth[date.Day];
        var dayOfWeekMatches = _daysOfWeek[(int)date.DayOfWeek];

        // Vixie's rule, spelled out: with either day field starred both must match, otherwise either may.
        return _dayOfMonthStarred || _dayOfWeekStarred
            ? dayOfMonthMatches && dayOfWeekMatches
            : dayOfMonthMatches || dayOfWeekMatches;
    }

    /// <summary>
    /// Whether the expression fires at <paramref name="localTime"/>'s minute - used by the tests and by nothing
    /// in the tick (the evaluator asks <see cref="Matches(DateOnly)"/> once per day and then iterates the
    /// pre-sorted hour/minute sets).
    /// </summary>
    public bool Matches(DateTime localTime) =>
        Matches(DateOnly.FromDateTime(localTime))
        && _hours[localTime.Hour]
        && _minutes[localTime.Minute];

    private static int? MonthValue(string token)
    {
        var upper = token.ToUpperInvariant();
        for (var index = 0; index < MonthNames.Length; index++)
        {
            if (string.Equals(MonthNames[index], upper, StringComparison.Ordinal))
            {
                return index + 1;
            }
        }

        return null;
    }

    private static int? DayValue(string token)
    {
        var upper = token.ToUpperInvariant();
        for (var index = 0; index < DayNames.Length; index++)
        {
            if (string.Equals(DayNames[index], upper, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>
    /// One field: a comma-separated list of <c>value</c>, <c>from-to</c>, <c>*</c>, or any of those with
    /// <c>/step</c>. Bounds are checked per field, so <c>60</c> in the minute field is refused instead of
    /// quietly wrapping.
    /// </summary>
    private static bool TryParseField(
        string field,
        int min,
        int max,
        Func<string, int?>? nameLookup,
        out SortedSet<int> values)
    {
        values = [];

        foreach (var item in field.Split(','))
        {
            if (item.Length == 0)
            {
                return false;
            }

            var stepText = string.Empty;
            var rangeText = item;
            var slash = item.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                rangeText = item[..slash];
                stepText = item[(slash + 1)..];
                if (rangeText.Length == 0 || stepText.Length == 0)
                {
                    return false;
                }
            }

            var step = 1;
            if (stepText.Length > 0
                && (!int.TryParse(stepText, NumberStyles.None, CultureInfo.InvariantCulture, out step)
                    || step < 1
                    || step > max - min + 1))
            {
                return false;
            }

            int from;
            int to;
            if (rangeText == "*")
            {
                from = min;
                to = max;
            }
            else
            {
                var dash = rangeText.IndexOf('-', StringComparison.Ordinal);
                if (dash > 0)
                {
                    if (!TryParseValue(rangeText[..dash], min, max, nameLookup, out from)
                        || !TryParseValue(rangeText[(dash + 1)..], min, max, nameLookup, out to))
                    {
                        return false;
                    }

                    if (from > to)
                    {
                        // A wrapping range (FRI-MON) is deliberately not supported: it reads differently in
                        // every cron implementation, so the form refuses rather than guesses.
                        return false;
                    }
                }
                else
                {
                    if (!TryParseValue(rangeText, min, max, nameLookup, out from))
                    {
                        return false;
                    }

                    // `5/10` is Vixie's shorthand for "from 5 to the field's maximum, every 10".
                    to = stepText.Length > 0 ? max : from;
                }
            }

            for (var value = from; value <= to; value += step)
            {
                values.Add(value);
            }
        }

        return values.Count > 0;
    }

    private static bool TryParseValue(string token, int min, int max, Func<string, int?>? nameLookup, out int value)
    {
        value = 0;
        if (token.Length == 0)
        {
            return false;
        }

        if (nameLookup is not null && !char.IsAsciiDigit(token[0]))
        {
            var named = nameLookup(token);
            if (named is not { } resolved)
            {
                return false;
            }

            value = resolved;
            return value >= min && value <= max;
        }

        return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= min
            && value <= max;
    }
}
