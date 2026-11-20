---
id: F-045
title: The parameters editor joins the list and inspector — one line per parameter, its fields in the pane
type: feature
priority: P2
size: M
status: review
created: 2026-11-20
updated: 2026-11-20
blocked: none
---

## Goal

A project's build parameters read as a list of lines - number, name, type - and the selected
parameter's fields open in the pane, the same shape the steps editor got in B-014. A list of ten
parameters fits on one screen.

## Background

Card F-021's parameters editor rendered every parameter as a fully expanded card, so ten parameters
were ten blocks of five fields each and the list's shape was invisible. B-014 built the frame
(`js/split-list.js` + the `.zk-split*` layout) for the steps editor and deliberately left it
list-agnostic: a list installs its row summary with `zenkinsSplit.summarize(key, fn)` and its words
arrive as data in `data-split-labels`, so this card is the frame's second consumer and needed no new
layout CSS.

Two properties had to be preserved, and both are the ones B-014 documented:

- **The fields are moved, not re-rendered.** `js/parameter-rows.js` serializes the DOM, so the
  serializer must read a row's fields through the pointer the frame leaves (`row._splitDetail`).
  Reading the row directly finds no inputs for the selected parameter and saves it as empty.
- **`Parameter {0}` went away.** The row's number is now a numeral in the line (as in the steps
  list), so the old heading string had no user left and the two-way localisation check requires
  deleting it.

The saved JSON is untouched: `ParametersJson` keeps its field names, its order, and its draft shape
(`name`, `type`, `defaultValue`, `choicesText`, `description`).

## Acceptance Criteria

- [x] Each saved parameter is one line: its number, its name (or the list's word for one without a
      name yet) and its type as a chip.
- [x] The selected parameter's fields are in the pane; the block is moved there and moved back, and
      below 60rem the same markup is an accordion.
- [x] "Add parameter" appends a row from the template, numbers it, and opens it — the reader lands in
      the fields they just created; removing a row re-numbers and moves the selection off the row
      that is gone.
- [x] A parameter's fields exist once: `ProjectParameterFields.razor` renders them for both the saved
      row and the row template.
- [x] The serialized `ParametersJson` is byte-for-byte the same shape as before (drafts, in DOM order).
- [x] `<template id="parameter-row-template"><li data-parameter-row` stays the template's own first
      child, and the script still resolves the row from `content` or the element (B-011).

## Subtasks

- [x] `src/Server/Components/Shared/ProjectParameterFields.razor` (new): one parameter's fields, the
      editor's only copy; carries `data-split-detail`.
- [x] `src/Server/Components/Shared/ProjectParametersEditor.razor`: the frame, the row lines, the pane,
      the empty state, and the template rebuilt on the shared field component.
- [x] `src/Server/wwwroot/js/parameter-rows.js`: `fieldScope`, the `parameters` summary, register +
      select after add, re-register after remove.
- [x] `src/Server/Localization/Translations.zh-Hans.json`: `Unnamed parameter` and
      `No parameters yet. Add one below.` added; `Parameter {0}` removed (no longer used).
- [x] `tests/Zenkins.Tests/ProjectUi/ProjectUiSplitEditorTests.cs`: one fact for the frame, the two
      saved lines, the template's fields, and the script reading through the pointer.

## Dependencies

- B-014 (the list + inspector frame, its move-don't-rebuild rule and its `data-split-labels`
  contract), F-021 (the parameters editor and its PRG save), B-011 (the template trap).

## Test Cases

### TC-001: Ten parameters read as ten lines

Steps:
1. Save ten parameters on a Freestyle project.
2. Open the `参数` tab on a wide viewport.

Expected: ten lines with their numbers, names and type chips; the first parameter's fields in the
pane.

### TC-002: Edit through the pane and save

Steps:
1. Select the fourth parameter, change its default.
2. Save, reload the page.

Expected: the new default is stored; the other parameters are unchanged and in the same order.

### TC-003: Add and remove

Steps:
1. Press "Add parameter" — the new line appears, numbered last, its fields open.
2. Fill it in, then remove the second parameter.

Expected: the new parameter keeps what was typed while other rows are selected; after the removal
the numbers are 1..n with no gap.

### TC-004: A narrow viewport

Steps:
1. Narrow the window below 60rem.

Expected: the pane disappears and the selected parameter's fields show inside its own row.

## Development Log

### 2026-11-20

- Implemented against the existing frame; no CSS was added, because B-014's `.zk-split*` rules were
  written without knowledge of steps (the only `.zk-` class this editor needs is `.zk-input` for its
  fields).
- The summary is name-or-`Unnamed parameter` plus the type chip. The type is the stored identifier
  (`string`, `choice`, …) exactly as the select renders it, so no new catalogue entry per type.
- The old markup's `data-parameter-label` attributes were dropped: nothing ever read them, and the
  template's labels now come from the same component the saved rows use.
- Ids changed from `param-name-0` to `param-0-name` (one stem from `IdPrefix`), matching the steps
  editor's `@Id("field")` shape. No test or script referenced the old ids.
- Deliberately not done: drag/↑↓ reordering for parameters (F-046 is the frame's drag support; the
  parameters list has never had an order control and this card does not add one).

## Bugs

| ID | Severity | Description | Status | Resolution |
|---|---|---|---|---|
| F-045-1 | low | The first draft of the new fact counted the row template's `<li>` as a saved row and expected two lines where the page renders three matches | fixed in the card | the fact now counts inside `<ol data-parameter-rows-list>` only |

## Verification

- Related files: `src/Server/Components/Shared/ProjectParameterFields.razor`,
  `src/Server/Components/Shared/ProjectParametersEditor.razor`,
  `src/Server/wwwroot/js/parameter-rows.js`,
  `src/Server/Localization/Translations.zh-Hans.json`
- `dotnet build src/Server/Zenkins.Server.csproj` — **0 warnings / 0 errors**.
- `node --check src/Server/wwwroot/js/parameter-rows.js` — clean.
- `dotnet test --filter "FullyQualifiedName~Zenkins.Tests.ProjectUi"` — **31 passed / 0 failed**
  (30 before, 1 new fact; the parameters row-template fact and the two split-editor facts hold).
- `dotnet test --filter "FullyQualifiedName~Zenkins.Tests.Localization|FullyQualifiedName~Zenkins.Tests.Theme"`
  — **38 passed / 0 failed** (the two new strings and the deleted `Parameter {0}` key pass the
  two-way check).
- `dotnet test --filter "FullyQualifiedName~Zenkins.Tests.Milestone.MilestoneParameterTests|FullyQualifiedName~Zenkins.Tests.Steps.PasswordParameterTests"`
  — **3 passed / 0 failed** (the save path and the value semantics are untouched).
- **Not run: the browser pass.** The environment could not start a browser, so the pane's moving,
  the chips, the accordion and the keys are unverified by eye; TC-001 to TC-004 are for a reviewer
  with a browser. The structure, the moved-block contract and the serialized shape are pinned by
  the facts above.
