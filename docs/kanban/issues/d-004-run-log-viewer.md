---
id: D-004
title: Run detail: live log controls (pause, search, per-step filter) on top of the grid it already has
type: feature
priority: P1
size: M
status: done
created: 2026-11-13
updated: 2026-11-14
blocked: none
---

## Goal

A run page usable while the run is happening: the log stops scrolling away when the operator is
reading it, lines can be searched and narrowed to a step, and the whole file downloads unchanged. The
panel already streams and already carries a step stamp; what is missing is the control surface.

## Background

Roadmap v0.9 ("live log viewer: pause auto-scroll, search, download"), and `RunLogPanel.razor`'s own
header comment says "Pause / search / per-step views are Phase 5" — this card is that phase. F-008
streams and persists; F-015 stamps `BuildLog.StepId`; F-016 lists artifacts on the run page. Nothing
here needs new server work: the download endpoint and the step attribution already exist.

Decisions:

- **The controls are client-side and do not "pause the run".** Pause freezes the viewport, not the
  stream: lines keep arriving and keep being persisted, and resuming jumps to the tail. A control that
  buffered the run server-side would be a different (and wrong) product.
- **Search is literal and case-insensitive, with a count and next/previous.** Regular expressions are
  a fine parking-lot idea and a poor default in a log viewer where every character is user input.
- **Per-step filter reuses `StepId`, not text matching.** F-015 already attributes every line to a
  step row; filtering on the message text would disagree with the step list above it the first time a
  step echoes another step's name.
- **Rendering stays bounded.** The panel caps what it renders and says so, because a 100 000-line run
  must not become 100 000 DOM nodes; the download is the way to get everything.
- **Failure surface first**: when a run failed, the reason, exit code and failing step are visible
  without scrolling or expanding anything.

## Acceptance Criteria

- [x] Pause stops the viewport from following new lines while lines continue to arrive and are still
      persisted; resuming returns to the newest line, and the indicator says which of the two states
      the panel is in.
- [x] Search finds literal, case-insensitive matches, reports how many there are, and moves between
      them; a search with no matches says so rather than showing an empty panel.
- [x] The per-step filter narrows lines to the selected step using `StepId`, and its counts agree with
      the step list's (a step that produced no output says it produced none).
- [x] Selecting a step from the step list jumps to the log filtered to that step.
- [x] "Download full log" still returns byte-identical content for the same run before and after this
      card (the endpoint is untouched and a test pins the bytes).
- [x] A failed run shows its reason, exit code and failing step above the fold.
- [x] A run with a large log (fixture of at least 5000 lines) renders a bounded number of nodes and
      stays responsive; the cap is stated in the UI.
- [x] Opening the page mid-run shows what has happened so far and reaches the end without a manual
      reload after the run finishes.

## Subtasks

- [x] Extract the log view state (follow/paused, search term and matches, step filter) into a testable
      component or view model rather than fields scattered in the page.
- [x] Wire the step list to the filter and add the failure summary block.
- [x] Tests (`Category=RunUi`): pause/resume semantics, search counting and navigation, step filtering
      by `StepId`, download bytes unchanged, bounded render, mid-run attach.

## Dependencies

- F-008 (streaming + persisted log + download), F-015 (step stamping and step list), F-009
  (cancellation states shown on the page)

## Test Cases

### TC-001: Pause does not stop the build

Steps:
1. Open a running run's page; pause; wait for at least ten new lines to be persisted; resume.

Expected:
- While paused the viewport does not move and the indicator says paused; the newly persisted lines are
  present in the database and appear on resume at the tail.

