using Xunit;
using Zenkins.Shared.LogStreaming;
using Zenkins.Server.Components.Shared;
using Zenkins.Tests.Localization;

namespace Zenkins.Tests.RunUi;

/// <summary>
/// Card D-004's control rules, asserted directly on <see cref="RunLogViewModel"/> - the card's own
/// demand that the log view state be extractable and testable without a full circuit. Covered here:
/// pause freezing the viewport while lines keep arriving (TC-001's viewer half), literal
/// case-insensitive search with count and next/previous (TC-002's count/navigation half), the
/// StepId-keyed step filter with its per-step counts, the bounded render with a 5000-line fixture,
/// and the exact indicator/cap/no-match wording the panel prints.
/// </summary>
[Trait("Category", "RunUi")]
public sealed class RunLogControlsTests
{
    private static readonly Dictionary<Guid, string> Headers = new();

    private static LogLineMessage Line(long seq, string message, Guid? stepId = null, string stream = LogStream.StdOut) =>
        new(seq, stream, message, DateTimeOffset.UtcNow, stepId);

    private static Dictionary<Guid, string> HeadersFor(params (Guid Id, string Name)[] steps) =>
        steps.ToDictionary(step => step.Id, step => step.Name);

    // ------------------------------------------------------------------
    // Pause / resume (TC-001)
    // ------------------------------------------------------------------

    [Fact]
    public void Pause_freezes_the_viewport_while_lines_keep_arriving()
    {
        var viewModel = new RunLogViewModel();
        viewModel.Add(Enumerable.Range(1, 30).Select(seq => Line(seq, $"line-{seq}")));

        var before = viewModel.Plan(Headers, TestText.English);
        Assert.Equal(30, before.Entries.Count);
        Assert.False(before.IsPaused);
        Assert.Equal("Following live", before.Indicator);

        viewModel.Pause();
        viewModel.Add(Enumerable.Range(31, 30).Select(seq => Line(seq, $"line-{seq}")));

        // The store took everything (the stream never stopped); the viewport did not move.
        Assert.Equal(60, viewModel.Count);
        var frozen = viewModel.Plan(Headers, TestText.English);
        Assert.True(frozen.IsPaused);
        Assert.Equal("Paused - 30 new lines waiting", frozen.Indicator);
        Assert.Equal(30, frozen.Entries.Count);
        Assert.Equal(30, frozen.NewWhilePaused);
        Assert.Equal(30, frozen.Entries[^1].Line.Seq);

        // The frozen plan is the plan from the pause instant: same newest visible Seq.
        Assert.Equal(before.Entries[^1].Line.Seq, frozen.Entries[^1].Line.Seq);
    }

    [Fact]
    public void Resume_jumps_to_the_tail_including_everything_arrived_while_paused()
    {
        var viewModel = new RunLogViewModel();
        viewModel.Add(Enumerable.Range(1, 30).Select(seq => Line(seq, $"line-{seq}")));
        viewModel.Pause();
        viewModel.Add(Enumerable.Range(31, 30).Select(seq => Line(seq, $"line-{seq}")));

        viewModel.Resume();

        var plan = viewModel.Plan(Headers, TestText.English);
        Assert.False(plan.IsPaused);
        Assert.Equal(0, plan.NewWhilePaused);
        Assert.Equal(60, plan.Entries.Count);
        Assert.Equal(60, plan.Entries[^1].Line.Seq);
        Assert.Equal("Following live", plan.Indicator);
    }

    [Fact]
    public void Pause_on_an_empty_panel_and_immediate_resume_are_both_harmless()
    {
        var viewModel = new RunLogViewModel();
        viewModel.Pause();
        var frozen = viewModel.Plan(Headers, TestText.English);
        Assert.True(frozen.IsPaused);
        Assert.Empty(frozen.Entries);
        Assert.Equal("Paused", frozen.Indicator);

        viewModel.Add([Line(1, "late line")]);
        Assert.Empty(viewModel.Plan(Headers, TestText.English).Entries);

        viewModel.Resume();
        Assert.Single(viewModel.Plan(Headers, TestText.English).Entries);
    }

