---
id: B-011
title: "Add step" and "Add parameter" do nothing — both row editors clone from template.content, which is empty once the circuit is interactive
type: bug
priority: P1
size: S
status: review
created: 2026-11-19
updated: 2026-11-19
blocked: none
---

## Goal

Every "Add step" (all four lists) and "Add parameter" button on a project's definition tabs adds a row
the reader can type in — on the live page, not only on the markup the server rendered.

## Background

Reported against the running UI on a freshly created Freestyle project: the **Steps** tab showed the
four sections and their **Add step** buttons, and clicking any of them did nothing at all — no row, no
message, no error on screen.

The console had the answer:

```text
TypeError: Cannot read properties of null (reading 'cloneNode')
    at addStep (/js/freestyle-steps.js:80:61)
```

Both editors render each new row from a `<template>` and clone `template.content.firstElementChild`
(`js/freestyle-steps.js`, `js/parameter-rows.js`). That is correct only for the HTML the *parser*
built. A `<template>`'s children live in `content` because the parser puts them there; `appendChild`
does not — it inserts into the element's own child list. Blazor's renderer builds a component's
children exactly that way, so the moment the interactive circuit takes over the prerendered page, the
row is a plain child of `<template>` and `content` is empty.

Measured in the browser on the affected page, after the circuit connected:
`template.content.childElementCount` = **0**, `template.childElementCount` = **1**. Before the
circuit (the static HTML) it is the other way round, which is why nothing in the server-rendered markup
looked wrong and no server-side fact could see it.

B-010's fix is what made the defect visible: the buttons only started *reaching* `addStep` once the
row handlers moved to a `document`-level `click` that survives a replaced DOM. Before that they were
dead earlier, for a different reason.

## Acceptance Criteria

- [x] **Add step** adds an empty row to the main list and to each of the three post lists, on a live
      interactive page whose circuit is connected.
- [x] **Add parameter** adds a row on the Parameters tab, same condition.
- [x] A row added this way survives typing and saving: the saved spec holds what was typed.
- [x] The insert-snippet path (a row filled from a `script-step` snippet) resolves its row the same
      way as the empty add path — both go through one helper, not two copies.
- [x] The row is resolved from `content` **or** from the template element, so the same code works
      before and after activation.
- [x] Facts pin both halves: the template markup the server renders, and the two scripts' resolution
      (fail-closed — the pre-fix files turn both facts red).

## Subtasks

- [x] `js/freestyle-steps.js`: one `templateRow(template)` helper (`content.firstElementChild ||
      firstElementChild`), used by `addStep` and `insertSnippetStep`; a null row returns instead of
      throwing.
- [x] `js/parameter-rows.js`: the same resolution inline in the `add` branch, which no longer throws on
      a missing row.
- [x] `tests/Zenkins.Tests/ProjectUi/ProjectUiRowTemplateTests.cs`: one fact per editor.
- [x] `CHANGELOG.md` (Unreleased).

## Dependencies

- B-010 — its delegation fix is what let the click reach this code path at all.
- F-020 (Freestyle steps editor), F-021 (parameters editor), F-030/F-031/F-032/F-033 (the fields the
  template carries).

## Test Cases

### TC-001: The steps editor on a live page

Steps:
1. Open a Freestyle project's **Steps** tab in a browser that has an interactive circuit connected.
2. Click **Add step** under each of the four sections.

Expected: each click adds one empty row with its fields; the console stays clean.

### TC-002: A row saved

Steps:
1. Click **Add step**, type a name and a script, click **Save steps**.

Expected: the redirect reports the steps were saved, and the row comes back on the reloaded page with
the typed name and script.

### TC-003: The parameters editor

Steps:
1. Open the **Parameters** tab and click **Add parameter**.

Expected: one new parameter row; no console error.

## Development Log

### 2026-11-19

- **Reproduced first, in a real browser.** The page was reached through the app's own sign-in form in
  headless Chrome against a locally running server seeded through the test harness, with cache
  disabled. The click produced the `cloneNode` TypeError above and 0 rows, and the template's
  content/element child counts read `0`/`1` — the defect, not a theory about it.
- **The fix.** One helper resolves the row: `template.content.firstElementChild ||
  template.firstElementChild`. Reading both is deliberate rather than switching to the element alone:
  static HTML (no circuit yet, or a page that never becomes interactive) still has the row in
  `content`, and both states must work. A missing row now returns instead of throwing, so the button
  cannot fail silently a second way.
- **Why the other editors were checked.** `js/snippets.js` and `js/project-kind.js` own no template.
  `#parameter-row-template` and the four `freestyle-step-template*` elements were the only
  `template.content` reads in `wwwroot`.
- **The facts.** `ProjectUiRowTemplateTests` renders the real pages and reads the real scripts: the
  row is inside the template in the server's markup, the resolution has both halves, nothing clones
  `content`'s first child directly, and each file reads `template.content.firstElementChild` exactly
  once (so a second, unguarded copy would go red). Run against the pre-fix scripts both facts fail
  (2 failed / 0 passed), which is the check that they are a net and not a description.

## Verification

- Real browser, headless Chrome, live server, cache disabled (macOS, Chrome 157):
  - Steps tab, project with no steps: rows 0 → **1** after **Add step**; typed `Compile` /
    `echo hello from the fixed editor`, **Save steps** → `?notice=steps-saved`, row present with both
    values, a further **Add step** → 2 rows.
  - All four lists: each **Add step** added exactly one row to its own list.
  - Parameters tab: rows 1 → **2** after **Add parameter**.
  - Console: no exceptions after the fix; before it, the `cloneNode` TypeError on the first click.
- `dotnet test --filter "FullyQualifiedName~ProjectUiRowTemplateTests"` — **2 passed / 0 failed**;
  against the reverted scripts **2 failed / 0 failed-passed**, then green again with the fix restored.
- `dotnet test --filter "FullyQualifiedName~ProjectUi|FullyQualifiedName~SnippetAffordanceTests|FullyQualifiedName~FreestyleRunTests"`
  — **42 passed / 0 failed**.
- `dotnet build Zenkins.slnx` — 0 warnings / 0 errors.
- **Not run: the whole gate** (`scripts/verify.py`). The full suite is heavy on this machine and the
  change is two static scripts plus one new test file; the Milestone, Recovery, Postgres and publish
  suites were not run, so nothing here speaks for them.

## Review

Pending.