Result: **pass.** Covered at two levels. Unit (`RunLogControlsTests.Pause_freezes_the_viewport_while_lines_keep_arriving`,
`Resume_jumps_to_the_tail_including_everything_arrived_while_paused`): after pausing, 30 further lines
grow the store (Count 60) while the plan keeps the pause instant's 30 entries and says
`Paused - 30 new lines waiting`; resume renders all 60 ending at the newest. End-to-end
(`RunLogDownloadPinTests.A_paused_and_filtered_panel_changes_nothing_about_the_download`, plus the
ingest persistence pins already owned by F-008's tests): the panel's view model is paused and filtered
while six lines are persisted server-side, and every row is in the database — pause is a viewer
control; nothing server-side buffers or stops. The indicator's two states are pinned by exact text
(`Following live` / `Paused` / `Paused - N new lines waiting`).

### TC-002: Search and step filter agree with the data

Steps:
1. Run a three-step pipeline whose second step prints a marker the first step also prints; search for
   it; then filter to step 2.

Expected:
- The search count equals the number of matching persisted lines; the step filter shows only step 2's
  lines, which is fewer than the search count.

Result: **pass.** `RunLogStepAgreementTests.Search_and_step_filter_agree_with_the_persisted_lines`
runs a real three-step run (`Build`, `Deploy`, `Verify`) through the real step endpoint and worker
hub: the marker (`release-7`) appears under Build twice and under Deploy once. The search count (3)
equals the matching database rows counted independently; filtering to Deploy by `StepId` shows only
the two Deploy lines (fewer than 3), dropping the Build lines that merely carry the same text. The
per-step counts equal the database rows per step, and the silent `Verify` step's filter says
`No output was captured for 3. Verify.` The no-regex rule has its own pin
(`Search_is_literal_never_a_regex`: `a.c` matches one literal line, not `abc`).

### TC-003: The download is not the view

Steps:
1. Pause the panel, filter to one step, then download.

Expected:
- The downloaded file contains every line of the run in order, identical to the pre-card bytes.

Result: **pass.** `RunLogDownloadPinTests` pins the bytes: with the view model paused and filtered to
one step (viewport: 2 of 6 lines), the download returns all six lines in Seq order, byte-for-byte
equal to the expected file built independently from the fixture (`Assert.Equal(Encoding.UTF8.GetBytes(expected), downloaded)`).
A second test pins that step-attributed lines download exactly as they did before the card (stamps
never appear in the file). The download endpoint (`RunEndpoints.DownloadLogAsync`) is untouched.

## Development Log

### 2026-11-13

- Planned from roadmap v0.9. Deliberately client-side: the streaming contract (F-008) and the step
  attribution (F-015) are already correct, and this card must not turn a viewer feature into a change
  in how runs execute.

### 2026-11-14

- Built. `RunLogViewModel` now carries the whole control state and computes a `RunLogPlan` per render:
  the bounded, filtered, searched window of entries, the indicator/cap/no-match sentences the panel
  prints verbatim, the per-step line counts, and the match positions. The panel (`RunLogPanel.razor`)
  only forwards user intent into the view model and renders the plan; the page (`RunDetail.razor`)
  owns the one shared bit — which step the log is filtered to — so the step list's "View log" links
  and the panel's dropdown are one state (page → panel parameter, panel → page event callback, so
  re-selecting the same step still applies).
- Decisions taken while building:
  - *Search narrows the view to the matching lines.* First written as a highlight-and-jump overlay
    (all lines rendered, Next/Previous moving a window); the AC's "a search with no matches says so
    rather than showing an empty panel" only means something if the search narrows, so the narrowing
    model won. Next/Previous moves to a match and the capped window starts there; "Back to latest"
    restores the tail without clearing the search.
  - *Cap = 2000 rendered lines (`RunLogViewModel.RenderCap`), keeping the newest.* The statement in
    the UI is the plan's exact sentence ("Showing 2000 of 5000 lines (view capped at 2000 lines) -
    the download has everything."), rendered next to the existing download link, and pinned by test.
  - *Pause composes with search and filter inside the frozen viewport*: the frozen viewport's upper
    bound is the newest Seq at pause time; new lines are counted as "waiting" in the indicator but
    never shown until resume. Filtering to a step whose lines all arrived after the pause honestly
    says "No output was captured for …" — that is the frozen viewport talking, not a claim about the
    database.
  - *The failure summary block* is gated on `Status == Failed` exactly (Lost/Canceled keep their
    existing surfaces; no F-009 regression), and says "No recorded step failed" for a run that died
    before any step could run (definition-phase failures).
  - *"View log" links opt out of enhanced navigation* (`data-enhance-nav="none"`): Blazor 8+'s
    enhanced nav would re-fetch and re-initialize the page even for a same-page fragment jump,
    throwing away the circuit's panel state (pause, search). With the opt-out the browser does a
    native same-document fragment scroll and the circuit keeps running.
- Surprises: none server-side — the F-008/F-015 contracts needed no changes. The one API gap was
  client-side: nothing linked the step list to the panel, so the page now owns the shared filter
  state. Two self-caught issues (D-004-B1/B2 below) were caught by the new tests before delivery.
- 5000-line fixtures are handled by the cap; the plan computation is O(n) per render with n = the
  store's size, and the render is capped at 2000 entries.
- Review pass (same day): fixed the test-count breakdown (15 unit tests, not 14), pinned the
  `data-enhance-nav="none"` opt-out in the rendered-page test, and recorded the two deferred P2s
  (D-004-B3 circuit-level test gap, D-004-B4 first-Next navigation quirk).

### 2026-11-20

