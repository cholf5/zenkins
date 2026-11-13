using Zenkins.Shared.LogStreaming;

namespace Zenkins.Server.Components.Shared;

/// <summary>
/// The interactive log panel's line store and control state (cards F-008 and D-004). History chunks
/// and live batches arrive on the same connection but can interleave (a live line flushed while the
/// history read is still streaming arrives after an earlier history chunk), so lines are stored per
/// Seq and presented sorted - duplicates (a line both in the history and in a live batch) collapse
/// onto one entry.
/// <para>
/// <b>D-004's control surface lives here rather than in fields scattered across the page</b>, so the
/// pause/search/filter rules are testable without an interactive circuit: the panel renders whatever
/// <see cref="Plan"/> computes and owns no decision of its own.
/// </para>
/// <para>
/// Memory is bounded by the log itself on the browser's server side: one entry per distinct Seq.
/// Rendering is bounded by <see cref="RenderCap"/> - a 100 000-line run must not become 100 000 DOM
/// nodes; the download is the way to get everything.
/// </para>
/// </summary>
public sealed class RunLogViewModel
{
    /// <summary>
    /// How many lines the panel renders at most (card D-004's render bound). The window keeps the
    /// newest lines; the cap is stated in the UI, and the full log is one download away.
    /// </summary>
    public const int RenderCap = 2000;

    private readonly object _gate = new();
    private readonly SortedDictionary<long, LogLineMessage> _lines = new();

    /// <summary>The viewport's upper bound while paused: the newest Seq visible when Pause() ran.</summary>
    private long _frozenUpperSeq;

    /// <summary>True while the viewport is frozen (card D-004's pause).</summary>
    private bool _paused;

    /// <summary>The search term, trimmed; empty means no search is active.</summary>
    private string _searchText = string.Empty;

    /// <summary>The step the panel is narrowed to, by the step row's id (never by text).</summary>
    private Guid? _stepFilter;

    /// <summary>The current match for navigation, 1-based into the match list; 0 = not navigating.</summary>
    private int _currentMatchIndex;

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

    /// <summary>Whether the viewport is frozen while lines keep arriving (card D-004).</summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    /// <summary>The active search term (trimmed), or the empty string when no search is active.</summary>
    public string SearchText
    {
        get
        {
            lock (_gate)
            {
                return _searchText;
            }
        }
    }

    /// <summary>The step the panel is narrowed to (card D-004), or null for all steps.</summary>
    public Guid? StepFilter
    {
        get
        {
            lock (_gate)
            {
                return _stepFilter;
            }
        }
    }

