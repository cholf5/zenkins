using Xunit;
using Zenkins.Server.Schedules;

namespace Zenkins.Tests.Schedules;

/// <summary>
/// Card F-024's cron arithmetic, proved without a Server: the expression grammar, the day rule, the time zone's
/// own DST rules in both directions, and the bounded "this never fires" answer.
/// <para>
/// The dates are real zone transitions: Europe/Berlin springs forward on 2026-03-29 (02:00 → 03:00, so 02:30 does
/// not exist that day) and falls back on 2026-10-25 (03:00 → 02:00, so 02:30 happens twice).
/// </para>
/// </summary>
[Trait("Category", "Schedules")]
public sealed class ScheduleCronTests
{
    private static ScheduleCron Parse(string text)
    {
        Assert.True(ScheduleCron.TryParse(text, out var cron, out var error), $"'{text}' was refused: {error}");
        return cron!;
    }

    private static TimeZoneInfo Zone(string id)
    {
        Assert.True(ScheduleEvaluator.TryResolveZone(id, out var zone), $"'{id}' did not resolve.");
        return zone;
    }

    [Fact]
    public void A_five_field_expression_is_read_with_ranges_steps_and_lists()
    {
        var cron = Parse("*/15 9-17 * * MON-FRI");

        Assert.Equal([0, 15, 30, 45], cron.Minutes);
        Assert.Equal(Enumerable.Range(9, 9), cron.Hours);
        Assert.Equal([1, 2, 3, 4, 5], cron.DaysOfWeek);

        // 2026-11-16 is a Monday, 2026-11-21 a Saturday: the weekday range is the difference.
        Assert.True(cron.Matches(new DateTime(2026, 11, 16, 9, 30, 0)));
        Assert.False(cron.Matches(new DateTime(2026, 11, 16, 9, 31, 0)));
        Assert.False(cron.Matches(new DateTime(2026, 11, 16, 8, 30, 0)));
        Assert.False(cron.Matches(new DateTime(2026, 11, 21, 9, 30, 0)));

        // A list mixes the shapes; a "from/step" runs to the field's maximum (Vixie's shorthand).
        Assert.Equal([1, 3, 5], Parse("1,3,5 * * * *").Minutes);
        Assert.Equal([5, 15, 25, 35, 45, 55], Parse("5/10 * * * *").Minutes);
        Assert.Equal([0, 30], Parse("0,30 * * * *").Minutes);
    }

    [Fact]
    public void The_day_rule_is_vixies_or_rule()
    {
        // Both day fields restricted: either may match - the first of the month OR any Monday.
        var either = Parse("0 0 1 * MON");
        Assert.True(either.Matches(new DateOnly(2026, 12, 1)));   // the 1st, a Tuesday
        Assert.True(either.Matches(new DateOnly(2026, 11, 16)));  // a Monday, not the 1st
        Assert.False(either.Matches(new DateOnly(2026, 11, 17)));  // neither

        // One of them starred: both must match.
        var dayOfMonthOnly = Parse("0 0 1 * *");
        Assert.True(dayOfMonthOnly.Matches(new DateOnly(2026, 12, 1)));
        Assert.False(dayOfMonthOnly.Matches(new DateOnly(2026, 11, 16)));

        var weekdayOnly = Parse("0 0 * * MON");
        Assert.True(weekdayOnly.Matches(new DateOnly(2026, 11, 16)));
        Assert.False(weekdayOnly.Matches(new DateOnly(2026, 12, 1)));

        // A starred-with-step field counts as starred, exactly as Vixie's DOM_STAR flag does: an odd day of the
        // month AND a Monday (the 9th passes, the 16th is a Monday but an even day).
        var stepped = Parse("0 0 */2 * MON");
        Assert.True(stepped.Matches(new DateOnly(2026, 11, 9)));
        Assert.False(stepped.Matches(new DateOnly(2026, 11, 16)));
    }