- **Superseded in placement, kept in semantics (F-047).** The inline panel on the run page and the
  same-page fragment navigation are gone: the log is a full-page console at
  `/runs/{id}/console`, and a step's "View log" is an ordinary link with `?step=`. The control
  semantics this card defined remain: pause freezes a viewport (never the build), search is literal
  and narrows, the step filter keys on `StepId`, and the download is untouched. Two deliberate
  changes: pause now has two sources (explicit and scroll-up), and the viewer retains 20,000 lines
  with a stated 2,000-line render cap. This card's test ids and assertions were updated where the
  placement changed; see F-047.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| D-004-B1 | Low | The step-filter dropdown's per-step counts only contained steps that had emitted at least one line, so a recorded-but-silent step had no count at all (surfaced as a KeyNotFound in the new tests). | Fixed | `RunLogViewModel.Plan` seeds every declared step's count at 0 before counting lines; a silent step reads as "no output" in the dropdown and its filter shows the no-output message. |
| D-004-B2 | Low | The first search implementation rendered all lines with a movable window (an overlay). A no-match search would have left the panel full while only the small summary line said anything - the card's "says so rather than showing an empty panel" failed in spirit. | Fixed | Search now narrows the view to the matching lines; a no-match search shows the exact statement (`No lines match "…".`) in place of the log. |
| D-004-B3 | P2 (low) | The step-list click → `RequestedStepId` → filter path and the pause+step-filter / pause+search compositions are code-verified but lack a circuit-level test (the page tests are static SSR). | Deferred | A bUnit-style circuit test would close both; the review passed it as a P2 follow-up because the state machine behind both paths is fully unit-tested (`RunLogControlsTests`) and the page render is pinned (`RunDetailFailureSummaryTests`), and a browser-automation circuit harness over SignalR/WebSockets (which TestServer cannot host) is a card of its own. |
| D-004-B4 | P2 (low) | Search navigation quirk: after typing a term, the first Next jumps to match 1 (the top of the log) rather than the nearest match below the current viewport. | Deferred | "Nearest visible match" needs a viewport/DOM concept the view model deliberately does not have; the current behavior is deterministic and undoable - the position is shown ("Match 1 of N"), Next/Previous wrap around, and "Back to latest" restores the tail without clearing the search. |

## Verification

- Files:
  - `src/Server/Components/Shared/RunLogViewModel.cs` — extended: line store (unchanged) plus
    pause/resume, literal case-insensitive search with count and navigation, `StepId` step filter,
    render cap, and `Plan`/`RunLogPlan` (the render model with indicator/cap/no-match text and
    per-step counts).
  - `src/Server/Components/Pages/Runs/RunLogPanel.razor` — the control surface (pause/resume +
    indicator, search box + previous/next + summary, step dropdown with per-step counts, "Back to
    latest"), the stated cap notice, the empty-view statements, and the current-match highlight;
    new `RequestedStepId`/`StepFilterChanged` parameters. Connection logic (F-008 incl. review fix
    F6) untouched.
  - `src/Server/Components/Pages/Runs/RunDetail.razor` — the failure summary block (reason, exit
    code, failing step; above the Pipeline card), per-step "View log" fragment links wired to the
    panel's filter, and the shared filter state + callback.
  - `src/Server/Runs/RunEndpoints.cs` — **untouched** (download byte behaviour pinned by test).
  - `tests/Zenkins.Tests/RunUi/` — new folder: `RunUiHost.cs` (host: real server + step rows +
    worker/browser hubs), `RunLogControlsTests.cs` (15 circuit-free unit tests),
    `RunLogDownloadPinTests.cs` (2), `RunLogStepAgreementTests.cs` (1), `RunLogMidRunAttachTests.cs`
    (1), `RunDetailFailureSummaryTests.cs` (3) — 22 tests, all `[Trait("Category", "RunUi")]`.
- How to run/verify:
  - `dotnet build -c Release` → **Build succeeded, 0 warnings, 0 errors.**
  - `dotnet test -c Release --filter "Category=RunUi"` → **total 22, failed 0, succeeded 22,
    skipped 0** (green on 7 consecutive runs, including after the review follow-ups).
  - `dotnet test -c Release --filter "Category!=Milestone"` → **total 931, failed 0, succeeded 931,
    skipped 0** (~1m 30s–1m 45s). One earlier full-suite run reported a single failure whose name was
    lost to an output filter; the immediate re-runs (twice, full output retained) were entirely
    green, and the new RunUi suite itself is stable across repeated runs - the profile matches the
    pre-existing short-wait host tests, not this card's code (the card touches no timing-sensitive
    surface). Flagging for the reviewer in case the flake is known.
  - `dotnet test -c Release --filter "Category=Milestone"` → **total 6, failed 0, succeeded 6,
    skipped 0** (34s).
- Results: all four commands green (2026-11-14). No schema change, no new packages; the PostgreSQL
  contract gate is not required (the model is unchanged).
