namespace Zenkins.Server.Schedules;

/// <summary>What kind of due time an evaluation produced.</summary>
public enum ScheduleDueKind
{
    /// <summary>An ordinary occurrence: the local wall clock exists on that date.</summary>
    Occurrence = 0,

    /// <summary>
    /// A local wall clock the zone skipped (the spring-forward gap). It has no instant of its own, so it is
    /// reported at the instant of the transition and never queued - "skip that day and record it", not "guess".
    /// </summary>
    Nonexistent = 1,
}

/// <summary>One due time of a schedule: the instant, in UTC, and whether the local clock really had it.</summary>
public sealed record ScheduleDue(DateTimeOffset At, ScheduleDueKind Kind);

/// <summary>
/// Turns a parsed cron expression plus an IANA time zone into due instants (card F-024), DST-aware in both
/// directions.
/// <para>
/// <b>How a date is read.</b> The day loop asks <see cref="ScheduleCron.Matches(DateOnly)"/> in the zone's own
/// local calendar; each matching day then yields the expression's hour × minute combinations as local wall
/// clocks, converted to UTC. So <c>30 2 * * *</c> in <c>Europe/Berlin</c> is 02:30 Berlin time on every day the
/// zone really has an 02:30 - not a fixed offset from UTC, which is what makes the same expression shift with
/// the zone's DST rules and keeps working when a government changes them.
/// </para>
/// <para>
/// <b>Spring forward: the missing hour is skipped and reported.</b> 02:30 on the day the clocks jump from 02:00
/// to 03:00 does not exist. <see cref="Enumerate"/> yields it as <see cref="ScheduleDueKind.Nonexistent"/> at the
/// transition instant; the sweeper records it as <c>skipped-nonexistent</c> and queues nothing. The alternative -
/// firing at 03:30, or crashing - would either lie about the time or take the whole tick down.
/// </para>
/// <para>
/// <b>Fall back: one due time, the first one.</b> 02:30 on the day the clocks repeat 02:00-03:00 happens twice.
/// The <em>earlier</em> instant is chosen, so a schedule fires once per day instead of twice in one day and zero
/// times in the next. (Ambiguity is resolved by the zone's possible offsets: the largest offset is the pre-transition
/// clock, which is the earlier instant.)
/// </para>
/// <para>
/// <b>Bounded.</b> Every scan is bounded by a lookahead (<see cref="MaxLookaheadDays"/>, eight years - enough to
/// cross a 29-February rule) and by an entry cap, and it stops as soon as it passes its upper bound. Nothing here
/// loops over "every minute until it matches", so a sparse expression costs days, not centuries.
/// </para>
/// </summary>
public static class ScheduleEvaluator
{
    /// <summary>
    /// How far ahead a scan looks. Eight years crosses a 29-February expression (a leap day recurs at most every
    /// eight years across century boundaries) and makes "<c>0 0 30 2 *</c> never fires" a provable answer.
    /// </summary>
    public const int MaxLookaheadDays = 366 * 8;

    /// <summary>The most entries one scan may produce, so a one-minute schedule in a long window stays bounded.</summary>
    public const int MaxEntriesPerScan = 2000;