    /// <summary>Merges one batch (history chunk or live flush) into the store, deduplicating by Seq.
    /// Pausing never stops the arrivals: the stream keeps feeding the store while the viewport is
    /// frozen, and resuming picks the new lines up at the tail.</summary>
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
    /// Freezes the viewport (card D-004): from here on the plan keeps showing the lines that were
    /// newest at this instant, while new lines keep arriving in the store (and stay persisted
    /// server-side - pause is a viewer control, never a build control). The panel's indicator says
    /// paused, and <see cref="RunLogPlan.NewWhilePaused"/> counts what is waiting.
    /// </summary>
    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
            _frozenUpperSeq = _lines.Count == 0 ? 0 : _lines.Keys.Max();
        }
    }

    /// <summary>
    /// Unfreezes the viewport: the next plan jumps to the tail (the newest lines, including
    /// everything that arrived while paused) and match navigation restarts.
    /// </summary>
    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
            _frozenUpperSeq = 0;
            _currentMatchIndex = 0;
        }
    }

    /// <summary>Sets the search term (literal, case-insensitive; D-004's decision - no regex).
    /// Changing the term restarts match navigation.</summary>
    public void SetSearch(string? text)
    {
        lock (_gate)
        {
            _searchText = (text ?? string.Empty).Trim();
            _currentMatchIndex = 0;
        }
    }

    /// <summary>
    /// Narrows the panel to one step by its row id (card D-004: the same <see cref="LogLineMessage.StepId"/>
    /// F-015 stamps on every line - never a text match, which would disagree with the step list the
    /// first time a step echoes another step's name). Changing the filter restarts match navigation.
    /// </summary>
    public void SetStepFilter(Guid? stepId)
    {
        lock (_gate)
        {
            _stepFilter = stepId;
            _currentMatchIndex = 0;
        }
    }

    /// <summary>Moves to the next search match, wrapping from the last back to the first.</summary>
    public void MoveToNextMatch()
    {
        lock (_gate)
        {
            var count = ComputeLocked([.. _lines.Values]).Matches.Count;
            if (count == 0)
            {
                return;
            }

            _currentMatchIndex = _currentMatchIndex >= count ? 1 : _currentMatchIndex + 1;
        }
    }

    /// <summary>Moves to the previous search match, wrapping from the first to the last.</summary>
    public void MoveToPreviousMatch()
    {
        lock (_gate)
        {
            var count = ComputeLocked([.. _lines.Values]).Matches.Count;
            if (count == 0)
            {
                return;
            }

            _currentMatchIndex = _currentMatchIndex <= 1 ? count : _currentMatchIndex - 1;
        }
    }

    /// <summary>
    /// Drops match navigation and shows the tail again - the affordance that brings the viewport back
    /// after Next/Previous moved it onto a match.
    /// </summary>
    public void JumpToTail()
    {
        lock (_gate)
        {
            _currentMatchIndex = 0;
        }
    }

    /// <summary>
    /// Computes everything the panel renders (card D-004): the bounded, filtered, searched window of
    /// lines with their step headers, the indicator and notice texts (the panel prints them verbatim,
    /// so tests can pin the exact UI wording), the per-step line counts the step filter's dropdown
    /// shows, and the search match positions for Next/Previous navigation.
    /// <para>
    /// While paused the window's upper bound is frozen at the pause instant, so the viewport does not
    /// move even as lines arrive; searching and filtering still work inside the frozen view, and
    /// <see cref="Resume"/> jumps to the tail.
    /// </para>
    /// </summary>
    /// <param name="stepHeaders">A recorded step id mapped to the header the page shows for it.</param>
    public RunLogPlan Plan(IReadOnlyDictionary<Guid, string> stepHeaders)
    {
        ArgumentNullException.ThrowIfNull(stepHeaders);

        lock (_gate)
        {
            // The store, in Seq order. The deduplication and ordering rules are the store's own;
            // everything below reads the snapshot.
            var all = new List<LogLineMessage>(_lines.Values);
            var (viewport, filtered, matches) = ComputeLocked(all);
            var waiting = all.Count - viewport.Count;

            // Per-step line counts over everything the store holds (what the step list's rows
            // produced), so the dropdown's counts agree with the step list. Every declared step is
            // present, a silent one as zero - a step that produced no output must be able to say so.
            var stepCounts = new Dictionary<Guid, int>();
            foreach (var header in stepHeaders.Keys)
            {
                stepCounts[header] = 0;
            }

            foreach (var line in all)
            {
                if (line.StepId is { } stepId)
                {
                    stepCounts[stepId] = stepCounts.TryGetValue(stepId, out var count) ? count + 1 : 1;
                }
            }

            var searchActive = _searchText.Length > 0;
            var cursor = _currentMatchIndex;

            // What the panel renders: the matches when a search is active (the search narrows the
            // panel - a search with no matches leaves an empty view, and the plan says why), the
            // step-filtered lines otherwise. Either way the render stays bounded.
            var view = searchActive ? matches : filtered;

            // The rendered window: the newest RenderCap lines while following the tail; after a
            // Next/Previous move, the cap-sized window of matches that starts at the current one
            // (so the navigation visibly jumps onto the match).
            List<LogLineMessage> window;
            if (cursor > 0 && matches.Count > 0)
            {
                cursor = Math.Min(cursor, matches.Count);
                window = [.. matches.Skip(cursor - 1).Take(RenderCap)];
            }
            else
            {
                cursor = 0;
                window = view.Count <= RenderCap
                    ? view
                    : [.. view.GetRange(view.Count - RenderCap, RenderCap)];
            }

            var entries = Segment(window, stepHeaders);
            var isCapped = view.Count > RenderCap;

            return new RunLogPlan(
                Entries: entries,
                TotalLines: all.Count,
                FilteredLines: view.Count,
                IsCapped: isCapped,
                CapNotice: isCapped
                    ? $"Showing {window.Count} of {view.Count} lines (view capped at {RenderCap} lines) - the download has everything."
                    : null,
                IsPaused: _paused,
                NewWhilePaused: waiting,
                Indicator: IndicatorText(_paused, waiting),
                SearchActive: searchActive,
                SearchText: _searchText,
                MatchCount: matches.Count,
                CurrentMatchIndex: matches.Count == 0 ? 0 : cursor,
                CurrentMatchSeq: matches.Count == 0 || cursor == 0 ? null : matches[cursor - 1].Seq,
                SearchSummary: SearchSummaryText(searchActive, _searchText, matches.Count, cursor),
                EmptyMessage: EmptyMessageText(
                    all.Count, view.Count, searchActive, _searchText,
                    _stepFilter, viewport, stepHeaders),
                StepFilter: _stepFilter,
                StepCounts: stepCounts);
        }
    }

    /// <summary>
    /// The locked core every reader shares: the frozen viewport (while paused, only lines up to the
    /// pause instant's bound exist for the viewer - newer ones are waiting, never shown), the step
    /// filter (equality on the step row's id, never a message-text match), and the search matches
    /// (literal, case-insensitive, one per matching line). Returns the three lists in Seq order.
    /// </summary>
    private (List<LogLineMessage> Viewport, List<LogLineMessage> Filtered, List<LogLineMessage> Matches)
        ComputeLocked(List<LogLineMessage> all)
    {
        var upperBound = _paused ? _frozenUpperSeq : long.MaxValue;
        var viewport = new List<LogLineMessage>(all.Count);
        foreach (var line in all)
        {
            if (line.Seq <= upperBound)
            {
                viewport.Add(line);
            }
        }

        var filtered = _stepFilter is null
            ? viewport
            : viewport.Where(line => line.StepId == _stepFilter).ToList();

        var matches = new List<LogLineMessage>(_searchText.Length == 0 ? 0 : filtered.Count);
        if (_searchText.Length > 0)
        {
            foreach (var line in filtered)
            {
                if (line.Message.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(line);
                }
            }
        }

        return (viewport, filtered, matches);
    }

    /// <summary>The indicator's exact text - the panel's own statement of which state it is in.</summary>
    private static string IndicatorText(bool paused, int waiting) => paused
        ? waiting > 0 ? $"Paused - {waiting} new lines waiting" : "Paused"
        : "Following live";

    private static string? SearchSummaryText(bool active, string text, int matchCount, int cursor)
    {
        if (!active)
        {
            return null;
        }

        if (matchCount == 0)
        {
            // The no-match statement D-004's AC asks for: the panel says so instead of showing silence.
            return $"No matches for \"{text}\".";
        }

        return cursor == 0
            ? $"{matchCount} match{(matchCount == 1 ? "" : "es")} for \"{text}\"."
            : $"Match {cursor} of {matchCount} for \"{text}\".";
    }

    private static string? EmptyMessageText(
        int totalLines,
        int viewLines,
        bool searchActive,
        string searchText,
        Guid? stepFilter,
        IReadOnlyList<LogLineMessage> viewport,
        IReadOnlyDictionary<Guid, string> stepHeaders)
    {
        if (totalLines == 0 || viewLines > 0)
        {
            return null;
        }

        // A selected step that produced no output says so (D-004's AC), distinguished from a search
        // that matched nothing. While the viewport is paused, "no output" means no output visible in
        // the frozen view - the honest statement either way.
        if (stepFilter is { } filter)
        {
            var header = stepHeaders.TryGetValue(filter, out var name) ? name : "the selected step";
            var stepLinesInView = viewport.Count(line => line.StepId == filter);
            if (stepLinesInView == 0)
            {
                return $"No output was captured for {header}.";
            }

            return $"No lines in {header} match \"{searchText}\".";
        }

        if (searchActive)
        {
            return $"No lines match \"{searchText}\".";
        }

        return null;
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

/// <summary>
/// Everything the log panel renders for one instant (card D-004): the bounded window of entries, the
/// indicator and notice texts the panel prints verbatim, and the state behind the controls. Immutable
/// per <see cref="RunLogViewModel.Plan"/> call, so the panel never renders a half-updated mixture.
/// </summary>
/// <param name="Entries">The lines to render, in Seq order, with their step headers. At most
/// <see cref="RunLogViewModel.RenderCap"/> of them.</param>
/// <param name="TotalLines">All distinct lines the store holds (the run's log so far).</param>
/// <param name="FilteredLines">The lines the view narrows to: the search matches while a search is
/// active, the step-filtered lines otherwise.</param>
/// <param name="IsCapped">Whether the render was truncated by the cap.</param>
/// <param name="CapNotice">The exact sentence stating the cap in the UI, or null when nothing was cut.</param>
/// <param name="IsPaused">Whether the viewport is frozen (lines still arrive and stay persisted).</param>
/// <param name="NewWhilePaused">Lines that arrived after the pause and are not shown yet.</param>
/// <param name="Indicator">The exact indicator text: which of the two states the panel is in.</param>
/// <param name="SearchActive">Whether a search term is set.</param>
/// <param name="SearchText">The active search term (trimmed).</param>
/// <param name="MatchCount">Lines matching the search (within the step filter); 0 when inactive or none.</param>
/// <param name="CurrentMatchIndex">The 1-based position of the current match, or 0 when not navigating.</param>
/// <param name="CurrentMatchSeq">The current match line's Seq, for the panel's highlight; null otherwise.</param>
/// <param name="SearchSummary">The exact search summary text (count, position, or the no-match
/// statement), or null when no search is active.</param>
/// <param name="EmptyMessage">Why the narrowed view is empty (a step with no output, or a search with
/// no matches), or null when there is something to show.</param>
/// <param name="StepFilter">The step the plan is narrowed to, or null for all steps.</param>
/// <param name="StepCounts">Per recorded step id, how many held lines it emitted - the counts the
/// dropdown shows, agreeing with the step list.</param>
public sealed record RunLogPlan(
    IReadOnlyList<RunLogEntry> Entries,
    int TotalLines,
    int FilteredLines,
    bool IsCapped,
    string? CapNotice,
    bool IsPaused,
    int NewWhilePaused,
    string Indicator,
    bool SearchActive,
    string SearchText,
    int MatchCount,
    int CurrentMatchIndex,
    long? CurrentMatchSeq,
    string? SearchSummary,
    string? EmptyMessage,
    Guid? StepFilter,
    IReadOnlyDictionary<Guid, int> StepCounts);