    // ------------------------------------------------------------------
    // Search: literal, case-insensitive, counted, navigable
    // ------------------------------------------------------------------

    [Fact]
    public void Search_counts_matching_lines_case_insensitively()
    {
        var viewModel = new RunLogViewModel();
        viewModel.Add(
        [
            Line(1, "Deploy started"),
            Line(2, "nothing here"),
            Line(3, "deploy finished"),
            Line(4, "DEPLOY AGAIN"),
            Line(5, "redeployment"), // contains the term inside a larger word: still a match
        ]);

        viewModel.SetSearch("deploy");
        var plan = viewModel.Plan(Headers, TestText.English);

        Assert.True(plan.SearchActive);
        Assert.Equal(4, plan.MatchCount);
        Assert.Equal(0, plan.CurrentMatchIndex);
        Assert.Equal("4 matches for \"deploy\".", plan.SearchSummary);
    }

    [Fact]
    public void Search_is_literal_never_a_regex()
    {
        var viewModel = new RunLogViewModel();
        viewModel.Add(
        [
            Line(1, "path a.c used"),
            Line(2, "abc should not match"),
        ]);

        viewModel.SetSearch("a.c");
        Assert.Equal(1, viewModel.Plan(Headers, TestText.English).MatchCount);
    }

    [Fact]
    public void Next_and_previous_navigate_matches_and_wrap_around()
    {
        var viewModel = new RunLogViewModel();
        viewModel.Add(
        [
            Line(1, "hit one"),
            Line(2, "miss"),
            Line(3, "hit two"),
            Line(4, "hit three"),
        ]);

        viewModel.SetSearch("hit");
        viewModel.MoveToNextMatch();
        var first = viewModel.Plan(Headers, TestText.English);
        Assert.Equal(1, first.CurrentMatchIndex);
        Assert.Equal(1, first.CurrentMatchSeq);
        Assert.Equal("Match 1 of 3 for \"hit\".", first.SearchSummary);

        viewModel.MoveToNextMatch();
        viewModel.MoveToNextMatch();
        var last = viewModel.Plan(Headers, TestText.English);
        Assert.Equal(3, last.CurrentMatchIndex);
        Assert.Equal(4, last.CurrentMatchSeq);

        // Wrapping: next past the last returns to the first, previous past the first to the last.
        viewModel.MoveToNextMatch();
        Assert.Equal((1, 1L), (viewModel.Plan(Headers, TestText.English).CurrentMatchIndex, viewModel.Plan(Headers, TestText.English).CurrentMatchSeq));
        viewModel.MoveToPreviousMatch();
        var wrapped = viewModel.Plan(Headers, TestText.English);
        Assert.Equal(3, wrapped.CurrentMatchIndex);
        Assert.Equal(4, wrapped.CurrentMatchSeq);
    }

    [Fact]
    public void Navigation_jumps_the_view_onto_the_matches_and_stays_bounded()
    {
        // More matches than the cap: navigation must still render a bounded window.
        var viewModel = new RunLogViewModel();
        viewModel.Add(Enumerable.Range(1, RunLogViewModel.RenderCap + 500)
            .Select(seq => Line(seq, $"needle {seq}")));

        viewModel.SetSearch("needle");
        Assert.Equal(2500, viewModel.Plan(Headers, TestText.English).MatchCount);

        viewModel.MoveToNextMatch();
        var first = viewModel.Plan(Headers, TestText.English);
        Assert.Equal(1, first.CurrentMatchIndex);
        Assert.Equal(1, first.CurrentMatchSeq);
        Assert.Equal(RunLogViewModel.RenderCap, first.Entries.Count); // the window is the cap...
        Assert.Equal(1, first.Entries[0].Line.Seq); // ...starting at the current match

        // A single match among thousands: the view is the matches, so it renders just that line.
        var sparse = new RunLogViewModel();
        sparse.Add(Enumerable.Range(1, RunLogViewModel.RenderCap + 500)
            .Select(seq => Line(seq, seq == 10 ? "the only needle here" : $"filler {seq}")));
        sparse.SetSearch("needle");
        sparse.MoveToNextMatch();
        var only = sparse.Plan(Headers, TestText.English);
        Assert.Single(only.Entries);
        Assert.Equal(10, only.Entries[0].Line.Seq);
        Assert.False(only.IsCapped);
        Assert.Null(only.CapNotice);

        // Jumping back to the tail drops the navigation cursor (and stays on the matches, since the
        // search is still active).
        sparse.JumpToTail();
        var tail = sparse.Plan(Headers, TestText.English);
        Assert.Equal(0, tail.CurrentMatchIndex);
        Assert.Null(tail.CurrentMatchSeq);
        Assert.Single(tail.Entries);
    }

