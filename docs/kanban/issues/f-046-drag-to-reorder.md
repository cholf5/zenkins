---
id: F-046
title: Rows can be dragged to a new place — the frame owns the gesture, each list owns its numbering
type: feature
priority: P2
size: M
status: review
created: 2026-11-20
updated: 2026-11-20
blocked: none
---

## Goal

A step or a parameter moves to where the pointer puts it, in any list, without a dozen ↑ presses. The
saved order is the order on screen afterwards.

## Background

B-014's list + inspector offered ↑↓ only, so a ten-step list needed up to nine presses per move, and
the parameters list (F-045) had no ordering control at all. Both lists are the same frame
(`js/split-list.js`), so the gesture belongs there and not in an editor: the frame moves a row, and
the list that owns the rows numbers them afterwards.

Two properties had to survive the move, both from B-014:

- **The row is the same node, moved with `insertBefore`.** A re-rendered row loses the text in its
  fields (`js/freestyle-steps.js` and `js/parameter-rows.js` serialize the DOM), and a selected row's
  block is not even inside the row any more — the pane holds it. Moving the node leaves both alone.
- **`draggable` on the handle only.** A draggable row swallows the text selection a reader makes
  inside its own fields, and the handle is the one element that is nothing but a grip.

HTML5 drag does not fire on a touch screen, so the handle is hidden below the pane's breakpoint
rather than being a control that does nothing there.

## Acceptance Criteria

- [x] Dragging a row by its handle drops it before the row whose middle is below the pointer, or after
      the last row when the pointer is past the end of the list.
- [x] Only the handle is draggable: `data-split-drag` count and `draggable="true"` count are equal on
      both editors' pages, and the fields keep their text selection.
- [x] The moving row is the same node (`insertBefore`, never a clone), so a selected row's block stays
      in the pane and an unselected row keeps the fields it holds.
- [x] A row is only reordered inside its own list — the four step lists share one form, and a step that
      jumped between them would change what the run does.
- [x] The frame says what happened (`split-reorder`, a bubbling `CustomEvent` on the list) and each
      editor numbers its own rows: four step lists in `js/freestyle-steps.js`, the parameters list in
      `js/parameter-rows.js`, each with its own index attribute.
- [x] The handle is `aria-hidden` and hidden below 60rem; the drag marks (`data-dragging`,
      `data-drop`) are cleared on `dragend`, so a cancelled drag leaves nothing behind.
- [x] The parameters list gains ↑↓ reordering (it had none) — without them, reordering would need a
      pointer *and* a wide window.
- [ ] In a real browser: drag the 4th row above the 1st, drop past the end of the list, save, reload —
      the order is the one that was dragged; the pane keeps its fields throughout; the console is clean.

## Subtasks

- [x] `src/Server/wwwroot/js/split-list.js`: the drag state, `dropPosition` (row rects, not the event's
      target), the landing mark, `insertBefore` on drop, the `split-reorder` event, and cleanup on
      `dragend`.
- [x] `src/Server/wwwroot/app.css`: `.zk-drag` (hidden below 60rem, `cursor: grab`),
      `[data-dragging]`, `[data-drop="before"|"after"]` as an inset edge mark — no layout shift.
- [x] `src/Server/Components/Shared/FreestyleStepsSection.razor`: a handle in the saved rows and in the
      row template.
- [x] `src/Server/Components/Shared/ProjectParametersEditor.razor`: the same handle, plus ↑↓ buttons in
      the saved rows and the template.
- [x] `src/Server/wwwroot/js/freestyle-steps.js` and `js/parameter-rows.js`: answer `split-reorder` by
      renumbering their own list; the parameters script also gained `moveRow` for its ↑↓ buttons.
- [x] `src/Server/Localization/Translations.zh-Hans.json`: four keys for the parameters' move buttons.
- [x] `tests/Zenkins.Tests/ProjectUi/ProjectUiSplitEditorTests.cs`: one fact pinning "only the handle is
      draggable", the frame's move-not-clone and its announcement, and both editors answering it.

## Dependencies

