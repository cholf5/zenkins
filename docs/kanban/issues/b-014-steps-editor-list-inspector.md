---
id: B-014
title: The steps editor is a list and an inspector — one line per step on the left, the selected step's fields on the right
type: refactor
priority: P1
size: M
status: review
created: 2026-11-20
updated: 2026-11-20
blocked: none
---

## Goal

Ten build steps read as ten lines instead of ten expanded forms: a reader sees the shape of the run
at a glance, and edits the one step they selected without scrolling past the other nine.

## Background

The Freestyle steps editor rendered every step as a fully expanded card, so a ten-step pipeline was
about ten screens of fields with the list's order and shape buried in it. Jenkins shows one collapsed
line per build step and opens the one you pick; Unity's Inspector is the same two-column shape. The
editor now does too.

Two constraints decided the implementation, and both are load-bearing (a future reader who "tidies"
them up breaks saving):

- **The fields are moved, not re-rendered.** `js/freestyle-steps.js` serializes DOM nodes, not a
  model, so a block that is re-created on selection loses whatever the reader typed into its
  `<textarea>`. `js/split-list.js` calls `appendChild` and nothing else.
- **`bb:` is prebuilt, purged Tailwind.** A class the package did not emit is silently inert - no
  build error, no console warning. The new layout is hand-written `.zk-*` in `app.css`.

The four lists (main + always/success/failure) share the same component, so the shape changed once.

## Acceptance Criteria

- [x] Each step is one line: its number, its name (or the script's first line, so an unnamed step still
      says what it does) and a chip per non-default setting — stage, env line count, artifact count,
      timeout, attempts, worker requirement, continue-on-error.
- [x] The selected step's fields are in the right-hand pane; the row's block is moved there, not
      re-rendered, and moved back when another row is selected.
- [x] Clicking a line selects it; ↑ / ↓ move the selection while a line has focus; `<details>` keeps
      "Advanced" fields in the DOM (a hidden node still serializes, a dropped one does not).
- [x] Below 60rem there is no pane and the same markup is an accordion; crossing the breakpoint moves
      the open block rather than restyling a dead node.
- [x] A step's fields exist once: `FreestyleStepFields.razor` renders both the saved row and the row
      template, so the template cannot lose a field the saved row has (the two hand-maintained field
      sets had already drifted - the template's stage and environment labels were untranslated).
- [x] Adding a step registers and selects the new row, so the reader lands in the fields they created.
      A removed row's block does not linger in the pane.
- [x] All four lists get the behaviour, keyed by `ListKey`; no list has a special case.
- [x] The `runs-on` placeholder shows `linux && (arm64 || x64)`, not the escaped spelling the
      browser was showing (found here: the attribute value was a C# string Razor escaped a second time).

## Subtasks

- [x] `src/Server/Components/Shared/FreestyleStepFields.razor` (new): one step's fields, the project's
      only copy.
- [x] `src/Server/Components/Shared/FreestyleStepsSection.razor`: rewritten to a row line (summary,
      ↑↓, remove) plus the pane; the two hand-written field sets became one.
- [x] `src/Server/wwwroot/js/split-list.js` (new): the frame — selection, moving the detail block into
      the pane, summaries, keyboard, the breakpoint, `MutationObserver`/`enhancedload` re-registration.
- [x] `src/Server/wwwroot/js/freestyle-steps.js`: serialize through `row._splitDetail`, compute the row
      summary (`stepSummary`), register and select after add/insert.
- [x] `src/Server/wwwroot/app.css`: `.zk-split*`, `.zk-chip`, `.zk-advanced`, `.zk-input`.
- [x] `src/Server/Localization/Translations.zh-Hans.json`: 11 keys added, the now-unused `"Step"`
      removed (the catalog is checked both ways).
- [x] `src/Server/Components/App.razor`: load `js/split-list.js` before `js/freestyle-steps.js`.
- [x] `tests/Zenkins.Tests/ProjectUi/ProjectUiSplitEditorTests.cs` (new): pins the structure and the
      move-don't-rebuild rule (`cloneNode` must not appear in the frame).

## Dependencies

- F-020 (the steps editor), F-031 (the three post lists), B-010/B-011 (why every handler is delegated
  from `document` and why the row template is read from either place).

## Test Cases

### TC-001: A ten-step list reads as ten lines

Steps:
1. Open a Freestyle project's `步骤` tab on a wide viewport.

Expected: one line per step with its number, name and its non-default chips; the first step's fields
in the pane.

### TC-002: Editing does not lose text

Steps:
1. Select the third step, type into its script.
2. Select the fourth step, then the third again.
3. Save, then reload.

Expected: the edits are in the pane when returning to the row, and saved.

### TC-003: A narrow viewport

Steps:
1. Narrow the window below 60rem with a step selected.

Expected: the pane disappears, the selected step's fields show inside its own row, and the block
moves back when the window widens.

### TC-004: The row template keeps its fields

Steps:
1. Press "Add step".

Expected: the new row is appended, numbered, selected and its fields are in the pane - and the
template's field set is the same one the saved rows render.

## Development Log

### 2026-11-20

- Implemented in one pass. The layout decision was made deliberately narrow: `split-list.js` is a
  frame with no knowledge of steps or parameters (a list installs its summary with
  `zenkinsSplit.summarize(key, fn)` and its words arrive as JSON in `data-split-labels`, so no
  Chinese or English string lives in a `.js` file). The parameters editor can adopt it unchanged.
- Summaries are rewritten only when the text actually differs (`setText` compares, `setChips`
  compares a signature): the observer that re-registers after a mutation would otherwise answer its
  own writes forever.
- The `runs-on` placeholder defect was found while porting the field set; the fix is its own commit
  (`fix(ui)`) because it is a one-character change in a large one.
- Not done here, on purpose: drag-and-drop reordering (only ↑↓), and the parameters editor (F-045).

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| B-014-1 | low | The `runs-on` placeholder reached the browser with its `&&` still entity-escaped, so the field showed the markup instead of the operator | fixed | the attribute now carries `&&`; Razor's own encoding gives the browser the operator |

## Verification

- Related files: `src/Server/Components/Shared/FreestyleStepFields.razor`,
  `src/Server/Components/Shared/FreestyleStepsSection.razor`, `src/Server/wwwroot/js/split-list.js`,
  `src/Server/wwwroot/js/freestyle-steps.js`, `src/Server/wwwroot/app.css`
- `dotnet build src/Server/Zenkins.Server.csproj` — **0 warnings / 0 errors**.
- `node --check` on `js/split-list.js` and `js/freestyle-steps.js` — clean.
- `dotnet test --filter "FullyQualifiedName~Zenkins.Tests.ProjectUi"` — **30 passed / 0 failed**
  (28 pre-existing, 2 new split-editor facts).
- `dotnet test --filter "FullyQualifiedName~Zenkins.Tests.Localization|FullyQualifiedName~Zenkins.Tests.Theme"`
  — **38 passed / 0 failed**.
- `dotnet test --filter "FullyQualifiedName~Zenkins.Tests.Runs.FreestyleRunTests|FullyQualifiedName~Zenkins.Tests.Snippets"`
  — **24 passed / 0 failed**.
- **Not run: the browser pass.** The environment could not start a browser, so the pixels, the pane's
  moving, the keyboard walk, the 60rem breakpoint and the accordion are unverified by eye. The
  structure and the move-don't-rebuild rule are pinned by tests instead. A reviewer with a browser
  should walk TC-001 to TC-004 and record the result here.