    [Fact]
    public void A_search_without_matches_says_so()
    {
        var viewModel = new RunLogViewModel();
        viewModel.Add([Line(1, "only ordinary lines")]);

        viewModel.SetSearch("nope");
        var plan = viewModel.Plan(Headers, TestText.English);

        Assert.Equal(0, plan.MatchCount);
        Assert.Equal(0, plan.CurrentMatchIndex);
        Assert.Null(plan.CurrentMatchSeq);
        Assert.Equal("No matches for \"nope\".", plan.SearchSummary);
        Assert.Equal("No lines match \"nope\".", plan.EmptyMessage);

        // Navigation with zero matches is a no-op, never a crash or a wrap to a bogus position.
        viewModel.MoveToNextMatch();
        viewModel.MoveToPreviousMatch();
        Assert.Equal(0, viewModel.Plan(Headers, TestText.English).CurrentMatchIndex);
    }

    [Fact]
    public void Clearing_the_search_restores_the_full_view()
    {
        var viewModel = new RunLogViewModel();
        viewModel.Add([Line(1, "one"), Line(2, "two")]);
        viewModel.SetSearch("one");
        Assert.Single(viewModel.Plan(Headers, TestText.English).Entries);

        viewModel.SetSearch("");
        var plan = viewModel.Plan(Headers, TestText.English);
        Assert.False(plan.SearchActive);
        Assert.Null(plan.SearchSummary);
        Assert.Equal(2, plan.Entries.Count);
    }

    // ------------------------------------------------------------------
    // Step filter: keyed on StepId, counts agreeing with the step list
    // ------------------------------------------------------------------

    [Fact]
    public void Step_filter_narrows_by_step_id_never_by_text()
    {
        var stepA = Guid.NewGuid();
        var stepB = Guid.NewGuid();
        var viewModel = new RunLogViewModel();
        viewModel.Add(
        [
            Line(1, "build is running", stepA),
            Line(2, "echoing the build step's name from another step", stepB),
            Line(3, "build done", stepA),
            Line(4, "no step at all"),
        ]);

        viewModel.SetStepFilter(stepB);
        var plan = viewModel.Plan(HeadersFor((stepA, "1. Build"), (stepB, "2. Echo")), TestText.English);

        Assert.Equal(stepB, plan.StepFilter);
        Assert.Single(plan.Entries);
        Assert.Equal(2, plan.Entries[0].Line.Seq);
        // The mere mention of "build" in step B's line did not pull step A's lines in: the filter
        // compared StepId, not message text.
        Assert.DoesNotContain(plan.Entries, entry => entry.Line.Seq is 1 or 3 or 4);
    }

    [Fact]
    public void Step_counts_agree_with_the_step_list_including_a_step_with_no_output()
    {
        var stepA = Guid.NewGuid();
        var stepB = Guid.NewGuid(); // recorded row, but never printed anything
        var viewModel = new RunLogViewModel();
        viewModel.Add([Line(1, "a-one", stepA), Line(2, "a-two", stepA)]);

        var plan = viewModel.Plan(HeadersFor((stepA, "1. Loud"), (stepB, "2. Silent")), TestText.English);

        Assert.Equal(2, plan.StepCounts[stepA]);
        Assert.Equal(0, plan.StepCounts[stepB]);

        // The silent step is selectable, and the panel says it produced no output.
        viewModel.SetStepFilter(stepB);
        var silent = viewModel.Plan(HeadersFor((stepA, "1. Loud"), (stepB, "2. Silent")), TestText.English);
        Assert.Empty(silent.Entries);
        Assert.Equal("No output was captured for 2. Silent.", silent.EmptyMessage);
    }

