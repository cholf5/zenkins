using Zenkins.Shared.LogStreaming;

namespace Zenkins.Server.Components.Shared;

/// <summary>
/// The interactive log panel's line store (card F-008). History chunks and live batches arrive on
/// the same connection but can interleave (a live line flushed while the history read is still
/// streaming arrives after an earlier history chunk), so lines are stored per Seq and presented
/// sorted - duplicates (a line both in the history and in a live batch) collapse onto one entry.
/// <para>
/// Memory is bounded by the log itself on the browser's server side: one entry per distinct Seq.
/// Rendering every line keeps "open a finished run, see the full history" true; pause/search and
/// virtualization are Phase 5.
/// </para>
/// </summary>
public sealed class RunLogViewModel
{
    private readonly object _gate = new();
    private readonly SortedDictionary<long, LogLineMessage> _lines = new();

    /// <summary>How many distinct lines are currently held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _lines.Count;
            }
        }
    }

    /// <summary>Merges one batch (history chunk or live flush) into the store, deduplicating by Seq.</summary>
    public void Add(IEnumerable<LogLineMessage> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        lock (_gate)
        {
            foreach (var line in lines)
            {
                _lines[line.Seq] = line;
            }
        }
    }

    /// <summary>A Seq-ordered snapshot for rendering.</summary>
    public IReadOnlyList<LogLineMessage> Snapshot()
    {
        lock (_gate)
        {
            return [.. _lines.Values];
        }
    }

    /// <summary>
    /// Turns a Seq-ordered list of lines into render entries, inserting a step header the first time a
    /// step's segment starts (card F-015). A line without a step id - the definition phase, the lines between
    /// steps, a loss marker - simply continues the current segment, so nothing is ever attributed to a step
    /// that did not emit it.
    /// <para>
    /// Pure and separate from the panel because the panel only renders it inside an interactive circuit (a
    /// prerender has no connection yet); the grouping rule is what the card pins, so it is testable without
    /// one.
    /// </para>
    /// </summary>
    /// <param name="lines">The lines in Seq order.</param>
    /// <param name="stepHeaders">A recorded step id mapped to the header the page shows for it.</param>
    public static IReadOnlyList<RunLogEntry> Segment(
        IReadOnlyList<LogLineMessage> lines,
        IReadOnlyDictionary<Guid, string> stepHeaders)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(stepHeaders);

        var entries = new List<RunLogEntry>(lines.Count);
        Guid? current = null;
        foreach (var line in lines)
        {
            string? header = null;
            if (line.StepId != current)
            {
                current = line.StepId;
                if (line.StepId is { } stepId && stepHeaders.TryGetValue(stepId, out var name))
                {
                    header = name;
                }
            }

            entries.Add(new RunLogEntry(header, line));
        }

        return entries;
    }
}

/// <summary>One rendered log line plus the step header it introduces, if it introduces one (card F-015).</summary>
public sealed record RunLogEntry(string? Header, LogLineMessage Line);
