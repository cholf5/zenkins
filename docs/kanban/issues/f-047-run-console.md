---
id: F-047
title: A full-page run console — block lines, follow the tail, live status and a step sidebar
type: feature
priority: P1
size: L
status: review
created: 2026-11-20
updated: 2026-11-20
blocked: none
---

## Goal

The run's log becomes a page of its own, shaped like the part of Jenkins people actually use: a
continuous console that follows the tail, stops following when the reader scrolls up, keeps every
line on its own row, and shows the run's status and step states while they change. The run page
keeps the summary and gains one "Console Output" entry.

## Background

A real Freestyle run exposed three defects that are three different layers (design:
`docs/superpowers/specs/2026-11-20-run-console-design.md`):

1. `RunLogPanel` rendered each line as an inline `<span>` and relied on a newline Razor trimmed, so a
   whole step's output became one wrapped line.
2. `RunDetail` read the run once; status, steps, elapsed and the failure block never changed after
   load, and the log panel never scrolled.
3. The log lived in a 384px card at the bottom of a `max-w-3xl` column with no page-level reading.

The design was reviewed before implementation; the console replaces the inline panel rather than
growing it. The D-004 control semantics (pause, search, step filter, download) are kept.

## Acceptance Criteria

- [x] `/runs/{id}/console` and `/projects/{id}/runs/{n}/console` render the same run; a foreign id is
      a 404; `?step=` opens the console filtered to that step.
- [x] The run page offers a `Console Output` link, the runs list offers a `Console` link, and each
      step's `View log` opens the console filtered to that step.
- [x] Every line is its own block; leading spaces and blank lines survive; stderr is red and has a
      left border; the current search match is marked.
- [x] The console follows the tail by default; scrolling up pauses following, "Jump to latest"
      resumes; an explicit pause survives returning to the bottom.
- [x] Status, step badges and elapsed update while the run is alive and stop at a terminal state;
      a failed read keeps the last good state.
- [x] The step sidebar (main and post steps) shows status, duration and a live elapsed; clicking a
      step filters, clicking it again clears.
- [x] The viewer retains at most 20,000 lines and renders at most 2,000; both bounds are stated, and
      the download remains the complete record and unchanged byte for byte.
- [ ] Real-browser pass: follow/scroll-up/jump, step filter, search, timestamps, wrap, mobile width,
      and a 5,000-line fixture. Pending; see Verification.

## Subtasks

- [x] Extract the retained-window and two-pause-source rules into `RunLogViewModel`.
- [x] Render lines through a block-per-line `RunConsoleLines` component.
- [x] Add the console route, header and navigation.
- [x] Add the 1s elapsed / 2s status poll (`RunLiveRefresher`) to both pages.
- [x] Add the follow controller (`js/run-console.js`) and the JS interop.
- [x] Add the page layout, step sidebar and console CSS.
- [x] Localize the new copy; amend D-004; update existing tests and the Milestone assertion.

## Dependencies

- F-008 (log streaming and persistence), D-004 (the control semantics), F-015 (step stamps),
  F-030/F-031 (the step lists the sidebar shows).

## Test Cases

### TC-001: The console follows the tail and pauses on scroll-up

Covered by `RunLogControlsTests` (pause/resume and the two pause sources) and
`RunConsoleFollowTests` (the script and interop wiring). The browser half is pending.

### TC-002: Every line renders on its own row

`RunConsoleLinesTests` renders the component statically and counts block elements; a three-line
entry set is three `.zk-console-line` blocks, with leading spaces and an empty line preserved.

### TC-003: Live status

`RunLiveRefresherTests`: a running read continues; a terminal read stops and is not repeated; a
terminal run applied at load is not read; a failed read keeps the last good run and clears the
error on the next success; an in-flight read is not started twice.

### TC-004: The console route and navigation

`RunConsolePageTests`: the page shell/header/toolbar/step list render; the run-page and all-runs
links are present; `?step=` opens the same console; a foreign id is a 404. `RunDetailFailureSummaryTests`
pins the run page's `Console Output` entry and the step links' new URL; `MilestoneHappyPathTests`
pins the run page's entry in the real-binary path.

### TC-005: Retention and render bounds

`RunLogControlsTests`: 100,005 received lines leave 20,000 retained and 80,005 dropped, with the
exact retention sentence; duplicates do not count twice; the 5,000-line render cap is unchanged.