    [Fact]
    public void Search_composes_with_the_step_filter()
    {
        var stepA = Guid.NewGuid();
        var stepB = Guid.NewGuid();
        var viewModel = new RunLogViewModel();
        viewModel.Add(
        [
            Line(1, "marker from build", stepA),
            Line(2, "marker from test", stepB),
            Line(3, "marker again", stepA),
        ]);

        viewModel.SetStepFilter(stepA);
        viewModel.SetSearch("marker");
        var plan = viewModel.Plan(HeadersFor((stepA, "1. Build"), (stepB, "2. Test")), TestText.English);

        Assert.Equal(2, plan.MatchCount); // not 3: step B's marker is filtered out
        viewModel.MoveToNextMatch();
        viewModel.MoveToNextMatch();
        Assert.Equal(3, viewModel.Plan(HeadersFor((stepA, "1. Build"), (stepB, "2. Test")), TestText.English).CurrentMatchSeq);

        // A search that matches nothing inside the step says where it looked.
        viewModel.SetSearch("from test");
        var empty = viewModel.Plan(HeadersFor((stepA, "1. Build"), (stepB, "2. Test")), TestText.English);
        Assert.Equal(0, empty.MatchCount);
        Assert.Equal("No lines in 1. Build match \"from test\".", empty.EmptyMessage);
    }

    // ------------------------------------------------------------------
    // Bounded render (the 5000-line fixture)
    // ------------------------------------------------------------------

    [Fact]
    public void A_5000_line_fixture_renders_the_capped_newest_window_and_states_the_cap()
    {
        var viewModel = new RunLogViewModel();
        viewModel.Add(Enumerable.Range(1, 5000).Select(seq => Line(seq, $"line-{seq}")));

        var plan = viewModel.Plan(Headers, TestText.English);

        Assert.Equal(5000, plan.TotalLines);
        Assert.True(plan.IsCapped);
        Assert.Equal(RunLogViewModel.RenderCap, plan.Entries.Count);
        // The cap keeps the newest lines, in order.
        Assert.Equal(5000 - RunLogViewModel.RenderCap + 1, plan.Entries[0].Line.Seq);
        Assert.Equal(5000, plan.Entries[^1].Line.Seq);
        Assert.Equal(
            $"Showing {RunLogViewModel.RenderCap} of 5000 lines (view capped at {RunLogViewModel.RenderCap} lines) - the download has everything.",
            plan.CapNotice);
    }

    [Fact]
    public void Under_the_cap_nothing_is_cut_and_no_notice_is_shown()
    {
        var viewModel = new RunLogViewModel();
        viewModel.Add(Enumerable.Range(1, 42).Select(seq => Line(seq, $"line-{seq}")));

        var plan = viewModel.Plan(Headers, TestText.English);

        Assert.Equal(42, plan.Entries.Count);
        Assert.False(plan.IsCapped);
        Assert.Null(plan.CapNotice);
    }

    [Fact]
    public void Segment_headers_survive_the_cap_window_starting_mid_segment()
    {
        var step = Guid.NewGuid();
        var viewModel = new RunLogViewModel();
        viewModel.Add(Enumerable.Range(1, 5000).Select(seq => Line(seq, $"line-{seq}", step)));

        var plan = viewModel.Plan(new Dictionary<Guid, string> { [step] = "1. Long" }, TestText.English);

        // The window begins inside step 1's segment; the header is still there, so the truncated
        // view never shows anonymous lines.
        Assert.Equal("1. Long", plan.Entries[0].Header);
    }

    // ------------------------------------------------------------------
    // F-047: the viewer's own retention bound and the two pause sources
    // ------------------------------------------------------------------