- B-014 (the frame, its move-don't-rebuild rule), F-045 (the parameters list, which had no ordering),
  B-010/B-011 (delegation from `document`, and the row template resolved from either place).

## Test Cases

### TC-001: Drag one row onto another

Steps:
1. On a Freestyle project with four steps, drag step 4 by its handle onto the upper half of step 1.
2. Release, then save and reload.

Expected: the mark shows above step 1 while dragging, the row lands first, the numbers read 1..4, and
the saved order is the new one.

### TC-002: Drop past the end

Steps:
1. Drag step 1 below the last row.

Expected: the mark appears on the last row's lower edge and the row lands last.

### TC-003: The open row keeps its fields

Steps:
1. Select step 3 and type into its script.
2. Drag step 3 to the top; then select another row and come back.

Expected: the text is still there, and the pane never blanked during the drag.

### TC-004: A narrow window

Steps:
1. Narrow the window below 60rem.

Expected: no handle is shown; the parameters list still reorders with ↑↓ and the steps lists with
their own move buttons.

## Development Log

### 2026-11-20

- **The frame announces, the list numbers.** A registered `renumber` callback would have been one more
  thing to install per list; a bubbling `CustomEvent` on the list element is answered by whichever
  script owns that list, through the delegated `document` listener both scripts already use. The frame
  still knows nothing about steps or parameters.
- **`dropPosition` reads the rows' own rects**, not `event.target`: the pointer hovers over whatever is
  deepest (a chip, the numeral), and the space between two rows belongs to neither. Walking the rows
  and comparing the pointer to each row's middle is what makes "between two rows" and "past the end"
  work at all.
- **Self-drop is a no-op** and is not previewed: hovering a row over itself clears the mark instead of
  promising a move that would not happen. The same guard covers "already in that place", which is not
  always an identity test - there is a whitespace text node between two `<li>`s, so a drop past the row
  that already follows the dragged one would otherwise move nothing and still announce a reorder.
- **Parameters gained ↑↓**, slightly beyond "add drag". Reason: the handle is hidden below the
  breakpoint (correctly, since touch browsers never fire HTML5 drag), and without a keyboard path a
  narrow window could not reorder parameters at all — the same argument B-014 used for keeping the
  steps' move buttons.
- Not built, on purpose: touch dragging (a pointer-events implementation with its own long-press
  semantics and scroll interference), and cross-list moves.

## Verification

- Related files: `src/Server/wwwroot/js/split-list.js`, `src/Server/wwwroot/app.css`,
  `src/Server/wwwroot/js/freestyle-steps.js`, `src/Server/wwwroot/js/parameter-rows.js`,
  `src/Server/Components/Shared/FreestyleStepsSection.razor`,
  `src/Server/Components/Shared/ProjectParametersEditor.razor`
- `dotnet build src/Server/Zenkins.Server.csproj` — **0 warnings / 0 errors**.
- `node --check` on `js/split-list.js`, `js/freestyle-steps.js` and `js/parameter-rows.js` — clean.
- `dotnet test --filter "FullyQualifiedName~Zenkins.Tests.ProjectUi"` — **32 passed / 0 failed**
  (31 before, 1 new fact).
- `dotnet test --filter "FullyQualifiedName~Zenkins.Tests.Localization|FullyQualifiedName~Zenkins.Tests.Theme"`
  — **38 passed / 0 failed** (the four new move labels pass the two-way check).
- `dotnet test --filter "FullyQualifiedName~Zenkins.Tests.Runs.FreestyleRunTests|FullyQualifiedName~Zenkins.Tests.Snippets"`
  — **28 passed / 0 failed**.
- `dotnet test --filter "FullyQualifiedName~arameter"` — **62 passed / 0 failed** (the parameters save
  path and its value semantics are untouched).
- **Not run: the browser pass.** The environment has no browser, so TC-001 to TC-004 stay for a
  reviewer: the drag gesture, the landing mark, the pane surviving a drag, and the console. What is
  pinned without a browser is the markup (only the handle is draggable), the frame's move-not-clone and
  its announcement, and both lists answering it.