### TC-006: Real browser

Pending. The repo has no browser harness; D-004-B3 records why TestServer cannot host the circuit.

## Development Log

### 2026-11-20

- Implemented from the approved design. Decisions taken while building:
  - The old inline panel on the run page was removed rather than grown; the console is the single
    viewer, and the download is the way to get everything.
  - Status uses a 1s elapsed clock and a 2s database read through `RunLiveRefresher`, not a new
    status hub. A terminal state stops the reads; the log connection stays open for late lines.
  - Pause has two sources (explicit and scroll); returning to the bottom clears only the scroll
    pause. This is what makes "scroll up to read" and "pause on purpose" coexist.
  - The viewer retains 20,000 lines and renders 2,000. The second bound alone does not bound memory;
    both are now stated in the UI and the download is canonical.
  - `?step=` is applied once on the panel, not on every parameter set: the 2s status poll re-renders
    the parent, and re-applying the query would silently reset a filter the user changed.
  - The sidebar and console headers both filter on the step row id, never on the header text.
- Review pass (same day): a reconnect replays history, and sequences retention already dropped were
  re-merged, inflating `ReceivedLines`/`DroppedFromViewer`; an evicted-through horizon now skips them
  without counting. An unknown `?step=` is ignored (decision 11) instead of showing an empty view.
  Both console routes joined the tenant probes (foreign 404 + positive control). The follow race -
  a scroll event and a log batch's `afterRender` in one frame - is closed by capturing position at
  event time, a sticky `away`, and an explicit `follow` on resume; the real-Chrome harness pins it.

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| F-047-B1 | Low | The component test's static renderer had no `Localizer`; the component inherits the global `@inject` even though it does not use the copy. | Fixed | The test registers `TestText.English` in its service collection. |
| F-047-B2 | Low | The first console-page test compared the heading literally, but the rendered middle dot is the HTML entity `&#xB7;`. | Fixed | The test asserts the heading's `Console` prefix and the stable ids instead. |

## Verification

- Related files: see the design's §7; the notable ones are `RunConsole.razor`,
  `RunConsoleLines.razor`, `RunLogViewModel.cs`, `RunLogPanel.razor`, `RunLiveRefresher.cs`,
  `js/run-console.js`, `app.css`, and the run page/step list.
- How to run/verify:
  - `dotnet build src/Server/Zenkins.Server.csproj -v q --nologo` → passed, 0 warnings.
  - `node --check src/Server/wwwroot/js/run-console.js` → passed.
  - `dotnet test --filter "Category=RunUi"` → 56/56 (after the review fixes).
  - `dotnet test --filter "Category=Pipeline"` → 209/209 (the Windows byte assertion is now
    platform-branched; the Windows half runs on CI).
  - `dotnet test --filter "Category=TenantIsolation"` → 178/178.
  - `dotnet test --filter "Category=WorkerRuntime"` → 46/46 + 2 Windows-only skipped.
  - `dotnet test -c Release --filter "Category=Milestone"` → 12/12.
  - **Isolated JS harness in a real Chrome** (the console DOM + `js/run-console.js`, fake
    `dotNet`): follow-to-bottom at load, a scroll-to-top callback reports `OnConsoleScroll(false)`,
    a paused `afterRender` holds the position, `afterRender(..., seq)` scrolls the match into view,
    and the scroll-up-vs-render race holds the position before an explicit `follow` pins it again.
    All five passed; this is the script half of the follow contract, not the full Blazor circuit.
  - `dotnet test -c Release --filter "Category!=Milestone"` (and a Debug retry): **did not complete
    within 8+ minutes in this environment; stopped.** The affected categories were run
    individually and are green (RunUi 54/54, WorkerRuntime 46/46 + 2 Windows-only skipped,
    Localization 18/18, Theme/TenantIsolation after the inventory update). The full-suite hang is
    not attributed to this branch; re-run on a quieter machine before the card is marked done.
  - Real-browser pass through the signed-in app, and the 5,000-line visual fixture: **still owed**
    (this session could not sign in to the dev instance). Run on a machine with a browser and the
    dev credentials before the card leaves review.
- Results: targeted automated gates green; the isolated Chrome harness green; the full app browser
  pass and the full-suite completion are the remaining acceptance items.