    /// <summary>
    /// Resolves an IANA time zone id (card F-024 requires IANA; <c>UTC</c> is one of them). <c>TimeZoneInfo</c>
    /// is the authority, on every platform .NET supports: on Windows it resolves IANA ids through ICU, so the
    /// id an operator types is the id the server evaluates - never a translation kept on this side.
    /// </summary>
    public static bool TryResolveZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id) || id.Length > ScheduleErrorCodes.MaxTimeZoneLength)
        {
            return false;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out zone!);
    }

    /// <summary>The first due instant strictly after <paramref name="after"/>, or null when the expression never fires.</summary>
    public static DateTimeOffset? NextDueAt(ScheduleCron cron, TimeZoneInfo zone, DateTimeOffset after)
    {
        var next = NextOccurrences(cron, zone, after, 1);
        return next.Count > 0 ? next[0] : null;
    }

    /// <summary>
    /// The next <paramref name="count"/> due instants strictly after <paramref name="after"/>, ascending. Missing
    /// local times are skipped (they are not occurrences); this is what the settings page previews.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> NextOccurrences(
        ScheduleCron cron,
        TimeZoneInfo zone,
        DateTimeOffset after,
        int count)
    {
        ArgumentNullException.ThrowIfNull(cron);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var results = new List<DateTimeOffset>(count);
        foreach (var due in Enumerate(cron, zone, after, after.AddDays(MaxLookaheadDays), MaxEntriesPerScan))
        {
            if (due.Kind != ScheduleDueKind.Occurrence)
            {
                continue;
            }

            results.Add(due.At);
            if (results.Count == count)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Every due time in <c>(after, until]</c>, ascending by instant - occurrences and the missing local times the
    /// window contains. Bounded by <paramref name="maxEntries"/>; a caller that reaches the cap must treat the
    /// window as truncated rather than as complete.
    /// </summary>
    public static IReadOnlyList<ScheduleDue> Enumerate(
        ScheduleCron cron,
        TimeZoneInfo zone,
        DateTimeOffset after,
        DateTimeOffset until,
        int maxEntries = MaxEntriesPerScan)
    {
        ArgumentNullException.ThrowIfNull(cron);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);

        var results = new List<ScheduleDue>();
        if (until <= after)
        {
            return results;
        }

        // The local calendar is what the expression matches against; start on the day `after` falls on (the
        // converter keeps the wall clock, which is the point of reading a cron in a zone).
        var firstLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime);

        // One extra day of slack for zones whose offset moves across midnight: the day after the last instant is
        // only reached when nothing earlier matched.
        var lookaheadDays = (int)Math.Min(MaxLookaheadDays, Math.Ceiling((until - after).TotalDays) + 2);

        for (var offset = 0; offset <= lookaheadDays; offset++)
        {
            var date = firstLocalDate.AddDays(offset);
            if (!cron.Matches(date))
            {
                continue;
            }

            DateTimeOffset? gapInstant = null;
            foreach (var hour in cron.Hours)
            {
                foreach (var minute in cron.Minutes)
                {
                    var local = date.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Unspecified);

                    if (zone.IsInvalidTime(local))
                    {
                        // The spring-forward gap: report the transition itself once per day, never queue it.
                        gapInstant ??= FindGapInstant(zone, date);
                        if (gapInstant is { } gap && gap > after && gap <= until)
                        {
                            results.Add(new ScheduleDue(gap, ScheduleDueKind.Nonexistent));
                            if (results.Count >= maxEntries)
                            {
                                return results;
                            }
                        }

                        continue;
                    }

                    var instant = ResolveLocalInstant(zone, local);
                    if (instant <= after)
                    {
                        continue;
                    }

                    if (instant > until)
                    {
                        // Candidates only move forward (the day loop ascends, and so do the hour/minute sets), so
                        // the window is finished - except that later days cannot be earlier than `until` either.
                        return results;
                    }

                    results.Add(new ScheduleDue(instant, ScheduleDueKind.Occurrence));
                    if (results.Count >= maxEntries)
                    {
                        return results;
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// One local wall clock → the UTC instant it names. An ambiguous clock (the fall-back hour) resolves to the
    /// <b>earlier</b> of its two instants, so a cron fires once for a repeated hour instead of twice.
    /// </summary>
    private static DateTimeOffset ResolveLocalInstant(TimeZoneInfo zone, DateTime local)
    {
        if (!zone.IsAmbiguousTime(local))
        {
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone)).ToUniversalTime();
        }

        // Both possible offsets are returned; the largest one is the pre-transition (daylight) clock and
        // subtracting it yields the earlier instant.
        var offsets = zone.GetAmbiguousTimeOffsets(local);
        var preTransition = offsets.Max();
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), preTransition)
            .ToUniversalTime();
    }

    /// <summary>
    /// The instant at which the zone jumps over a local wall clock (the gap's start), found by walking the day's
    /// minutes once. Only a DST transition day pays for this, and it is what gives a skipped due time a stable,
    /// unique instant to be recorded at.
    /// </summary>
    private static DateTimeOffset? FindGapInstant(TimeZoneInfo zone, DateOnly date)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(midnight))
        {
            // A zone whose transition covers midnight (the day starts at 01:00). There is no unambiguous
            // midnight to start from, and a skipped due time is recorded without a substitute instant rather
            // than with an invented one - the caller logs it instead.
            return null;
        }

        var start = TimeZoneInfo.ConvertTimeToUtc(midnight, zone);

        // The comparison is between wall clocks, not instants: UTC minute boundaries are always 60 seconds apart,
        // so a jump is visible only in the local DateTime the zone converts them to.
        var previousWallClock = TimeZoneInfo.ConvertTime(new DateTimeOffset(start, TimeSpan.Zero), zone).DateTime;
        for (var minute = 1; minute <= 24 * 60; minute++)
        {
            var utc = new DateTimeOffset(start.AddMinutes(minute), TimeSpan.Zero);
            var wallClock = TimeZoneInfo.ConvertTime(utc, zone).DateTime;
            if (wallClock - previousWallClock > TimeSpan.FromMinutes(1))
            {
                // The local clock jumped forward here: this is the instant the missing wall clocks lost.
                return utc;
            }

            previousWallClock = wallClock;
        }

        return null;
    }
}
