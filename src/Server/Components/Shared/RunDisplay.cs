using BlazorBlueprint.Components;
using Zenkins.Server.Data.Entities;

namespace Zenkins.Server.Components.Shared;

/// <summary>
/// The one place a build run's status and duration become display text (card D-002): the dashboard
/// and the runs list render the same run through these bits, so a run cannot read as, say,
/// Destructive on one page and Outline on the other. The run detail page uses them too.
/// <para>
/// Duration and elapsed share one formatter on purpose: a finished run shows its duration, a
/// running one shows live elapsed time from the caller's clock instant - never a blank and never a
/// zero - and a run that never started shows "-" rather than an invented duration.
/// </para>
/// </summary>
public static class RunDisplay
{
    /// <summary>
    /// The badge variant for a run status. Every well-known status has an explicit answer (a
    /// Canceled run is an outline, a Lost one is destructive) and an unknown status still renders
    /// as an outline instead of throwing.
    /// </summary>
    public static BadgeVariant BadgeFor(string status) => status switch
    {
        BuildRunStatus.Queued => BadgeVariant.Secondary,
        BuildRunStatus.Running => BadgeVariant.Default,
        BuildRunStatus.Success => BadgeVariant.Success,
        BuildRunStatus.Failed => BadgeVariant.Destructive,
        BuildRunStatus.Canceled => BadgeVariant.Outline,
        BuildRunStatus.Lost => BadgeVariant.Destructive,
        _ => BadgeVariant.Outline,
    };

    /// <summary>
    /// A duration as prose: minutes with zero-padded seconds once the run is a minute old, a
    /// one-decimal seconds readout below that. Hours stay in minutes (a 90-minute run reads
    /// "90m 05s") - the same shape every page has shown since F-006.
    /// </summary>
    public static string FormatDuration(TimeSpan duration) => duration.TotalMinutes >= 1
        ? $"{(int)duration.TotalMinutes}m {duration.Seconds:D2}s"
        : $"{duration.TotalSeconds:F1}s";

    /// <summary>
    /// What the duration column shows for one run at clock instant <paramref name="nowUtc"/>:
    /// the stored duration for a finished run, live elapsed time (marked as running) for one still
    /// executing, and "-" for a run that never started (queued, or canceled while queued).
    /// </summary>
    public static string DurationOrElapsed(
        DateTimeOffset? startedAt,
        DateTimeOffset? endedAt,
        DateTimeOffset nowUtc)
    {
        if (startedAt is not { } start)
        {
            return "-";
        }

        return endedAt is { } end
            ? FormatDuration(end - start)
            : $"{FormatDuration(nowUtc - start)} (running)";
    }
}
