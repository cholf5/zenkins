---
id: D-004
title: Run detail: live log controls (pause, search, per-step filter) on top of the grid it already has
type: feature
priority: P1
size: M
status: todo
created: 2026-11-13
updated: 2026-11-13
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

- [ ] Pause stops the viewport from following new lines while lines continue to arrive and are still
      persisted; resuming returns to the newest line, and the indicator says which of the two states
      the panel is in.
- [ ] Search finds literal, case-insensitive matches, reports how many there are, and moves between
      them; a search with no matches says so rather than showing an empty panel.
- [ ] The per-step filter narrows lines to the selected step using `StepId`, and its counts agree with
      the step list's (a step that produced no output says it produced none).
- [ ] Selecting a step from the step list jumps to the log filtered to that step.
- [ ] "Download full log" still returns byte-identical content for the same run before and after this
      card (the endpoint is untouched and a test pins the bytes).
- [ ] A failed run shows its reason, exit code and failing step above the fold.
- [ ] A run with a large log (fixture of at least 5000 lines) renders a bounded number of nodes and
      stays responsive; the cap is stated in the UI.
- [ ] Opening the page mid-run shows what has happened so far and reaches the end without a manual
      reload after the run finishes.

## Subtasks

- [ ] Extract the log view state (follow/paused, search term and matches, step filter) into a testable
      component or view model rather than fields scattered in the page.
- [ ] Wire the step list to the filter and add the failure summary block.
- [ ] Tests (`Category=RunUi`): pause/resume semantics, search counting and navigation, step filtering
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

### TC-002: Search and step filter agree with the data

Steps:
1. Run a three-step pipeline whose second step prints a marker the first step also prints; search for
   it; then filter to step 2.

Expected:
- The search count equals the number of matching persisted lines; the step filter shows only step 2's
  lines, which is fewer than the search count.

### TC-003: The download is not the view

Steps:
1. Pause the panel, filter to one step, then download.

Expected:
- The downloaded file contains every line of the run in order, identical to the pre-card bytes.

## Development Log

### 2026-11-13

- Planned from roadmap v0.9. Deliberately client-side: the streaming contract (F-008) and the step
  attribution (F-015) are already correct, and this card must not turn a viewer feature into a change
  in how runs execute.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|

## Verification

- Related files: `src/Server/Components/Pages/Runs/RunLogPanel.razor`,
  `src/Server/Components/Pages/Runs/RunDetail.razor`, `src/Server/Components/Shared/RunLogViewModel.cs`,
  `src/Server/Runs/RunEndpoints.cs` (download, read-only), `tests/Zenkins.Tests/RunUi/*`
- How to run/verify: `dotnet build -c Release`; `dotnet test -c Release --filter "Category=RunUi"`;
  full suite; PostgreSQL contract gate not required unless the model changes.
- Results: not run (card not started).