    [Fact]
    public void Month_names_and_sunday_as_seven_are_accepted()
    {
        var sunday = Parse("0 0 * * 7");
        Assert.Equal([0], sunday.DaysOfWeek);
        Assert.True(sunday.Matches(new DateOnly(2026, 11, 15)));   // a Sunday
        Assert.False(sunday.Matches(new DateOnly(2026, 11, 16)));

        Assert.Equal([0], Parse("0 0 * * 0").DaysOfWeek);
        Assert.Equal([0, 6], Parse("0 0 * * 0,6").DaysOfWeek);

        var quarterStart = Parse("0 0 1 JAN-MAR *");
        Assert.Equal([1, 2, 3], quarterStart.Months);
        Assert.True(quarterStart.Matches(new DateOnly(2026, 2, 1)));
        Assert.False(quarterStart.Matches(new DateOnly(2026, 4, 1)));

        Assert.Equal([10], Parse("0 0 * OCT *").Months);
        Assert.Equal([4], Parse("0 0 * * thu").DaysOfWeek);
    }

    [Theory]
    [InlineData("", ScheduleErrorCodes.CronRequired)]
    [InlineData("   ", ScheduleErrorCodes.CronRequired)]
    [InlineData("0 0 * *", ScheduleErrorCodes.CronInvalid)]              // four fields
    [InlineData("0 0 0 * * *", ScheduleErrorCodes.CronInvalid)]          // a seconds field is not accepted
    [InlineData("60 * * * *", ScheduleErrorCodes.CronInvalid)]           // minute out of range
    [InlineData("* 24 * * *", ScheduleErrorCodes.CronInvalid)]
    [InlineData("0 0 0 * *", ScheduleErrorCodes.CronInvalid)]            // no day 0
    [InlineData("0 0 * 13 *", ScheduleErrorCodes.CronInvalid)]
    [InlineData("0 0 * * 8", ScheduleErrorCodes.CronInvalid)]
    [InlineData("@daily", ScheduleErrorCodes.CronInvalid)]               // macros are not supported
    [InlineData("0 0 ? * MON", ScheduleErrorCodes.CronInvalid)]
    [InlineData("0 0 * * FRI-MON", ScheduleErrorCodes.CronInvalid)]      // wrapping ranges are refused
    [InlineData("*/0 * * * *", ScheduleErrorCodes.CronInvalid)]
    [InlineData("1--2 * * * *", ScheduleErrorCodes.CronInvalid)]
    [InlineData("1,,2 * * * *", ScheduleErrorCodes.CronInvalid)]
    public void Malformed_expressions_are_refused_with_a_field_code(string text, string expected)
    {
        Assert.False(ScheduleCron.TryParse(text, out var cron, out var error));
        Assert.Null(cron);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void Next_due_times_are_computed_in_the_schedules_own_zone()
    {
        var after = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        var cron = Parse("30 2 * * *");

        // 02:30 in Tokyo is 17:30 UTC the previous day, so the next one after 12:00Z on the 16th is the 17th's
        // 02:30 local = 16th 17:30 UTC.
        var tokyo = ScheduleEvaluator.NextOccurrences(cron, Zone("Asia/Tokyo"), after, 3);
        Assert.Equal(
            [
                new DateTimeOffset(2026, 11, 16, 17, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 17, 17, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 11, 18, 17, 30, 0, TimeSpan.Zero),
            ],
            tokyo);

        // The same expression in UTC is a different schedule altogether.
        var utc = ScheduleEvaluator.NextOccurrences(cron, Zone("UTC"), after, 1);
        Assert.Equal([new DateTimeOffset(2026, 11, 17, 2, 30, 0, TimeSpan.Zero)], utc);

        // ...and in Berlin (UTC+1 in November) it is another instant again.
        var berlin = ScheduleEvaluator.NextOccurrences(cron, Zone("Europe/Berlin"), after, 1);
        Assert.Equal([new DateTimeOffset(2026, 11, 17, 1, 30, 0, TimeSpan.Zero)], berlin);
    }

    [Fact]
    public void A_local_time_the_zone_skips_is_reported_and_never_run()
    {
        var zone = Zone("Europe/Berlin");
        var cron = Parse("30 2 * * *");

        // The night of 2026-03-28 → 29: 02:00 becomes 03:00, so 02:30 never happens. The window covers the last
        // ordinary 02:30 before it (the 28th, 01:30Z) and the transition itself.
        var from = new DateTimeOffset(2026, 3, 28, 0, 0, 0, TimeSpan.Zero);
        var until = new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.Zero);
        var due = ScheduleEvaluator.Enumerate(cron, zone, from, until);

        var skipped = Assert.Single(due, entry => entry.Kind == ScheduleDueKind.Nonexistent);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), skipped.At);   // the transition

        var occurrences = due.Where(entry => entry.Kind == ScheduleDueKind.Occurrence).Select(entry => entry.At);
        Assert.Equal([new DateTimeOffset(2026, 3, 28, 1, 30, 0, TimeSpan.Zero)], occurrences);

        // The preview the settings card shows skips the missing day entirely - it previews real runs.
        var next = ScheduleEvaluator.NextOccurrences(cron, zone, until, 2);
        Assert.Equal(
            [
                new DateTimeOffset(2026, 3, 30, 0, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 31, 0, 30, 0, TimeSpan.Zero),
            ],
            next);
    }

    [Fact]
    public void A_local_time_the_zone_repeats_fires_once_at_the_earlier_instant()
    {
        var zone = Zone("Europe/Berlin");

        // 2026-10-25: 03:00 becomes 02:00, so 02:30 happens twice (00:30Z on CEST, 01:30Z on CET).
        var from = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var until = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var due = ScheduleEvaluator.Enumerate(Parse("30 2 * * *"), zone, from, until)
            .Where(entry => entry.Kind == ScheduleDueKind.Occurrence)
            .Select(entry => entry.At)
            .ToList();

        Assert.Equal(
            [
                new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero),
            ],
            due);
    }

    [Fact]
    public void An_expression_that_never_fires_says_so_instead_of_looping_forever()
    {
        var never = Parse("0 0 30 2 *");
        var after = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(ScheduleEvaluator.NextDueAt(never, Zone("UTC"), after));

        var preview = SchedulePreview.Compute("0 0 30 2 *", "UTC", after);
        Assert.False(preview.IsValid);
        Assert.Equal(ScheduleErrorCodes.CronNeverFires, preview.ErrorCode);

        // A 29-February expression is not "never": it fires within the eight-year lookahead.
        var leapDay = SchedulePreview.Compute("0 0 29 2 *", "UTC", after);
        Assert.True(leapDay.IsValid);
        Assert.Equal(new DateTimeOffset(2028, 2, 29, 0, 0, 0, TimeSpan.Zero), leapDay.DueTimes[0]);
    }

    [Fact]
    public void An_unknown_time_zone_is_refused_and_preview_names_the_field()
    {
        var after = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

        Assert.False(ScheduleEvaluator.TryResolveZone("Mars/Olympus", out _));
        Assert.False(ScheduleEvaluator.TryResolveZone(null, out _));
        Assert.False(ScheduleEvaluator.TryResolveZone(string.Empty, out _));
        Assert.True(ScheduleEvaluator.TryResolveZone("UTC", out var utc));
        Assert.Equal(TimeSpan.Zero, utc.GetUtcOffset(after));

        Assert.Equal(ScheduleErrorCodes.TimeZoneUnknown, SchedulePreview.Compute("0 3 * * *", "Mars/Olympus", after).ErrorCode);
        Assert.Equal(ScheduleErrorCodes.TimeZoneRequired, SchedulePreview.Compute("0 3 * * *", null, after).ErrorCode);

        // The preview a valid pair answers with is the card's "next three due times".
        var preview = SchedulePreview.Compute("0 3 * * *", "Europe/Berlin", after);
        Assert.True(preview.IsValid);
        Assert.Equal(3, preview.DueTimes.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 2, 0, 0, TimeSpan.Zero), preview.DueTimes[0]);
    }

    [Fact]
    public void A_daily_expression_keeps_its_local_time_across_a_dst_change()
    {
        var zone = Zone("Europe/Berlin");
        var cron = Parse("0 9 * * *");
        var after = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

        // Berlin is UTC+2 before the fall-back and UTC+1 after it: 09:00 local is 07:00Z, then 08:00Z, without
        // the expression changing - which is the point of reading the cron in the zone.
        Assert.Equal(
            [
                new DateTimeOffset(2026, 10, 24, 7, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero),
            ],
            ScheduleEvaluator.NextOccurrences(cron, zone, after, 3));
    }
}
