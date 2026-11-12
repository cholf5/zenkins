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
}