    [Fact]
    public void A_viewer_keeps_only_the_newest_twenty_thousand_lines()
    {
        var viewModel = new RunLogViewModel();
        viewModel.Add(Enumerable.Range(1, 100_005).Select(seq => Line(seq, $"line-{seq}")));

        var plan = viewModel.Plan(Headers, TestText.English);

        Assert.Equal(RunLogViewModel.RetainedCap, viewModel.Count);
        Assert.Equal(100_005, plan.ReceivedLines);
        Assert.Equal(RunLogViewModel.RetainedCap, plan.RetainedLines);
        Assert.Equal(100_005 - RunLogViewModel.RetainedCap, plan.DroppedFromViewer);
        Assert.Equal(100_005 - RunLogViewModel.RetainedCap + 1, viewModel.Snapshot()[0].Seq);
        Assert.Equal(
            $"This viewer keeps the newest {RunLogViewModel.RetainedCap} lines; {plan.DroppedFromViewer} older line(s) are not shown - download the full log.",
            plan.RetentionNotice);
    }

    [Fact]
    public void Replaying_evicted_history_does_not_inflate_the_retention_counts()
    {
        var viewModel = new RunLogViewModel();
        viewModel.Add(Enumerable.Range(1, 100_005).Select(seq => Line(seq, $"line-{seq}")));
        var before = viewModel.Plan(Headers, TestText.English);

        // A reconnect replays the whole history. Sequences retention already dropped are old news:
        // they must not be merged, counted, or evicted again.
        viewModel.Add(Enumerable.Range(1, 100_005).Select(seq => Line(seq, $"line-{seq}")));
        var after = viewModel.Plan(Headers, TestText.English);

        Assert.Equal(before.ReceivedLines, after.ReceivedLines);
        Assert.Equal(before.DroppedFromViewer, after.DroppedFromViewer);
        Assert.Equal(before.RetainedLines, after.RetainedLines);
        Assert.Equal(before.RetentionNotice, after.RetentionNotice);
    }

    [Fact]
    public void Duplicate_sequences_do_not_count_as_received_twice()
    {
        var viewModel = new RunLogViewModel();
        viewModel.Add([Line(1, "first")]);
        viewModel.Add([Line(1, "first again")]);

        var plan = viewModel.Plan(Headers, TestText.English);

        Assert.Equal(1, viewModel.Count);
        Assert.Equal(1, plan.ReceivedLines);
        Assert.Equal(0, plan.DroppedFromViewer);
        Assert.Null(plan.RetentionNotice);
    }

    [Fact]
    public void Scroll_pause_is_cleared_by_returning_to_the_bottom_but_explicit_pause_is_not()
    {
        var viewModel = new RunLogViewModel();
        viewModel.Add(Enumerable.Range(1, 10).Select(seq => Line(seq, $"line-{seq}")));

        viewModel.PauseFromScroll();
        Assert.True(viewModel.IsPaused);
        Assert.True(viewModel.IsPausedByScroll);

        viewModel.ResumeFromScroll();
        Assert.False(viewModel.IsPaused);
        Assert.False(viewModel.IsPausedByScroll);

        viewModel.Pause();
        viewModel.ResumeFromScroll();
        Assert.True(viewModel.IsPaused);
        Assert.False(viewModel.IsPausedByScroll);

        viewModel.Resume();
        Assert.False(viewModel.IsPaused);
    }

    [Fact]
    public void New_while_paused_counts_by_sequence_when_old_lines_are_evicted()
    {
        var viewModel = new RunLogViewModel();
        viewModel.Add(Enumerable.Range(1, RunLogViewModel.RetainedCap).Select(seq => Line(seq, $"line-{seq}")));
        viewModel.Pause();
        viewModel.Add(Enumerable.Range(RunLogViewModel.RetainedCap + 1, 5).Select(seq => Line(seq, $"line-{seq}")));

        var plan = viewModel.Plan(Headers, TestText.English);

        // Five lines arrived after the pause; five of the oldest retained lines (seq 1..5) were
        // evicted by the retention bound. The waiting count is still about the new lines, and the
        // loss is stated separately.
        Assert.Equal(5, plan.NewWhilePaused);
        Assert.Equal(5, plan.DroppedFromViewer);
        Assert.Equal(5, plan.DroppedWhilePaused);
        Assert.Equal(
            "Paused - 5 line(s) were dropped by the viewer's retention while you were paused.",
            plan.DroppedWhilePausedNotice);
    }
}
